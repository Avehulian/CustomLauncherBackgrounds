namespace PreyToLive.CustomLauncherBackgrounds;

using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Routers;

/// <summary>
/// Picks a random image from the configured folder on server start and serves it at the
/// classic launcher background route (/files/launcher/bg) - the same mechanism the
/// original SPT 3.x version of this mod used.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class BackgroundSelectionService
{
    public const string ModName = "CustomLauncherBackgrounds";
    public const string LauncherBackgroundRoute = "/files/launcher/bg";
    public const string RemotePayloadRoute = "/files/launcher/bg-fitted";
    public const string RemoteLauncherRoute = "/files/launcher/clb-launcher";
    public const string RemoteInfoRoute = "/files/launcher/clb-info";

    // Download routes for the optional in-game prompt plugin / standalone patcher.
    // Files that the static middleware would not serve (unknown extensions) are exposed as .bin copies
    // through the file router so browsers download them instead of 404/rendering them.
    public const string DownloadPluginRoute = "/files/launcher/clb-plugin";
    public const string DownloadPatcherLinuxRoute = "/files/launcher/clb-patcher-linux";
    public const string DownloadPatcherCmdRoute = "/files/launcher/clb-patcher-cmd";

    private const string ModVersion = "2.48.4";

    private static readonly string[] SupportedExtensions = [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".mp4"];

    private static bool IsVideoFile(string path) => path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);

    /// <summary>Panel opacity below this value (i.e. panels cleared from the stock frosted look) tints the window border.</summary>
    private const int BorderTintPanelOpacityThreshold = 70;

    /// <summary>Text the launcher looks for when hiding the local server card.</summary>
    private const string HideLocalServerToken = "127.0.0.1";

    private static IEnumerable<string> GetFilesIfExists(string directory)
        => Directory.Exists(directory) ? Directory.GetFiles(directory) : [];

    private readonly ImageRouter _imageRouter;
    private readonly ISptLogger<BackgroundSelectionService> _logger;
    private readonly HttpConfig _httpConfig;
    private LauncherRotationServer? _rotationServer;
    private LauncherWindowWatcher? _windowWatcher;

    public BackgroundSelectionService(ImageRouter imageRouter, ISptLogger<BackgroundSelectionService> logger, ModConfig config, HttpConfig httpConfig)
    {
        _imageRouter = imageRouter;
        _logger = logger;
        _httpConfig = httpConfig;
        Config = config;
        ModDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
    }

    public ModConfig Config { get; }

    public string ModDirectory { get; }

    /// <summary>True when the mod is enabled in config.json.</summary>
    public bool Enabled => Config.ModEnabled;

    /// <summary>True when a random image has replaced the default launcher background route.</summary>
    public bool Active { get; private set; }

    /// <summary>The folder the current background was picked from ("res" or a subfolder name).</summary>
    public string ActiveFolder { get; private set; } = string.Empty;

    /// <summary>File name of the image currently served at /files/launcher/bg.</summary>
    public string? SelectedFileName { get; private set; }

    /// <summary>Number of usable images in the active folder.</summary>
    public int ImageCount { get; private set; }

    /// <summary>Changes every time a background is picked; used to defeat browser caching of the preview image.</summary>
    public string CacheToken { get; private set; } = "0";

    /// <summary>Status of the launcher background patch (shown on the SIC page).</summary>
    public string? LauncherPatchInfo { get; private set; }

    /// <summary>Status of the Corter-ModSync integration (shown on the SIC page).</summary>
    public string? ModSyncInfo { get; private set; }

    /// <summary>Status of the background rotation (shown on the SIC page).</summary>
    public string? RotationInfo { get; private set; }

    /// <summary>Status of the borderless launcher window watcher (shown on the SIC page).</summary>
    public string? LauncherWindowInfo { get; private set; }

    /// <summary>Human readable reason the mod is not active (shown on the SIC page).</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Picks a new random image and (re)points the launcher background route at it.
    /// Called on server load, and again whenever the config is changed in the web config editor.
    /// </summary>
    public void Apply()
    {
        Active = false;
        ActiveFolder = string.Empty;
        SelectedFileName = null;
        ImageCount = 0;
        LastError = null;
        LauncherPatchInfo = null;
        ModSyncInfo = null;

        StopRotation();
        StopWindowWatcher();

        if (!Config.ModEnabled)
        {
            RotationInfo = "Disabled (mod disabled).";
            _logger.Info($"Mod: {ModName}: Disabled via config.json");
            return;
        }

        // Close numbering gaps left by deleted or manually removed files (1-bg, 3-bg -> 1-bg, 2-bg) so the
        // uploads keep following the sequence and the slideshow stays in a tidy order.
        try
        {
            var renumbered = MediaLibrary.Renumber(ModDirectory);
            if (renumbered > 0)
            {
                _logger.Info($"Mod: {ModName}: renumbered {renumbered} media file(s) to close numbering gaps");
            }
        }
        catch (Exception exception)
        {
            _logger.Warning($"Mod: {ModName}: media renumbering failed: {exception.Message}");
        }

        // Default playlist (empty or "."): the res root plus the Images and Videos folders.
        // A name resolves to res/<name>, falling back to the legacy res/launchers/<name> layout.
        var folderName = (Config.LauncherFolderToUse ?? string.Empty).Trim();
        var useDefaultPlaylist = folderName.Length == 0 || folderName == ".";
        string launcherFolderPath;
        IEnumerable<string> candidates;

        if (useDefaultPlaylist)
        {
            folderName = "res (Images + Videos)";
            launcherFolderPath = Path.Combine(ModDirectory, "res");
            candidates = GetFilesIfExists(launcherFolderPath)
                .Concat(GetFilesIfExists(Path.Combine(launcherFolderPath, "Images")))
                .Concat(GetFilesIfExists(Path.Combine(launcherFolderPath, "Videos")));
        }
        else
        {
            var directPath = Path.Combine(ModDirectory, "res", folderName);
            launcherFolderPath = Directory.Exists(directPath)
                ? directPath
                : Path.Combine(ModDirectory, "res", "launchers", folderName);
            candidates = GetFilesIfExists(launcherFolderPath);
        }

        if (!Directory.Exists(launcherFolderPath))
        {
            LastError = $"Unable to find contents for \"launcherFolderToUse\": {launcherFolderPath}";
            _logger.Error($"Mod: {ModName}: {LastError}");
            return;
        }

        // Natural order so 1-bg.png, 2-bg.png ... 10-bg.png rotate in sequence (not alphabetically).
        var allFiles = candidates
            .Where(file => SupportedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(file => Path.GetFileName(file), Comparer<string>.Create(NaturalOrder.Compare))
            .ToArray();

        if (allFiles.Length == 0)
        {
            LastError = useDefaultPlaylist
                ? $"No images (.jpg, .jpeg, .png, .webp, .bmp) or videos (.mp4) found in: {launcherFolderPath} (looked in res, res/Images and res/Videos)"
                : $"No images (.jpg, .jpeg, .png, .webp, .bmp) or videos (.mp4) found in: {launcherFolderPath}";
            _logger.Error($"Mod: {ModName}: {LastError}");
            return;
        }

        var stillFiles = allFiles.Where(file => !IsVideoFile(file)).ToArray();
        var videoFiles = allFiles.Where(IsVideoFile).ToArray();

        // The embedded background, classic route and download payload are always still images; the toggles
        // only shape the rotation playlist (images-only / videos-only / alternating when both are on).
        string? chosenImage = stillFiles.Length > 0
            ? stillFiles[Random.Shared.Next(stillFiles.Length)]
            : null;

        if (chosenImage is not null)
        {
            _imageRouter.AddRoute(LauncherBackgroundRoute, chosenImage);
        }

        var playlist = BuildRotationPlaylist(stillFiles, videoFiles);

        Active = true;
        ActiveFolder = folderName;
        SelectedFileName = chosenImage is null ? null : Path.GetFileName(chosenImage);
        ImageCount = playlist.Length;
        CacheToken = DateTime.UtcNow.Ticks.ToString();

        _logger.Success($"Mod: {ModName}: Background chosen: {SelectedFileName} ({ImageCount} item(s) in the rotation playlist: {stillFiles.Length} image(s), {videoFiles.Length} video(s))");

        if (Config.ConsoleLogs && chosenImage is not null)
        {
            _logger.Info($"Mod: {ModName}: Console Logs");
            _logger.Info($"{LauncherBackgroundRoute} => {chosenImage}");
        }

        // Patch the local launcher directly (ModSync handles distributing to clients)
        if (Config.PatchLauncher)
        {
            LauncherPatchInfo = ApplyLauncherPatch(chosenImage);
        }
        else
        {
            LauncherPatchInfo = "Disabled via config.json (patchLauncher: false).";
        }

        // Save the patched launcher to wwwroot for download by friends
        try
        {
            string? sptRoot = null;
            var current = ModDirectory;
            for (int i = 0; i < 10; i++)
            {
                var sptRuntimeDir = Path.Combine(current, "SPT_Runtime");
                if (Directory.Exists(sptRuntimeDir))
                {
                    sptRoot = current;
                    break;
                }
                var parent = Directory.GetParent(current);
                if (parent == null || !parent.Exists)
                {
                    break;
                }
                current = parent.FullName;
            }
            if (sptRoot != null)
            {
                var runtimeDir = Path.Combine(sptRoot, "SPT_Runtime");
                SavePatchedLauncherForDownload(runtimeDir);
                SaveLauncherInfo(runtimeDir);
                StageForModSync(sptRoot);
                // Also copy to the configured output folder outside SPT_Runtime
                SavePatchedLauncherToOutputFolder(sptRoot);
            }
        }
        catch
        {
            // best effort — doesn't affect mod functionality
        }

        RegisterDownloads();
        ApplyModSyncIntegration();
        StartRotation(playlist, chosenImage);
        StartWindowWatcher();
    }

    /// <summary>
    /// Builds the rotation playlist from the enabled kinds. Both on -> strictly alternating image, video,
    /// image, video... (so a slideshow flips between artwork and clips); single kind -> only that kind;
    /// both off -> empty (rotation disabled).
    /// </summary>
    private string[] BuildRotationPlaylist(string[] stills, string[] videos)
    {
        var images = Config.UseImages ? stills : [];
        var clips = Config.UseVideos ? videos : [];

        if (images.Length > 0 && clips.Length > 0)
        {
            // Both kinds on: strictly alternate image, video, image, video... wrapping the shorter list,
            // so a slideshow always flips between artwork and a clip.
            var length = 2 * Math.Max(images.Length, clips.Length);
            var result = new string[length];

            for (var slot = 0; slot < length; slot++)
            {
                result[slot] = slot % 2 == 0
                    ? images[slot / 2 % images.Length]
                    : clips[slot / 2 % clips.Length];
            }

            return result;
        }

        return images.Length > 0 ? images : clips;
    }

    /// <summary>
    /// Makes the local launcher window borderless while the server runs (the host usually plays on the same
    /// machine); controlled by "launcherWindowBorderless". The border tint follows the Panel opacity setting:
    /// below <see cref="BorderTintPanelOpacityThreshold"/> (panels are being cleared) the window border is
    /// tinted in the launcher's light shade so it blends, while the stock look leaves the border alone. The
    /// in-game plugin applies the same tweak on players' machines.
    /// </summary>
    private void StartWindowWatcher()
    {
        var tintBorder = Config.LauncherPanelOpacityPercent < BorderTintPanelOpacityThreshold;

        if ((!Config.LauncherWindowBorderless && !tintBorder) || !OperatingSystem.IsWindows())
        {
            LauncherWindowInfo = !Config.LauncherWindowBorderless && !tintBorder
                ? $"Disabled (borderless off and panel opacity {Config.LauncherPanelOpacityPercent}% is at the stock look, so the border is left alone)."
                : "Not available on this platform.";
            return;
        }

        try
        {
            _windowWatcher = new LauncherWindowWatcher(Config.LauncherWindowBorderless, tintBorder, _logger);
            _windowWatcher.Start();
            LauncherWindowInfo = Config.LauncherWindowBorderless
                ? "Active - the launcher window is made borderless while the server runs."
                : $"Active - the launcher window keeps its title bar, with the border tinted in the launcher's light shade (panel opacity {Config.LauncherPanelOpacityPercent}%).";
        }
        catch (Exception exception)
        {
            StopWindowWatcher();
            LauncherWindowInfo = "Failed to start: " + exception.Message;
            _logger.Warning($"Mod: {ModName}: launcher window watcher failed to start: {exception.Message}");
        }
    }

    private void StopWindowWatcher()
    {
        try
        {
            _windowWatcher?.Dispose();
        }
        catch
        {
            // best effort
        }

        _windowWatcher = null;
    }

    /// <summary>
    /// Starts (or restarts) the plain-HTTP rotation server that the patched launcher polls for its
    /// rotating background. Configured with "rotationSeconds" (0 disables it).
    /// </summary>
    private int ImageIntervalSeconds() => Config.ImageRotationSeconds ?? Config.RotationSeconds;

    private int VideoIntervalSeconds() => Config.VideoRotationSeconds ?? Config.RotationSeconds;

    private void StartRotation(string[] playlist, string? preferredInitial)
    {
        var imageSeconds = ImageIntervalSeconds();
        var videoSeconds = VideoIntervalSeconds();

        if (imageSeconds <= 0 && videoSeconds <= 0)
        {
            RotationInfo = "Disabled via config.json (rotation seconds: 0).";
            return;
        }

        if (playlist.Length == 0)
        {
            RotationInfo = "Disabled - image and video playback are both off (useImages/useVideos).";
            return;
        }

        try
        {
            var port = Math.Clamp(Config.RotationHttpPort, 1, 65535);
            var bindAddresses = ResolveRotationBindAddresses();
            // Number order from the beginning, or the original random start point.
            var initial = Config.RandomRotationStart && preferredInitial is not null && playlist.Contains(preferredInitial)
                ? preferredInitial
                : playlist[0];

            _rotationServer = new LauncherRotationServer(
                playlist,
                initial,
                Math.Clamp(imageSeconds, 1, 86_400),
                Math.Clamp(videoSeconds, 1, 86_400),
                port,
                bindAddresses,
                Config.ConsoleLogs,
                InfoPanelBoxes(),
                Config.InfoPanelLink ?? string.Empty,
                _logger);
            _rotationServer.Start();

            var localUrl = RotationUrlLocal();
            var remoteUrl = RotationUrlRemote();
            var remoteNote = string.Equals(localUrl, remoteUrl, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : $" (other computers: {remoteUrl})";
            RotationInfo = $"Images every {Math.Clamp(imageSeconds, 1, 86_400)}s, videos every {Math.Clamp(videoSeconds, 1, 86_400)}s on {localUrl}{remoteNote} (currently {Path.GetFileName(initial)}).";
        }
        catch (Exception exception)
        {
            StopRotation();
            RotationInfo = "Failed to start: " + exception.Message;
            _logger.Warning($"Mod: {ModName}: background rotation server failed to start: {exception.Message}");
        }
    }

    private void StopRotation()
    {
        try
        {
            _rotationServer?.Dispose();
        }
        catch
        {
            // best effort
        }

        _rotationServer = null;
    }

    /// <summary>URL the local (same machine) launcher polls - always loopback.</summary>
    /// <summary>The configured Info boxes, falling back to the legacy single text for older configs.</summary>
    private List<string> InfoPanelBoxes()
    {
        if (Config.InfoPanelBoxes is { Count: > 0 })
        {
            return Config.InfoPanelBoxes;
        }

        return string.IsNullOrWhiteSpace(Config.InfoPanelText) ? new List<string>() : new List<string> { Config.InfoPanelText };
    }

    private string RotationUrlLocal()
    {
        return $"http://127.0.0.1:{Math.Clamp(Config.RotationHttpPort, 1, 65535)}";
    }

    /// <summary>
    /// Addresses the rotation server listens on:
    /// - rotationHttpIP set to a specific IP: binds ONLY to that IP (remote-only, no loopback)
    /// - rotationHttpIP = "0.0.0.0": binds to all interfaces (covers remote + loopback implicitly)
    /// - rotationHttpIP empty + rotationBindAll true: binds to all interfaces
    /// - rotationHttpIP empty + rotationBindAll false: binds to loopback only
    /// </summary>
    private IReadOnlyList<System.Net.IPAddress> ResolveRotationBindAddresses()
    {
        var configured = (Config.RotationHttpIP ?? string.Empty).Trim();

        if (configured.Length > 0)
        {
            if (System.Net.IPAddress.TryParse(configured, out var address))
            {
                if (address.Equals(System.Net.IPAddress.Any))
                {
                    return [System.Net.IPAddress.Any];
                }

                // Specific IP: bind ONLY to that IP (remote-only, no loopback)
                return [address];
            }

            _logger.Warning($"Mod: {ModName}: rotationHttpIP '{configured}' is not a valid IP address - using {(Config.RotationBindAll ? "all interfaces" : "loopback")}");
        }

        return Config.RotationBindAll ? [System.Net.IPAddress.Any] : [System.Net.IPAddress.Loopback];
    }

    /// <summary>
    /// URL baked into launchers downloaded by other players: uses rotationRemoteIp when set,
    /// otherwise rotationHttpIP (if a specific IP), otherwise auto-detects the server's remote IP.
    /// </summary>
    private string RotationUrlRemote()
    {
        // 1. Explicit remote IP override (for Tailscale, VPN, etc.)
        var remoteIp = (Config.RotationRemoteIp ?? string.Empty).Trim();
        if (remoteIp.Length > 0)
        {
            return $"http://{remoteIp}:{Math.Clamp(Config.RotationHttpPort, 1, 65535)}";
        }

        // 2. rotationHttpIP set to a specific IP
        var host = (Config.RotationHttpIP ?? string.Empty).Trim();
        if (host.Length > 0 && System.Net.IPAddress.TryParse(host, out var parsed) && !parsed.Equals(System.Net.IPAddress.Any))
        {
            return $"http://{host}:{Math.Clamp(Config.RotationHttpPort, 1, 65535)}";
        }

        // 3. Auto-detect the server's remote IP
        host = _httpConfig.BackendIp;
        if (string.IsNullOrWhiteSpace(host) || host == "0.0.0.0")
        {
            try
            {
                var hostEntry = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                foreach (var ip in hostEntry.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                        !ip.Equals(System.Net.IPAddress.Loopback) &&
                        !ip.Equals(System.Net.IPAddress.IPv6Loopback))
                    {
                        host = ip.ToString();
                        break;
                    }
                }
            }
            catch
            {
                // Fallback
            }

            if (string.IsNullOrWhiteSpace(host) || host == "0.0.0.0")
            {
                host = "127.0.0.1";
            }
        }

        return $"http://{host}:{Math.Clamp(Config.RotationHttpPort, 1, 65535)}";
    }

    /// <summary>Rotation interval in milliseconds for the patched launcher script; 0 when disabled.</summary>
    private int RotationIntervalMilliseconds()
    {
        var seconds = Math.Max(ImageIntervalSeconds(), VideoIntervalSeconds());
        return seconds <= 0 ? 0 : Math.Clamp(seconds, 1, 86_400) * 1000;
    }

    /// <summary>
    /// Registers the patched launcher with Corter-ModSync (when present) so players receive it through
    /// their normal sync; ModSync applies restartRequired files only after the game closes.
    /// </summary>
    private void ApplyModSyncIntegration()
    {
        if (!Config.ModSyncIntegration)
        {
            ModSyncInfo = "Disabled via config.json (modSyncIntegration: false).";
            _logger.Info($"Mod: {ModName}: {ModSyncInfo}");
            return;
        }

        var result = ModSyncIntegration.EnsureLauncherEntry(ModDirectory, _logger);
        if (result == null)
        {
            ModSyncInfo = "Not installed — Corter-ModSync not found on this server.";
        }
        else
        {
            ModSyncInfo = result;
        }
        _logger.Info($"Mod: {ModName}: {ModSyncInfo}");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  Web editor support (media management + settings)
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Media files in res/Images and res/Videos (for the web editor).</summary>
    internal IReadOnlyList<MediaFileInfo> ListMedia() => MediaLibrary.List(ModDirectory);

    /// <summary>Deletes one media file and re-applies the mod so playlist/rotation pick it up.</summary>
    internal string? DeleteMedia(string fileName, MediaKind kind)
    {
        var deleted = MediaLibrary.Delete(ModDirectory, fileName, kind);
        if (deleted is not null)
        {
            _logger.Info($"Mod: {ModName}: media deleted: {deleted}");
            Apply();
        }

        return deleted;
    }

    /// <summary>Deletes several files (any mix of kinds) and re-applies once.</summary>
    internal IReadOnlyList<string> DeleteMedia(IReadOnlyList<(string FileName, MediaKind Kind)> files)
    {
        var deleted = new List<string>();

        foreach (var kind in new[] { MediaKind.Image, MediaKind.Video })
        {
            var names = files.Where(file => file.Kind == kind).Select(file => file.FileName).ToArray();
            if (names.Length == 0)
            {
                continue;
            }

            deleted.AddRange(MediaLibrary.DeleteMany(ModDirectory, names, kind));
        }

        if (deleted.Count > 0)
        {
            _logger.Info($"Mod: {ModName}: media deleted: {string.Join(", ", deleted)}");
            Apply();
        }

        return deleted;
    }

    /// <summary>
    /// Formats and imports uploaded files as numbered backgrounds, then re-applies the mod.
    /// Returns a human readable result per upload.
    /// </summary>
    internal IReadOnlyList<string> ImportMedia(IReadOnlyList<(string FileName, byte[] Content)> uploads, MediaFormatOptions formatOptions)
    {
        var results = new List<string>();

        foreach (var (fileName, content) in uploads)
        {
            try
            {
                if (MediaLibrary.IsImage(fileName))
                {
                    results.Add(ImportImage(fileName, content, formatOptions));
                }
                else if (MediaLibrary.IsVideo(fileName))
                {
                    results.Add(ImportVideo(fileName, content, formatOptions, Config.VideoFfmpegPath));
                }
                else
                {
                    results.Add($"{fileName}: unsupported file type (images: {string.Join(", ", MediaLibrary.ImageExtensions)}; videos: .mp4)");
                }
            }
            catch (Exception exception)
            {
                results.Add($"{fileName}: import failed - {exception.Message}");
            }
        }

        Apply();
        return results;
    }

    /// <summary>
    /// File based import for uploads: video clips can be gigabytes, so they are streamed to disk (never held
    /// in memory). Each temp file is consumed and deleted; the web editor streams the upload straight into
    /// these files.
    /// </summary>
    internal IReadOnlyList<string> ImportMediaFiles(IReadOnlyList<(string FileName, string TempPath)> uploads, MediaFormatOptions formatOptions)
    {
        var results = new List<string>();

        foreach (var (fileName, tempPath) in uploads)
        {
            try
            {
                if (MediaLibrary.IsVideo(fileName))
                {
                    results.Add(ImportVideoFile(fileName, tempPath, formatOptions, Config.VideoFfmpegPath));
                }
                else
                {
                    results.Add($"{fileName}: unsupported file type (videos: .mp4)");
                }
            }
            catch (Exception exception)
            {
                results.Add($"{fileName}: import failed - {exception.Message}");
            }
            finally
            {
                DeleteTempFile(tempPath);
            }
        }

        Apply();
        return results;
    }

    private string ImportVideoFile(string fileName, string tempPath, MediaFormatOptions formatOptions, string? ffmpegPath)
    {
        if (!MediaFormatter.LooksLikeMp4File(tempPath))
        {
            throw new InvalidOperationException("does not look like an mp4 file (only .mp4 is supported)");
        }

        var contentPath = tempPath;
        string? formattedPath = null;
        string? formatNote = null;

        if (formatOptions.TargetWidth > 0 && formatOptions.TargetHeight > 0)
        {
            formattedPath = tempPath + ".formatted.mp4";
            var (outputPath, note) = MediaFormatter.FormatVideoFile(tempPath, formattedPath, formatOptions, MediaFormatter.FindFfmpegExecutable(ffmpegPath, ModDirectory));
            formatNote = note;
            if (outputPath is not null)
            {
                contentPath = outputPath;
            }
        }

        try
        {
            var duplicate = MediaLibrary.FindDuplicateFromFile(ModDirectory, contentPath, MediaKind.Video);
            if (duplicate is not null)
            {
                _logger.Info($"Mod: {ModName}: skipped duplicate video {fileName} (identical to {duplicate})");
                return $"{fileName}: identical to {duplicate} - not added (no duplicate copy)";
            }

            var previous = MediaLibrary.FindStoredForOriginal(ModDirectory, fileName);
            if (previous is not null && MediaLibrary.ReplaceFromFile(ModDirectory, previous, contentPath) is not null)
            {
                _logger.Info($"Mod: {ModName}: replaced {previous} with the new version of {fileName} (hash refreshed)");
                return $"{fileName} -> replaced {previous} (updated version, same number)";
            }

            var stored = MediaLibrary.ImportFromFile(ModDirectory, contentPath, MediaKind.Video, ".mp4", fileName);
            var sizeMb = new FileInfo(contentPath).Length / (1024.0 * 1024.0);
            var hint = sizeMb > 50 ? " - note: large videos are re-fetched on every rotation, consider a smaller clip" : string.Empty;
            var noteText = formatNote is null ? string.Empty : $" - {formatNote}";
            _logger.Info($"Mod: {ModName}: media imported: {stored} (from {fileName}, {sizeMb:F1} MB){noteText}");
            return $"{fileName} -> {stored} ({sizeMb:F1} MB){noteText}{hint}";
        }
        finally
        {
            if (formattedPath is not null)
            {
                DeleteTempFile(formattedPath);
            }
        }
    }

    private static void DeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort
        }
    }

    private string ImportImage(string fileName, byte[] content, MediaFormatOptions formatOptions)
    {
        var sourceExtension = Path.GetExtension(fileName);
        (byte[] Content, string Extension) formatted;

        try
        {
            formatted = MediaFormatter.FormatImage(content, formatOptions with { SourceExtension = sourceExtension });
        }
        catch (Exception exception)
        {
            // Formatting unavailable (non-Windows host) or the file is not a readable image: store it
            // as-is when the format is one the launcher can display.
            if (!MediaLibrary.ImageExtensions.Contains(sourceExtension, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("not a readable image and the format is not supported as-is (" + exception.Message + ")");
            }

            var normalised = sourceExtension.ToLowerInvariant() switch
            {
                ".jpeg" => ".jpg",
                var extension => extension,
            };
            formatted = (content, normalised);
        }

        var duplicate = MediaLibrary.FindDuplicate(ModDirectory, formatted.Content, MediaKind.Image);
        if (duplicate is not null)
        {
            _logger.Info($"Mod: {ModName}: skipped duplicate image {fileName} (identical to {duplicate})");
            return $"{fileName}: identical to {duplicate} - not added (no duplicate copy)";
        }

        // Same source file name as an earlier upload? Replace that stored copy in place (new version).
        var previous = MediaLibrary.FindStoredForOriginal(ModDirectory, fileName);
        if (previous is not null && MediaLibrary.Replace(ModDirectory, previous, formatted.Content) is not null)
        {
            _logger.Info($"Mod: {ModName}: replaced {previous} with the new version of {fileName} (hash refreshed)");
            return $"{fileName} -> replaced {previous} (updated version, same number)";
        }

        var stored = MediaLibrary.Import(ModDirectory, formatted.Content, MediaKind.Image, formatted.Extension, fileName);
        var detected = MediaFormatter.DetectImageFormat(content);
        var formatNote = detected is not null
            && !string.Equals(detected, sourceExtension, StringComparison.OrdinalIgnoreCase)
            && !(detected == ".jpg" && sourceExtension == ".jpeg")
            ? $" - file is really {detected.TrimStart('.')} (name said {sourceExtension.TrimStart('.')})"
            : string.Empty;
        _logger.Info($"Mod: {ModName}: media imported: {stored} (from {fileName}, {formatted.Content.Length / 1024} KB{formatNote})");
        return $"{fileName} -> {stored} ({formatted.Content.Length / 1024} KB){formatNote}";
    }

    private string ImportVideo(string fileName, byte[] content, MediaFormatOptions formatOptions, string? ffmpegPath)
    {
        if (!MediaFormatter.LooksLikeMp4(content))
        {
            throw new InvalidOperationException("does not look like an mp4 file (only .mp4 is supported)");
        }

        var (bytes, note) = MediaFormatter.FormatVideo(content, formatOptions, MediaFormatter.FindFfmpegExecutable(ffmpegPath, ModDirectory));

        var duplicate = MediaLibrary.FindDuplicate(ModDirectory, bytes, MediaKind.Video);
        if (duplicate is not null)
        {
            _logger.Info($"Mod: {ModName}: skipped duplicate video {fileName} (identical to {duplicate})");
            return $"{fileName}: identical to {duplicate} - not added (no duplicate copy)";
        }

        // Same source file name as an earlier upload? Replace that stored copy in place (new version).
        var previous = MediaLibrary.FindStoredForOriginal(ModDirectory, fileName);
        if (previous is not null && MediaLibrary.Replace(ModDirectory, previous, bytes) is not null)
        {
            _logger.Info($"Mod: {ModName}: replaced {previous} with the new version of {fileName} (hash refreshed)");
            return $"{fileName} -> replaced {previous} (updated version, same number)";
        }

        var stored = MediaLibrary.Import(ModDirectory, bytes, MediaKind.Video, ".mp4", fileName);
        var sizeMb = bytes.Length / (1024.0 * 1024.0);
        var hint = sizeMb > 50 ? " - note: large videos are re-fetched on every rotation, consider a smaller clip" : string.Empty;
        var noteText = note is null ? string.Empty : $" - {note}";
        _logger.Info($"Mod: {ModName}: media imported: {stored} (from {fileName}, {sizeMb:F1} MB){noteText}");
        return $"{fileName} -> {stored} ({sizeMb:F1} MB){noteText}{hint}";
    }

    /// <summary>Applies settings from the web editor: updates the shared config, persists it and re-applies.</summary>
    public async Task SaveSettingsAsync(ModConfig updated, CancellationToken cancellationToken = default)
    {
        Config.ModEnabled = updated.ModEnabled;
        Config.ConsoleLogs = updated.ConsoleLogs;
        Config.LauncherFolderToUse = updated.LauncherFolderToUse ?? string.Empty;
        Config.PatchLauncher = updated.PatchLauncher;

        Config.RotationSeconds = updated.RotationSeconds;
        Config.RotationHttpPort = updated.RotationHttpPort;
        Config.RotationHttpIP = updated.RotationHttpIP ?? string.Empty;
        Config.ReplaceInfoPanel = updated.ReplaceInfoPanel;
        Config.InfoPanelText = updated.InfoPanelText ?? string.Empty;
        Config.InfoPanelLink = updated.InfoPanelLink ?? string.Empty;
        Config.InfoPanelBoxes = updated.InfoPanelBoxes ?? new List<string>();
        Config.InfoPanelText = Config.InfoPanelBoxes.FirstOrDefault() ?? string.Empty;
        Config.RotationBindAll = updated.RotationBindAll;
        Config.UseImages = updated.UseImages;
        Config.UseVideos = updated.UseVideos;
        Config.VideoFfmpegPath = updated.VideoFfmpegPath ?? string.Empty;
        Config.RandomRotationStart = updated.RandomRotationStart;
        Config.ImageRotationSeconds = updated.ImageRotationSeconds;
        Config.VideoRotationSeconds = updated.VideoRotationSeconds;
        Config.HideLocalServerCard = updated.HideLocalServerCard;
        Config.LauncherBackgroundVeilPercent = updated.LauncherBackgroundVeilPercent;
        Config.LauncherWindowBorderless = updated.LauncherWindowBorderless;
        Config.LauncherOutputFolder = updated.LauncherOutputFolder ?? string.Empty;
        Config.RotationRemoteIp = updated.RotationRemoteIp ?? string.Empty;
        Config.LauncherPanelOpacityPercent = updated.LauncherPanelOpacityPercent;

        // An explicit save/apply is the server owner asking players to refresh their launcher, so the
        // published build id advances even when the resulting exe stays byte-identical.
        BumpLauncherRevision();

        await ConfigRegistration.SaveConfigAsync(Config, cancellationToken);
        Apply();
    }

    /// <summary>Re-applies the mod (used by the web editor's apply button).</summary>
    public void Reapply()
    {
        // The web editor's apply button: treat it like a save, so the in-game update prompt appears again
        // for players whose launcher predates this apply.
        BumpLauncherRevision();
        Apply();
    }

    /// <summary>
    /// Advances the published launcher revision (cache/launcher-revision.txt) and returns the new value.
    /// It is part of the build id the in-game prompt compares against, so every explicit apply in the web
    /// editor makes clients see a newer build - while a plain server restart keeps the same value (no prompt
    /// spam on restarts). Failures only degrade to the previous/zero revision.
    /// </summary>
    private int BumpLauncherRevision()
    {
        try
        {
            var path = Path.Combine(ModDirectory, "cache", "launcher-revision.txt");
            var next = ReadLauncherRevision() + 1;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ModDirectory);
            File.WriteAllText(path, next.ToString());
            _logger.Info($"Mod: {ModName}: launcher revision is now {next} (players will be prompted to update)");
            return next;
        }
        catch (Exception exception)
        {
            _logger.Warning($"Mod: {ModName}: could not bump the launcher revision: {exception.Message}");
            return ReadLauncherRevision();
        }
    }

    private int ReadLauncherRevision()
    {
        try
        {
            var path = Path.Combine(ModDirectory, "cache", "launcher-revision.txt");
            return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var value) ? value : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Exposes the bundled pre-patched launcher for download. Friends can download this file and
    /// replace their SPT.Launcher.exe directly — no client plugin or ModSync needed.
    /// </summary>
    private void RegisterDownloads()
    {
        try
        {
            // Serve the pre-patched launcher for download
            var patchedLauncherPath = Path.Combine("wwwroot", "LauncherPatcher", "win", "SPT.Launcher.exe");
            var patchedLauncher = Path.Combine(ModDirectory, patchedLauncherPath);
            if (File.Exists(patchedLauncher))
            {
                AddDownload(
                    RemoteLauncherRoute,
                    patchedLauncherPath,
                    "SPT.Launcher.exe");
            }
            else
            {
                _logger.Info($"Mod: {ModName}: no pre-patched launcher found at {patchedLauncherPath} — the launcher will be patched on first startup and served afterwards.");
            }
        }
        catch (Exception exception)
        {
            _logger.Warning($"Mod: {ModName}: registering download routes failed: {exception.Message}");
        }
    }

    private void AddDownload(string route, string relativeSource, string fileName)
    {
        var source = Path.Combine(ModDirectory, relativeSource);
        if (!File.Exists(source))
        {
            return;
        }

        var cacheDirectory = Path.Combine(ModDirectory, "cache");
        Directory.CreateDirectory(cacheDirectory);
        var binCopy = Path.Combine(cacheDirectory, fileName + ".bin");
        File.Copy(source, binCopy, overwrite: true);
        _imageRouter.AddRoute(route, binCopy);
    }

    /// <summary>
    /// Writes the chosen image into the launcher executables so the launcher displays it directly
    /// (the 4.1+/5.0 launcher has its background baked in and no longer downloads it from the server).
    /// </summary>
    private string ApplyLauncherPatch(string? staticImagePath)
    {
        if (!Config.PatchLauncher)
        {
            return "Disabled via config.json (patchLauncher: false).";
        }

        try
        {
            // Find the SPT root by walking up from the mod directory (same as ModSync integration)
            string? sptRoot = null;
            var current = ModDirectory;
            for (int i = 0; i < 10; i++)
            {
                var sptRuntimeDir = Path.Combine(current, "SPT_Runtime");
                if (Directory.Exists(sptRuntimeDir))
                {
                    sptRoot = current;
                    break;
                }
                var parent = Directory.GetParent(current);
                if (parent == null || !parent.Exists)
                {
                    break;
                }
                current = parent.FullName;
            }

            if (sptRoot == null)
            {
                return "No SPT_Runtime folder found - skipping launcher patch.";
            }

            var runtimeDir = Path.Combine(sptRoot, "SPT_Runtime");
            LauncherPatcher.SetLogger(_logger);
            var results = new List<LauncherPatcher.PatchResult>();
            var rotationUrl = Config.RotationSeconds > 0 ? RotationUrlRemote() : null;
            results = LauncherPatcher.ApplyToRuntime(runtimeDir, staticImagePath, rotationUrl, RotationIntervalMilliseconds(), Config.LauncherPanelOpacityPercent, Config.HideLocalServerCard ? HideLocalServerToken : null, Config.LauncherBackgroundVeilPercent, Config.ReplaceInfoPanel).ToList();

            if (results.Count == 0)
            {
                return "No launcher executable found in SPT_Runtime - skipping.";
            }

            foreach (var result in results.Where(result => result.Applied))
            {
                _logger.Success($"Mod: {ModName}: launcher background applied: {result.FileName} - {result.Detail}");
            }

            foreach (var result in results.Where(result => !result.Applied))
            {
                _logger.Warning($"Mod: {ModName}: launcher background not applied: {result.FileName} - {result.Detail}");
            }

            return string.Join(" ", results.Select(result => $"{result.FileName}: {(result.Applied ? "applied" : "skipped")} - {result.Detail}"));
        }
        catch (Exception exception)
        {
            _logger.Error($"Mod: {ModName}: launcher background update failed: {exception.Message}");
            return $"Failed: {exception.Message}";
        }
    }

    /// <summary>
    /// Saves the patched launcher to wwwroot/LauncherPatcher/win/SPT.Launcher.exe so it can be
    /// downloaded by friends who want to replace their launcher manually.
    /// </summary>
    private void SavePatchedLauncherForDownload(string runtimeDirectory)
    {
        var launcherPath = Path.Combine(runtimeDirectory, "SPT.Launcher.exe");
        if (!File.Exists(launcherPath))
        {
            return;
        }

        var wwwrootLauncherDir = Path.Combine(ModDirectory, "wwwroot", "LauncherPatcher", "win");
        Directory.CreateDirectory(wwwrootLauncherDir);
        var wwwrootLauncherPath = Path.Combine(wwwrootLauncherDir, "SPT.Launcher.exe");

        try
        {
            File.Copy(launcherPath, wwwrootLauncherPath, overwrite: true);
            _logger.Info($"Mod: {ModName}: saved patched launcher to {wwwrootLauncherPath} for download");
        }
        catch (Exception ex)
        {
            _logger.Warning($"Mod: {ModName}: failed to save patched launcher for download: {ex.Message}");
        }
    }

    /// <summary>
    /// Generates the launcher info JSON and saves it to cache/launcher-info.json so it can be
    /// served at /files/launcher/clb-info for the in-game prompt plugin.
    /// </summary>
    private void SaveLauncherInfo(string runtimeDirectory)
    {
        var launcherPath = Path.Combine(runtimeDirectory, "SPT.Launcher.exe");
        if (!File.Exists(launcherPath))
        {
            return;
        }

        try
        {
            var launcherBytes = File.ReadAllBytes(launcherPath);
            var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(launcherBytes)).ToLowerInvariant();
            var revision = ReadLauncherRevision();
            var buildId = $"{ModVersion}-{revision}";

            var info = new
            {
                image = SelectedFileName ?? "",
                fileName = "SPT.Launcher.exe",
                size = launcherBytes.LongLength,
                sha256 = sha256,
                url = "/files/launcher/clb-launcher",
                modVersion = ModVersion,
                build = buildId
            };

            var cacheDir = Path.Combine(ModDirectory, "cache");
            Directory.CreateDirectory(cacheDir);
            var infoPath = Path.Combine(cacheDir, "launcher-info.json");
            var json = System.Text.Json.JsonSerializer.Serialize(info, new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(infoPath, json);

            // Register the route if not already registered
            _imageRouter.AddRoute(RemoteInfoRoute, infoPath);

            _logger.Info($"Mod: {ModName}: launcher info saved (build={buildId}, sha256={sha256[..16]}...)");
        }
        catch (Exception ex)
        {
            _logger.Warning($"Mod: {ModName}: failed to save launcher info: {ex.Message}");
        }
    }

    /// <summary>
    /// Copies the patched launcher to a folder outside SPT_Runtime (configured via launcherOutputFolder).
    /// This makes the patched launcher easy to find for friends to download.
    /// </summary>
    private void SavePatchedLauncherToOutputFolder(string sptRoot)
    {
        var folderName = (Config.LauncherOutputFolder ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(folderName))
        {
            return;
        }

        var outputDir = Path.Combine(sptRoot, folderName);
        Directory.CreateDirectory(outputDir);
        var outputLauncherPath = Path.Combine(outputDir, "SPT.Launcher.exe");

        // Copy from SPT_Runtime where the patcher wrote it
        var runtimeDir = Path.Combine(sptRoot, "SPT_Runtime");
        var patchedLauncherPath = Path.Combine(runtimeDir, "SPT.Launcher.exe");
        if (!File.Exists(patchedLauncherPath))
        {
            _logger.Info($"Mod: {ModName}: no patched launcher at {patchedLauncherPath} — skipping output folder copy");
            return;
        }

        try
        {
            File.Copy(patchedLauncherPath, outputLauncherPath, overwrite: true);
            _logger.Info($"Mod: {ModName}: copied patched launcher to {outputLauncherPath}");
        }
        catch (Exception ex)
        {
            _logger.Warning($"Mod: {ModName}: failed to copy patched launcher to output folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Stages the patched launcher for Corter-ModSync. When present, the client plugin will
    /// stage the patched launcher in ModSync_Data/PendingUpdates/SPT_Runtime/ so ModSync.Updater.exe
    /// applies it after the game exits.
    /// </summary>
    private void StageForModSync(string sptRoot)
    {
        if (!Config.ModSyncIntegration)
        {
            return;
        }

        var updaterPath = Path.Combine(sptRoot, "ModSync.Updater.exe");
        if (!File.Exists(updaterPath))
        {
            return; // ModSync not installed
        }

        var runtimeDir = Path.Combine(sptRoot, "SPT_Runtime");
        var patchedLauncherPath = Path.Combine(runtimeDir, "SPT.Launcher.exe");
        if (!File.Exists(patchedLauncherPath))
        {
            return;
        }

        try
        {
            var pendingDir = Path.Combine(sptRoot, "ModSync_Data", "PendingUpdates", "SPT_Runtime");
            Directory.CreateDirectory(pendingDir);
            var stagedPath = Path.Combine(pendingDir, "SPT.Launcher.exe");

            File.Copy(patchedLauncherPath, stagedPath, overwrite: true);
            _logger.Info($"Mod: {ModName}: staged patched launcher for ModSync at {stagedPath}");

            // Also ensure a backup exists so unticking the sync entry restores the original
            var backupPath = patchedLauncherPath + ".modsync-bak";
            if (!File.Exists(backupPath))
            {
                File.Copy(patchedLauncherPath, backupPath, overwrite: false);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Mod: {ModName}: failed to stage launcher for ModSync: {ex.Message}");
        }
    }
}
