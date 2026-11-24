namespace BepInEx;

/// <summary>
/// Minimal stand-in for the BepInEx type that <c>BackendUrlProvider.GetGameRoot()</c> reads, so the
/// harness can compile the exact plugin source without the game runtime.
/// </summary>
internal static class Paths
{
    internal static string GameRootPath = string.Empty;
}
