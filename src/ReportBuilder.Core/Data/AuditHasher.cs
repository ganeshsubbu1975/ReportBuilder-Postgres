using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ReportBuilder.Core.Data;

public static class AuditHasher
{
    public static string Compute(string prevHash, AuditEntry e, DateTime runAtUtc)
    {
        var text = string.Join("|", prevHash,
            runAtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture),
            e.UserName, e.Question, e.Status, e.MetricId?.ToString(CultureInfo.InvariantCulture) ?? "", e.Sql ?? "",
            e.RowCount.ToString(CultureInfo.InvariantCulture), e.Findings, e.DurationMs.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>Truncates to whole milliseconds so the value round-trips through datetime2(3).</summary>
    public static DateTime NowUtcMs()
    {
        var n = DateTime.UtcNow;
        return new DateTime(n.Ticks - (n.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
    }
}
