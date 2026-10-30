using Microsoft.AspNetCore.HttpOverrides;
using StokvelSync.Web.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false).AddEnvironmentVariables();
builder.Services.AddControllersWithViews();
builder.Services.AddMemoryCache(); builder.Services.AddSingleton<AbuseLimits>();
builder.Services.AddSingleton<Db>(); builder.Services.AddSingleton<StokvelService>(); builder.Services.AddSingleton<Providers>();
builder.Services.AddHttpClient(); builder.Services.AddHostedService<SmsWorker>();
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 32768);
var app = builder.Build();
var db = app.Services.GetRequiredService<Db>();
if (args.Contains("--migrate") || args.Contains("--seed"))
{
    await db.Migrate(app.Environment.ContentRootPath);
    if (args.Contains("--seed"))
    {
        if (!app.Environment.IsDevelopment()) throw new InvalidOperationException("Demo seed is only allowed in Development.");
        await DemoSeed.Run(db, Environment.GetEnvironmentVariable("SEED_DEMO_PASSWORD") ?? "StokvelDemo!2026");
        Console.WriteLine("Demo data created. Treasurer mobile: 0820000001. Password: use SEED_DEMO_PASSWORD or the documented local default.");
    }
    Console.WriteLine("MySQL migrations applied."); return;
}
var config = app.Configuration;
var payment = config["Payments:Provider"]; var sms = config["Sms:Provider"];
if (!new[] { "sandbox", "payfast-sandbox", "payfast-live" }.Contains(payment) || !new[] { "console", "twilio" }.Contains(sms)) throw new InvalidOperationException("Invalid payment or SMS provider.");
if (payment != "sandbox" && new[] { "Payments:MerchantId", "Payments:MerchantKey", "Payments:Passphrase" }.Any(k => string.IsNullOrWhiteSpace(config[k]))) throw new InvalidOperationException("Payfast credentials and passphrase are required.");
if (sms == "twilio" && new[] { "Sms:AccountSid", "Sms:AuthToken", "Sms:FromNumber", "Sms:WebhookUrl" }.Any(k => string.IsNullOrWhiteSpace(config[k]))) throw new InvalidOperationException("Twilio settings are incomplete.");
if (!app.Environment.IsDevelopment() && (!config.GetValue<bool>("App:CookieSecure") || !config["App:PublicUrl"]!.StartsWith("https://", StringComparison.Ordinal))) throw new InvalidOperationException("Production requires HTTPS PublicUrl and CookieSecure=true.");
if (config.GetValue<bool>("App:TrustProxy"))
{
    var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, ForwardLimit = 1 };
    if (config["App:KnownProxy"] is { Length: > 0 } proxy) options.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
    app.UseForwardedHeaders(options);
}
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseMiddleware<ApiMiddleware>();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = ctx => { if (ctx.File.Name == "sw.js") ctx.Context.Response.Headers.CacheControl = "no-cache"; } });
app.MapGet("/api/health", async (Db database) => { await database.Rows("SELECT 1"); return Results.Json(new { status = "ok", database = "mysql", paymentProvider = payment, smsProvider = sms }); });
app.MapControllers();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.Run();
public partial class Program { }
