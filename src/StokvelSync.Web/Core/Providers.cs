using MySqlConnector;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace StokvelSync.Web.Core;

public sealed class Providers(Db db, IConfiguration config, IHttpClientFactory clients)
{
    public static string Encode(string value)
    {
        var s = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value.Trim()))
            if ((b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9') || b is (byte)'-' or (byte)'_' or (byte)'.') s.Append((char)b);
            else if (b == ' ') s.Append('+'); else s.Append('%').Append(b.ToString("X2"));
        return s.ToString();
    }
    public static string ParameterString(IDictionary<string, string> fields) => string.Join('&', fields.Where(p => p.Key != "signature" && p.Value != "").Select(p => p.Key + "=" + Encode(p.Value)));
    public static string Signature(IDictionary<string, string> fields, string passphrase)
    {
        var text = ParameterString(fields) + (passphrase.Length == 0 ? "" : "&passphrase=" + Encode(passphrase));
        // MD5 is required by Payfast's wire protocol; passwords use PBKDF2.
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));
    }
    public Dictionary<string, string> Checkout(string id, int cents, string name, string group)
    {
        var url = config["App:PublicUrl"]!.TrimEnd('/'); var names = name.Split(' ', 2);
        var fields = new Dictionary<string, string>
        {
            ["merchant_id"] = config["Payments:MerchantId"]!, ["merchant_key"] = config["Payments:MerchantKey"]!,
            ["return_url"] = url + "/?payment=returned", ["cancel_url"] = url + "/?payment=cancelled", ["notify_url"] = url + "/api/webhooks/payfast",
            ["name_first"] = names[0], ["name_last"] = names.Length == 2 ? names[1] : names[0], ["m_payment_id"] = id,
            ["amount"] = (cents / 100m).ToString("F2", System.Globalization.CultureInfo.InvariantCulture), ["item_name"] = group[..Math.Min(60, group.Length)] + " contribution"
        };
        fields["signature"] = Signature(fields, config["Payments:Passphrase"]!); return fields;
    }
    public async Task<Row> ValidatePayfast(HttpRequest request)
    {
        var provider = config["Payments:Provider"];
        if (provider == "sandbox") throw new AppException(404, "Gateway not enabled.");
        var fields = (await request.ReadFormAsync()).ToDictionary(p => p.Key, p => p.Value.ToString());
        var expected = Signature(fields, config["Payments:Passphrase"]!);
        if (!fields.TryGetValue("signature", out var received) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(received))) throw new AppException(400, "Invalid payment signature.");
        if (fields.GetValueOrDefault("merchant_id") != config["Payments:MerchantId"]) throw new AppException(400, "Merchant mismatch.");
        var hosts = provider == "payfast-live" ? new[] { "www.payfast.co.za", "w1w.payfast.co.za", "w2w.payfast.co.za" } : ["sandbox.payfast.co.za"];
        var ips = (await Task.WhenAll(hosts.Select(Dns.GetHostAddressesAsync))).SelectMany(x => x);
        var source = request.HttpContext.Connection.RemoteIpAddress;
        if (source is null || !ips.Any(x => x.MapToIPv6().Equals(source.MapToIPv6()))) throw new AppException(400, "Invalid payment source. Check trusted proxy settings.");
        var client = clients.CreateClient(); client.Timeout = TimeSpan.FromSeconds(15);
        using var response = await client.PostAsync((provider == "payfast-live" ? "https://www.payfast.co.za" : "https://sandbox.payfast.co.za") + "/eng/query/validate", new StringContent(ParameterString(fields), Encoding.UTF8, "application/x-www-form-urlencoded"));
        if (!response.IsSuccessStatusCode || (await response.Content.ReadAsStringAsync()).Trim() != "VALID") throw new AppException(400, "Payment data could not be validated.");
        var intent = (await db.Rows("SELECT * FROM payment_intents WHERE id=?", [fields.GetValueOrDefault("m_payment_id", "")])).SingleOrDefault();
        if (intent is null || intent.Text("provider") != provider || Domain.Cents(fields.GetValueOrDefault("amount_gross", "0")) != intent.Number("amount_cents")) throw new AppException(400, "Payment amount or reference mismatch.");
        return intent;
    }
    public Task<int> QueueSms(MySqlConnection c, MySqlTransaction tx, string g, string u, string body) => db.Exec("INSERT INTO sms_messages(id,group_id,user_id,direction,body,status) VALUES (?,?,?,'outbound',?,?)", [Domain.Id(), g, u, body, config["Sms:Provider"] == "console" ? "simulated" : "queued"], c, tx);
    public async Task DeliverSms(CancellationToken ct)
    {
        if (config["Sms:Provider"] != "twilio") return;
        var messages = await db.Transaction(async (c, tx) =>
        {
            var rows = await db.Rows("SELECT s.id,s.body,u.phone FROM sms_messages s JOIN users u ON u.id=s.user_id WHERE s.direction='outbound' AND s.status='queued' ORDER BY s.created_at LIMIT 10 FOR UPDATE SKIP LOCKED", c: c, tx: tx);
            foreach (var row in rows) await db.Exec("UPDATE sms_messages SET status='sending' WHERE id=?", [row["id"]], c, tx); return rows;
        });
        var client = clients.CreateClient(); client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(config["Sms:AccountSid"] + ":" + config["Sms:AuthToken"])));
        foreach (var m in messages)
        {
            try
            {
                using var response = await client.PostAsync("https://api.twilio.com/2010-04-01/Accounts/" + config["Sms:AccountSid"] + "/Messages.json", new FormUrlEncodedContent(new Dictionary<string, string> { ["From"] = config["Sms:FromNumber"]!, ["To"] = m.Text("phone"), ["Body"] = m.Text("body") }), ct);
                response.EnsureSuccessStatusCode();
                using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                await db.Exec("UPDATE sms_messages SET status='sent',provider_sid=? WHERE id=?", [json.RootElement.GetProperty("sid").GetString(), m["id"]]);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { await db.Exec("UPDATE sms_messages SET status='failed' WHERE id=?", [m["id"]]); }
        }
    }
}
public sealed class SmsWorker(Providers providers, Db db, ILogger<SmsWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try { while (await timer.WaitForNextTickAsync(stoppingToken)) { try { await providers.DeliverSms(stoppingToken); await db.Exec("DELETE FROM sessions WHERE expires_at<UTC_TIMESTAMP()"); } catch (Exception e) { logger.LogWarning("Background task failed: {Type}", e.GetType().Name); } } }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
