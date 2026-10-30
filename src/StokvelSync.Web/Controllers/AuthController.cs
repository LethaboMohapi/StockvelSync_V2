using Microsoft.AspNetCore.Mvc;
using StokvelSync.Web.Core;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StokvelSync.Web.Controllers;

[ApiController, Route("api")]
public sealed class AuthController(Db db, StokvelService service, IConfiguration config) : ControllerBase
{
    Row UserRow => (Row)HttpContext.Items["user"]!;
    async Task SignIn(string userId)
    {
        var token = Domain.Token();
        await db.Exec("INSERT INTO sessions(token_hash,user_id,expires_at) VALUES (?,?,?)", [Domain.Hash(token), userId, DateTime.UtcNow.AddDays(7)]);
        Response.Cookies.Append("stokvel_session", token, new CookieOptions { HttpOnly = true, Secure = config.GetValue<bool>("App:CookieSecure"), SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromDays(7) });
    }
    [HttpPost("auth/register")]
    public async Task<object> Register(JsonElement b)
    {
        var id = Domain.Id(); var name = Domain.Str(b, "name", 2); var phone = Domain.Phone(Domain.Str(b, "phone", 10, 25)); var password = Domain.Str(b, "password", 12, 128);
        await db.Exec("INSERT INTO users(id,full_name,phone,password_hash) VALUES (?,?,?,?)", [id, name, phone, Domain.PasswordHash(password)]);
        await SignIn(id); Response.StatusCode = 201; return new { id };
    }
    [HttpPost("auth/login")]
    public async Task<object> Login(JsonElement b)
    {
        var phone = Domain.Phone(Domain.Str(b, "phone", 10, 25)); var password = Domain.Str(b, "password", 1, 128);
        var u = (await db.Rows("SELECT * FROM users WHERE phone=?", [phone])).SingleOrDefault();
        var hash = u?.Text("password_hash") ?? "pbkdf2:210000:AAAAAAAAAAAAAAAAAAAAAA==:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
        if (!Domain.VerifyPassword(password, hash) || u is null) throw new AppException(401, "Mobile number or password is incorrect.");
        await SignIn(u.Text("id")); return new { id = u["id"] };
    }
    [HttpPost("auth/logout")]
    public async Task<object> Logout()
    {
        if (Request.Cookies.TryGetValue("stokvel_session", out var token)) await db.Exec("DELETE FROM sessions WHERE token_hash=?", [Domain.Hash(token)]);
        Response.Cookies.Delete("stokvel_session", new CookieOptions { Path = "/", Secure = config.GetValue<bool>("App:CookieSecure"), SameSite = SameSiteMode.Strict }); return new { ok = true };
    }
    [HttpGet("me")]
    public async Task<object> Me() => new { user = UserRow, groups = await service.Groups(UserRow.Text("id")), providers = new { payment = config["Payments:Provider"], sms = config["Sms:Provider"] } };
    [HttpPatch("me")]
    public async Task<object> Profile(JsonElement b)
    {
        var language = Domain.Str(b, "language", 2, 2); if (!new[] { "en", "zu", "xh", "af" }.Contains(language)) throw new AppException(400, "Unsupported language.");
        var g = b.TryGetProperty("smsGroupId", out var v) && v.ValueKind == JsonValueKind.String ? Domain.Uuid(v.GetString()!) : null;
        if (g is not null) await service.Member(g, UserRow.Text("id"));
        string? pin = b.TryGetProperty("pin", out _) ? Domain.Str(b, "pin", 6, 6) : null;
        if (pin is not null && !Regex.IsMatch(pin, @"^\d{6}$")) throw new AppException(400, "Use a six-digit SMS PIN.");
        await db.Transaction(async (c, tx) =>
        {
            await db.Exec("UPDATE users SET language=?,sms_consent=?,sms_group_id=? WHERE id=?", [language, Domain.Bool(b, "smsConsent"), g, UserRow["id"]], c, tx);
            if (pin is not null) await db.Exec("UPDATE users SET sms_pin_hash=? WHERE id=?", [Domain.PasswordHash(pin), UserRow["id"]], c, tx);
            return true;
        });
        return new { ok = true };
    }
    [HttpPost("me/password")]
    public async Task<object> Password(JsonElement b)
    {
        var current = Domain.Str(b, "currentPassword", 1, 128); var password = Domain.Str(b, "password", 12, 128);
        var u = (await db.Rows("SELECT password_hash FROM users WHERE id=?", [UserRow["id"]])).Single();
        if (!Domain.VerifyPassword(current, u.Text("password_hash"))) throw new AppException(403, "Current password is incorrect.");
        await db.Transaction(async (c, tx) => { await db.Exec("UPDATE users SET password_hash=? WHERE id=?", [Domain.PasswordHash(password), UserRow["id"]], c, tx); await db.Exec("DELETE FROM sessions WHERE user_id=?", [UserRow["id"]], c, tx); return true; });
        await SignIn(UserRow.Text("id")); return new { ok = true };
    }
}
