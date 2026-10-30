using Microsoft.Extensions.Caching.Memory;
using MySqlConnector;

namespace StokvelSync.Web.Core;

public sealed class AbuseLimits(IMemoryCache cache)
{
    sealed class Counter { public int Count; }
    public void Check(string key, int max, TimeSpan window)
    {
        var counter = cache.GetOrCreate(key, entry => { entry.AbsoluteExpirationRelativeToNow = window; return new Counter(); })!;
        lock (counter) { if (++counter.Count > max) throw new AppException(429, "Too many requests. Please try again later."); }
    }
}
public sealed class ApiMiddleware(RequestDelegate next)
{
    public async Task Invoke(HttpContext ctx, Db db, IConfiguration config, AbuseLimits limits, ILogger<ApiMiddleware> logger)
    {
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["X-Frame-Options"] = "DENY";
        ctx.Response.Headers["Referrer-Policy"] = "same-origin";
        ctx.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; font-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self' https://www.payfast.co.za https://sandbox.payfast.co.za";
        var path = ctx.Request.Path.Value ?? "";
        try
        {
            if (path.StartsWith("/api/", StringComparison.Ordinal))
            {
                ctx.Response.Headers.CacheControl = "no-store";
                var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                limits.Check("api:" + ip, 240, TimeSpan.FromMinutes(1));
                if (path is "/api/auth/login" or "/api/auth/register" or "/api/me/password") limits.Check("auth:" + ip, 15, TimeSpan.FromMinutes(15));
                var webhook = path.StartsWith("/api/webhooks/", StringComparison.Ordinal);
                if (ctx.Request.Method is not ("GET" or "HEAD" or "OPTIONS") && !webhook)
                {
                    var origin = ctx.Request.Headers.Origin.ToString(); var expected = new Uri(config["App:PublicUrl"]!).GetLeftPart(UriPartial.Authority);
                    if (origin.Length > 0 && origin != expected) throw new AppException(403, "Request origin is not allowed.");
                    if (!ctx.Request.HasJsonContentType()) throw new AppException(415, "Use application/json.");
                    if (ctx.Request.Headers["X-StokvelSync"] != "1") throw new AppException(403, "Missing application request header.");
                }
                var anonymous = webhook || path is "/api/health" or "/api/auth/login" or "/api/auth/register" or "/api/auth/logout";
                if (!anonymous)
                {
                    if (!ctx.Request.Cookies.TryGetValue("stokvel_session", out var token)) throw new AppException(401, "Please sign in.");
                    var user = (await db.Rows("SELECT u.id,u.full_name AS name,u.phone,u.language,u.sms_consent AS smsConsent,u.sms_group_id AS smsGroupId FROM sessions s JOIN users u ON u.id=s.user_id WHERE s.token_hash=? AND s.expires_at>UTC_TIMESTAMP(3)", [Domain.Hash(token)])).SingleOrDefault() ?? throw new AppException(401, "Your session has expired. Please sign in again.");
                    ctx.Items["user"] = user;
                    if (path.EndsWith("/sms-test", StringComparison.Ordinal)) limits.Check("sms-test:" + user.Text("id"), 10, TimeSpan.FromMinutes(15));
                }
            }
            await next(ctx);
        }
        catch (Exception e) when (!ctx.Response.HasStarted)
        {
            var status = e is AppException app ? app.Status : e is MySqlException sql && sql.Number == 1062 ? 409 : e is System.Text.Json.JsonException ? 400 : 500;
            var message = e is AppException ? e.Message : status == 409 ? "This record already exists. Check the phone number or rotation date." : status == 400 ? "Invalid request data." : "The server could not complete this request.";
            if (status == 500) logger.LogError("Request failed: {Type} {Code}", e.GetType().Name, e is MySqlException m ? m.Number : 0);
            ctx.Response.StatusCode = status; await ctx.Response.WriteAsJsonAsync(new { error = message });
        }
    }
}
