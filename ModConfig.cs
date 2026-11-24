namespace PreyToLive.CustomLauncherBackgrounds;

/// <summary>
/// User-editable settings, persisted to config.json next to the mod assembly.
/// Field names match the original SPT 3.x version of this mod, so existing settings carry over.
/// </summary>
public class ModConfig
{
    /// <summary>Enables/disables the mod. When false the default SPT launcher background is served.</summary>
    public bool ModEnabled { get; set; } = true;

    /// <summary>When true, extra diagnostics are written to the server console.</summary>
    public bool ConsoleLogs { get; set; }

    /// <summary>
    /// Folder to play backgrounds from. Empty (the default) plays <c>res/Images</c> and <c>res/Videos</c>
    /// (plus files directly in <c>res</c>); a name uses <c>res/&lt;name&gt;</c> (older installs may keep
    /// using <c>res/launchers/&lt;name&gt;</c>).
    /// </summary>
    public string LauncherFolderToUse { get; set; } = string.Empty;

    /// <summary>
    /// Writes the chosen background directly into the launcher (SPT.Launcher.exe / SPT.Launcher.Linux)
    /// so the launcher itself displays it. A one-time backup (&lt;file&gt;.original) is kept next to the launcher.
    /// </summary>
    public bool PatchLauncher { get; set; } = true;

    /// <summary>
    /// How often the launcher background rotates, in seconds. Legacy value: it is used for BOTH kinds when
    /// the per-kind values below are absent from config.json, and 0 still disables rotation entirely.
    /// </summary>
    public int RotationSeconds { get; set; } = 60;

    /// <summary>
    /// Seconds an image stays visible before the playlist advances (its own interval). Null = use
    /// <see cref="RotationSeconds"/> (configs written before this option existed keep their behaviour).
    /// </summary>
    public int? ImageRotationSeconds { get; set; }

    /// <summary>
    /// Seconds a video stays visible before the playlist advances (its own interval). Null = use
    /// <see cref="RotationSeconds"/>.
    /// </summary>
    public int? VideoRotationSeconds { get; set; }

    /// <summary>Local HTTP port the rotation server listens on (clients poll http://127.0.0.1:&lt;port&gt;/bg.jpg).</summary>
    public int RotationHttpPort { get; set; } = 6970;

    /// <summary>
    /// When true the rotation server binds all interfaces (needed so launchers on other computers can
    /// reach it - remote launchers get the backend ip baked in). Default: loopback only. Ignored when
    /// <see cref="RotationHttpIP"/> names an address.
    /// </summary>
    public bool RotationBindAll { get; set; }

    /// <summary>
    /// Optional IP address the rotation server listens on (e.g. your LAN/public address, or "0.0.0.0"
    /// for all interfaces). Loopback is bound as well so the local launcher keeps working, and launchers
    /// downloaded by other players poll this address. Empty = automatic (loopback, or all interfaces with
    /// rotationBindAll).
    /// </summary>
    public string RotationHttpIP { get; set; } = string.Empty;

    /// <summary>
    /// Whether the rotation playlist includes images. With useVideos on too, the slideshow strictly
    /// alternates image -> video -> image...; false drops images from the rotation.
    /// </summary>
    public bool UseImages { get; set; } = true;

    /// <summary>
    /// Whether the rotation playlist includes videos. With useImages on too, the slideshow strictly
    /// alternates image -> video -> image...; false drops videos from the rotation.
    /// </summary>
    public bool UseVideos { get; set; } = true;

    /// <summary>
    /// Optional full path to an ffmpeg executable used to re-format uploaded videos (scale/crop to a
    /// chosen size, H.264 mp4). Empty = auto-detect on the PATH (or C:\ffmpeg).
    /// </summary>
    public string VideoFfmpegPath { get; set; } = string.Empty;

    /// <summary>
    /// When true the rotation starts on a random item each server start (original behaviour). When false it
    /// always starts on the lowest-numbered file (1-bg...), so the slideshow plays in number order from the
    /// beginning. Within a run the playlist is always in numeric order.
    /// </summary>
    public bool RandomRotationStart { get; set; } = true;

    /// <summary>
    /// When true, the launcher hides its "Local Server" card (any server card whose address contains
    /// 127.0.0.1). Baked into the launcher when it is (re-)patched, so close the launcher and save.
    /// </summary>
    public bool HideLocalServerCard { get; set; }

    /// <summary>
    /// Replaces the launcher's Info page: its built-in link cards are hidden (via the stylesheet) and
    /// <see cref="InfoPanelText"/> is shown instead, fetched live from the rotation server's /info endpoint.
    /// </summary>
    public bool ReplaceInfoPanel { get; set; }

    /// <summary>Text shown on the launcher's Info page when <see cref="ReplaceInfoPanel"/> is on. Line breaks are kept.</summary>
    public string InfoPanelText { get; set; } = string.Empty;

    /// <summary>
    /// Text boxes shown on the launcher's Info page when <see cref="ReplaceInfoPanel"/> is on: one launcher
    /// card per entry, in order. Every box opens <see cref="InfoPanelLink"/> when clicked. Kept in sync with
    /// <see cref="InfoPanelText"/> (its first entry) for older configs.
    /// </summary>
    public List<string> InfoPanelBoxes { get; set; } = new();

    /// <summary>
    /// URL opened when the Info text is clicked. Empty makes the text inert (the launcher's stock
    /// sp-mod.com link on that card is suppressed either way).
    /// </summary>
    public string InfoPanelLink { get; set; } = string.Empty;

    /// <summary>
    /// Strength of the launcher's veil over the background image/video, in percent: 100 = the launcher's
    /// stock dark veil, 0 = the artwork shows fully clear, values in between scale the tint (60% veil -&gt;
    /// about half the stock tint). The side bars/panels are unaffected. Baked in when the launcher is
    /// (re-)patched.
    /// </summary>
    public int LauncherBackgroundVeilPercent { get; set; } = 100;

    /// <summary>
    /// When true the local SPT launcher window is made borderless (no native title bar, no window border)
    /// so the background artwork reaches every edge. Move it with Win+Arrow or Alt+Space, close with Alt+F4.
    /// Off by default - the window keeps its normal title bar.
    /// </summary>
    public bool LauncherWindowBorderless { get; set; }

    /// <summary>
    /// Opacity of the launcher's frosted panels (cards, dialogs, tabs, popovers) in percent: 0 = fully
    /// transparent artwork, 100 = solid. The launcher's stock value is 70; applied when the launcher is
    /// (re-)patched.
    /// </summary>
    public int LauncherPanelOpacityPercent { get; set; } = 45;

    /// <summary>
    /// Folder name (relative to the SPT root, outside SPT_Runtime) where the patched launcher is
    /// generated. Only regenerated when the launcher build ID changes. Default: "PreyToLive".
    /// Set to empty to disable launcher patching entirely.
    /// </summary>
    public string LauncherOutputFolder { get; set; } = "PreyToLive";

    /// <summary>
    /// Explicit rotation URL host for remote launchers. When set, the patched launcher connects to
    /// this IP (e.g., your Tailscale IP like 100.127.48.41). When empty, the mod auto-detects the
    /// server's remote IP. Does NOT affect what addresses the rotation server binds to — that's
    /// controlled by rotationHttpIP.
    /// </summary>
    public string RotationRemoteIp { get; set; } = string.Empty;

    /// <summary>
    /// When true, integrates with Corter-ModSync to automatically distribute the patched launcher
    /// to all players. When false, players must download the launcher manually from the mod page.
    /// </summary>
    public bool ModSyncIntegration { get; set; } = true;
}
