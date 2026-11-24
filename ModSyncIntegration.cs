using System.Text;
using SPTarkov.Common.Models.Logging;

namespace PreyToLive.CustomLauncherBackgrounds;

/// <summary>
/// Corter-ModSync integration: registers the server's patched SPT.Launcher.exe as a served path so every
/// player receives the current background through the sync tool they already use.
///
/// Corter-ModSync treats <c>restartRequired</c> files as pending updates and applies them
/// <em>after the game exits</em> (ModSync.Updater.exe) or at the next game start (its prepatcher) -
/// which is exactly the "close the game, then patch the launcher" flow this mod wants,
/// without shipping any client-side code.
/// </summary>
internal static class ModSyncIntegration
{
    private const string LauncherEntryJsonPath = "../SPT_Runtime/SPT.Launcher.exe";
    private const string EntryName = "SPT Launcher (custom background)";

    /// <summary>
    /// Ensures the launcher sync entry exists in <c>user/mods/Corter-ModSync/config.jsonc</c>.
    /// Returns a human readable status, or null when Corter-ModSync is not installed on this server.
    /// </summary>
    public static string? EnsureLauncherEntry(string modDirectory, ISptLogger<BackgroundSelectionService> logger)
    {
        try
        {
            var configPath = Path.GetFullPath(Path.Combine(modDirectory, "..", "Corter-ModSync", "config.jsonc"));
            if (!File.Exists(configPath))
            {
                return null; // server does not use Corter-ModSync
            }

            var runtimeDirectory = Path.GetFullPath(Path.Combine(modDirectory, "..", "..", ".."));
            var launcherPath = Path.Combine(runtimeDirectory, "SPT.Launcher.exe");
            if (!File.Exists(launcherPath))
            {
                return "skipped (no local SPT.Launcher.exe to serve)";
            }

            var config = File.ReadAllText(configPath, Encoding.UTF8);
            if (config.Contains("SPT.Launcher.exe", StringComparison.OrdinalIgnoreCase))
            {
                return "active (entry already present)";
            }

            const string anchor = "\"syncPaths\": [";
            var anchorIndex = config.IndexOf(anchor, StringComparison.Ordinal);
            if (anchorIndex < 0)
            {
                return "failed (could not find the syncPaths list in Corter-ModSync's config)";
            }

            var newline = config.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

            // one-time backup so the host can always revert the edit
            var backupPath = configPath + ".clb-backup";
            if (!File.Exists(backupPath))
            {
                File.Copy(configPath, backupPath, overwrite: false);
            }

            var entry = string.Join(
                newline,
                string.Empty,
                "        // ── Added by PreyToLive-CustomLauncherBackgrounds ──",
                "        // Keeps the server's patched launcher (random background) in sync for every",
                "        // player. Players can untick it in the F12 menu, which restores their original",
                "        // launcher. Remove this entry and set modSyncIntegration to false in the mod's",
                "        // config.json to stop managing it.",
                "        {",
                $"            \"path\": \"{LauncherEntryJsonPath}\",",
                $"            \"name\": \"{EntryName}\",",
                "            \"enabled\": true,",
                "            \"optional\": true,",
                "            \"headless\": false",
                "        },");

            var insertAt = anchorIndex + anchor.Length;
            var updated = config[..insertAt] + entry + config[insertAt..];
            File.WriteAllText(configPath, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            logger.Info($"Mod: {BackgroundSelectionService.ModName}: Corter-ModSync integration: added the launcher sync entry to {configPath} (takes effect when Corter-ModSync next starts)");
            return "active (entry added - it takes effect when Corter-ModSync next starts)";
        }
        catch (Exception exception)
        {
            logger.Warning($"Mod: {BackgroundSelectionService.ModName}: Corter-ModSync integration failed: {exception.Message}");
            return "failed: " + exception.Message;
        }
    }
}
