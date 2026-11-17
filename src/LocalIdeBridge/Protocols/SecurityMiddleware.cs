using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using LocalIdeBridge.Core;

namespace LocalIdeBridge.Protocols;

/// <summary>
/// Checks authentication and request limits before a request reaches a tool.
/// Remote connections require HTTPS when configured; browser requests are also
/// checked against the allowed origins. Access tokens are never written to logs.
/// </summary>
public sealed class SecurityMiddleware
{
    private sealed class WindowCounter
    {
        public long WindowMinute;
        public int Count;
    }

    private readonly RequestDelegate _next;
    private readonly AppConfig _config;
    private readonly CredentialStore _credentials;
    private readonly RedactingLogger _logger;
    private readonly ConcurrentDictionary<string, WindowCounter> _reqCounters = new();
    private readonly ConcurrentDictionary<string, WindowCounter> _failCounters = new();
    private long _lastCleanupMinute;

    public SecurityMiddleware(
        RequestDelegate next,
        AppConfig config,
        CredentialStore credentials,
        RedactingLogger logger)
    {
        _next = next;
        _config = config;
        _credentials = credentials;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var remoteIp = context.Connection.RemoteIpAddress;
        var isLoopback = remoteIp is null || IPAddress.IsLoopback(remoteIp);
        var ipKey = remoteIp?.ToString() ?? "local";
        var nowMinute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        MaybePruneCounters(nowMinute);

        // Minimal security response headers.
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Cache-Control"] = "no-store";

        // Enforce HTTPS on non-loopback direct remote connections.
        if (!isLoopback && _config.Security.RequireHttpsForRemote && !context.Request.IsHttps)
        {
            await WriteJsonError(context, StatusCodes.Status400BadRequest, "https_required",
                "HTTPS is required for non-loopback remote requests.");
            return;
        }

        // Defend against browser DNS-rebinding attacks when an Origin header is supplied.
        if (context.Request.Headers.TryGetValue("Origin", out var originValues))
        {
            var origin = originValues.ToString();
            if (!string.IsNullOrWhiteSpace(origin) && !IsAllowedOrigin(origin))
            {
                _logger.Warn("security", $"Rejected disallowed Origin '{origin}' from {ipKey}.");
                await WriteJsonError(context, StatusCodes.Status403Forbidden, "forbidden_origin",
                    "Request Origin is not authorized in security.allowedOrigins.");
                return;
            }
        }

        // Enforce the configured cap even on chunked uploads with no Content-Length.
        // The length header alone is not a memory limit; Kestrel must stop reads.
        var maxBytes = _config.Security.MaxRequestBodyBytes;
        var requestSize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (requestSize is { IsReadOnly: false })
            requestSize.MaxRequestBodySize = maxBytes;
        if (context.Request.ContentLength.HasValue && context.Request.ContentLength.Value > maxBytes)
        {
            await WriteJsonError(context, StatusCodes.Status413PayloadTooLarge, "payload_too_large",
                $"Request body exceeds configured limit of {maxBytes} bytes.");
            return;
        }

        // Unauthenticated liveness is tiny and exposes no sensitive metadata.
        var path = context.Request.Path.Value ?? "/";
        if (path == "/health" && HttpMethods.IsGet(context.Request.Method))
        {
            await _next(context);
            return;
        }

        // Always verify a valid key BEFORE reading the failed-auth counter.
        // A public scanner sharing the loopback tunnel IP must not lock out the owner.
        var token = ExtractToken(context.Request);
        if (!_credentials.Verify(token))
        {
            var failures = IncrementCounter(_failCounters, ipKey, nowMinute);
            if (failures > _config.Security.FailedAuthPerMinute ||
                IncrementCounter(_reqCounters, ipKey, nowMinute) > _config.Security.RequestsPerMinute)
            {
                context.Response.Headers["Retry-After"] = "60";
                await WriteJsonError(context, StatusCodes.Status429TooManyRequests,
                    "auth_rate_limited", "Too many rejected requests. Wait and retry.");
                return;
            }
            _logger.Warn("auth", $"Rejected unauthorized {context.Request.Method} {path} from {ipKey} (failures={failures}).");
            context.Response.Headers["WWW-Authenticate"] = "Bearer realm=\"LocalIdeBridge\"";
            await WriteJsonError(context, StatusCodes.Status401Unauthorized, "unauthorized",
                "Valid access key required via Authorization: Bearer or X-LocalIdeBridge-Key.");
            return;
        }

        try
        {
            await _next(context);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            if (!context.Response.HasStarted)
            {
                await WriteJsonError(context, StatusCodes.Status413PayloadTooLarge, "payload_too_large",
                    $"Request body exceeds configured limit of {maxBytes} bytes.");
            }
        }
    }

    private static string? ExtractToken(HttpRequest request)
    {
        if (request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            var raw = authHeader.ToString();
            if (raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return raw["Bearer ".Length..].Trim();
        }
        if (request.Headers.TryGetValue("X-LocalIdeBridge-Key", out var keyHeader))
        {
            return keyHeader.ToString().Trim();
        }
        return null;
    }

    private bool IsAllowedOrigin(string origin)
    {
        if (_config.Security.AllowedOrigins.Any(a => string.Equals(a, origin, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return false;

        // Require the exact origin authority, including scheme and port. Merely matching
        // a host name accidentally trusts unrelated websites on another port/protocol.
        if (!string.IsNullOrWhiteSpace(_config.Connection.PublicEndpoint) &&
            Uri.TryCreate(_config.Connection.PublicEndpoint, UriKind.Absolute, out var pub) &&
            string.Equals(uri.Scheme, pub.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(uri.Host, pub.Host, StringComparison.OrdinalIgnoreCase) &&
            uri.Port == pub.Port)
        {
            return true;
        }

        return (uri.Host is "127.0.0.1" or "::1" or "localhost") &&
               uri.Port == _config.Connection.Port &&
               string.Equals(uri.Scheme, _config.Connection.UseHttps ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static int IncrementCounter(ConcurrentDictionary<string, WindowCounter> dict, string key, long minute)
    {
        // Limit the key space even under distributed brute-force traffic. A full
        // table must not allocate indefinitely just because scanners rotate IPs.
        if (!dict.ContainsKey(key) && dict.Count >= 4096)
            return int.MaxValue;
        var counter = dict.GetOrAdd(key, _ => new WindowCounter { WindowMinute = minute });
        lock (counter)
        {
            if (counter.WindowMinute != minute)
            {
                counter.WindowMinute = minute;
                counter.Count = 0;
            }
            return ++counter.Count;
        }
    }

    private static int GetCount(ConcurrentDictionary<string, WindowCounter> dict, string key, long minute)
    {
        if (!dict.TryGetValue(key, out var counter)) return 0;
        lock (counter)
        {
            return counter.WindowMinute == minute ? counter.Count : 0;
        }
    }

    private void MaybePruneCounters(long currentMinute)
    {
        var prev = Interlocked.Read(ref _lastCleanupMinute);
        if (currentMinute - prev < 2) return;
        if (Interlocked.CompareExchange(ref _lastCleanupMinute, currentMinute, prev) != prev) return;

        // Remove inactive IP entries; the size still depends on recent request traffic.
        foreach (var kv in _reqCounters)
            if (currentMinute - kv.Value.WindowMinute >= 2) _reqCounters.TryRemove(kv.Key, out _);
        foreach (var kv in _failCounters)
            if (currentMinute - kv.Value.WindowMinute >= 2) _failCounters.TryRemove(kv.Key, out _);
    }

    private static async Task WriteJsonError(HttpContext ctx, int statusCode, string code, string message)
    {
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsJsonAsync(new { ok = false, error = new { code, message } });
    }
}
