using System.Diagnostics;
using System.Threading;
using AiGateway.Config;
using AiGateway.Logging;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;

namespace AiGateway.Core;

/// <summary>
/// Translates Titanium proxy events into <see cref="LogEntry"/> records.
/// Kept separate from <see cref="ProxyService"/> so the wiring (start/stop) and the
/// per-request logic stay independently testable.
/// </summary>
public sealed class RequestHandler
{
    private readonly AppConfig _config;
    private readonly ApiRouter _router;
    private readonly LogService _log;

    // Correlates a request with its response across the two events.
    private sealed class RequestState
    {
        public int SessionId;
        public long StartTimestamp; // Stopwatch ticks
    }

    private int _sessionCounter;

    public RequestHandler(AppConfig config, ApiRouter router, LogService log)
    {
        _config = config;
        _router = router;
        _log = log;
    }

    /// <summary>
    /// Decide whether a tunneled HTTPS connection should be decrypted so we can log it.
    /// AI hosts are always decrypted; everything else is decrypted only when the
    /// "only log AI requests" filter is off (transparent tunnel otherwise).
    /// </summary>
    public Task OnBeforeTunnelConnect(object sender, TunnelConnectSessionEventArgs e)
    {
        var host = e.HttpClient.ConnectRequest?.RequestUri?.Host ?? "";
        e.DecryptSsl = _router.IsAiHost(host) || !_config.OnlyLogAiRequests;
        return Task.CompletedTask;
    }

    public Task OnBeforeRequest(object sender, SessionEventArgs e)
    {
        Request request = e.HttpClient.Request;
        var host = request.RequestUri?.Host ?? request.Host ?? "";

        // Respect the AI-only filter for plain-HTTP requests too.
        if (_config.OnlyLogAiRequests && !_router.IsAiHost(host))
            return Task.CompletedTask;

        var id = Interlocked.Increment(ref _sessionCounter);
        e.UserData = new RequestState { SessionId = id, StartTimestamp = Stopwatch.GetTimestamp() };

        var entry = new LogEntry
        {
            SessionId = id,
            Provider = _router.ProviderName(host),
            Method = request.Method ?? "",
            Url = request.Url ?? "",
            Host = host,
            RequestSize = request.ContentLength > 0 ? request.ContentLength : 0,
        };

        _log.AddRequest(entry);
        return Task.CompletedTask;
    }

    public Task OnBeforeResponse(object sender, SessionEventArgs e)
    {
        if (e.UserData is not RequestState state)
            return Task.CompletedTask;

        var elapsedMs = Stopwatch.GetElapsedTime(state.StartTimestamp).TotalMilliseconds;
        Response response = e.HttpClient.Response;
        long responseSize = response.ContentLength > 0 ? response.ContentLength : 0;

        _log.CompleteResponse(state.SessionId, response.StatusCode, elapsedMs, responseSize);
        return Task.CompletedTask;
    }
}
