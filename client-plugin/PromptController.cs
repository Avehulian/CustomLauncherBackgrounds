using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using EFT.UI;
using PreyToLive.CustomLauncherBackgrounds;
using UnityEngine;

namespace CLB.Gui;

/// <summary>
/// The in-game window. Once the player is in the main menu it asks the connected server whether a
/// custom launcher background is available and, when one is available (and the player has not already
/// updated or opted out), shows a window offering to update the launcher.
///
/// Accepting the update closes the SPT launcher (which locks its own file), stages the patched launcher
/// and closes the game - the launcher file is replaced right after the game has exited.
/// After a successful update - or after "Don't ask again" - the window never appears again
/// (see the BepInEx config, both flags can be reset to be asked again).
/// </summary>
public class PromptController : MonoBehaviour
{
    private enum Phase
    {
        WaitingForMenu,
        Checking,
        Hidden,
        Prompt,
        Updating,
        Success,
        Failed,
    }

    /// <summary>EFT's main-menu scene (verified against the game's build settings).</summary>
    private const string MenuSceneName = "MenuUIScene";

    // The window is drawn in "virtual" units and scaled up for readability.
    private const float Scale = 1.5f;
    private const float WindowWidth = 640f;
    private const float WindowHeight = 300f;
    private const float Pad = 28f;
    private const float ButtonHeight = 48f;
    private const float SecondaryButtonHeight = 40f;
    private const float PromptDelayAfterMenuSeconds = 5f;

    private volatile int _phase = (int)Phase.WaitingForMenu;
    private float _menuFirstSeenAt = -1f;
    private float _lastMenuCheck;
    private bool _inMenu;
    private bool _busy;
    private MenuUI _menuUi;

    private string _serverOrigin = string.Empty;
    private LauncherExeUpdater.LauncherUpdateInfo _info;
    private bool _buildUpdated;
    private volatile string _status = string.Empty;
    private volatile string _error = string.Empty;
    private volatile string _result = string.Empty;
    private float _closingAt = -1f;

    private Rect _windowRect = new Rect(0f, 0f, WindowWidth * Scale, WindowHeight * Scale);
    private bool _positioned;
    private bool _dragging;
    private Vector2 _dragOffset;

    private Phase CurrentPhase => (Phase)_phase;

    private void Update()
    {
        var now = Time.time;

        if (_closingAt > 0f && now >= _closingAt)
        {
            _closingAt = -1f;
            Plugin.Log.LogInfo("Closing the game so the staged launcher update can be applied.");
            Application.Quit();
            return;
        }

        var phase = CurrentPhase;

        // Menu tracking only runs in the phases that need it. Before this, FindObjectOfType<MenuUI>()
        // was polled every second in *every* phase - a full scan of the loaded objects - which hitched
        // the game once per second while in a raid (with the prompt still waiting to be answered).
        // While waiting for the menu the UI-object check stays (it verifies the menu is really up);
        // afterwards only the cheap active-scene check runs.
        if ((phase == Phase.WaitingForMenu || phase == Phase.Prompt) && now - _lastMenuCheck > 1f)
        {
            _lastMenuCheck = now;
            _inMenu = phase == Phase.WaitingForMenu ? MenuDetected() : MenuVisible();
        }

        if (phase == Phase.WaitingForMenu)
        {
            if (_inMenu && _menuFirstSeenAt < 0f)
            {
                _menuFirstSeenAt = now;
                Plugin.Log.LogInfo($"Main menu detected (scene '{ActiveSceneName()}'), waiting to offer the launcher background.");

                // The launcher usually sits in the tray while playing - give its window the same chrome
                // treatment as the server does for the local host: border tint always (it matches the
                // launcher's own light line shade), title bar removed only when the player wants it.
                try
                {
                    LauncherWindowChrome.ApplyToLauncherWindows(Plugin.BorderlessLauncherWindow.Value, tint: true);
                }
                catch (Exception chromeException)
                {
                    Plugin.Log.LogWarning("Launcher window chrome failed: " + chromeException.Message);
                }
            }

            // Fallback for setups where the menu could not be detected: offer after 30 seconds.
            var readyByMenu = _menuFirstSeenAt > 0f && now - _menuFirstSeenAt > PromptDelayAfterMenuSeconds;
            var readyByTime = now > 30f;

            if ((readyByMenu || readyByTime) && !_busy)
            {
                _busy = true;
                _phase = (int)Phase.Checking;
                Task.Run(CheckServer);
            }
        }
    }

    private bool MenuDetected()
    {
        try
        {
            if (_menuUi != null)
            {
                return true;
            }

            _menuUi = UnityEngine.Object.FindObjectOfType<MenuUI>();
            return _menuUi != null;
        }
        catch
        {
            return Time.time > 30f;
        }
    }

    /// <summary>
    /// Cheap "is the main menu up" check for the prompt phase: the cached MenuUI object (liveness +
    /// active state - no scanning) or the active scene name. FindObjectOfType is never called here,
    /// so this is safe to poll once per second while the player is in a raid.
    /// </summary>
    private bool MenuVisible()
    {
        try
        {
            if (_menuUi != null && _menuUi.gameObject.activeInHierarchy)
            {
                return true;
            }
        }
        catch
        {
            // destroyed interop object - fall through to the scene check
        }

        return InMenuScene();
    }

    /// <summary>
    /// Cheap "is the main menu up" check: the active scene name. Unlike FindObjectOfType&lt;MenuUI&gt;()
    /// this never scans the loaded objects, so it is safe to poll every second while the player is in
    /// a raid. A wrong/renamed scene simply keeps the prompt hidden until the fallback path runs.
    /// </summary>
    private static bool InMenuScene()
    {
        return string.Equals(ActiveSceneName(), MenuSceneName, StringComparison.Ordinal);
    }

    private static string ActiveSceneName()
    {
        try
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            return scene.IsValid() ? scene.name : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void CheckServer()
    {
        try
        {
            using var http = LauncherExeUpdater.CreateHttpClient();
            // The info fetch is tiny - keep a short timeout so trying fallback origins cannot stall.
            http.Timeout = TimeSpan.FromSeconds(20);

            LauncherExeUpdater.LauncherUpdateInfo info = null;
            var failures = new List<string>();
            foreach (var origin in BackendUrlProvider.GetCandidates())
            {
                info = LauncherExeUpdater.FetchInfo(http, origin, out var error);
                if (info is not null)
                {
                    _serverOrigin = origin;
                    break;
                }

                failures.Add($"{origin} ({error})");
            }

            if (info is null)
            {
                Plugin.Log.LogInfo("Server does not offer a launcher background. Tried: " + string.Join("; ", failures));
                _phase = (int)Phase.Hidden;
                return;
            }

            _info = info;

            // When the launcher is managed by ModSync, make sure a backup exists next to it so that
            // removing the sync entry (untick / retirement) restores a working launcher.
            try
            {
                var gameRoot = BackendUrlProvider.GetGameRoot();
                if (LauncherExeUpdater.HasModSyncUpdater(gameRoot))
                {
                    var launcherPath = LauncherExeUpdater.FindLauncherFile(gameRoot);
                    if (launcherPath is not null)
                    {
                        LauncherExeUpdater.EnsureModSyncBackup(launcherPath);
                    }
                }
            }
            catch (Exception backupException)
            {
                Plugin.Log.LogWarning("ModSync launcher backup failed: " + backupException.Message);
            }

            // Prompt unless the player is covered: "Don't ask again", or they already updated and the
            // server's launcher build has not changed since (then there is nothing new for them).
            _buildUpdated = Plugin.LauncherUpdated.Value
                && !string.Equals(Plugin.LastAppliedBuild.Value, info.Build, StringComparison.OrdinalIgnoreCase);

            if (!PromptDecider.ShouldPrompt(
                    Plugin.NeverAsk.Value,
                    Plugin.LauncherUpdated.Value,
                    Plugin.LastAppliedBuild.Value,
                    Plugin.LastAppliedImage.Value,
                    info))
            {
                Plugin.Log.LogInfo($"Launcher prompt not needed (image {info.Image}, build {Short(info.Build)}).");
                _phase = (int)Phase.Hidden;
                return;
            }

            Plugin.Log.LogInfo(_buildUpdated
                ? $"Offering launcher refresh: the server updated its launcher build ({Short(info.Build)})."
                : $"Offering launcher background '{info.Image}' from {_serverOrigin}.");
            _phase = (int)Phase.Prompt;
        }
        catch (Exception exception)
        {
            Plugin.Log.LogWarning("Launcher background check failed: " + exception.Message);
            _phase = (int)Phase.Hidden;
        }
        finally
        {
            _busy = false;
        }
    }

    private void StartUpdate()
    {
        _phase = (int)Phase.Updating;
        _busy = true;
        _status = "Preparing...";

        Task.Run(() =>
        {
            try
            {
                var gameRoot = BackendUrlProvider.GetGameRoot();
                var launcherPath = LauncherExeUpdater.FindLauncherFile(gameRoot);
                if (launcherPath is null)
                {
                    throw new InvalidOperationException("SPT.Launcher.exe was not found in the SPT folder");
                }

                // Download and validate while the game still runs, then stage the file and let an
                // external applier do the swap after the game has closed (no locked-launcher races).
                var bytes = LauncherExeUpdater.DownloadPatchedLauncher(_serverOrigin, _info, text => _status = text);

                // The SPT launcher usually stays open (in the tray) while the game runs and locks its
                // own executable - close it so the staged update can be applied once the game exits.
                _status = "Closing the SPT launcher...";
                var closedLaunchers = LauncherProcessCloser.CloseSptLauncher();
                if (closedLaunchers.Count > 0)
                {
                    Plugin.Log.LogInfo("Closed SPT launcher: " + string.Join(", ", closedLaunchers));
                }

                var pid = Process.GetCurrentProcess().Id;

                if (LauncherExeUpdater.HasModSyncUpdater(gameRoot))
                {
                    _status = "Staging the update for ModSync...";
                    LauncherExeUpdater.EnsureModSyncBackup(launcherPath);
                    var staged = LauncherExeUpdater.StageForModSync(gameRoot, bytes);
                    Plugin.Log.LogInfo("Staged launcher update for ModSync: " + staged);

                    var updater = Path.Combine(gameRoot, "ModSync.Updater.exe");
                    Plugin.Log.LogInfo($"Starting ModSync updater: {updater} --silent {pid}");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = updater,
                        Arguments = $"--silent {pid}",
                        WorkingDirectory = gameRoot,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });

                    _result = "Update staged for ModSync.";
                }
                else
                {
                    // Fallback without ModSync: our standalone patcher acts as the post-exit applier.
                    _status = "Preparing the launcher patcher...";
                    var helperPath = Path.Combine(Path.GetTempPath(), "CustomLauncherBackgrounds", "CustomLauncherBackgrounds.Patcher.exe");
                    LauncherExeUpdater.DownloadHelperTool(_serverOrigin, helperPath);

                    var staged = LauncherExeUpdater.StageForHelper(launcherPath, bytes);
                    Plugin.Log.LogInfo($"Staged launcher update: {staged}; starting helper: {helperPath}");
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = helperPath,
                        Arguments = $"--apply-staged \"{launcherPath}\" \"{staged}\" --wait-pid {pid}",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });

                    _result = "Update staged.";
                }

                Plugin.LastAppliedImage.Value = _info.Image;
                Plugin.LastAppliedBuild.Value = _info.Build;

                // Do not show the window again until the server rebuilds its launcher.
                Plugin.LauncherUpdated.Value = true;

                _phase = (int)Phase.Success;
                _closingAt = Time.time + 3f;
            }
            catch (Exception exception)
            {
                var message = exception.Message;
                if (exception is IOException or UnauthorizedAccessException)
                {
                    message += " (is the SPT Launcher still running? close it and try again)";
                }

                _error = message;
                Plugin.Log.LogWarning("Launcher update failed: " + exception);
                _phase = (int)Phase.Failed;
            }
            finally
            {
                _busy = false;
            }
        });
    }

    private static string Short(string value)
    {
        return string.IsNullOrEmpty(value) ? "unknown" : (value.Length > 12 ? value.Substring(0, 12) : value);
    }

    private void OnGUI()
    {
        var phase = CurrentPhase;
        if (phase == Phase.WaitingForMenu || phase == Phase.Checking || phase == Phase.Hidden)
        {
            return;
        }

        // While simply being prompted, only show the window in the main menu.
        if (phase == Phase.Prompt && !_inMenu)
        {
            return;
        }

        try
        {
            DrawWindow(phase);
        }
        catch (Exception exception)
        {
            Plugin.Log.LogError("Gui error: " + exception);
            _phase = (int)Phase.Hidden;
        }
    }

    private void DrawWindow(Phase phase)
    {
        if (!_positioned)
        {
            _windowRect.width = WindowWidth * Scale;
            _windowRect.height = WindowHeight * Scale;
            _windowRect.x = (Screen.width - _windowRect.width) / 2f;
            _windowRect.y = (Screen.height - _windowRect.height) / 2f;
            _positioned = true;
        }

        HandleDrag();

        // Draw in virtual units, scaled and positioned via the GUI matrix so the text and buttons are
        // comfortably large on every resolution.
        var previousMatrix = GUI.matrix;
        GUI.matrix = Matrix4x4.TRS(
            new Vector3(_windowRect.x, _windowRect.y, 0f),
            Quaternion.identity,
            new Vector3(Scale, Scale, 1f));

        try
        {
            EnsureStyles();

            // Rounded dark card, like the reference design.
            GUI.Box(new Rect(0f, 0f, WindowWidth, WindowHeight), GUIContent.none, _panelStyle!);

            var x = Pad;
            var width = WindowWidth - Pad * 2f;

            DrawTitle(22f, "Custom Launcher Backgrounds");

            switch (phase)
            {
                case Phase.Prompt:
                    DrawMessage(80f, "The server has updated it's launcher since your last session.");
                    DrawHighlighted(118f, "Launcher is outdated", _highlightStyle!);

                    var gap = 16f;
                    var halfWidth = (width - gap) / 2f;

                    if (GUI.Button(new Rect(x, 186f, width, ButtonHeight), "Update", _primaryButtonStyle!))
                    {
                        StartUpdate();
                    }

                    if (GUI.Button(new Rect(x, 246f, halfWidth, SecondaryButtonHeight), "Skip", _buttonStyle!))
                    {
                        _phase = (int)Phase.Hidden;
                    }

                    if (GUI.Button(new Rect(x + halfWidth + gap, 246f, halfWidth, SecondaryButtonHeight), "Never", _buttonStyle!))
                    {
                        Plugin.NeverAsk.Value = true;
                        _phase = (int)Phase.Hidden;
                    }

                    break;

                case Phase.Updating:
                    DrawStrong(x, 68f, width, $"Updating launcher background ({_info.Image})...");
                    DrawLabel(x, 108f, width, _status);
                    DrawLabel(x, 148f, width, "The SPT launcher and the game are closed, then the update is applied");
                    DrawLabel(x, 174f, width, "automatically. Please wait, this can take a moment.");
                    break;

                case Phase.Success:
                    DrawStrong(x, 68f, width, _result);
                    DrawLabel(x, 108f, width, "Closing the game now - your launcher is patched right after.");
                    DrawLabel(x, 134f, width, "Start the launcher again to see the new background.");
                    DrawLabel(x, 160f, width, "(If the game does not close on its own, exit it manually.)");
                    break;

                case Phase.Failed:
                    DrawStrong(x, 68f, width, "The launcher could not be updated:");
                    var errorLines = WrapText(_error, 76, 3);
                    var errorY = 100f;
                    foreach (var line in errorLines)
                    {
                        DrawLabel(x, errorY, width, line);
                        errorY += 26f;
                    }

                    if (GUI.Button(new Rect(x, 186f, width, ButtonHeight), "Retry", _primaryButtonStyle!))
                    {
                        StartUpdate();
                    }

                    if (GUI.Button(new Rect(x, 246f, width, SecondaryButtonHeight), "Close", _buttonStyle!))
                    {
                        _phase = (int)Phase.Hidden;
                    }

                    break;
            }
        }
        finally
        {
            GUI.matrix = previousMatrix;
        }
    }

    private static void DrawLabel(float x, float y, float width, string text)
    {
        GUI.Label(new Rect(x, y, width, 24f), text, _messageStyle ?? GUI.skin.label);
    }

    /// <summary>Centered bright line - the "headline" of a status phase.</summary>
    private static void DrawStrong(float x, float y, float width, string text)
    {
        GUI.Label(new Rect(x, y, width, 24f), text, _strongStyle ?? GUI.skin.label);
    }

    /// <summary>Bold, slightly larger, with a highlight bar behind - for the window title.</summary>
    private static void DrawTitle(float y, string text)
    {
        DrawHighlighted(y, text, _titleStyle!);
    }

    /// <summary>Centered dark line of body text, used by the status phases.</summary>
    private static void DrawMessage(float y, string text)
    {
        GUI.Label(new Rect(Pad, y, WindowWidth - Pad * 2f, 24f), text, _messageStyle!);
    }

    /// <summary>Draws the text centered with a soft highlight bar behind it (bold styles read as highlighted).</summary>
    private static void DrawHighlighted(float y, string text, GUIStyle style)
    {
        var size = style.CalcSize(new GUIContent(text));
        var highlight = new Rect((WindowWidth - size.x) / 2f - 14f, y - 5f, size.x + 28f, size.y + 12f);
        GUI.Box(highlight, GUIContent.none, _highlightBarStyle!);
        GUI.Label(new Rect(0f, y, WindowWidth, size.y), text, style);
    }

    // ── Light grey panel styling (created once, on the first OnGUI call) ──────────────────────────

    private static Texture2D _panelTexture;
    private static Texture2D _highlightTexture;
    private static Texture2D _buttonNormalTexture;
    private static Texture2D _buttonHoverTexture;
    private static Texture2D _buttonActiveTexture;
    private static Texture2D _quietNormalTexture;
    private static Texture2D _quietHoverTexture;
    private static Texture2D _quietActiveTexture;
    private static GUIStyle _panelStyle;
    private static GUIStyle _highlightBarStyle;
    private static GUIStyle _titleStyle;
    private static GUIStyle _highlightStyle;
    private static GUIStyle _messageStyle;
    private static GUIStyle _strongStyle;
    private static GUIStyle _primaryButtonStyle;
    private static GUIStyle _buttonStyle;
    private static bool _stylesReady;

    private static void EnsureStyles()
    {
        if (_stylesReady)
        {
            return;
        }

        _stylesReady = true;

        // Palette from the reference design: dark card, white headings, grey copy, one blue action.
        var card = new Color(0.129f, 0.141f, 0.169f, 0.99f);
        var cardEdge = new Color(0.235f, 0.255f, 0.290f, 0.85f);
        var primary = new Color(0.208f, 0.471f, 0.949f, 1f);
        var primaryHover = new Color(0.294f, 0.549f, 0.973f, 1f);
        var primaryActive = new Color(0.157f, 0.392f, 0.855f, 1f);
        var quiet = new Color(0.098f, 0.110f, 0.129f, 1f);
        var quietHover = new Color(0.145f, 0.161f, 0.188f, 1f);
        var quietActive = new Color(0.075f, 0.086f, 0.102f, 1f);
        var quietEdge = new Color(0.200f, 0.216f, 0.243f, 1f);

        var heading = new Color(0.960f, 0.965f, 0.975f, 1f);
        var body = new Color(0.620f, 0.650f, 0.690f, 1f);

        _panelTexture = MakeRoundedTexture(48, 16, card, cardEdge, 1);
        _highlightTexture = MakeRoundedTexture(32, 10, new Color(primary.r, primary.g, primary.b, 0.20f), null, 0);
        _buttonNormalTexture = MakeRoundedTexture(32, 10, primary, null, 0);
        _buttonHoverTexture = MakeRoundedTexture(32, 10, primaryHover, null, 0);
        _buttonActiveTexture = MakeRoundedTexture(32, 10, primaryActive, null, 0);
        _quietNormalTexture = MakeRoundedTexture(32, 10, quiet, quietEdge, 1);
        _quietHoverTexture = MakeRoundedTexture(32, 10, quietHover, quietEdge, 1);
        _quietActiveTexture = MakeRoundedTexture(32, 10, quietActive, quietEdge, 1);

        var panel = new GUIStyle(GUI.skin.box)
        {
            border = new RectOffset(17, 17, 17, 17),
        };
        panel.normal.background = _panelTexture;
        _panelStyle = panel;

        var bar = new GUIStyle(GUI.skin.box)
        {
            border = new RectOffset(11, 11, 11, 11),
        };
        bar.normal.background = _highlightTexture;
        _highlightBarStyle = bar;

        var title = new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold,
            fontSize = 21,
            alignment = TextAnchor.MiddleCenter,
        };
        title.normal.textColor = heading;
        _titleStyle = title;

        var highlight = new GUIStyle(title) { fontSize = 16 };
        _highlightStyle = highlight;

        var message = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14,
            alignment = TextAnchor.MiddleCenter,
        };
        message.normal.textColor = body;
        _messageStyle = message;

        var strong = new GUIStyle(message) { fontSize = 15 };
        strong.normal.textColor = heading;
        _strongStyle = strong;

        var primaryButton = new GUIStyle(GUI.skin.button)
        {
            fontStyle = FontStyle.Bold,
            fontSize = 15,
            border = new RectOffset(11, 11, 11, 11),
        };
        primaryButton.normal.background = _buttonNormalTexture;
        primaryButton.normal.textColor = heading;
        primaryButton.hover.background = _buttonHoverTexture;
        primaryButton.hover.textColor = heading;
        primaryButton.active.background = _buttonActiveTexture;
        primaryButton.active.textColor = heading;
        _primaryButtonStyle = primaryButton;

        var quietButton = new GUIStyle(primaryButton)
        {
            fontStyle = FontStyle.Normal,
        };
        quietButton.normal.background = _quietNormalTexture;
        quietButton.normal.textColor = body;
        quietButton.hover.background = _quietHoverTexture;
        quietButton.hover.textColor = heading;
        quietButton.active.background = _quietActiveTexture;
        quietButton.active.textColor = heading;
        _buttonStyle = quietButton;
    }

    /// <summary>
    /// Builds a rounded rectangle texture (with an optional 1px edge) for the card, the highlight bars and
    /// the buttons; used as a 9-sliced background so rounded corners survive any size.
    /// </summary>
    private static Texture2D MakeRoundedTexture(int size, int radius, Color fill, Color? edge, int edgeWidth)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };

        var half = (size - 1) / 2f;
        var inner = half - radius;
        var pixels = new Color[size * size];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = Mathf.Max(Mathf.Abs(x - half) - inner, 0f);
                var dy = Mathf.Max(Mathf.Abs(y - half) - inner, 0f);
                var distance = Mathf.Sqrt((dx * dx) + (dy * dy)) - radius;

                // one pixel of antialiasing on the rounded outline
                var coverage = Mathf.Clamp01(0.5f - distance);
                if (coverage <= 0f)
                {
                    pixels[(y * size) + x] = new Color(0f, 0f, 0f, 0f);
                    continue;
                }

                var color = fill;
                if (edge.HasValue && edgeWidth > 0 && distance > -edgeWidth)
                {
                    color = Color.Lerp(fill, edge.Value, Mathf.Clamp01(-distance / edgeWidth));
                }

                color.a *= coverage;
                pixels[(y * size) + x] = color;
            }
        }

        texture.SetPixels(pixels);
        texture.Apply();
        return texture;
    }

    /// <summary>Very small word wrapper so long error messages stay readable inside the window.</summary>
    private static List<string> WrapText(string text, int maxChars, int maxLines)
    {
        var result = new List<string>();
        var rest = text ?? string.Empty;

        while (rest.Length > maxChars && result.Count < maxLines)
        {
            var cut = rest.LastIndexOf(' ', Math.Min(maxChars, rest.Length - 1));
            if (cut <= 0)
            {
                cut = maxChars;
            }

            result.Add(rest.Substring(0, cut).TrimEnd());
            rest = rest.Substring(cut).TrimStart();
        }

        if (result.Count < maxLines && rest.Length > 0)
        {
            result.Add(rest.Length > maxChars ? rest.Substring(0, maxChars - 3) + "..." : rest);
        }

        return result;
    }

    private void HandleDrag()
    {
        var current = Event.current;
        if (current == null)
        {
            return;
        }

        // Screen-space title bar (the window rect is in screen pixels).
        var titleBar = new Rect(_windowRect.x, _windowRect.y, _windowRect.width, 30f * Scale);

        if (current.type == EventType.MouseDown && titleBar.Contains(current.mousePosition))
        {
            _dragging = true;
            _dragOffset = current.mousePosition - new Vector2(_windowRect.x, _windowRect.y);
        }
        else if (current.type == EventType.MouseUp)
        {
            _dragging = false;
        }
        else if (_dragging && current.type == EventType.MouseDrag)
        {
            _windowRect.x = current.mousePosition.x - _dragOffset.x;
            _windowRect.y = current.mousePosition.y - _dragOffset.y;
        }
    }
}
