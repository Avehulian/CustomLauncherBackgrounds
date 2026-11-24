using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;

namespace PreyToLive.CustomLauncherBackgrounds;

/// <summary>
/// HTTP media server for the launcher's background rotation.
/// The playlist is served in order (1-bg.png, 2-bg.png, ... 100), starting at the item the server
/// picked for this run, so the numbered backgrounds form a proper slideshow.
///
/// The launcher's WebView can fetch from HTTP cross-origin (different port) when the main page is HTTPS.
/// The launcher's patched script polls it every <c>rotationSeconds</c>.
/// </summary>
internal sealed class LauncherRotationServer : IDisposable
{
    private readonly string[] _images;
    private readonly string _initialImage;
    private readonly int _imageIntervalSeconds;
    private readonly int _videoIntervalSeconds;
    private readonly IReadOnlyList<IPAddress> _bindAddresses;
    private readonly bool _logRotations;
    private readonly IReadOnlyList<string> _infoBoxes;
    private readonly string _infoLink;
    private readonly ISptLogger<BackgroundSelectionService> _logger;
    private int _index;

    private readonly List<TcpListener> _listeners = [];
    private CancellationTokenSource? _cancellation;
    // The current item is streamed from disk (never held in memory - videos can be gigabytes).
    private volatile string _currentPath = string.Empty;
    private volatile string _currentName = string.Empty;
    private volatile bool _currentIsVideo;
    private volatile string _currentContentType = "image/jpeg";

    public LauncherRotationServer(string[] images, string initialImage, int imageIntervalSeconds, int videoIntervalSeconds, int port, IReadOnlyList<IPAddress> bindAddresses, bool logRotations, IReadOnlyList<string> infoBoxes, string infoLink, ISptLogger<BackgroundSelectionService> logger)
    {
        _images = images;
        _initialImage = initialImage;
        _imageIntervalSeconds = imageIntervalSeconds;
        _videoIntervalSeconds = videoIntervalSeconds;
        _bindAddresses = bindAddresses;
        _logRotations = logRotations;
        _infoBoxes = infoBoxes ?? Array.Empty<string>();
        _infoLink = infoLink ?? string.Empty;
        _logger = logger;
        Port = port;
        BaseUrl = $"http://{bindAddresses[0]}:{port}";
    }

    public int Port { get; }

    public string BaseUrl { get; }

    public string CurrentImage => _currentName;

    public void Start()
    {
        _cancellation = new CancellationTokenSource();

        foreach (var address in _bindAddresses)
        {
            var listener = new TcpListener(address, Port);
            listener.Start();
            _listeners.Add(listener);
            _ = Task.Run(() => AcceptLoopAsync(listener, _cancellation.Token));
        }

        var startIndex = Array.FindIndex(_images, path => string.Equals(path, _initialImage, StringComparison.OrdinalIgnoreCase));
        _index = startIndex >= 0 ? startIndex : 0;
        SetCurrent(_images[_index]);

        _ = Task.Run(() => RotationLoopAsync(_cancellation.Token));

        var scope = string.Join(" + ", _bindAddresses.Select(address => address.Equals(IPAddress.Any) ? "all interfaces" : address.ToString()));
        _logger.Success($"Mod: {BackgroundSelectionService.ModName}: background rotation server listening on port {Port} ({scope}) - rotating {_images.Length} item(s) in order (images every {_imageIntervalSeconds}s, videos every {_videoIntervalSeconds}s), starting at {Path.GetFileName(_images[_index])}");
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                await Task.Delay(250, cancellationToken);
                continue;
            }

            _ = Task.Run(() => HandleClient(client), CancellationToken.None);
        }
    }

    private void HandleClient(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 15000;

                Stream stream = client.GetStream();

                // Read the request line + headers (we only need the path).
                var buffer = new byte[4096];
                var read = stream.Read(buffer, 0, buffer.Length);
                var request = Encoding.ASCII.GetString(buffer, 0, Math.Max(read, 0));
                var firstLine = request.Split('\n')[0].Trim();

                var path = _currentPath;
                var isVideo = _currentIsVideo;
                var length = File.Exists(path) ? new FileInfo(path).Length : 0L;

                if (firstLine.StartsWith("GET /bg.jpg", StringComparison.OrdinalIgnoreCase))
                {
                    if (length <= 0 || isVideo || !File.Exists(path))
                    {
                        SendResponse(stream, "404 Not Found", "text/plain", Encoding.ASCII.GetBytes("no image"));
                        return;
                    }

                    SendFileResponse(stream, _currentContentType, path, length);
                    return;
                }

                if (firstLine.StartsWith("GET /bg.mp4", StringComparison.OrdinalIgnoreCase))
                {
                    if (length <= 0 || !isVideo || !File.Exists(path))
                    {
                        SendResponse(stream, "404 Not Found", "text/plain", Encoding.ASCII.GetBytes("no video"));
                        return;
                    }

                    SendFileResponse(stream, "video/mp4", path, length);
                    return;
                }

                // Tiny change feed for the patched launcher: "<kind>|<name>", fetched every few seconds.
                // The launcher only downloads media when this string changes, so rotation shows up within
                // seconds of the server switching - independent of the rotation interval.
                // The Info page payload (text boxes + link) in one request, used by the baked script.
                if (firstLine.StartsWith("GET /b", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = JsonSerializer.Serialize(new { t = _infoBoxes, l = _infoLink });
                    SendResponse(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(payload), cacheControl: "no-store");
                    return;
                }

                // The link the Info text opens (empty = the text is inert).
                if (firstLine.StartsWith("GET /l", StringComparison.OrdinalIgnoreCase))
                {
                    SendResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(_infoLink), cacheControl: "no-store");
                    return;
                }

                // The launcher Info page text (edited in the web editor, served live). "GET /i" also matches
                // the long form /info, so both URLs work - the baked script uses the short one to save bytes.
                if (firstLine.StartsWith("GET /i", StringComparison.OrdinalIgnoreCase))
                {
                    SendResponse(stream, "200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(string.Join("\n\n", _infoBoxes)), cacheControl: "no-store");
                    return;
                }

                if (firstLine.StartsWith("GET /state", StringComparison.OrdinalIgnoreCase))
                {
                    var state = (isVideo ? "v|" : "i|") + _currentName;
                    SendResponse(stream, "200 OK", "text/plain", Encoding.ASCII.GetBytes(state), cacheControl: "no-store");
                    return;
                }

                if (firstLine.StartsWith("GET / ", StringComparison.OrdinalIgnoreCase)
                    || firstLine.StartsWith("GET /index", StringComparison.OrdinalIgnoreCase))
                {
                    var info = $"Custom Launcher Backgrounds rotation: {_currentName} ({(isVideo ? "video" : "image")}), images every {_imageIntervalSeconds}s / videos every {_videoIntervalSeconds}s";
                    SendResponse(stream, "200 OK", "text/plain", Encoding.ASCII.GetBytes(info));
                    return;
                }

                SendResponse(stream, "404 Not Found", "text/plain", Encoding.ASCII.GetBytes("not found"));
            }
        }
        catch
        {
            // client went away mid-request - nothing to do
        }
    }

    /// <summary>Streams a file straight from disk (works for gigabyte videos without loading them).</summary>
    private static void SendFileResponse(Stream stream, string contentType, string path, long length)
    {
        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {length}\r\n" +
            "Cache-Control: private, max-age=30\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Connection: close\r\n\r\n");

        stream.Write(header, 0, header.Length);

        using var file = File.OpenRead(path);
        file.CopyTo(stream, 81920);
        stream.Flush();
    }

    private static void SendResponse(Stream stream, string status, string contentType, byte[] body, string cacheControl = "private, max-age=30")
    {
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            $"Cache-Control: {cacheControl}\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Connection: close\r\n\r\n");

        stream.Write(header, 0, header.Length);
        stream.Write(body, 0, body.Length);
        stream.Flush();
    }

    private async Task RotationLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // Every item is shown for its own kind's interval: images 10s, videos 60s - each configurable.
            var wait = _currentIsVideo ? _videoIntervalSeconds : _imageIntervalSeconds;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait, 1, 86_400)), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                _index = (_index + 1) % _images.Length;
                SetCurrent(_images[_index]);

                // Only with "consoleLogs": true - otherwise a line per rotation would spam the console.
                if (_logRotations)
                {
                    _logger.Info($"Mod: {BackgroundSelectionService.ModName}: background rotation -> {Path.GetFileName(_images[_index])} ({_index + 1}/{_images.Length})");
                }
            }
            catch (Exception exception)
            {
                _logger.Warning($"Mod: {BackgroundSelectionService.ModName}: background rotation failed: {exception.Message}");
            }
        }
    }

    private void SetCurrent(string imagePath)
    {
        _currentPath = imagePath;
        _currentIsVideo = imagePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
        _currentContentType = Path.GetExtension(imagePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".jpeg" or ".jpg" => "image/jpeg",
            _ => "application/octet-stream",
        };
        _currentName = Path.GetFileName(imagePath);
    }

    public void Dispose()
    {
        try
        {
            _cancellation?.Cancel();

            foreach (var listener in _listeners)
            {
                listener.Stop();
                listener.Dispose();
            }

            _listeners.Clear();
            _cancellation?.Dispose();
        }
        catch
        {
            // best effort
        }
        finally
        {
            _listeners.Clear();
            _cancellation = null;
        }
    }
}
