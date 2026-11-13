using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace AGLPTZ;

internal static class RemotePairingHttp
{
    internal const string CookieName = "agl_ptz_device";
    private const int MaxPairBytes = 1024;
    private static readonly SemaphoreSlim FailureDelays = new(4, 4);

    internal static async Task PairAsync(HttpContext context, RemotePairing pairing, IPAddress address, string origin)
    {
        var request = context.Request;
        context.Response.Headers.CacheControl = "no-store";
        if (!string.Equals(request.Headers.Origin.ToString(), origin, StringComparison.Ordinal) ||
            request.Headers.TryGetValue("Sec-Fetch-Site", out var site) && site.ToString() != "same-origin")
        {
            await Reply(context, 403, new { ok = false });
            return;
        }
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var media) ||
            !string.Equals(media.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            await Reply(context, 415, new { ok = false });
            return;
        }
        if (request.ContentLength > MaxPairBytes)
        {
            await Reply(context, 413, new { ok = false });
            return;
        }
        var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = MaxPairBytes;

        PairingResult result;
        try
        {
            using var payload = await JsonDocument.ParseAsync(request.Body, cancellationToken: context.RequestAborted);
            if (payload.RootElement.ValueKind != JsonValueKind.Object)
            {
                await Reply(context, 400, new { ok = false });
                return;
            }
            result = pairing.Pair(address, ReadString(payload.RootElement, "code") ?? "",
                ReadString(payload.RootElement, "name"));
        }
        catch (BadHttpRequestException error)
        {
            await Reply(context, error.StatusCode, new { ok = false });
            return;
        }
        catch (JsonException)
        {
            await Reply(context, 400, new { ok = false });
            return;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            await Reply(context, 503, new { ok = false });
            return;
        }

        if (result.RetryAfter > 0) { await RateLimited(context, result.RetryAfter); return; }
        if (result.Full) { await Reply(context, 403, new { ok = false, reason = "device_limit" }); return; }
        if (result.Token == null)
        {
            if (FailureDelays.Wait(0))
            {
                try { await Task.Delay(300, context.RequestAborted); }
                finally { FailureDelays.Release(); }
            }
            await Reply(context, 403, new { ok = false });
            return;
        }
        context.Response.Cookies.Append(CookieName, result.Token, new CookieOptions
        {
            HttpOnly = true, SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict, Path = "/",
            MaxAge = TimeSpan.FromDays(365),
        });
        await Reply(context, 200, new { ok = true, name = result.Name });
    }

    internal static async Task SessionAsync(HttpContext context, RemotePairing pairing, IPAddress address)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.Cookies.TryGetValue(CookieName, out var token))
        {
            await Reply(context, 401, new { ok = false, reason = "unpaired" });
            return;
        }
        PairingResult result;
        try { result = pairing.Session(address, token); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            await Reply(context, 503, new { ok = false });
            return;
        }
        if (result.RetryAfter > 0) await RateLimited(context, result.RetryAfter);
        else if (result.Name == null) await Reply(context, 401, new { ok = false, reason = "revoked" });
        else await Reply(context, 200, new { ok = true, name = result.Name });
    }

    private static string? ReadString(JsonElement json, string key) =>
        json.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    private static Task RateLimited(HttpContext context, int seconds)
    {
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return Reply(context, 429, new { ok = false, reason = "rate_limited" });
    }

    private static Task Reply<T>(HttpContext context, int status, T body)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(body, context.RequestAborted);
    }
}
