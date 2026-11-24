using SPTarkov.Common.Models.Logging;

namespace PreyToLive.CustomLauncherBackgrounds;

/// <summary>
/// Watches for the local SPT launcher window (the machine that runs this server is usually also the one
/// running the launcher) and makes it borderless - no native title bar, no DWM border line - so the
/// background artwork reaches every window edge. Controlled by "launcherWindowBorderless".
/// </summary>
internal sealed class LauncherWindowWatcher : IDisposable
{
    private readonly bool _enabled;
    private readonly bool _borderless;
    private readonly bool _tint;
    private readonly ISptLogger<BackgroundSelectionService> _logger;
    private CancellationTokenSource? _cancellation;
    private bool _loggedOnce;

    public LauncherWindowWatcher(bool borderless, bool tint, ISptLogger<BackgroundSelectionService> logger)
    {
        _borderless = borderless;
        _tint = tint;
        _enabled = borderless || tint;
        _logger = logger;
    }

    public void Start()
    {
        if (!_enabled || !OperatingSystem.IsWindows())
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        _ = Task.Run(() => WatchLoopAsync(_cancellation.Token));

        _logger.Info($"Mod: {BackgroundSelectionService.ModName}: watching for the launcher window (borderless: {_borderless}, border tint: {_tint})");
    }

    private async Task WatchLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var changed = LauncherWindowChrome.ApplyToLauncherWindows(_borderless, _tint);
                if (changed > 0 && !_loggedOnce)
                {
                    _loggedOnce = true;
                    _logger.Success(_borderless
                        ? $"Mod: {BackgroundSelectionService.ModName}: launcher window made borderless (no title bar/border)"
                        : $"Mod: {BackgroundSelectionService.ModName}: launcher window border tinted (light shade)");
                }
            }
            catch
            {
                // best effort - keep watching
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _cancellation?.Cancel();
            _cancellation?.Dispose();
        }
        catch
        {
            // best effort
        }
        finally
        {
            _cancellation = null;
        }
    }
}
