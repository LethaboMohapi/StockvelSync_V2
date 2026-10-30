using MySqlConnector;
using System.Text.Json;

namespace StokvelSync.Web.Core;

public sealed class StokvelService(Db db)
{
    public async Task<Row> Member(string g, string u, MySqlConnection? c = null, MySqlTransaction? tx = null, bool treasurer = false)
    {
        var m = (await db.Rows("SELECT * FROM memberships WHERE group_id=? AND user_id=?", [g, u], c, tx)).SingleOrDefault();
        if (m is null || (treasurer && m.Text("role") != "treasurer")) throw new AppException(403, "You do not have permission to do that in this stokvel.");
        return m;
    }
    public Task<int> Audit(MySqlConnection c, MySqlTransaction tx, string g, string u, string action, string detail) => db.Exec("INSERT INTO audit_events(id,group_id,user_id,action,detail) VALUES (?,?,?,?,?)", [Domain.Id(), g, u, action, detail], c, tx);
    public async Task<object> Mutate(string g, string u, string operationId, string payload, Func<MySqlConnection, MySqlTransaction, Task<object>> fn)
    {
        return await db.Transaction<object>(async (c, tx) =>
        {
            if ((await db.Rows("SELECT id FROM stokvel_groups WHERE id=? FOR UPDATE", [g], c, tx)).Count == 0) throw new AppException(404, "Stokvel not found.");
            await Member(g, u, c, tx);
            var hash = Domain.Hash(payload);
            var previous = (await db.Rows("SELECT * FROM sync_operations WHERE id=?", [operationId], c, tx)).SingleOrDefault();
            if (previous is not null)
            {
                if (previous.Text("user_id") != u || previous.Text("group_id") != g || previous.Text("payload_hash") != hash) throw new AppException(409, "This operation reference was already used for different data.");
                return JsonSerializer.Deserialize<JsonElement>(previous.Text("result"));
            }
            var result = await fn(c, tx);
            await db.Exec("INSERT INTO sync_operations(id,user_id,group_id,payload_hash,result) VALUES (?,?,?,?,?)", [operationId, u, g, hash, JsonSerializer.Serialize(result)], c, tx);
            return result;
        });
    }
    public Task<List<Row>> Groups(string u) => db.Rows("SELECT g.id,g.name,g.location,g.contribution_cents AS contributionCents,m.role FROM stokvel_groups g JOIN memberships m ON m.group_id=g.id WHERE m.user_id=? ORDER BY g.created_at", [u]);
    public async Task<object> Snapshot(string g, string u)
    {
        return await db.Transaction<object>(async (c, tx) =>
        {
            var member = await Member(g, u, c, tx);
            var group = (await db.Rows("SELECT id,name,description,location,contribution_cents AS contributionCents,due_day AS dueDay,join_code AS joinCode FROM stokvel_groups WHERE id=?", [g], c, tx)).Single();
            group["role"] = member["role"]; if (member.Text("role") != "treasurer") group.Remove("joinCode");
            var members = await db.Rows("SELECT u.id,u.full_name AS name,u.phone,u.language,u.sms_consent AS smsConsent,m.role,m.joined_at AS joinedAt FROM memberships m JOIN users u ON u.id=m.user_id WHERE m.group_id=? ORDER BY m.joined_at,u.full_name", [g], c, tx);
            if (member.Text("role") != "treasurer") foreach (var m in members) if (m.Text("id") != u) m["phone"] = "+27 ••••• " + m.Text("phone")[^3..];
            var contributions = await db.Rows("SELECT c.id,c.user_id AS userId,u.full_name AS name,c.amount_cents AS amountCents,c.period,c.method,c.reference,c.status,c.created_at AS createdAt FROM contributions c JOIN users u ON u.id=c.user_id WHERE c.group_id=? ORDER BY c.created_at DESC", [g], c, tx);
            var ledger = await db.Rows("SELECT l.id,l.user_id AS userId,u.full_name AS name,l.amount_cents AS amountCents,l.kind,l.reference,l.period,l.created_at AS createdAt FROM ledger_entries l JOIN users u ON u.id=l.user_id WHERE l.group_id=? ORDER BY l.created_at DESC,l.id", [g], c, tx);
            var rotations = await db.Rows("SELECT r.id,r.user_id AS userId,u.full_name AS name,r.payout_date AS date,r.amount_cents AS amountCents,r.status,r.paid_reference AS reference FROM rotations r JOIN users u ON u.id=r.user_id WHERE r.group_id=? ORDER BY r.payout_date", [g], c, tx);
            var messages = await db.Rows("SELECT s.id,s.user_id AS userId,u.full_name AS name,s.direction,s.body,s.status,s.created_at AS createdAt FROM sms_messages s JOIN users u ON u.id=s.user_id WHERE s.group_id=?" + (member.Text("role") == "treasurer" ? "" : " AND s.user_id=?") + " ORDER BY s.created_at DESC LIMIT 100", member.Text("role") == "treasurer" ? [g] : [g, u], c, tx);
            var events = await db.Rows("SELECT a.id,u.full_name AS name,a.action,a.detail,a.created_at AS createdAt FROM audit_events a JOIN users u ON u.id=a.user_id WHERE a.group_id=? ORDER BY a.created_at DESC LIMIT 30", [g], c, tx);
            return new { group, members, contributions, ledger, rotations, messages, events, serverTime = DateTime.UtcNow };
        });
    }
    public async Task<object> CreateGroup(string u, JsonElement b)
    {
        var name = Domain.Str(b, "name", 2); var description = Domain.Str(b, "description", 0, 500, ""); var location = Domain.Str(b, "location", 2);
        var amount = Domain.Int(b, "contributionCents", 100, 100000000); var due = Domain.Int(b, "dueDay", 1, 28);
        return await db.Transaction<object>(async (c, tx) =>
        {
            var id = Domain.Id();
            await db.Exec("INSERT INTO stokvel_groups(id,name,description,location,contribution_cents,due_day,join_code,created_by) VALUES (?,?,?,?,?,?,?,?)", [id, name, description, location, amount, due, Domain.Token(10), u], c, tx);
            await db.Exec("INSERT INTO memberships(id,group_id,user_id,role) VALUES (?,?,?,?)", [Domain.Id(), id, u, "treasurer"], c, tx);
            await Audit(c, tx, id, u, "group.created", "Stokvel created"); return new { id };
        });
    }
    public async Task<object> Contribution(MySqlConnection c, MySqlTransaction tx, string g, string u, JsonElement b)
    {
        var target = b.TryGetProperty("userId", out var v) ? Domain.Uuid(v.GetString() ?? "") : u;
        if (target != u) await Member(g, u, c, tx, true); await Member(g, target, c, tx);
        var amount = Domain.Int(b, "amountCents", 100, 100000000); var period = Domain.PeriodValue(b); var method = Domain.Str(b, "method", 1, 10);
        if (!new[] { "cash", "eft", "sms" }.Contains(method)) throw new AppException(400, "Use cash or EFT for a manual record.");
        var id = Domain.Id(); await db.Exec("INSERT INTO contributions(id,group_id,user_id,amount_cents,period,method,reference) VALUES (?,?,?,?,?,?,?)", [id, g, target, amount, period, method, Domain.Str(b, "reference", 0, 100, "")], c, tx);
        await Audit(c, tx, g, u, "contribution.recorded", "Contribution recorded for review"); return new { id, status = "pending" };
    }
    public async Task<object> Review(MySqlConnection c, MySqlTransaction tx, string g, string u, string id, string status)
    {
        await Member(g, u, c, tx, true);
        if (!new[] { "confirmed", "rejected" }.Contains(status)) throw new AppException(400, "Invalid review status.");
        var item = (await db.Rows("SELECT * FROM contributions WHERE id=? AND group_id=? FOR UPDATE", [id, g], c, tx)).SingleOrDefault() ?? throw new AppException(404, "Contribution not found.");
        if (item.Text("status") != "pending") throw new AppException(409, "This contribution has already been reviewed.");
        await db.Exec("UPDATE contributions SET status=?,reviewed_by=? WHERE id=?", [status, u, id], c, tx);
        if (status == "confirmed") await db.Exec("INSERT INTO ledger_entries(id,group_id,user_id,amount_cents,kind,source_id,reference,period) VALUES (?,?,?,?,?,?,?,?)", [Domain.Id(), g, item["user_id"], item["amount_cents"], "contribution", id, item["reference"], item["period"]], c, tx);
        await Audit(c, tx, g, u, "contribution." + status, "Contribution " + id + " " + status); return new { id, status };
    }
    public async Task<object> Schedule(MySqlConnection c, MySqlTransaction tx, string g, string u, JsonElement b)
    {
        await Member(g, u, c, tx, true);
        if (!b.TryGetProperty("userIds", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is < 1 or > 100) throw new AppException(400, "Choose 1 to 100 members.");
        var ids = list.EnumerateArray().Select(x => Domain.Uuid(x.GetString() ?? "")).ToArray();
        if (ids.Distinct().Count() != ids.Length) throw new AppException(400, "Each member can appear only once in a rotation cycle.");
        foreach (var id in ids) await Member(g, id, c, tx);
        var start = Domain.Str(b, "startDate", 10, 10); var amount = Domain.Int(b, "amountCents", 100, 100000000);
        if (Domain.MonthlyDate(start, 0) < Domain.Today) throw new AppException(400, "The first payout date cannot be in the past.");
        if (!Domain.Bool(b, "replace") && (await db.Rows("SELECT id FROM rotations WHERE group_id=? AND status='planned'", [g], c, tx)).Count != 0) throw new AppException(409, "An upcoming schedule exists. Select replace to regenerate it.");
        await db.Exec("DELETE FROM rotations WHERE group_id=? AND status='planned'", [g], c, tx);
        for (var i = 0; i < ids.Length; i++) await db.Exec("INSERT INTO rotations(id,group_id,user_id,payout_date,amount_cents) VALUES (?,?,?,?,?)", [Domain.Id(), g, ids[i], Domain.MonthlyDate(start, i).ToString("yyyy-MM-dd"), amount], c, tx);
        await Audit(c, tx, g, u, "rotation.scheduled", ids.Length + " monthly payouts scheduled"); return new { count = ids.Length };
    }
    public async Task<object> Payout(MySqlConnection c, MySqlTransaction tx, string g, string u, string id, string reference)
    {
        await Member(g, u, c, tx, true);
        var r = (await db.Rows("SELECT * FROM rotations WHERE id=? AND group_id=? FOR UPDATE", [id, g], c, tx)).SingleOrDefault() ?? throw new AppException(404, "Payout not found.");
        if (r.Text("status") == "paid") throw new AppException(409, "This payout was already recorded.");
        if (DateOnly.Parse(r.Text("payout_date")) > Domain.Today) throw new AppException(409, "Record a payout on or after its scheduled date.");
        var balance = (await db.Rows("SELECT COALESCE(SUM(amount_cents),0) AS total FROM ledger_entries WHERE group_id=?", [g], c, tx)).Single().Number("total");
        if (balance < r.Number("amount_cents")) throw new AppException(409, "The confirmed balance is too low for this payout.");
        await db.Exec("UPDATE rotations SET status='paid',paid_reference=? WHERE id=?", [reference, id], c, tx);
        await db.Exec("INSERT INTO ledger_entries(id,group_id,user_id,amount_cents,kind,source_id,reference,period) VALUES (?,?,?,?,?,?,?,?)", [Domain.Id(), g, r["user_id"], -r.Number("amount_cents"), "payout", id, reference, r.Text("payout_date")[..7]], c, tx);
        await Audit(c, tx, g, u, "payout.recorded", "External transfer recorded: " + reference); return new { id, status = "paid" };
    }
    public async Task<object> CompletePayment(string id, string providerReference)
    {
        return await db.Transaction<object>(async (c, tx) =>
        {
            var first = (await db.Rows("SELECT group_id FROM payment_intents WHERE id=?", [id], c, tx)).SingleOrDefault() ?? throw new AppException(404, "Payment not found.");
            await db.Rows("SELECT id FROM stokvel_groups WHERE id=? FOR UPDATE", [first["group_id"]], c, tx);
            var p = (await db.Rows("SELECT * FROM payment_intents WHERE id=? FOR UPDATE", [id], c, tx)).Single();
            if (p.Text("status") == "complete") { if (p.Text("provider_reference") != providerReference) throw new AppException(409, "Payment reference mismatch."); return new { id, status = "complete" }; }
            if (p.Text("status") != "pending") throw new AppException(409, "Payment is no longer pending.");
            await db.Exec("UPDATE payment_intents SET status='complete',provider_reference=? WHERE id=?", [providerReference, id], c, tx);
            await db.Exec("INSERT INTO contributions(id,group_id,user_id,amount_cents,period,method,reference,status) VALUES (?,?,?,?,?,'mobile',?,'confirmed')", [id, p["group_id"], p["user_id"], p["amount_cents"], p["period"], providerReference], c, tx);
            await db.Exec("INSERT INTO ledger_entries(id,group_id,user_id,amount_cents,kind,source_id,reference,period) VALUES (?,?,?,?,?,?,?,?)", [Domain.Id(), p["group_id"], p["user_id"], p["amount_cents"], "contribution", id, providerReference, p["period"]], c, tx);
            await Audit(c, tx, p.Text("group_id"), p.Text("user_id"), "payment.confirmed", p.Text("provider") + " confirmation received"); return new { id, status = "complete" };
        });
    }
    public async Task<string> SmsCommand(string u, string g, string body, string operationId)
    {
        var p = Domain.ParseSms(body);
        if (p.Command == "HELP") return "StokvelSync: BAL <PIN>, NEXT <PIN>, PAY <PIN> <rand amount>. PAY records a pending claim; it does not transfer money.";
        var user = (await db.Rows("SELECT sms_pin_hash,language FROM users WHERE id=?", [u])).Single();
        if (!Domain.VerifyPassword(p.Pin!, user.Text("sms_pin_hash"))) throw new AppException(403, "Incorrect SMS PIN. Reset it in StokvelSync settings.");
        await Member(g, u); var group = (await db.Rows("SELECT name FROM stokvel_groups WHERE id=?", [g])).Single();
        if (p.Command == "BAL")
        {
            var total = (await db.Rows("SELECT COALESCE(SUM(amount_cents),0) AS total FROM ledger_entries WHERE group_id=?", [g])).Single().Number("total");
            var label = user.Text("language") switch { "zu" => "Ibhalansi eqinisekisiwe", "xh" => "Ibhalansi eqinisekisiweyo", "af" => "Bevestigde saldo", _ => "Confirmed balance" };
            return group.Text("name") + ": " + label + " " + Domain.Rand(total);
        }
        if (p.Command == "NEXT")
        {
            var r = (await db.Rows("SELECT r.payout_date,u.full_name FROM rotations r JOIN users u ON u.id=r.user_id WHERE r.group_id=? AND r.status='planned' ORDER BY r.payout_date LIMIT 1", [g])).SingleOrDefault();
            return r is null ? "No upcoming payout scheduled." : group.Text("name") + ": " + r.Text("full_name") + ", " + r.Text("payout_date");
        }
        var b = JsonSerializer.SerializeToElement(new { amountCents = p.Amount, period = Domain.Period, method = "sms", reference = "SMS contribution claim" });
        await Mutate(g, u, operationId, b.GetRawText(), (c, tx) => Contribution(c, tx, g, u, b));
        return group.Text("name") + ": " + Domain.Rand(p.Amount) + " recorded for review. No money transferred.";
    }
}
