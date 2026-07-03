namespace AiGateway.Core;

/// <summary>Common lifecycle for a gateway backend (reverse or forward proxy).</summary>
public interface IProxyBackend : IDisposable
{
    bool IsRunning { get; }

    /// <summary>Human-readable status/diagnostic messages for the UI.</summary>
    event Action<string>? Message;

    void Start(int port);

    void Stop();
}
