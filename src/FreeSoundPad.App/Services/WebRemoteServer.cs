using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using FreeSoundPad.App.ViewModels;

namespace FreeSoundPad.App.Services;

/// <summary>
/// Tiny LAN-only HTTP server used by the phone/stream-deck remote.
/// TcpListener is intentional: HttpListener needs an URL ACL on Windows.
/// </summary>
public sealed class WebRemoteServer : IDisposable
{
    private const int MaxClients = 16;
    private const int MaxRequestBytes = 64 * 1024;
    private static readonly ConcurrentDictionary<string, byte[]> ResourceCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly MainViewModel _viewModel;
    private readonly Dispatcher _dispatcher;
    private readonly int _port;
    private readonly string _token;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private TcpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private Task? _acceptLoop;
    private int _clients;
    private int _disposed;

    public WebRemoteServer(MainViewModel viewModel, Dispatcher dispatcher, int port, string token)
    {
        _viewModel = viewModel;
        _dispatcher = dispatcher;
        _port = port;
        _token = token;
        Url = $"http://{FindLanAddress()}:{port}/";
    }

    public int Port => _port;
    public string Token => _token;
    public string Url { get; }
    public bool IsRunning => _listener != null;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_listener != null) return;

        var listener = new TcpListener(IPAddress.Any, _port);
        try
        {
            listener.Start(32);
        }
        catch
        {
            listener.Stop();
            throw;
        }

        _listener = listener;
        _cancellation = new CancellationTokenSource();
        _acceptLoop = AcceptLoopAsync(listener, _cancellation.Token);
        Log.Info($"Web remote listening on {Url}");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        try
        {
            _cancellation?.Cancel();
            _listener?.Stop();
            _acceptLoop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // ignore shutdown races
        }

        _cancellation?.Dispose();
        _cancellation = null;
        _listener = null;
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
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                Log.Info($"Web remote accept failed: {ex.Message}");
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (Interlocked.Increment(ref _clients) > MaxClients)
            {
                Interlocked.Decrement(ref _clients);
                client.Dispose();
                continue;
            }

            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var stream = client.GetStream();
                stream.ReadTimeout = 6000;
                stream.WriteTimeout = 6000;

                var request = await ReadRequestAsync(stream, cancellationToken);
                if (request == null) return;

                await RouteAsync(request, stream, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // application closing
            }
            catch (IOException)
            {
                // browser disconnected
            }
            catch (Exception ex)
            {
                Log.Info($"Web remote request failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _clients);
            }
        }
    }

    private async Task RouteAsync(HttpRequest request, NetworkStream stream, CancellationToken cancellationToken)
    {
        var path = request.Path;

        if (request.Method == "GET" && path is "/" or "/index.html")
        {
            await SendResourceAsync(stream, "text/html; charset=utf-8", LoadResource("index.html"), cancellationToken);
            return;
        }

        if (request.Method == "GET" && path == "/styles.css")
        {
            await SendResourceAsync(stream, "text/css; charset=utf-8", LoadResource("styles.css"), cancellationToken);
            return;
        }

        if (request.Method == "GET" && path == "/app.js")
        {
            await SendResourceAsync(stream, "text/javascript; charset=utf-8", LoadResource("app.js"), cancellationToken);
            return;
        }

        if (request.Method == "GET" && path == "/favicon.ico")
        {
            await SendResourceAsync(stream, "image/x-icon", LoadResource("favicon.ico"), cancellationToken);
            return;
        }

        if (request.Method == "GET" && path == "/icon.png")
        {
            await SendResourceAsync(stream, "image/png", LoadResource("icon.png"), cancellationToken);
            return;
        }

        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await SendJsonAsync(stream, 404, new { error = "not_found" }, cancellationToken);
            return;
        }

        if (!IsAuthorized(request))
        {
            await SendJsonAsync(stream, 401, new { error = "unauthorized" }, cancellationToken);
            return;
        }

        if (request.Method == "GET" && path == "/api/state")
        {
            await SendJsonAsync(stream, 200, BuildState(), cancellationToken);
            return;
        }

        if (request.Method == "GET" && path == "/api/ping")
        {
            await SendJsonAsync(stream, 200, new { ok = true, app = "FreeSoundPad" }, cancellationToken);
            return;
        }

        if (request.Method == "POST" && path == "/api/stop-all")
        {
            await OnUiAsync(() =>
            {
                _viewModel.StopEverything();
                return true;
            });
            await SendJsonAsync(stream, 200, new { ok = true }, cancellationToken);
            return;
        }

        if (request.Method == "POST" && path == "/api/settings")
        {
            await HandleSettingsAsync(request, stream, cancellationToken);
            return;
        }

        if (request.Method == "POST" && path == "/api/browser/navigate")
        {
            await HandleNavigateAsync(request, stream, cancellationToken);
            return;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (request.Method == "POST"
            && segments.Length == 4
            && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
            && segments[1].Equals("pads", StringComparison.OrdinalIgnoreCase))
        {
            var padId = WebUtility.UrlDecode(segments[2]);
            var action = segments[3];
            var pad = await OnUiAsync(() => _viewModel.Pads.FirstOrDefault(p => p.Id == padId));
            if (pad == null)
            {
                await SendJsonAsync(stream, 404, new { error = "pad_not_found" }, cancellationToken);
                return;
            }

            if (action.Equals("play", StringComparison.OrdinalIgnoreCase))
            {
                await OnUiAsync(() =>
                {
                    _viewModel.PlayPad(pad);
                    return true;
                });
                await SendJsonAsync(stream, 200, new { ok = true }, cancellationToken);
                return;
            }

            if (action.Equals("stop", StringComparison.OrdinalIgnoreCase))
            {
                await OnUiAsync(() =>
                {
                    _viewModel.StopPad(pad);
                    return true;
                });
                await SendJsonAsync(stream, 200, new { ok = true }, cancellationToken);
                return;
            }
        }

        await SendJsonAsync(stream, 404, new { error = "not_found" }, cancellationToken);
    }

    private async Task HandleSettingsAsync(HttpRequest request, NetworkStream stream, CancellationToken cancellationToken)
    {
        RemoteSettingsPatch? patch;
        try
        {
            patch = JsonSerializer.Deserialize<RemoteSettingsPatch>(request.Body, _json);
        }
        catch
        {
            await SendJsonAsync(stream, 400, new { error = "bad_json" }, cancellationToken);
            return;
        }

        if (patch == null)
        {
            await SendJsonAsync(stream, 400, new { error = "empty_settings" }, cancellationToken);
            return;
        }

        await OnUiAsync(() =>
        {
            if (patch.MicPassthrough is bool mic) _viewModel.MicPassthrough = mic;
            if (patch.MonitorEnabled is bool monitor) _viewModel.MonitorEnabled = monitor;
            if (patch.BrowserSend is bool send) _viewModel.BrowserSend = send;
            if (patch.BrowserMonitor is bool bmonitor) _viewModel.BrowserMonitor = bmonitor;
            if (patch.BrowserCaptureEnabled is bool capture) _viewModel.BrowserCaptureEnabled = capture;
            if (patch.BrowserMuted is bool muted) _viewModel.SetBrowserMuted(muted);
            if (patch.OverlayVisible is bool overlay) _viewModel.OverlayVisible = overlay;

            if (patch.MasterGain is double master) _viewModel.MasterGain = Math.Clamp(master, 0, 100);
            if (patch.MicGain is double micGain) _viewModel.MicGain = Math.Clamp(micGain, 0, 100);
            if (patch.PadGain is double padGain) _viewModel.PadGain = Math.Clamp(padGain, 0, 100);
            if (patch.WebGain is double webGain) _viewModel.WebGain = Math.Clamp(webGain, 0, 100);
            if (patch.MonitorGain is double monitorGain) _viewModel.MonitorGain = Math.Clamp(monitorGain, 0, 100);

            _viewModel.Save();
            return true;
        });

        await SendJsonAsync(stream, 200, new { ok = true }, cancellationToken);
    }

    private async Task HandleNavigateAsync(HttpRequest request, NetworkStream stream, CancellationToken cancellationToken)
    {
        RemoteNavigateRequest? body;
        try
        {
            body = JsonSerializer.Deserialize<RemoteNavigateRequest>(request.Body, _json);
        }
        catch
        {
            await SendJsonAsync(stream, 400, new { error = "bad_json" }, cancellationToken);
            return;
        }

        var input = body?.Url?.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            await SendJsonAsync(stream, 400, new { error = "url_required" }, cancellationToken);
            return;
        }

        if (!input.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            input = "https://" + input;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            await SendJsonAsync(stream, 400, new { error = "bad_url" }, cancellationToken);
            return;
        }

        await OnUiAsync(() =>
        {
            _viewModel.NavigateBrowser(uri.ToString());
            return true;
        });

        await SendJsonAsync(stream, 200, new { ok = true }, cancellationToken);
    }

    private object BuildState()
    {
        return OnUi(() =>
        {
            var current = _viewModel.CurrentPad;
            var pads = _viewModel.Pads
                .OrderByDescending(p => p.Order)
                .ThenBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(p => new
                {
                    id = p.Id,
                    title = p.Title,
                    category = p.Category,
                    color = NormalizeColor(p.ColorHex),
                    duration = p.DurationText,
                    hotkey = p.HotkeyText,
                    favorite = p.Favorite,
                    loop = p.Loop,
                    playing = _viewModel.IsNowPlaying && current?.Id == p.Id,
                    progress = current?.Id == p.Id ? Math.Clamp(p.Progress, 0, 1) : 0,
                })
                .ToList();

            return new
            {
                app = "FreeSoundPad",
                version = "1.0",
                auth = true,
                pads,
                nowPlaying = new
                {
                    title = _viewModel.NowPlayingTitle,
                    meta = _viewModel.NowPlayingMeta,
                    time = _viewModel.NowPlayingTime,
                    progress = Math.Clamp(_viewModel.NowPlayingProgress, 0, 100),
                    isPlaying = _viewModel.IsNowPlaying,
                    padId = current?.Id,
                },
                settings = new
                {
                    micPassthrough = _viewModel.MicPassthrough,
                    monitorEnabled = _viewModel.MonitorEnabled,
                    canMonitor = _viewModel.CanMonitor,
                    browserSend = _viewModel.BrowserSend,
                    browserMonitor = _viewModel.BrowserMonitor,
                    browserCaptureEnabled = _viewModel.BrowserCaptureEnabled,
                    browserCaptureActive = _viewModel.BrowserCaptureActive,
                    browserMuted = _viewModel.BrowserMuted,
                    overlayVisible = _viewModel.OverlayVisible,
                    masterGain = _viewModel.MasterGain,
                    micGain = _viewModel.MicGain,
                    padGain = _viewModel.PadGain,
                    webGain = _viewModel.WebGain,
                    monitorGain = _viewModel.MonitorGain,
                },
                browser = new
                {
                    ready = _viewModel.IsBrowserReady,
                    title = _viewModel.BrowserTitle,
                    url = _viewModel.BrowserUrl,
                },
                output = new
                {
                    device = _viewModel.OutputDeviceName,
                    cable = _viewModel.OutputIsCable,
                    cableDetected = _viewModel.CableDetected,
                },
            };
        });
    }

    private static string NormalizeColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "#6EE7B7";
        var text = value.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        if (text.Length != 7) return "#6EE7B7";

        foreach (var c in text.AsSpan(1))
        {
            if (!Uri.IsHexDigit(c)) return "#6EE7B7";
        }

        return text;
    }

    private bool IsAuthorized(HttpRequest request)
    {
        if (string.IsNullOrEmpty(_token)) return true;

        var supplied = request.Headers.GetValueOrDefault("x-fsp-token");
        if (string.IsNullOrWhiteSpace(supplied))
        {
            supplied = request.Query.GetValueOrDefault("token");
        }

        if (string.IsNullOrWhiteSpace(supplied)
            && request.Headers.TryGetValue("authorization", out var auth)
            && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            supplied = auth["Bearer ".Length..].Trim();
        }

        if (string.IsNullOrWhiteSpace(supplied)) return false;
        return FixedTimeEquals(supplied.Trim().ToUpperInvariant(), _token.ToUpperInvariant());
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    private T OnUi<T>(Func<T> action)
    {
        if (_dispatcher.CheckAccess()) return action();
        return _dispatcher.Invoke(action);
    }

    private Task<T> OnUiAsync<T>(Func<T> action)
    {
        if (_dispatcher.CheckAccess())
        {
            try
            {
                return Task.FromResult(action());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }

        return _dispatcher.InvokeAsync(action).Task;
    }

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var received = new MemoryStream();
        var buffer = new byte[4096];
        var headerEnd = -1;

        while (received.Length < MaxRequestBytes)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            received.Write(buffer, 0, count);

            var data = received.GetBuffer();
            headerEnd = FindHeaderEnd(data, 0, (int)received.Length);
            if (headerEnd >= 0) break;
        }

        if (headerEnd < 0) return null;

        var all = received.ToArray();
        var headerText = Encoding.UTF8.GetString(all, 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0) return null;

        var firstLine = lines[0].Split(' ');
        if (firstLine.Length < 2) return null;

        var method = firstLine[0].ToUpperInvariant();
        var target = firstLine[1];
        var queryStart = target.IndexOf('?');
        var rawPath = queryStart >= 0 ? target[..queryStart] : target;
        var rawQuery = queryStart >= 0 ? target[(queryStart + 1)..] : "";
        var path = Uri.UnescapeDataString(rawPath);
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(rawQuery))
        {
            foreach (var pair in rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = eq >= 0 ? pair[..eq] : pair;
                var value = eq >= 0 ? pair[(eq + 1)..] : "";
                query[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value);
            }
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < lines.Length; index++)
        {
            var separator = lines[index].IndexOf(':');
            if (separator <= 0) continue;
            headers[lines[index][..separator].Trim()] = lines[index][(separator + 1)..].Trim();
        }

        var length = 0;
        if (headers.TryGetValue("content-length", out var contentLength))
        {
            int.TryParse(contentLength, out length);
        }

        if (length < 0 || length > MaxRequestBytes) return null;

        var bodyStart = headerEnd + 4;
        var bodyBytes = new byte[length];
        var available = Math.Min(length, all.Length - bodyStart);
        if (available > 0)
        {
            Buffer.BlockCopy(all, bodyStart, bodyBytes, 0, available);
        }

        var read = available;
        while (read < length)
        {
            var count = await stream.ReadAsync(bodyBytes.AsMemory(read, length - read), cancellationToken);
            if (count == 0) break;
            read += count;
        }

        var body = read > 0 ? Encoding.UTF8.GetString(bodyBytes, 0, read) : "";
        return new HttpRequest(method, path, query, headers, body);
    }

    private static int FindHeaderEnd(byte[] buffer, int start, int count)
    {
        var end = start + count - 3;
        for (var index = start; index < end; index++)
        {
            if (buffer[index] == '\r'
                && buffer[index + 1] == '\n'
                && buffer[index + 2] == '\r'
                && buffer[index + 3] == '\n')
            {
                return index;
            }
        }

        return -1;
    }

    private static async Task SendJsonAsync(NetworkStream stream, int status, object payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
        await SendAsync(stream, status, "application/json; charset=utf-8", bytes, cancellationToken);
    }

    private static async Task SendResourceAsync(NetworkStream stream, string contentType, byte[] bytes, CancellationToken cancellationToken)
    {
        await SendAsync(stream, 200, contentType, bytes, cancellationToken);
    }

    private static async Task SendAsync(NetworkStream stream, int status, string contentType, byte[] body, CancellationToken cancellationToken)
    {
        var reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            405 => "Method Not Allowed",
            500 => "Internal Server Error",
            _ => "OK",
        };

        var header = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(body.Length).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("Referrer-Policy: no-referrer\r\n")
            .Append("Connection: close\r\n")
            .Append("\r\n")
            .ToString();

        var headerBytes = Encoding.UTF8.GetBytes(header);
        await stream.WriteAsync(headerBytes, cancellationToken);
        if (body.Length > 0) await stream.WriteAsync(body, cancellationToken);
    }

    private static byte[] LoadResource(string fileName)
    {
        if (ResourceCache.TryGetValue(fileName, out var cached)) return cached;

        var assembly = typeof(WebRemoteServer).Assembly;
        var suffix = ".WebRemote." + fileName;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (name == null) throw new FileNotFoundException($"Embedded web resource not found: {fileName}");

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"Embedded web resource not found: {fileName}");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        ResourceCache[fileName] = bytes;
        return bytes;
    }

    public static string FindLanAddress()
    {
        try
        {
            var candidates = new List<LanCandidate>();
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up) continue;
                if (network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                var properties = network.GetIPProperties();
                var hasGateway = properties.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork
                    && !g.Address.Equals(IPAddress.Any));

                foreach (var unicast in properties.UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(address)) continue;

                    var bytes = address.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254) continue;
                    candidates.Add(new LanCandidate(address, hasGateway, network.NetworkInterfaceType, network.Description));
                }
            }

            var physical = candidates
                .Where(c => !IsVirtualInterface(c.Description))
                .OrderByDescending(c => c.HasGateway)
                .ThenByDescending(c => c.Type == NetworkInterfaceType.Wireless80211)
                .ThenByDescending(c => c.HasGateway)
                .ToList();

            var preferred = physical.FirstOrDefault()
                            ?? candidates.FirstOrDefault(c => c.HasGateway)
                            ?? candidates.FirstOrDefault();

            return preferred?.Address.ToString() ?? "127.0.0.1";
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    private static bool IsVirtualInterface(string description)
    {
        var text = description ?? "";
        string[] markers =
        [
            "virtual", "vmware", "hyper-v", "virtualbox", "vbox", "tap-", "tap adapter",
            "vpn", "loopback", "bluetooth", "docker", "wsl", "host-only", "pseudo",
        ];

        return markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record LanCandidate(
        IPAddress Address,
        bool HasGateway,
        NetworkInterfaceType Type,
        string Description);

    private sealed record HttpRequest(
        string Method,
        string Path,
        Dictionary<string, string> Query,
        Dictionary<string, string> Headers,
        string Body);

    private sealed class RemoteSettingsPatch
    {
        public bool? MicPassthrough { get; set; }
        public bool? MonitorEnabled { get; set; }
        public bool? BrowserSend { get; set; }
        public bool? BrowserMonitor { get; set; }
        public bool? BrowserCaptureEnabled { get; set; }
        public bool? BrowserMuted { get; set; }
        public bool? OverlayVisible { get; set; }
        public double? MasterGain { get; set; }
        public double? MicGain { get; set; }
        public double? PadGain { get; set; }
        public double? WebGain { get; set; }
        public double? MonitorGain { get; set; }
    }

    private sealed class RemoteNavigateRequest
    {
        public string? Url { get; set; }
    }
}
