using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StokvelSync.Web.Core;

public sealed class AppException(int status, string message) : Exception(message) { public int Status { get; } = status; }
public static partial class Domain
{
    public static string Id() => Guid.NewGuid().ToString();
    public static string Token(int size = 32) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(size));
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string PasswordHash(string value)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(value, salt, 210000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2:210000:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(key)}";
    }
    public static bool VerifyPassword(string value, string? hash)
    {
        if (hash is null) return false;
        var p = hash.Split(':');
        if (p.Length != 4 || p[0] != "pbkdf2") return false;
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(p[3]), Rfc2898DeriveBytes.Pbkdf2(value, Convert.FromBase64String(p[2]), int.Parse(p[1], CultureInfo.InvariantCulture), HashAlgorithmName.SHA256, 32)); }
        catch (FormatException) { return false; }
    }
    public static string Phone(string value)
    {
        var s = Regex.Replace(value, @"[\s()-]", "");
        if (Regex.IsMatch(s, @"^0[6-8]\d{8}$")) return "+27" + s[1..];
        if (Regex.IsMatch(s, @"^\+27[6-8]\d{8}$")) return s;
        throw new AppException(400, "Enter a South African mobile number, for example 082 123 4567.");
    }
    public static int Cents(string value)
    {
        if (!Regex.IsMatch(value, @"^\d{1,7}(\.\d{1,2})?$", RegexOptions.CultureInvariant) || !decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) || amount < 1 || amount > 1000000)
            throw new AppException(400, "Amount must be between R1 and R1,000,000 with at most two decimals.");
        return checked((int)(amount * 100));
    }
    public static DateOnly MonthlyDate(string start, int offset)
    {
        if (!DateOnly.TryParseExact(start, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) throw new AppException(400, "Use a valid payout date.");
        return date.AddMonths(offset);
    }
    public static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, OperatingSystem.IsWindows() ? "South Africa Standard Time" : "Africa/Johannesburg"));
    public static string Period => Today.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    public static string Rand(long cents) => "R" + (cents / 100m).ToString("N2", CultureInfo.GetCultureInfo("en-ZA"));
    public static string Csv(string value)
    {
        if (Regex.IsMatch(value, @"^\s*[=+@-]")) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
    public static string Str(JsonElement b, string key, int min = 0, int max = 100, string? fallback = null)
    {
        var s = b.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;
        if (key is not ("password" or "currentPassword")) s = s?.Trim();
        if (s is null || s.Length < min || s.Length > max) throw new AppException(400, $"{key} must contain {min} to {max} characters.");
        return s;
    }
    public static int Int(JsonElement b, string key, int min, int max)
    {
        if (!b.TryGetProperty(key, out var v) || !v.TryGetInt32(out var n) || n < min || n > max) throw new AppException(400, $"{key} must be between {min} and {max}.");
        return n;
    }
    public static bool Bool(JsonElement b, string key) => b.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
    public static string Uuid(string s) => Guid.TryParse(s, out var id) ? id.ToString() : throw new AppException(400, "A valid operation or record ID is required.");
    public static string PeriodValue(JsonElement b)
    {
        var s = Str(b, "period", 7, 7);
        if (!Regex.IsMatch(s, @"^20\d{2}-(0[1-9]|1[0-2])$")) throw new AppException(400, "Use a valid contribution month.");
        return s;
    }
    public static (string Command, string? Pin, int Amount) ParseSms(string text)
    {
        var p = Regex.Split(text.Trim(), @"\s+"); var command = p[0].ToUpperInvariant();
        if (command == "HELP") return (command, null, 0);
        if (!new[] { "BAL", "NEXT", "PAY" }.Contains(command) || p.Length < 2 || !Regex.IsMatch(p[1], @"^\d{6}$") || p.Length != (command == "PAY" ? 3 : 2))
            throw new AppException(400, "Send BAL <6-digit PIN>, NEXT <PIN>, or PAY <PIN> <rand amount>. PAY records a claim for treasurer review.");
        return (command, p[1], command == "PAY" ? Cents(p[2]) : 0);
    }
}
