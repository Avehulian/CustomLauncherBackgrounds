namespace PreyToLive.CustomLauncherBackgrounds;

using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Web;

/// <summary>
/// Mod metadata read by the SPT 5.0 (SPTushonka) mod loader.
/// Implementing <see cref="IModBlazorMetadata"/> also registers this mod's web page and
/// puts a card for it on the SIC landing page / the launcher's Mod Pages panel.
/// </summary>
public record ModMetadata : IModMetadata, IModBlazorMetadata
{
    public string ModGuid { get; init; } = "com.preytolive.customlauncherbackgrounds";
    public string Name { get; init; } = "Custom Launcher Backgrounds";
    public string Author { get; init; } = "PreyToLive";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("2.48.4");
    public SemanticVersioning.Range SptVersion { get; init; } = new("4.1.6");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; } = "https://github.com/NEWBR33D/CustomLauncherBackgrounds";
    public bool HasPrepatcher { get; init; }
    public string License { get; init; } = "MIT";

    public string? WWWRootUrl { get; init; }
    public string? HomePage { get; init; } = "/custom-launcher-backgrounds";
    public string? HomePageDescription { get; init; } = "Random launcher background, remote patcher and in-game launcher update prompt for players";
}
