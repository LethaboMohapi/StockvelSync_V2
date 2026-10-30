using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using StokvelSync.Web.Core;
using System.Text.Json;
using Twilio.Security;

namespace StokvelSync.Web.Controllers;

[ApiController, Route("api")]
public sealed class WebhooksController(Db db, StokvelService service, Providers providers, IConfiguration config, AbuseLimits limits) : ControllerBase
{
    [HttpPost("payments/{id}/sandbox-confirm")]
    public async Task<object> Confirm(string id)
    {
        if (config["Payments:Provider"] != "sandbox") throw new AppException(404, "Local sandbox is disabled.");
        var u = ((Row)HttpContext.Items["user"]!).Text("id"); id = Domain.Uuid(id);
        if ((await db.Rows("SELECT id FROM payment_intents WHERE id=? AND user_id=? AND provider='sandbox'", [id, u])).Count == 0) throw new AppException(404, "Sandbox payment not found.");
        return await service.CompletePayment(id, "DEMO-" + id[..8]);
    }
    [HttpPost("webhooks/payfast")]
    public async Task<IActionResult> Payfast()
    {
        var p = await providers.ValidatePayfast(Request); var form = await Request.ReadFormAsync();
        if (form["payment_status"] == "COMPLETE")
        {
            var reference = form["pf_payment_id"].ToString(); if (reference.Length is < 1 or > 100) throw new AppException(400, "Invalid payment reference.");
            await service.CompletePayment(p.Text("id"), reference);
        }
        return Content("OK");
    }
    [HttpPost("webhooks/sms")]
    public async Task<IActionResult> Sms()
    {
        if (config["Sms:Provider"] != "twilio") throw new AppException(404, "SMS gateway not enabled.");
        var form = await Request.ReadFormAsync(); var fields = form.ToDictionary(p => p.Key, p => p.Value.ToString());
        if (!new RequestValidator(config["Sms:AuthToken"]).Validate(config["Sms:WebhookUrl"], fields, Request.Headers["X-Twilio-Signature"].ToString())) throw new AppException(403, "Invalid SMS signature.");
        var phone = Domain.Phone(fields.GetValueOrDefault("From", "")); var sid = fields.GetValueOrDefault("MessageSid", ""); var body = fields.GetValueOrDefault("Body", "");
        if (sid.Length is < 10 or > 100 || body.Length > 1600) throw new AppException(400, "Invalid SMS request.");
        var previous = (await db.Rows("SELECT response FROM inbound_receipts WHERE sid=?", [sid])).SingleOrDefault();
        if (previous is not null) { if (previous["response"] is null) throw new AppException(503, "Message is still processing."); return Content(previous.Text("response"), "text/xml"); }
        limits.Check("sms:" + phone, 10, TimeSpan.FromMinutes(15));
        try { await db.Exec("INSERT INTO inbound_receipts(sid) VALUES (?)", [sid]); } catch (MySqlException e) when (e.Number == 1062) { throw new AppException(503, "Message is still processing."); }
        string reply;
        try
        {
            var u = (await db.Rows("SELECT id,sms_group_id FROM users WHERE phone=?", [phone])).SingleOrDefault();
            reply = u is not null && u.Text("sms_group_id").Length > 0 ? await service.SmsCommand(u.Text("id"), u.Text("sms_group_id"), body, Domain.Id()) : "Set your SMS PIN and default stokvel in StokvelSync settings first.";
        }
        catch (AppException e) { reply = e.Message; }
        // An unexpected failure leaves a receipt for manual reconciliation instead of risking a duplicate claim.
        var twiml = new Twilio.TwiML.MessagingResponse(); twiml.Message(reply); var xml = twiml.ToString();
        await db.Exec("UPDATE inbound_receipts SET response=? WHERE sid=?", [xml, sid]); return Content(xml, "text/xml");
    }
}
