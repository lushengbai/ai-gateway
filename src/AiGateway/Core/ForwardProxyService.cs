using System.Net;
using AiGateway.Config;
using AiGateway.Logging;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;

namespace AiGateway.Core;

/// <summary>
/// Forward-proxy backend (Titanium.Web.Proxy): explicit HTTP(S) proxy on
/// 127.0.0.1:{port}. Observes/decrypts recognized AI hosts, tunnels the rest,
/// and can chain through an upstream HTTP proxy.
/// </summary>
public sealed class ForwardProxyService : IProxyBackend
{
    private readonly AppConfig _config;
    private readonly LogService _log;
    private readonly ApiRouter _router;
    private readonly RequestHandler _handler;

    private ProxyServer? _proxyServer;
    private ExplicitProxyEndPoint? _endPoint;

    public event Action<string>? Message;

    public bool IsRunning { get; private set; }

    public ForwardProxyService(AppConfig config, LogService log)
    {
        _config = config;
        _log = log;
        _router = new ApiRouter(config);
        _handler = new RequestHandler(config, _router, log);
    }

    public void Start(int port)
    {
        if (IsRunning)
            return;

        _proxyServer = new ProxyServer
        {
            ForwardToUpstreamGateway = false,
        };

        _proxyServer.CertificateManager.RootCertificateName = "AiGateway Root Certificate";
        _proxyServer.CertificateManager.RootCertificateIssuerName = "AiGateway";

        if (_config.TrustRootCertificate)
        {
            try
            {
                _proxyServer.CertificateManager.EnsureRootCertificate();
                _proxyServer.CertificateManager.TrustRootCertificate();
                Message?.Invoke("Root certificate ensured/trusted (CurrentUser store).");
            }
            catch (Exception ex)
            {
                Message?.Invoke($"Warning: could not trust root certificate: {ex.Message}");
            }
        }

        // Route all outbound traffic through the upstream proxy, if configured.
        var up = _config.UpstreamProxy;
        if (up.Enabled && !string.IsNullOrWhiteSpace(up.Host))
        {
            var external = new ExternalProxy
            {
                HostName = up.Host,
                Port = up.Port,
            };
            if (up.HasCredentials)
            {
                external.UserName = up.Username;
                external.Password = up.Password;
            }
            _proxyServer.UpStreamHttpProxy = external;
            _proxyServer.UpStreamHttpsProxy = external;
            Message?.Invoke($"Upstream proxy: {up.Host}:{up.Port}");
        }

        _endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, port, decryptSsl: false);
        _endPoint.BeforeTunnelConnectRequest += _handler.OnBeforeTunnelConnect;

        _proxyServer.AddEndPoint(_endPoint);
        _proxyServer.BeforeRequest += _handler.OnBeforeRequest;
        _proxyServer.BeforeResponse += _handler.OnBeforeResponse;

        _proxyServer.Start();

        IsRunning = true;
        Message?.Invoke($"Forward proxy started on 127.0.0.1:{port}");
    }

    public void Stop()
    {
        if (!IsRunning || _proxyServer is null)
            return;

        try
        {
            if (_endPoint is not null)
                _endPoint.BeforeTunnelConnectRequest -= _handler.OnBeforeTunnelConnect;
            _proxyServer.BeforeRequest -= _handler.OnBeforeRequest;
            _proxyServer.BeforeResponse -= _handler.OnBeforeResponse;
            _proxyServer.Stop();
        }
        catch (Exception ex)
        {
            Message?.Invoke($"Error while stopping: {ex.Message}");
        }
        finally
        {
            _proxyServer.Dispose();
            _proxyServer = null;
            _endPoint = null;
            IsRunning = false;
            Message?.Invoke("Forward proxy stopped.");
        }
    }

    public void Dispose() => Stop();
}
