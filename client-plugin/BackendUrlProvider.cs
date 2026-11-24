using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace CLB.Gui;

/// <summary>Reads the backend URL from the launch arguments and locates the SPT folder.</summary>
internal static class BackendUrlProvider
{
    // The SPT launcher appends "-config={'BackendUrl':'https://host:port','Version':'live',...}".
    // The JSON can arrive as one argument, split into two ("-config=" + "{...}") or with double
    // quotes instead of single ones - and the key itself is quoted - so quotes around the key and
    // around the value are optional here. (The old pattern only matched an *unquoted* key, which is
    // why the resolved server silently fell back to 127.0.0.1 and the prompt never appeared.)
    private const string BackendUrlPattern = @"['""]?BackendUrl['""]?\s*[:=]\s*['""]?([^'""\s,}]+)";

    // Used when the launch arguments cannot be read (or name nothing reachable): the usual SPT hosts.
    private static readonly string[] FallbackOrigins =
    {
        "https://127.0.0.1:6969",
        "https://localhost:6969",
    };

    /// <summary>
    /// Server origins to try, in order: every BackendUrl found on the command line (the raw line
    /// first - which also covers values that ended up in a separate argument - then each argument),
    /// followed by the usual local addresses. The update flow uses the first origin that answers.
    /// </summary>
    public static List<string> GetCandidates()
    {
        var candidates = new List<string>();

        ParseCandidates(Environment.CommandLine, candidates);

        foreach (var argument in Environment.GetCommandLineArgs())
        {
            ParseCandidates(argument, candidates);
        }

        foreach (var fallback in FallbackOrigins)
        {
            AddCandidate(candidates, fallback);
        }

        return candidates;
    }

    /// <summary>The first candidate (kept for callers that only want a single address).</summary>
    public static string GetBackendUrl()
    {
        var candidates = GetCandidates();
        return candidates.Count > 0 ? candidates[0] : FallbackOrigins[0];
    }

    /// <summary>Extracts every BackendUrl value from <paramref name="text"/> and adds it to the list.</summary>
    internal static void ParseCandidates(string text, List<string> candidates)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        foreach (Match match in Regex.Matches(text, BackendUrlPattern, RegexOptions.IgnoreCase))
        {
            AddCandidate(candidates, match.Groups[1].Value);
        }
    }

    private static void AddCandidate(List<string> candidates, string value)
    {
        var normalized = Normalize(value);
        if (normalized.Length == 0)
        {
            return;
        }

        foreach (var existing in candidates)
        {
            if (string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        candidates.Add(normalized);
    }

    public static string GetGameRoot()
    {
        try
        {
            var path = BepInEx.Paths.GameRootPath;
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                return path;
            }
        }
        catch
        {
            // fall through to the assembly based search
        }

        // Fallback: walk up from this plugin's location (…/<root>/BepInEx/plugins/<plugin>/…).
        var directory = Path.GetDirectoryName(typeof(BackendUrlProvider).Assembly.Location);
        for (var i = 0; i < 5 && !string.IsNullOrEmpty(directory); i++)
        {
            if (Directory.Exists(Path.Combine(directory, "BepInEx")))
            {
                return directory;
            }

            if (string.Equals(Path.GetFileName(directory), "BepInEx", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetDirectoryName(directory) ?? directory;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return AppDomain.CurrentDomain.BaseDirectory;
    }

    private static string Normalize(string server)
    {
        server = (server ?? string.Empty).Trim();
        if (server.Length == 0)
        {
            return string.Empty;
        }

        if (!server.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !server.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            server = "https://" + server;
        }

        return server.TrimEnd('/');
    }
}
