using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;

namespace CLB.Gui;

/// <summary>
/// In-game prompt that offers players the server's custom launcher background.
/// On request it downloads a ready-patched SPT.Launcher.exe from the connected SPT server and
/// installs it over the local launcher file (with a one-time backup).
///
/// Built for SPT 4.1.6 with BepInEx 5.x. Uses BepInEx 6.x's BasePlugin from extracted/refs
/// for compile-time reference (same API as BepInEx 5.x).
/// </summary>
[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "com.preytolive.customlauncherbackgrounds.gui";
    public const string Name = "Custom Launcher Backgrounds (in-game prompt)";
    public const string Version = "2.48.4";

    internal static ManualLogSource Log;

    internal static ConfigEntry<bool> NeverAsk;
    internal static ConfigEntry<bool> BorderlessLauncherWindow;
    internal static ConfigEntry<bool> LauncherUpdated;
    internal static ConfigEntry<string> LastAppliedImage;
    internal static ConfigEntry<string> LastAppliedBuild;

    public override void Load()
    {
        Log = base.Log;

        NeverAsk = Config.Bind(
            "Update",
            "NeverAsk",
            false,
            new ConfigDescription("When true the launcher update prompt is never shown (set by the 'Don't ask again' button). Set it back to false to be asked again.")
        );
        BorderlessLauncherWindow = Config.Bind(
            "Update",
            "BorderlessLauncherWindow",
            false,
            new ConfigDescription("When true the local SPT launcher window is made borderless (no native title bar, no window border) so the background reaches every edge. Move it with Win+Arrow or Alt+Space, close with Alt+F4.")
        );
        LauncherUpdated = Config.Bind(
            "Update",
            "LauncherUpdated",
            false,
            new ConfigDescription("Set automatically after a successful launcher update so the prompt does not appear on later launches. Set it back to false to be asked again.")
        );
        LastAppliedImage = Config.Bind(
            "Update",
            "LastAppliedImage",
            "",
            new ConfigDescription("Name of the server background image that was most recently applied by this plugin. Used to avoid asking again while the server keeps the same background.")
        );
        LastAppliedBuild = Config.Bind(
            "Update",
            "LastAppliedBuild",
            "",
            new ConfigDescription("Build id of the server launcher that was most recently applied. When the server rebuilds its launcher (mod update, SPT update, changed rotation settings) the prompt shows up again so the launcher can be refreshed.")
        );

        // Create a GameObject to host the PromptController MonoBehaviour
        var go = new GameObject("CLB_PromptController");
        go.AddComponent<PromptController>();
        Object.DontDestroyOnLoad(go);

        Log.LogInfo($"{Name} v{Version} loaded (SPT 4.1.6 / BepInEx 5.x)");
    }
}