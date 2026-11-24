namespace PreyToLive.CustomLauncherBackgrounds;

using SPTarkov.DI.Annotations;
using SPTarkov.Server.Web.Models.Configs;
using SPTarkov.Server.Web.Services;

/// <summary>
/// Registers the mod's config.json with the SIC web config editor, so it can be edited
/// in the browser (Configs page) instead of by hand.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class ConfigProvider(ModConfig config, BackgroundSelectionService service) : IConfigEditorConfigProvider
{
    public IEnumerable<ConfigEditorConfigRegistration> GetConfigs()
    {
        var metadata = new ModMetadata();

        var configPath = Path.Combine(service.ModDirectory, "config.json");
        var relativeConfigPath = Path.GetRelativePath(Directory.GetCurrentDirectory(), configPath);

        yield return ConfigEditorConfigRegistration.Create(metadata.ModGuid, metadata.Name, config, relativeConfigPath) with
        {
            OnAppliedToRuntimeAsync = OnConfigAppliedToRuntime,
        };
    }

    private ValueTask OnConfigAppliedToRuntime(object modifiedConfig, CancellationToken cancellationToken)
    {
        // The injected ModConfig singleton is updated in place by the config editor; just re-apply.
        service.Apply();
        return ValueTask.CompletedTask;
    }
}
