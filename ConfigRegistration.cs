namespace PreyToLive.CustomLauncherBackgrounds;

using System.Reflection;
using System.Text.Json;
using SPTarkov.Server.Core.DI;

/// <summary>
/// Loads config.json from the mod folder during DI construction and registers it as a
/// singleton so every class in the mod (and the web config editor) shares one instance.
/// </summary>
public class ConfigRegistration : IOnDIConstruct
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static async Task OnDIConstructAsync(IServiceCollection serviceCollection, CancellationToken cancellationToken)
    {
        var config = await LoadConfigFromDiskAsync(cancellationToken);
        serviceCollection.AddSingleton(config);
    }

    /// <summary>Persists the shared settings to config.json (used by the mod's own web editor).</summary>
    public static Task SaveConfigAsync(ModConfig config, CancellationToken cancellationToken = default)
        => SaveConfigToDiskAsync(config, GetConfigPath(), cancellationToken);

    public static string GetConfigPath()
    {
        var modDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
        return Path.Combine(modDirectory, "config.json");
    }

    private static async Task<ModConfig> LoadConfigFromDiskAsync(CancellationToken cancellationToken)
    {
        var configPath = GetConfigPath();

        if (!File.Exists(configPath))
        {
            var defaultConfig = new ModConfig();
            await SaveConfigToDiskAsync(defaultConfig, configPath, cancellationToken);
            return defaultConfig;
        }

        await using var stream = File.OpenRead(configPath);
        return await JsonSerializer.DeserializeAsync<ModConfig>(stream, ReadOptions, cancellationToken) ?? new ModConfig();
    }

    private static async Task SaveConfigToDiskAsync(ModConfig config, string path, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, config, WriteOptions, cancellationToken);
    }
}
