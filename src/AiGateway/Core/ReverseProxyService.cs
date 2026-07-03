using System.Buffers;
using System.Net;
using System.Net.Http;
using System.Threading;
using AiGateway.Config;
using AiGateway.Logging;

namespace AiGateway.Core;

/// <summary>
/// Reverse-proxy backend built on <see cref="HttpListener"/>. Clients point their
/// base_url at http://127.0.0.1:{port}[/prefix]; the gateway rewrites the request to the
/// matching route's third-party API and forwards it (optionally via an upstream HTTP proxy),
/// streaming the response back. No TLS interception / certificate is required.
/// </summary>
public sealed class ReverseProxyService : IProxyBackend
{
    private readonly AppConfig _config;
    private readonly LogService _log;
    private readonly ApiRouter _router;

    private HttpListener? _listener;
    private HttpClient? _client;
    private CancellationTokenSource? _cts;
    private int _sessionCounter;

    public event Action<string>? Message;

    public bool IsRunning { get; private set; }

    // Headers that must not be forwarded verbatim (hop-by-hop, per RFC 7230).
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Host", "Proxy-Connection",
    };

    private static readonly HashSet<string> ContentHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type", "Content-Length", "Content-Encoding", "Content-Language",
        "Content-Location", "Content-MD5", "Content-Range", "Content-Disposition",
        "Allow", "Expires", "Last-Modified",
    };

    public ReverseProxyService(AppConfig config, LogService log)
    {
        _config = config;
        _log = log;
        _router = new ApiRouter(config);
    }

    public void Start(int port)
    {
        if (IsRunning)
            return;

        _client = BuildHttpClient();

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();

        _cts = new CancellationTokenSource();
        IsRunning = true;

        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));

        var up = _config.UpstreamProxy;
        if (up.Enabled && !string.IsNullOrWhiteSpace(up.Host))
            Message?.Invoke($"Reverse proxy started on 127.0.0.1:{port} · upstream {up.Host}:{up.Port}");
        else
            Message?.Invoke($"Reverse proxy started on 127.0.0.1:{port}");
    }

    private HttpClient BuildHttpClient()
    {
        // SocketsHttpHandler gives explicit control over connection pooling/HTTP2,
        // which matters when many requests hit the same upstream API host.
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None, // forward payloads untouched
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90),
            MaxConnectionsPerServer = 256,
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = TimeSpan.FromSeconds(15), // bound connect; overall stays unlimited for streaming
        };

        var up = _config.UpstreamProxy;
        if (up.Enabled && !string.IsNullOrWhiteSpace(up.Host))
        {
            var proxy = new WebProxy(up.Host, up.Port);
            if (up.HasCredentials)
                proxy.Credentials = new NetworkCredential(up.Username, up.Password);
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }
        else
        {
            handler.UseProxy = false;
        }

        return new HttpClient(handler)
        {
            // Rely on client disconnect / streaming rather than a fixed timeout (SSE friendly).
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Message?.Invoke($"Listener error: {ex.Message}");
                break;
            }

            // Handle each request independently so slow/streaming ones don't block others.
            _ = Task.Run(() => HandleAsync(ctx, ct));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var request = ctx.Request;
        var response = ctx.Response;
        var startTs = System.Diagnostics.Stopwatch.GetTimestamp();
        var id = Interlocked.Increment(ref _sessionCounter);

        var path = request.Url?.AbsolutePath ?? "/";
        var query = request.Url?.Query ?? "";
        var match = _router.ResolveReverse(path, query);

        if (match is null)
        {
            var entry = NewEntry(id, "", request.HttpMethod, request.Url?.ToString() ?? path, request.Url?.Host ?? "", request.ContentLength64);
            _log.AddRequest(entry);
            await WriteErrorAsync(response, 502, $"No route matches path '{path}'.");
            _log.CompleteResponse(id, 502, Elapsed(startTs), 0);
            return;
        }

        var targetUri = new Uri(match.TargetUrl);
        var logEntry = NewEntry(id, match.Route.Name, request.HttpMethod, match.TargetUrl, targetUri.Host, request.ContentLength64);
        _log.AddRequest(logEntry);

        try
        {
            using var upstreamRequest = BuildUpstreamRequest(request, targetUri);

            using var upstreamResponse = await _client!.SendAsync(
                upstreamRequest, HttpCompletionOption.ResponseHeadersRead, ct);

            response.StatusCode = (int)upstreamResponse.StatusCode;
            bool eagerFlush = CopyResponseHeaders(upstreamResponse, response);

            long written = await StreamBodyAsync(upstreamResponse, response, eagerFlush, ct);

            _log.CompleteResponse(id, response.StatusCode, Elapsed(startTs), written);
        }
        catch (Exception ex)
        {
            try { await WriteErrorAsync(response, 502, $"Upstream request failed: {ex.Message}"); }
            catch { /* client may already be gone */ }
            _log.CompleteResponse(id, 502, Elapsed(startTs), 0);
            Message?.Invoke($"Forward error → {targetUri.Host}: {ex.Message}");
        }
        finally
        {
            try { response.OutputStream.Close(); } catch { }
            try { response.Close(); } catch { }
        }
    }

    private static HttpRequestMessage BuildUpstreamRequest(HttpListenerRequest request, Uri targetUri)
    {
        var message = new HttpRequestMessage(new HttpMethod(request.HttpMethod), targetUri)
        {
            // Prefer HTTP/2 (multiplexing) but transparently fall back for HTTP/1.1-only upstreams/proxies.
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        var hasBody = request.HasEntityBody &&
                      !string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) &&
                      !string.Equals(request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase);

        if (hasBody)
            message.Content = new StreamContent(request.InputStream);

        foreach (string? key in request.Headers.AllKeys)
        {
            if (key is null || HopByHop.Contains(key))
                continue;

            var value = request.Headers[key];
            if (value is null)
                continue;

            if (ContentHeaders.Contains(key))
            {
                if (message.Content is null)
                    continue;
                if (string.Equals(key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (request.ContentLength64 >= 0)
                        message.Content.Headers.ContentLength = request.ContentLength64;
                    continue;
                }
                message.Content.Headers.TryAddWithoutValidation(key, value);
            }
            else
            {
                message.Headers.TryAddWithoutValidation(key, value);
            }
        }

        return message;
    }

    /// <summary>
    /// Copies upstream response headers to the client response and chooses the framing.
    /// Returns true when the body should be flushed eagerly per chunk (SSE / unknown length),
    /// false when it can be buffered and flushed once (known Content-Length).
    /// </summary>
    private static bool CopyResponseHeaders(HttpResponseMessage upstream, HttpListenerResponse outResponse)
    {
        void Copy(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
        {
            foreach (var header in headers)
            {
                if (HopByHop.Contains(header.Key) ||
                    string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // length/framing is managed by the listener
                }

                var value = string.Join(", ", header.Value);
                try
                {
                    if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                        outResponse.ContentType = value;
                    else
                        outResponse.Headers.Set(header.Key, value);
                }
                catch
                {
                    // Some headers are restricted on HttpListenerResponse; skip quietly.
                }
            }
        }

        Copy(upstream.Headers);
        if (upstream.Content is not null)
            Copy(upstream.Content.Headers);

        long? length = upstream.Content?.Headers.ContentLength;
        bool isEventStream = string.Equals(
            upstream.Content?.Headers.ContentType?.MediaType,
            "text/event-stream", StringComparison.OrdinalIgnoreCase);

        if (length.HasValue && !isEventStream)
        {
            // Known, non-streaming length: use Content-Length framing (no chunk overhead, buffered flush).
            outResponse.SendChunked = false;
            try { outResponse.ContentLength64 = length.Value; } catch { }
            return false;
        }

        // Streaming / unknown length: chunked + eager flush so bytes reach the client live.
        outResponse.SendChunked = true;
        return true;
    }

    private static async Task<long> StreamBodyAsync(
        HttpResponseMessage upstream, HttpListenerResponse outResponse, bool eagerFlush, CancellationToken ct)
    {
        long total = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            await using var source = await upstream.Content.ReadAsStreamAsync(ct);
            var dest = outResponse.OutputStream;

            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                await dest.WriteAsync(buffer.AsMemory(0, read), ct);
                if (eagerFlush)
                    await dest.FlushAsync(ct); // push SSE/streaming bytes out immediately
                total += read;
            }

            if (!eagerFlush)
                await dest.FlushAsync(ct); // single flush for buffered (Content-Length) responses
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return total;
    }

    private static async Task WriteErrorAsync(HttpListenerResponse response, int status, string message)
    {
        response.StatusCode = status;
        response.ContentType = "text/plain; charset=utf-8";
        var bytes = System.Text.Encoding.UTF8.GetBytes(message);
        response.SendChunked = false;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    private static LogEntry NewEntry(int id, string provider, string method, string url, string host, long reqLen)
        => new()
        {
            SessionId = id,
            Provider = provider,
            Method = method,
            Url = url,
            Host = host,
            RequestSize = reqLen > 0 ? reqLen : 0,
        };

    private static double Elapsed(long startTs)
        => System.Diagnostics.Stopwatch.GetElapsedTime(startTs).TotalMilliseconds;

    public void Stop()
    {
        if (!IsRunning)
            return;

        IsRunning = false;
        try { _cts?.Cancel(); } catch { }

        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;

        _client?.Dispose();
        _client = null;

        _cts?.Dispose();
        _cts = null;

        Message?.Invoke("Reverse proxy stopped.");
    }

    public void Dispose() => Stop();
}
