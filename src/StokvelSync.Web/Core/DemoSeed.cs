namespace StokvelSync.Web.Core;

public static class DemoSeed
{
    public static async Task Run(Db db, string password)
    {
        if (password.Length < 12) throw new AppException(400, "Demo password must have at least 12 characters.");
        if ((await db.Rows("SELECT id FROM users LIMIT 1")).Count > 0) throw new AppException(409, "Seed is only allowed in an empty database.");
        await db.Transaction(async (c, tx) =>
        {
            string[] names = ["Nomsa Dlamini", "Thabo Molefe", "Lerato Nkosi", "Sipho Mthembu", "Ayanda Ndlovu", "Pieter van Wyk", "Zanele Khumalo", "Bongani Mokoena"];
            string[] langs = ["en", "zu", "en", "xh", "zu", "af", "zu", "xh"];
            var ids = names.Select(_ => Domain.Id()).ToArray(); var group = Domain.Id(); var hash = Domain.PasswordHash(password); var now = Domain.Today;
            for (var i = 0; i < names.Length; i++) await db.Exec("INSERT INTO users(id,full_name,phone,password_hash,language,sms_consent) VALUES (?,?,?,?,?,TRUE)", [ids[i], names[i], "+2782000000" + (i + 1), hash, langs[i]], c, tx);
            await db.Exec("INSERT INTO stokvel_groups(id,name,description,location,contribution_cents,due_day,join_code,created_by) VALUES (?,?,?,?,?,?,?,?)", [group, "Ubuntu Savings Circle", "Building brighter futures, one contribution at a time.", "Soweto, Gauteng", 50000, 5, Domain.Token(10), ids[0]], c, tx);
            for (var i = 0; i < ids.Length; i++) await db.Exec("INSERT INTO memberships(id,group_id,user_id,role,joined_at) VALUES (?,?,?,?,?)", [Domain.Id(), group, ids[i], i == 0 ? "treasurer" : "member", now.AddMonths(-7).ToDateTime(TimeOnly.MinValue).AddMinutes(i)], c, tx);
            for (var month = -6; month <= 0; month++) for (var i = 0; i < (month == 0 ? 6 : 8); i++)
            {
                var date = new DateOnly(now.Year, now.Month, Math.Min(now.Day, 5)).AddMonths(month); var period = date.ToString("yyyy-MM"); var id = Domain.Id(); var reference = "UBU-" + period.Replace("-", "") + "-" + (i + 1); var created = date.ToDateTime(new TimeOnly(8 + i, 15));
                await db.Exec("INSERT INTO contributions(id,group_id,user_id,amount_cents,period,method,reference,status,created_at) VALUES (?,?,?,?,?,?,?,'confirmed',?)", [id, group, ids[i], 50000, period, i % 2 == 0 ? "mobile" : "eft", reference, created], c, tx);
                await db.Exec("INSERT INTO ledger_entries(id,group_id,user_id,amount_cents,kind,source_id,reference,period,created_at) VALUES (?,?,?,?,?,?,?,?,?)", [Domain.Id(), group, ids[i], 50000, "contribution", id, reference, period, created], c, tx);
            }
            await db.Exec("INSERT INTO contributions(id,group_id,user_id,amount_cents,period,method,reference) VALUES (?,?,?,?,?,'cash',?)", [Domain.Id(), group, ids[6], 50000, Domain.Period, "Meeting contribution"], c, tx);
            for (var i = 0; i < 3; i++)
            {
                var id = Domain.Id(); var date = new DateOnly(now.Year, now.Month, 5).AddMonths(i - 3); var reference = "PAYOUT-" + date.ToString("yyyyMM");
                await db.Exec("INSERT INTO rotations(id,group_id,user_id,payout_date,amount_cents,status,paid_reference) VALUES (?,?,?,?,?,'paid',?)", [id, group, ids[i], date.ToString("yyyy-MM-dd"), 400000, reference], c, tx);
                await db.Exec("INSERT INTO ledger_entries(id,group_id,user_id,amount_cents,kind,source_id,reference,period,created_at) VALUES (?,?,?,?,?,?,?,?,?)", [Domain.Id(), group, ids[i], -400000, "payout", id, reference, date.ToString("yyyy-MM"), date.ToDateTime(new TimeOnly(12, 0))], c, tx);
            }
            for (var i = 0; i < 8; i++) await db.Exec("INSERT INTO rotations(id,group_id,user_id,payout_date,amount_cents) VALUES (?,?,?,?,?)", [Domain.Id(), group, ids[(i + 3) % 8], new DateOnly(now.Year, now.Month, 5).AddMonths(i + 1).ToString("yyyy-MM-dd"), 400000], c, tx);
            await db.Exec("INSERT INTO audit_events(id,group_id,user_id,action,detail) VALUES (?,?,?,'group.created','Ubuntu Savings Circle is ready to grow together')", [Domain.Id(), group, ids[0]], c, tx);
            return true;
        });
    }
}
