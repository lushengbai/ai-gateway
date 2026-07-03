using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiGateway.Config;

/// <summary>How the gateway exposes itself to clients.</summary>
public enum ProxyMode
{
    /// <summary>Reverse proxy: client sets base_url to http://127.0.0.1:{port}[/prefix];
    /// the gateway rewrites the request to the configured third-party API and forwards it.</summary>
    ReverseProxy = 0,

    /// <summary>Forward proxy (Titanium): client sets HTTP(S)_PROXY to 127.0.0.1:{port};
    /// the gateway observes/decrypts recognized AI hosts and tunnels the rest.</summary>
    ForwardProxy = 1,
}

/// <summary>
/// A reverse-proxy route: requests whose path starts with <see cref="PathPrefix"/> are
/// forwarded to <see cref="TargetBaseUrl"/> (the prefix is stripped before forwarding).
/// </summary>
public sealed class ApiRoute
{
    /// <summary>Friendly name shown in the UI / log filter (e.g. "OpenAI", "MyApi").</summary>
    public string Name { get; set; } = "";

    /// <summary>Path prefix to match, e.g. "/openai". Use "" (or "/") for a catch-all default route.</summary>
    public string PathPrefix { get; set; } = "";

    /// <summary>Upstream base URL, e.g. "https://api.openai.com". Scheme + host (+ optional base path).</summary>
    public string TargetBaseUrl { get; set; } = "";

    public bool Enabled { get; set; } = true;
}

/// <summary>Optional upstream HTTP proxy that all forwarded traffic is routed through.</summary>
public sealed class UpstreamProxyConfig
{
    public bool Enabled { get; set; }

    public string Host { get; set; } = "";

    public int Port { get; set; } = 7890;

    /// <summary>Optional proxy auth.</summary>
    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    [JsonIgnore]
    public bool HasCredentials => !string.IsNullOrEmpty(Username);
}

/// <summary>
/// Describes one upstream AI API host recognized in <b>forward-proxy</b> mode.
/// </summary>
public sealed class ApiTarget
{
    public string Name { get; set; } = "";
    public string HostSuffix { get; set; } = "";
    public bool Decrypt { get; set; } = true;
}

/// <summary>Application configuration. Persisted as JSON next to the executable.</summary>
public sealed class AppConfig
{
    /// <summary>Local port the gateway listens on (127.0.0.1 only).</summary>
    public int Port { get; set; } = 8080;

    /// <summary>Active gateway mode.</summary>
    public ProxyMode Mode { get; set; } = ProxyMode.ReverseProxy;

    /// <summary>Upstream HTTP proxy applied to all forwarded requests.</summary>
    public UpstreamProxyConfig UpstreamProxy { get; set; } = new();

    /// <summary>Reverse-proxy routes (path prefix -> third-party API base URL).</summary>
    public List<ApiRoute> Routes { get; set; } = new()
    {
        new ApiRoute { Name = "OpenAI", PathPrefix = "/openai", TargetBaseUrl = "https://api.openai.com",    Enabled = true },
        new ApiRoute { Name = "Claude", PathPrefix = "/claude", TargetBaseUrl = "https://api.anthropic.com", Enabled = true },
    };

    // ---- Forward-proxy (Titanium) settings ----

    /// <summary>When true, only requests matching a known <see cref="Targets"/> host are logged/decrypted.</summary>
    public bool OnlyLogAiRequests { get; set; } = true;

    /// <summary>When true, install/trust the proxy root certificate on start (forward mode only).</summary>
    public bool TrustRootCertificate { get; set; } = true;

    /// <summary>Recognized upstream AI API hosts (forward mode).</summary>
    public List<ApiTarget> Targets { get; set; } = new()
    {
        new ApiTarget { Name = "OpenAI", HostSuffix = "api.openai.com",    Decrypt = true },
        new ApiTarget { Name = "Claude", HostSuffix = "api.anthropic.com", Decrypt = true },
    };

    [JsonIgnore]
    public static string ConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "appconfig.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (cfg is not null)
                {
                    cfg.UpstreamProxy ??= new UpstreamProxyConfig();
                    cfg.Routes ??= new List<ApiRoute>();
                    cfg.Targets ??= new List<ApiTarget>();
                    return cfg;
                }
            }
        }
        catch
        {
            // Fall back to defaults on any read/parse error.
        }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch
        {
            // Persistence is best-effort; ignore failures.
        }
    }
}
