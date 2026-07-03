using CommunityToolkit.Mvvm.ComponentModel;

namespace AiGateway.Logging;

/// <summary>
/// A single proxied request/response record shown in the UI log grid.
/// Mutable + observable because it is created at request time and completed at response time.
/// </summary>
public sealed partial class LogEntry : ObservableObject
{
    /// <summary>Correlation id, matches Titanium's session HttpClient hash.</summary>
    public int SessionId { get; init; }

    /// <summary>Wall-clock time the request left the proxy.</summary>
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>Friendly provider name resolved by the router ("OpenAI" / "Claude" / "").</summary>
    public string Provider { get; init; } = "";

    public string Method { get; init; } = "";

    public string Url { get; init; } = "";

    /// <summary>Just the host, for compact display / grouping.</summary>
    public string Host { get; init; } = "";

    [ObservableProperty]
    private int _statusCode;

    [ObservableProperty]
    private double _elapsedMs;

    [ObservableProperty]
    private long _requestSize;

    [ObservableProperty]
    private long _responseSize;

    /// <summary>False until the response has been observed.</summary>
    [ObservableProperty]
    private bool _completed;

    public string TimestampText => Timestamp.ToString("HH:mm:ss.fff");
}
