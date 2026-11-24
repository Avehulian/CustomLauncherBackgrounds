using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CLB.Gui;

/// <summary>
/// Pure (.NET only, no Unity) logic for downloading a patched SPT.Launcher.exe from the SPT server
/// and installing it over the local launcher file. Shared between the in-game plugin and the test harness.
/// </summary>
public static class LauncherExeUpdater
{
    public const string Marker = "CLB-BG-PATCH:";

    public sealed class LauncherUpdateInfo
    {
        public string Image { get; }
        public string FileName { get; }
        public long Size { get; }
        public string Sha256 { get; }
        public string Url { get; }
        public string ModVersion { get; }
        public string Build { get; }

        public LauncherUpdateInfo(string image, string fileName, long size, string sha256, string url, string modVersion, string build)
        {
            Image = image;
            FileName = fileName;
            Size = size;
            Sha256 = sha256;
            Url = url;
            ModVersion = modVersion;
            Build = build;
        }
    }

    public static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            // SPT servers use self-signed certificates
            ServerCertificateCustomValidationCallback = (_, __, ___, ____) => true,
        };

        return new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(3) };
    }

    /// <summary>Fetches the update information the server publishes for the in-game prompt.</summary>
    public static LauncherUpdateInfo FetchInfo(HttpClient http, string serverOrigin)
    {
        return FetchInfo(http, serverOrigin, out _);
    }

    /// <summary>
    /// Fetches the update information the server publishes for the in-game prompt.
    /// Returns null and sets <paramref name="error"/> when the server cannot be asked.
    /// </summary>
    public static LauncherUpdateInfo FetchInfo(HttpClient http, string serverOrigin, out string error)
    {
        error = string.Empty;
        try
        {
            var json = http.GetStringAsync(serverOrigin.TrimEnd('/') + "/files/launcher/clb-info").GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            string GetString(string name) => root.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;

            var info = new LauncherUpdateInfo(
                GetString("image"),
                GetString("fileName"),
                root.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                GetString("sha256"),
                GetString("url"),
                GetString("modVersion"),
                GetString("build"));

            if (string.IsNullOrEmpty(info.Image) || string.IsNullOrEmpty(info.Url) || info.Size <= 0 || info.Sha256.Length != 64)
            {
                error = "the launcher info is incomplete";
                return null;
            }

            return info;
        }
        catch (Exception exception)
        {
            error = exception.GetBaseException().Message;
            return null;
        }
    }

    /// <summary>Finds SPT.Launcher.exe under the game root (checks the common layouts).</summary>
    public static string FindLauncherFile(string gameRootPath)
    {
        var candidates = new List<string>
        {
            Path.Combine(gameRootPath, "SPT_Runtime", "SPT.Launcher.exe"),
            Path.Combine(gameRootPath, "SPT.Launcher.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        try
        {
            foreach (var directory in Directory.GetDirectories(gameRootPath))
            {
                var candidate = Path.Combine(directory, "SPT.Launcher.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // ignore and report below
        }

        return null;
    }

    /// <summary>
    /// Downloads the patched launcher and installs it over <paramref name="launcherPath"/>
    /// (one-time .original backup, checksum validation, temp-file swap, verify after write).
    /// Used for direct updates (test harness / tools); the in-game flow stages the file instead so
    /// the game can close first.
    /// </summary>
    public static string Update(string serverOrigin, LauncherUpdateInfo info, string launcherPath, Action<string> progress)
    {
        var bytes = DownloadPatchedLauncher(serverOrigin, info, progress);

        progress("Backing up current launcher...");
        var backupPath = launcherPath + ".original";
        var currentIsPatched = ContainsMarker(File.ReadAllBytes(launcherPath));
        if (!File.Exists(backupPath) || !currentIsPatched)
        {
            File.Copy(launcherPath, backupPath, overwrite: true);
        }

        progress("Writing launcher...");
        var tempPath = launcherPath + ".clb-new";
        try
        {
            File.WriteAllBytes(tempPath, bytes);
            File.Delete(launcherPath);
            File.Move(tempPath, launcherPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // best effort
                }
            }
        }

        progress("Verifying...");
        var verify = File.ReadAllBytes(launcherPath);
        if (verify.LongLength != bytes.LongLength
            || !ContainsMarker(verify)
            || !string.Equals(ToHex(SHA256.Create().ComputeHash(verify)), ToHex(SHA256.Create().ComputeHash(bytes)), StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("verification after writing failed");
        }

        return $"applied {info.Image}";
    }

    /// <summary>Downloads the patched launcher and validates it (MZ, size, sha256, patch marker).</summary>
    public static byte[] DownloadPatchedLauncher(string serverOrigin, LauncherUpdateInfo info, Action<string> progress)
    {
        var url = serverOrigin.TrimEnd('/') + info.Url;

        progress("Downloading launcher...");
        byte[] bytes;
        using (var http = CreateHttpClient())
        using (var response = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
        {
            response.EnsureSuccessStatusCode();
            using var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            using var memory = new MemoryStream(info.Size > 0 && info.Size < int.MaxValue ? (int)info.Size : 16 * 1024 * 1024);
            var buffer = new byte[128 * 1024];
            long total = 0;
            int read;
            var lastPercent = -1;

            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                memory.Write(buffer, 0, read);
                total += read;
                if (info.Size > 0)
                {
                    var percent = (int)(total * 100 / info.Size);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress($"Downloading launcher... {percent}%");
                    }
                }
            }

            bytes = memory.ToArray();
        }

        progress("Validating download...");
        if (bytes.Length < 1024 * 1024 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
        {
            throw new InvalidOperationException("the downloaded file is not a Windows executable");
        }

        if (info.Size > 0 && bytes.LongLength != info.Size)
        {
            throw new InvalidOperationException($"download size mismatch (expected {info.Size} bytes, got {bytes.LongLength})");
        }

        var sha256 = ToHex(SHA256.Create().ComputeHash(bytes));
        if (!string.Equals(sha256, info.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("download checksum mismatch - try again");
        }

        if (!ContainsMarker(bytes))
        {
            throw new InvalidOperationException("the downloaded launcher does not carry the background patch");
        }

        return bytes;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    //  "Close the game, then patch the launcher" - staging helpers
    //  The in-game flow never overwrites the launcher while the game runs. It stages the patched file
    //  and lets an external applier do the swap after the game has exited:
    //   - Corter-ModSync present  -> ModSync_Data/PendingUpdates (applied by ModSync.Updater.exe
    //     after the game exits, or by ModSync's prepatcher at the next game start)
    //   - no Corter-ModSync       -> <launcher>.clb-pending (applied by our patcher helper
    //     CustomLauncherBackgrounds.Patcher.exe --apply-staged, spawned with --wait-pid)
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>True when Corter-ModSync's updater launcher is present next to the game.</summary>
    public static bool HasModSyncUpdater(string gameRoot)
    {
        return File.Exists(Path.Combine(gameRoot, "ModSync.Updater.exe"));
    }

    /// <summary>
    /// Makes sure a ModSync backup of the launcher exists, so unticking the sync entry (or the server
    /// retiring it) restores a working launcher instead of deleting the file.
    /// </summary>
    public static void EnsureModSyncBackup(string launcherPath)
    {
        var backupPath = launcherPath + ".modsync-bak";
        if (!File.Exists(backupPath) && File.Exists(launcherPath))
        {
            File.Copy(launcherPath, backupPath, overwrite: false);
        }
    }

    /// <summary>
    /// Stages the patched launcher for Corter-ModSync. The path layout matches ModSync's wire paths
    /// ("SPT_Runtime/..."), because ModSync.Updater moves PendingUpdates/* onto the game folder.
    /// </summary>
    public static string StageForModSync(string gameRoot, byte[] patchedBytes, string launcherFileName = "SPT.Launcher.exe")
    {
        var pendingDirectory = Path.Combine(gameRoot, "ModSync_Data", "PendingUpdates", "SPT_Runtime");
        Directory.CreateDirectory(pendingDirectory);
        var stagedPath = Path.Combine(pendingDirectory, launcherFileName);
        File.WriteAllBytes(stagedPath, patchedBytes);
        return stagedPath;
    }

    /// <summary>Stages the patched launcher next to the real file for our patcher helper (--apply-staged).</summary>
    public static string StageForHelper(string launcherPath, byte[] patchedBytes)
    {
        var stagedPath = launcherPath + ".clb-pending";
        File.WriteAllBytes(stagedPath, patchedBytes);
        return stagedPath;
    }

    /// <summary>Downloads the standalone patcher (used as the post-exit applier when ModSync is absent).</summary>
    public static string DownloadHelperTool(string serverOrigin, string targetPath)
    {
        var url = serverOrigin.TrimEnd('/') + "/PreyToLive-CustomLauncherBackgrounds/LauncherPatcher/win/CustomLauncherBackgrounds.Patcher.exe";

        using var http = CreateHttpClient();
        var bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        if (bytes.Length < 64 * 1024 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
        {
            throw new InvalidOperationException("the patcher helper download is invalid");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
        File.WriteAllBytes(targetPath, bytes);
        return targetPath;
    }

    /// <summary>Checks whether a launcher file (or downloaded payload) carries the background patch marker.</summary>
    public static bool ContainsMarker(byte[] data)
    {
        return data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(Marker)) >= 0;
    }

    /// <summary>Converts a byte array to a lowercase hex string (for .NET Framework 4.8 compatibility).</summary>
    private static string ToHex(byte[] data)
    {
        return BitConverter.ToString(data).Replace("-", "").ToLowerInvariant();
    }
}
