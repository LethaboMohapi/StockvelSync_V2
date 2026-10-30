using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using StokvelSync.Web.Core;
using System.Text;
using System.Text.Json;

namespace StokvelSync.Web.Controllers;

[ApiController, Route("api/groups")]
public sealed class GroupsController(Db db, StokvelService service, Providers providers, IConfiguration config) : ControllerBase
{
    string U => ((Row)HttpContext.Items["user"]!).Text("id");
    Task<object> Mutate(string g, JsonElement b, Func<MySqlConnection, MySqlTransaction, Task<object>> fn) => service.Mutate(Domain.Uuid(g), U, Domain.Uuid(Request.Headers["Idempotency-Key"].ToString()), Request.Path + "|" + b.GetRawText(), fn);
    [HttpPost] public Task<object> Create(JsonElement b) => service.CreateGroup(U, b);
    [HttpPost("join")]
    public async Task<object> Join(JsonElement b)
    {
        var code = Domain.Str(b, "code", 20, 20);
        return await db.Transaction<object>(async (c, tx) =>
        {
            var g = (await db.Rows("SELECT id FROM stokvel_groups WHERE join_code=? FOR UPDATE", [code], c, tx)).SingleOrDefault() ?? throw new AppException(404, "That invitation code was not found.");
            await db.Exec("INSERT IGNORE INTO memberships(id,group_id,user_id) VALUES (?,?,?)", [Domain.Id(), g["id"], U], c, tx);
            await service.Audit(c, tx, g.Text("id"), U, "member.joined", "Member joined with invitation code"); return new { id = g["id"] };
        });
    }
    [HttpGet("{g}")] public Task<object> Snapshot(string g) => service.Snapshot(Domain.Uuid(g), U);
    [HttpPost("{g}/contributions")] public Task<object> Contribution(string g, JsonElement b) => Mutate(g, b, (c, tx) => service.Contribution(c, tx, g, U, b));
    [HttpPost("{g}/contributions/{id}/review")] public Task<object> Review(string g, string id, JsonElement b) => Mutate(g, b, (c, tx) => service.Review(c, tx, g, U, Domain.Uuid(id), Domain.Str(b, "status", 1, 20)));
    [HttpPost("{g}/rotations")] public Task<object> Schedule(string g, JsonElement b) => Mutate(g, b, (c, tx) => service.Schedule(c, tx, g, U, b));
    [HttpPost("{g}/rotations/{id}/payout")] public Task<object> Payout(string g, string id, JsonElement b) => Mutate(g, b, (c, tx) => service.Payout(c, tx, g, U, Domain.Uuid(id), Domain.Str(b, "reference", 3)));
    [HttpPatch("{g}/settings")]
    public Task<object> Settings(string g, JsonElement b) => Mutate(g, b, async (c, tx) =>
    {
        await service.Member(g, U, c, tx, true);
        await db.Exec("UPDATE stokvel_groups SET name=?,description=?,location=?,contribution_cents=?,due_day=? WHERE id=?", [Domain.Str(b, "name", 2), Domain.Str(b, "description", 0, 500, ""), Domain.Str(b, "location", 2), Domain.Int(b, "contributionCents", 100, 100000000), Domain.Int(b, "dueDay", 1, 28), g], c, tx);
        await service.Audit(c, tx, g, U, "group.updated", "Stokvel settings updated"); return new { ok = true };
    });
    [HttpPost("{g}/invite-code")]
    public Task<object> Invite(string g, JsonElement b) => Mutate(g, b, async (c, tx) =>
    {
        await service.Member(g, U, c, tx, true); var code = Domain.Token(10);
        await db.Exec("UPDATE stokvel_groups SET join_code=? WHERE id=?", [code, g], c, tx); await service.Audit(c, tx, g, U, "invite.rotated", "Invitation code replaced"); return new { code };
    });
    [HttpPost("{g}/reminders")]
    public Task<object> Reminders(string g, JsonElement b) => Mutate(g, b, async (c, tx) =>
    {
        await service.Member(g, U, c, tx, true);
        if ((await db.Rows("SELECT id FROM audit_events WHERE group_id=? AND action='reminders.queued' AND created_at>UTC_TIMESTAMP()-INTERVAL 1 DAY", [g], c, tx)).Count != 0) throw new AppException(429, "Reminders were already prepared in the last 24 hours.");
        var group = (await db.Rows("SELECT name,contribution_cents FROM stokvel_groups WHERE id=?", [g], c, tx)).Single();
        var members = await db.Rows("SELECT u.id,u.language FROM memberships m JOIN users u ON u.id=m.user_id WHERE m.group_id=? AND u.sms_consent=TRUE AND (SELECT COALESCE(SUM(c.amount_cents),0) FROM contributions c WHERE c.group_id=m.group_id AND c.user_id=m.user_id AND c.period=? AND c.status='confirmed')<?", [g, Domain.Period, group["contribution_cents"]], c, tx);
        foreach (var member in members)
        {
            var text = member.Text("language") switch { "zu" => "Isikhathi somnikelo wakho we-stokvel sesifikile", "xh" => "Lifikile ixesha lomnikelo wakho we-stokvel", "af" => "Jou stokvelbydrae is verskuldig", _ => "Your stokvel contribution is due" };
            await providers.QueueSms(c, tx, g, member.Text("id"), group.Text("name") + ": " + text + ". " + Domain.Rand(group.Number("contribution_cents")) + ". Open StokvelSync to check your contributions.");
        }
        await service.Audit(c, tx, g, U, "reminders.queued", members.Count + " contribution reminders prepared"); return new { count = members.Count };
    });
    [HttpPost("{g}/payments")]
    public Task<object> Payment(string g, JsonElement b) => Mutate(g, b, async (c, tx) =>
    {
        var id = Domain.Id(); var amount = Domain.Int(b, "amountCents", 100, 100000000); var period = Domain.PeriodValue(b); var provider = config["Payments:Provider"] ?? "sandbox";
        await db.Exec("INSERT INTO payment_intents(id,group_id,user_id,amount_cents,period,provider) VALUES (?,?,?,?,?,?)", [id, g, U, amount, period, provider], c, tx);
        if (provider == "sandbox") return new { id, provider };
        var user = (await db.Rows("SELECT full_name FROM users WHERE id=?", [U], c, tx)).Single(); var group = (await db.Rows("SELECT name FROM stokvel_groups WHERE id=?", [g], c, tx)).Single();
        return new { id, provider, action = provider == "payfast-live" ? "https://www.payfast.co.za/eng/process" : "https://sandbox.payfast.co.za/eng/process", fields = providers.Checkout(id, amount, user.Text("full_name"), group.Text("name")) };
    });
    [HttpPost("{g}/sms-test")]
    public async Task<object> SmsTest(string g, JsonElement b)
    {
        if (config["Sms:Provider"] != "console") throw new AppException(404, "Use the configured SMS number.");
        await service.Member(g, U); return new { reply = await service.SmsCommand(U, g, Domain.Str(b, "body", 1, 1600), Domain.Uuid(Domain.Str(b, "operationId", 36, 36))) };
    }
    [HttpGet("{g}/export")]
    public async Task<IActionResult> Export(string g)
    {
        await service.Member(Domain.Uuid(g), U);
        var rows = await db.Rows("SELECT l.created_at,u.full_name,l.kind,l.amount_cents,l.reference,l.period FROM ledger_entries l JOIN users u ON u.id=l.user_id WHERE l.group_id=? ORDER BY l.created_at DESC", [g]);
        var lines = new List<string> { "Date,Member,Type,Amount (ZAR),Reference,Period" };
        lines.AddRange(rows.Select(r => string.Join(',', new[] { r.Text("created_at"), r.Text("full_name"), r.Text("kind"), (r.Number("amount_cents") / 100m).ToString("F2", System.Globalization.CultureInfo.InvariantCulture), r.Text("reference"), r.Text("period") }.Select(Domain.Csv))));
        return File(Encoding.UTF8.GetBytes("\uFEFF" + string.Join("\r\n", lines)), "text/csv", "stokvelsync-ledger.csv");
    }
}
