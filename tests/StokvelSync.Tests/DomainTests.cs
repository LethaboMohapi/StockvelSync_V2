using StokvelSync.Web.Core;
using Xunit;

namespace StokvelSync.Tests;

public sealed class DomainTests
{
    [Theory]
    [InlineData("082 123 4567", "+27821234567")]
    [InlineData("+27 72 123 4567", "+27721234567")]
    public void SouthAfricanNumbersAreNormalized(string input, string output) => Assert.Equal(output, Domain.Phone(input));
    [Theory]
    [InlineData("+14155552671")][InlineData("123")]
    public void InvalidOrForeignNumbersAreRejected(string input) => Assert.Throws<AppException>(() => Domain.Phone(input));
    [Fact] public void CurrencyUsesExactCents() { Assert.Equal(10001, Domain.Cents("100.01")); Assert.Equal(100000000, Domain.Cents("1000000")); Assert.Throws<AppException>(() => Domain.Cents("0.009")); Assert.Throws<AppException>(() => Domain.Cents("-1")); }
    [Fact] public void RotationClampsMonthEndsWithoutDrift() { Assert.Equal(new DateOnly(2028, 2, 29), Domain.MonthlyDate("2028-01-31", 1)); Assert.Equal(new DateOnly(2028, 3, 31), Domain.MonthlyDate("2028-01-31", 2)); Assert.Equal(new DateOnly(2027, 2, 28), Domain.MonthlyDate("2027-01-31", 1)); }
    [Fact] public void SmsRequiresPinAndRecordsAnAmountOnly() { Assert.Equal(("PAY", "123456", 50050), Domain.ParseSms("pay 123456 500.50")); Assert.Equal("BAL", Domain.ParseSms("BAL 123456").Command); Assert.Throws<AppException>(() => Domain.ParseSms("PAY 123456 -500")); Assert.Throws<AppException>(() => Domain.ParseSms("BAL")); }
    [Fact] public void PasswordHashesAreSaltedAndVerified() { var a = Domain.PasswordHash("a strong password"); var b = Domain.PasswordHash("a strong password"); Assert.NotEqual(a, b); Assert.True(Domain.VerifyPassword("a strong password", a)); Assert.False(Domain.VerifyPassword("wrong password", a)); }
    [Theory][InlineData("=HYPERLINK(\"bad\")")][InlineData(" +123")][InlineData("@SUM(1)")]
    public void CsvNeutralizesSpreadsheetFormulas(string value) => Assert.StartsWith("\"'", Domain.Csv(value));
    [Fact] public void PayfastSignaturePreservesWireOrderAndFormEncoding()
    {
        var f = new Dictionary<string, string> { ["merchant_id"] = "10000100", ["item_name"] = "Ubuntu Savings + ~" };
        Assert.Equal("merchant_id=10000100&item_name=Ubuntu+Savings+%2B+%7E", Providers.ParameterString(f));
        var sig = Providers.Signature(f, "secret"); f["signature"] = sig; Assert.Equal(sig, Providers.Signature(f, "secret"));
        f["item_name"] = "Tampered"; Assert.NotEqual(sig, Providers.Signature(f, "secret"));
    }
}
