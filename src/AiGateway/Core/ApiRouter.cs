using AiGateway.Config;

namespace AiGateway.Core;

/// <summary>
/// Resolves requests to a target:
/// - Reverse mode: longest path-prefix match against <see cref="AppConfig.Routes"/>.
/// - Forward mode: host-suffix match against <see cref="AppConfig.Targets"/>.
/// </summary>
public sealed class ApiRouter
{
    private readonly AppConfig _config;

    public ApiRouter(AppConfig config) => _config = config;

    /// <summary>Result of matching a reverse-proxy path.</summary>
    public sealed record RouteMatch(ApiRoute Route, string TargetUrl);

    /// <summary>
    /// Match an incoming request path (e.g. "/openai/v1/chat") + query to a route,
    /// producing the rewritten absolute target URL. Longest matching prefix wins;
    /// an empty/"/" prefix acts as a catch-all default.
    /// </summary>
    public RouteMatch? ResolveReverse(string path, string query)
    {
        ApiRoute? best = null;
        int bestLen = -1;

        foreach (var route in _config.Routes)
        {
            if (!route.Enabled || string.IsNullOrWhiteSpace(route.TargetBaseUrl))
                continue;

            var prefix = NormalizePrefix(route.PathPrefix);

            bool matches = prefix.Length == 0 ||
                           path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                           path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);

            if (matches && prefix.Length > bestLen)
            {
                best = route;
                bestLen = prefix.Length;
            }
        }

        if (best is null)
            return null;

        var matchedPrefix = NormalizePrefix(best.PathPrefix);
        var remainder = path.Length >= matchedPrefix.Length
            ? path.Substring(matchedPrefix.Length)
            : "";
        if (remainder.Length > 0 && remainder[0] != '/')
            remainder = "/" + remainder;

        var baseUrl = best.TargetBaseUrl.TrimEnd('/');
        var targetUrl = baseUrl + remainder + query;
        return new RouteMatch(best, targetUrl);
    }

    private static string NormalizePrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix == "/")
            return "";
        prefix = "/" + prefix.Trim().Trim('/');
        return prefix;
    }

    // ---- Forward-proxy host resolution ----

    public ApiTarget? Resolve(string host)
    {
        if (string.IsNullOrEmpty(host))
            return null;

        foreach (var target in _config.Targets)
        {
            if (host.Equals(target.HostSuffix, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + target.HostSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return target;
            }
        }
        return null;
    }

    public string ProviderName(string host) => Resolve(host)?.Name ?? "";
    public bool IsAiHost(string host) => Resolve(host) is not null;
    public bool ShouldDecrypt(string host) => Resolve(host)?.Decrypt ?? false;
}
