using CLB.Gui;

// Usage: LauncherUpdaterTest <serverOrigin> <gameRoot>
//        LauncherUpdaterTest decide            (self-test of the prompt decision rules)
//        LauncherUpdaterTest backend           (self-test of the launch-argument URL parsing)
// Runs the same fetch/validate/replace code that the in-game plugin uses.

if (args.Length > 0 && args[0] == "decide")
{
    return RunDeciderTests();
}

if (args.Length > 0 && args[0] == "backend")
{
    return RunBackendUrlTests();
}

if (args.Length < 2)
{
    Console.WriteLine("usage: LauncherUpdaterTest <serverOrigin> <gameRoot> | decide");
    return 2;
}

var serverOrigin = args[0];
var gameRoot = args[1];

Console.WriteLine($"server : {serverOrigin}");
Console.WriteLine($"gameRoot: {gameRoot}");

using var http = LauncherExeUpdater.CreateHttpClient();
var info = LauncherExeUpdater.FetchInfo(http, serverOrigin);

if (info is null)
{
    Console.WriteLine("RESULT: server does not provide launcher update info");
    return 1;
}

Console.WriteLine($"info   : image={info.Image} size={info.Size} sha256={info.Sha256[..16]}... url={info.Url} mod={info.ModVersion}");

var launcherPath = LauncherExeUpdater.FindLauncherFile(gameRoot);
Console.WriteLine($"launcher: {launcherPath}");

if (launcherPath is null)
{
    Console.WriteLine("RESULT: launcher not found");
    return 1;
}

try
{
    var result = LauncherExeUpdater.Update(serverOrigin, info, launcherPath, text => Console.WriteLine("  " + text));
    Console.WriteLine($"RESULT: {result}");

    var bytes = File.ReadAllBytes(launcherPath);
    var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    Console.WriteLine($"verify : size={bytes.LongLength} sha256={sha[..16]}... matchesInfo={sha == info.Sha256} marker={LauncherExeUpdater.ContainsMarker(bytes)}");
    Console.WriteLine($"backup : {File.Exists(launcherPath + ".original")}");
    return 0;
}
catch (Exception exception)
{
    Console.WriteLine($"FAILED: {exception.GetType().Name}: {exception.Message}");
    return 1;
}

// ─────────────────────────────────────────────────────────────────────────────────────────────────
//  Prompt decision self-test - mirrors the in-game flow's checks.
// ─────────────────────────────────────────────────────────────────────────────────────────────────
static int RunDeciderTests()
{
    var failures = 0;

    void Check(string name, bool expected, bool actual)
    {
        var ok = expected == actual;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: {name}");
        if (!ok)
        {
            failures++;
        }
    }

    LauncherExeUpdater.LauncherUpdateInfo Make(string build, string image)
        => new(image, "SPT.Launcher.exe", 100, new string('a', 64), "/files/launcher/clb-launcher", "2.5.1", build);

    // "Don't ask again" always wins.
    Check("neverAsk + updated + same build -> quiet", false, PromptDecider.ShouldPrompt(true, true, "b1", "x", Make("b1", "x")));
    Check("neverAsk + fresh -> quiet", false, PromptDecider.ShouldPrompt(true, false, "", "", Make("b1", "x")));

    // Fresh player is prompted.
    Check("fresh player -> prompt", true, PromptDecider.ShouldPrompt(false, false, "", "", Make("b1", "x")));

    // Updated before: quiet until the server's launcher build changes.
    Check("updated + same build -> quiet", false, PromptDecider.ShouldPrompt(false, true, "b1", "x", Make("b1", "x")));
    Check("updated + new build -> prompt", true, PromptDecider.ShouldPrompt(false, true, "b1", "x", Make("b2", "x")));
    Check("updated with legacy plugin (no build) -> prompt once", true, PromptDecider.ShouldPrompt(false, true, "", "x", Make("b1", "x")));

    // Chose "Later": asked again only when image or build differs.
    Check("later + same image/build -> quiet", false, PromptDecider.ShouldPrompt(false, false, "b1", "x", Make("b1", "x")));
    Check("later + new image (same build) -> prompt", true, PromptDecider.ShouldPrompt(false, false, "b1", "x", Make("b1", "y")));
    Check("later + new build -> prompt", true, PromptDecider.ShouldPrompt(false, false, "b1", "x", Make("b2", "x")));

    Console.WriteLine(failures == 0 ? "ALL DECIDER TESTS PASSED" : $"{failures} FAILURE(S)");
    return failures == 0 ? 0 : 1;
}

// ─────────────────────────────────────────────────────────────────────────────────────────────────
//  Backend URL self-test - the SPT launcher appends -config={'BackendUrl':'https://host:port',...}.
// ─────────────────────────────────────────────────────────────────────────────────────────────────
static int RunBackendUrlTests()
{
    var failures = 0;

    void Check(string name, bool ok)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: {name}");
        if (!ok)
        {
            failures++;
        }
    }

    List<string> Parse(string text)
    {
        var list = new List<string>();
        BackendUrlProvider.ParseCandidates(text, list);
        return list;
    }

    // Exactly what the SPT launcher appends (single-quoted key - the old pattern missed this and
    // the plugin silently fell back to 127.0.0.1).
    const string launcherArguments = "-config={'BackendUrl':'https://100.127.48.41:6969','Version':'live','MatchingVersion':'live'}";
    var parsed = Parse(launcherArguments);
    Check("single-quoted key (launcher format)", parsed.Count == 1 && parsed[0] == "https://100.127.48.41:6969");

    parsed = Parse("G:\\Games\\SPTarkov-5.0.0\\EscapeFromTarkov.exe -force-gfx-jobs native -token=abc " + launcherArguments);
    Check("URL found inside a full command line", parsed.Count == 1 && parsed[0] == "https://100.127.48.41:6969");

    parsed = Parse("-config= {\"BackendUrl\":\"https://10.0.0.2:6970\",\"Version\":\"live\"}");
    Check("double quotes + split argument", parsed.Count == 1 && parsed[0] == "https://10.0.0.2:6970");

    parsed = Parse("BackendUrl=https://10.0.0.3:6969");
    Check("equals separator", parsed.Count == 1 && parsed[0] == "https://10.0.0.3:6969");

    parsed = Parse("-config={'BackendUrl':'https://10.0.0.4:6969/'}");
    Check("trailing slash trimmed", parsed.Count == 1 && parsed[0] == "https://10.0.0.4:6969");

    parsed = Parse("-config={'Version':'live'}");
    Check("no BackendUrl -> no candidates", parsed.Count == 0);

    Console.WriteLine(failures == 0 ? "ALL BACKEND TESTS PASSED" : $"{failures} FAILURE(S)");
    return failures == 0 ? 0 : 1;
}
