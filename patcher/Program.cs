using System.Net.Http;
using System.Text.Json;
using PreyToLive.CustomLauncherBackgrounds;

namespace CLB.LauncherPatcher;

/// <summary>
/// Standalone tool for players whose launcher runs on a different computer than the SPT server
/// (remote servers). It downloads the ready-to-apply background payload from the server and patches
/// the local launcher files (SPT.Launcher.exe / SPT.Launcher.Linux) exactly like the server mod does
/// on a local install.
/// </summary>
internal static class Program
{
    private const string Version = "2.48.4";

    private static int Main(string[] args)
    {
        // Post-exit applier mode used by the in-game prompt when Corter-ModSync is not present:
        //   --apply-staged <launcherPath> <stagedFile> [--wait-pid <pid>]
        if (args.Contains("--apply-staged", StringComparer.OrdinalIgnoreCase))
        {
            return ApplyStaged(args);
        }

        Console.WriteLine($"Custom Launcher Backgrounds - launcher patcher v{Version}");
        Console.WriteLine();

        try
        {
            var serverArg = GetOption(args, "--server");
            var pathArg = GetOption(args, "--path");

            var runtimeDirectory = ResolveRuntimeDirectory(pathArg);
            Console.WriteLine($"Launcher folder : {runtimeDirectory}");

            var server = NormalizeServer(serverArg)
                ?? ResolveServerFromSettings(runtimeDirectory)
                ?? "https://127.0.0.1:6969";
            Console.WriteLine($"Server          : {server}");

            using var http = CreateHttpClient();
            var (payload, imageName) = DownloadPayload(http, server);
            Console.WriteLine($"Background      : {imageName} ({payload.Length} bytes)");
            Console.WriteLine();

            var applied = 0;
            foreach (var fileName in new[] { "SPT.Launcher.exe", "SPT.Launcher.Linux" })
            {
                var launcherPath = Path.Combine(runtimeDirectory, fileName);
                if (!File.Exists(launcherPath))
                {
                    continue;
                }

                try
                {
                    var located = LauncherPayload.LoadAndLocate(launcherPath);
                    if (payload.Length > located.Region.DataLength)
                    {
                        Console.WriteLine(
                            $"  {fileName}: skipped - the server background ({payload.Length} bytes) does not fit this launcher " +
                            $"({located.Region.DataLength} bytes); probably a different SPT version than the server");
                        continue;
                    }

                    var padded = LauncherPayload.PadToExactSize(payload, located.Region.DataLength, LauncherPayload.Marker + imageName);
                    var detail = LauncherPayload.SwapInPlace(launcherPath, located, padded);
                    Console.WriteLine($"  {fileName}: {detail}");
                    applied++;
                }
                catch (LauncherPayload.LauncherPatchException ex)
                {
                    Console.WriteLine($"  {fileName}: skipped - {ex.Message}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.WriteLine($"  {fileName}: FAILED - {ex.Message}");
                    Console.WriteLine("      (is the launcher running? close it and try again)");
                }
            }

            Console.WriteLine();
            if (applied == 0)
            {
                Console.WriteLine("Nothing was patched.");
                return 1;
            }

            Console.WriteLine("Done - start SPT.Launcher.exe to see the new background.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Waits for the game process to exit and applies a previously staged launcher
    /// (used by the in-game prompt so the launcher is only ever written while the game is closed).
    /// </summary>
    private static int ApplyStaged(string[] args)
    {
        var flagIndex = Array.FindIndex(args, a => string.Equals(a, "--apply-staged", StringComparison.OrdinalIgnoreCase));
        if (flagIndex < 0 || flagIndex + 2 >= args.Length)
        {
            Console.WriteLine("usage: --apply-staged <launcherPath> <stagedFile> [--wait-pid <pid>]");
            return 2;
        }

        var launcherPath = Path.GetFullPath(args[flagIndex + 1]);
        var stagedPath = Path.GetFullPath(args[flagIndex + 2]);
        var pidText = GetOption(args, "--wait-pid");
        var logPath = Path.Combine(Path.GetDirectoryName(launcherPath) ?? ".", "clb-launcher-update.log");

        void Log(string message)
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            Console.WriteLine(line);
            try
            {
                File.AppendAllText(logPath, line + Environment.NewLine);
            }
            catch
            {
                // logging is best effort
            }
        }

        Log($"Apply-staged start: launcher='{launcherPath}' staged='{stagedPath}' waitPid='{pidText}'");

        if (!File.Exists(launcherPath))
        {
            Log("ERROR: the launcher file does not exist - nothing to patch.");
            return 1;
        }

        if (!File.Exists(stagedPath))
        {
            Log("ERROR: the staged update file does not exist.");
            return 1;
        }

        try
        {
            if (pidText is not null && int.TryParse(pidText, out var pid))
            {
                Log($"Waiting for process {pid} to exit...");
                var deadline = DateTime.UtcNow.AddHours(2);
                while (DateTime.UtcNow < deadline)
                {
                    var running = false;
                    try
                    {
                        using var process = System.Diagnostics.Process.GetProcessById(pid);
                        running = !process.HasExited;
                    }
                    catch
                    {
                        running = false;
                    }

                    if (!running)
                    {
                        break;
                    }

                    Thread.Sleep(1000);
                }

                Log("Game process exited - applying the update.");
            }

            // The SPT launcher usually stays open (in the tray) while the game runs and locks its own
            // executable file - close it before swapping the launcher.
            var closedLaunchers = LauncherProcessCloser.CloseSptLauncher();
            if (closedLaunchers.Count > 0)
            {
                Log("Closed running SPT launcher: " + string.Join(", ", closedLaunchers));
            }

            var attempts = 0;
            while (true)
            {
                attempts++;
                try
                {
                    var located = LauncherPayload.LoadAndLocate(launcherPath);
                    var staged = LauncherPayload.LoadAndLocate(stagedPath);

                    var payload = new byte[staged.Region.DataLength];
                    Array.Copy(staged.FileBytes, staged.DataOffset, payload, 0, payload.Length);

                    if (payload.Length != located.Region.DataLength)
                    {
                        Log($"ERROR: staged slot ({payload.Length} bytes) does not match the installed launcher slot ({located.Region.DataLength} bytes). Keeping the staged file.");
                        return 1;
                    }

                    var detail = LauncherPayload.SwapInPlace(launcherPath, located, payload);

                    // Carry over the rotation script slots (navFade.js / app.js) when both files have
                    // them and the slot sizes match.
                    foreach (var stagedSlot in staged.Scripts)
                    {
                        var localSlot = located.FindScript(stagedSlot.Name);
                        if (localSlot is null || localSlot.DataLength != stagedSlot.DataLength)
                        {
                            continue;
                        }

                        try
                        {
                            var payloadBytes = new byte[stagedSlot.DataLength];
                            Array.Copy(staged.FileBytes, stagedSlot.DataOffset, payloadBytes, 0, payloadBytes.Length);
                            detail += "; " + LauncherPayload.SwapScriptInPlace(launcherPath, located, localSlot, payloadBytes);
                        }
                        catch (Exception scriptException)
                        {
                            detail += $"; {stagedSlot.Name} skipped: {scriptException.Message}";
                        }
                    }

                    File.Delete(stagedPath);
                    Log($"SUCCESS: launcher updated - {detail}");
                    return 0;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempts < 30)
                {
                    if (attempts == 1)
                    {
                        Log($"Launcher is locked ({exception.Message}) - retrying for a minute...");
                    }
                    else if (attempts == 3)
                    {
                        var again = LauncherProcessCloser.CloseSptLauncher();
                        if (again.Count > 0)
                        {
                            Log("Closed SPT launcher again: " + string.Join(", ", again));
                        }
                    }

                    Thread.Sleep(2000);
                }
                catch (Exception exception)
                {
                    Log($"ERROR: {exception.GetType().Name}: {exception.Message} (staged file kept at '{stagedPath}')");
                    return 1;
                }
            }
        }
        catch (Exception exception)
        {
            Log("ERROR: " + exception);
            return 1;
        }
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var i = 1; i < args.Length; i++)
        {
            if (!string.Equals(args[i - 1], name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i < args.Length && !args[i].StartsWith("--", StringComparison.Ordinal))
            {
                return args[i];
            }
        }

        return null;
    }

    private static string ResolveRuntimeDirectory(string? pathArg)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(pathArg))
        {
            candidates.Add(Path.GetFullPath(pathArg));
        }

        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        candidates.Add(baseDir);
        candidates.Add(Path.Combine(baseDir, "SPT_Runtime"));

        var parent = Directory.GetParent(baseDir)?.FullName;
        if (parent is not null)
        {
            candidates.Add(parent);
            candidates.Add(Path.Combine(parent, "SPT_Runtime"));
        }

        var current = Directory.GetCurrentDirectory();
        candidates.Add(current);
        candidates.Add(Path.Combine(current, "SPT_Runtime"));

        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "SPT.Launcher.exe")) || File.Exists(Path.Combine(candidate, "SPT.Launcher.Linux")))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "Could not find SPT.Launcher next to this tool - put the tool into your SPT_Runtime folder or pass --path <folder>");
    }

    private static string? NormalizeServer(string? server)
    {
        if (string.IsNullOrWhiteSpace(server))
        {
            return null;
        }

        server = server.Trim();
        if (!server.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !server.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            server = "https://" + server;
        }

        return server.TrimEnd('/');
    }

    private static string? ResolveServerFromSettings(string runtimeDirectory)
    {
        var settingsPath = Path.Combine(runtimeDirectory, "user", "Launcher", "LauncherSettings.json");
        if (!File.Exists(settingsPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (!document.RootElement.TryGetProperty("Servers", out var servers) || servers.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var server in servers.EnumerateArray())
            {
                if (server.TryGetProperty("IpAddress", out var address) && address.GetString() is { Length: > 0 } value)
                {
                    Console.WriteLine($"Using server from LauncherSettings.json: {value}");
                    return NormalizeServer(value);
                }
            }
        }
        catch
        {
            // fall back to the default below
        }

        return null;
    }

    private static (byte[] Bytes, string Name) DownloadPayload(HttpClient http, string server)
    {
        // The server exposes a ready-to-apply payload padded to the launcher slot size. Fall back to the
        // plain image for servers that do not provide it (older mod version or patchLauncher disabled).
        foreach (var route in new[] { "/files/launcher/bg-fitted", "/files/launcher/bg" })
        {
            try
            {
                using var response = http.GetAsync(server + route).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                if (bytes.Length < 3 || bytes[0] != 0xFF || bytes[1] != 0xD8 || bytes[2] != 0xFF)
                {
                    continue;
                }

                var name = LauncherPayload.GetMarkerName(bytes) ?? "server background";
                return (bytes, name);
            }
            catch (HttpRequestException)
            {
                // try the next route, report below when nothing worked
            }
        }

        throw new InvalidOperationException(
            $"Could not download a background from {server} - is the server running and is a launcher background configured?");
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            // SPT servers use self-signed certificates
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }
}
