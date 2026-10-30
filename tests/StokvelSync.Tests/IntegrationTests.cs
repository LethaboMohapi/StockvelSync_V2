using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StokvelSync.Web.Core;
using Xunit;

namespace StokvelSync.Tests;

public sealed class MySqlFactAttribute : FactAttribute
{
    public MySqlFactAttribute() { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STOKVELSYNC_TEST_MYSQL"))) Skip = "Set STOKVELSYNC_TEST_MYSQL to a dedicated MySQL test database connection string."; }
}
public sealed class Factory(string connection) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:MySql"] = connection, ["Payments:Provider"] = "sandbox", ["Sms:Provider"] = "console", ["App:PublicUrl"] = "http://localhost", ["App:CookieSecure"] = "false" }));
    }
}
public sealed class IntegrationTests
{
    static async Task<HttpResponseMessage> Post(HttpClient client, string path, object body, string? op = null, string method = "POST")
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-StokvelSync", "1"); if (op is not null) request.Headers.Add("Idempotency-Key", op);
        return await client.SendAsync(request);
    }
    static async Task<JsonElement> Json(HttpResponseMessage response) { var text = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, text); return JsonSerializer.Deserialize<JsonElement>(text); }
    static async Task<string> Register(HttpClient client, string name)
    {
        var phone = "+2782" + Random.Shared.Next(1000000, 9999999); var result = await Json(await Post(client, "/api/auth/register", new { name, phone, password = "Integration!Password2026" })); return result.GetProperty("id").GetString()!;
    }
    [MySqlFact]
    public async Task MultiTenantLedgerPaymentsSmsAndConcurrentPayoutsStayConsistent()
    {
        using var factory = new Factory(Environment.GetEnvironmentVariable("STOKVELSYNC_TEST_MYSQL")!);
        var owner = factory.CreateClient(); var member = factory.CreateClient(); var outsider = factory.CreateClient();
        var database = factory.Services.GetRequiredService<Db>();
        await database.Migrate(Path.Combine(FindRoot(), "src", "StokvelSync.Web"));
        var ownerId = await Register(owner, "Test Treasurer"); var memberId = await Register(member, "Test Member"); await Register(outsider, "Other Stokvel");
        var created = await Json(await Post(owner, "/api/groups", new { name = "Integration Circle", description = "Test", location = "Soweto", contributionCents = 50000, dueDay = 5 })); var g = created.GetProperty("id").GetString()!;
        var initial = await owner.GetFromJsonAsync<JsonElement>("/api/groups/" + g); var code = initial.GetProperty("group").GetProperty("joinCode").GetString();
        await Json(await Post(member, "/api/groups/join", new { code }));
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync("/api/groups/" + g)).StatusCode);
        var b = new { amountCents = 50000, period = Domain.Period, method = "eft", reference = "IDEMPOTENT-REF" }; var op = Domain.Id();
        var first = await Json(await Post(member, $"/api/groups/{g}/contributions", b, op)); var duplicate = await Json(await Post(member, $"/api/groups/{g}/contributions", b, op));
        Assert.Equal(first.GetProperty("id").GetString(), duplicate.GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Post(member, $"/api/groups/{g}/contributions", new { amountCents = 60000, period = Domain.Period, method = "eft", reference = "CHANGED" }, op)).StatusCode);
        var contributionId = first.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(member, $"/api/groups/{g}/contributions/{contributionId}/review", new { status = "confirmed" }, Domain.Id())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(outsider, $"/api/groups/{g}/contributions", b, Domain.Id())).StatusCode);
        var reviewOp = Domain.Id(); await Json(await Post(owner, $"/api/groups/{g}/contributions/{contributionId}/review", new { status = "confirmed" }, reviewOp));
        await Json(await Post(owner, $"/api/groups/{g}/contributions/{contributionId}/review", new { status = "confirmed" }, reviewOp));
        Assert.Equal(HttpStatusCode.Conflict, (await Post(owner, $"/api/groups/{g}/contributions/{contributionId}/review", new { status = "confirmed" }, Domain.Id())).StatusCode);
        var s = await owner.GetFromJsonAsync<JsonElement>("/api/groups/" + g); Assert.Single(s.GetProperty("ledger").EnumerateArray());
        var memberView = await member.GetFromJsonAsync<JsonElement>("/api/groups/" + g); Assert.False(memberView.GetProperty("group").TryGetProperty("joinCode", out _));
        Assert.Contains("•", memberView.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("id").GetString() == ownerId).GetProperty("phone").GetString());
        var p = await Json(await Post(member, $"/api/groups/{g}/payments", new { amountCents = 50000, period = Domain.Period }, Domain.Id())); var paymentId = p.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NotFound, (await Post(outsider, $"/api/payments/{paymentId}/sandbox-confirm", new { })).StatusCode);
        await Json(await Post(member, $"/api/payments/{paymentId}/sandbox-confirm", new { })); await Json(await Post(member, $"/api/payments/{paymentId}/sandbox-confirm", new { }));
        s = await owner.GetFromJsonAsync<JsonElement>("/api/groups/" + g); Assert.Equal(2, s.GetProperty("ledger").GetArrayLength());
        await Json(await Post(member, "/api/me", new { language = "zu", smsConsent = true, smsGroupId = g, pin = "234567" }, method: "PATCH"));
        var sms = await Json(await Post(member, $"/api/groups/{g}/sms-test", new { body = "BAL 234567", operationId = Domain.Id() })); Assert.Contains("Ibhalansi", sms.GetProperty("reply").GetString());
        var smsOp = Domain.Id(); await Json(await Post(member, $"/api/groups/{g}/sms-test", new { body = "PAY 234567 50", operationId = smsOp })); await Json(await Post(member, $"/api/groups/{g}/sms-test", new { body = "PAY 234567 50", operationId = smsOp }));
        s = await owner.GetFromJsonAsync<JsonElement>("/api/groups/" + g); Assert.Equal(3, s.GetProperty("contributions").GetArrayLength()); Assert.Equal(2, s.GetProperty("ledger").GetArrayLength());
        var schedule = new { userIds = new[] { memberId }, startDate = Domain.Today.ToString("yyyy-MM-dd"), amountCents = 75000, replace = false };
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(member, $"/api/groups/{g}/rotations", schedule, Domain.Id())).StatusCode);
        await Json(await Post(owner, $"/api/groups/{g}/rotations", schedule, Domain.Id()));
        s = await owner.GetFromJsonAsync<JsonElement>("/api/groups/" + g); var rotation = s.GetProperty("rotations")[0].GetProperty("id").GetString();
        var calls = await Task.WhenAll(Post(owner, $"/api/groups/{g}/rotations/{rotation}/payout", new { reference = "BANK-TRANSFER-01" }, Domain.Id()), Post(owner, $"/api/groups/{g}/rotations/{rotation}/payout", new { reference = "BANK-TRANSFER-01" }, Domain.Id()));
        Assert.Single(calls, r => r.IsSuccessStatusCode); Assert.Single(calls, r => r.StatusCode == HttpStatusCode.Conflict);
        s = await owner.GetFromJsonAsync<JsonElement>("/api/groups/" + g); Assert.Equal(25000, s.GetProperty("ledger").EnumerateArray().Sum(l => l.GetProperty("amountCents").GetInt32()));
        var csv = await owner.GetAsync($"/api/groups/{g}/export"); Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        using var hostile = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout") { Content = JsonContent.Create(new { }) }; hostile.Headers.Add("X-StokvelSync", "1"); hostile.Headers.Add("Origin", "https://other.example"); Assert.Equal(HttpStatusCode.Forbidden, (await owner.SendAsync(hostile)).StatusCode);
        await Json(await Post(member, "/api/auth/logout", new { })); Assert.Equal(HttpStatusCode.Unauthorized, (await member.GetAsync("/api/me")).StatusCode);
    }
    static string FindRoot() { var dir = new DirectoryInfo(AppContext.BaseDirectory); while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "StokvelSync_V2.sln"))) dir = dir.Parent; return dir?.FullName ?? throw new InvalidOperationException("Solution root not found."); }
}
