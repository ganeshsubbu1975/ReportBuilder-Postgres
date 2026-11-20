using System.Data;
using Npgsql;
using Microsoft.Extensions.Options;

namespace ReportBuilder.Core.Data;

public sealed class SqlAuditStore : IAuditStore
{
    private readonly string _cs;
    public SqlAuditStore(IOptions<DatabaseOptions> options) => _cs = options.Value.AppConnectionString;

    public async Task<long> AppendAsync(AuditEntry e, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var tx = (NpgsqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        string prev;
        await using (var c = new NpgsqlCommand("SELECT row_hash FROM audit.reportrun ORDER BY run_id DESC LIMIT 1 FOR UPDATE", conn, tx))
            prev = (await c.ExecuteScalarAsync(ct)) as string ?? "";

        var at = AuditHasher.NowUtcMs();
        var hash = AuditHasher.Compute(prev, e, at);
        await using var ins = new NpgsqlCommand(
            "INSERT INTO audit.reportrun (run_at, user_name, question, status, metric_id, sql_text, row_count, findings, duration_ms, prev_hash, row_hash) " +
            "VALUES (@at, @u, @q, @s, @m, @sql, @rc, @f, @d, @p, @h) RETURNING run_id", conn, tx);
        ins.Parameters.Add(new NpgsqlParameter("@at", NpgsqlTypes.NpgsqlDbType.Timestamp) { Value = DateTime.SpecifyKind(at, DateTimeKind.Unspecified) });
        ins.Parameters.AddWithValue("@u", e.UserName);
        ins.Parameters.AddWithValue("@q", e.Question);
        ins.Parameters.AddWithValue("@s", e.Status);
        ins.Parameters.AddWithValue("@m", (object?)e.MetricId ?? DBNull.Value);
        ins.Parameters.AddWithValue("@sql", (object?)e.Sql ?? DBNull.Value);
        ins.Parameters.AddWithValue("@rc", e.RowCount);
        ins.Parameters.AddWithValue("@f", e.Findings);
        ins.Parameters.AddWithValue("@d", e.DurationMs);
        ins.Parameters.AddWithValue("@p", prev);
        ins.Parameters.AddWithValue("@h", hash);
        var id = Convert.ToInt64(await ins.ExecuteScalarAsync(ct));
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<IReadOnlyList<AuditRun>> GetRecentAsync(DateTime sinceUtc, string? userName, int max, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT run_id, run_at, user_name, question, status, metric_id, sql_text, row_count, findings, duration_ms, prev_hash, row_hash " +
            "FROM audit.reportrun WHERE run_at >= @since AND (@u::text IS NULL OR user_name = @u) ORDER BY run_id DESC LIMIT @max", conn);
        cmd.Parameters.AddWithValue("@max", max);
        cmd.Parameters.Add(new NpgsqlParameter("@since", NpgsqlTypes.NpgsqlDbType.Timestamp) { Value = sinceUtc });
        cmd.Parameters.AddWithValue("@u", (object?)userName ?? DBNull.Value);
        var list = new List<AuditRun>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new AuditRun(r.GetInt64(0), DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Utc), r.GetString(2), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetInt32(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetInt32(7), r.GetString(8), r.GetInt32(9),
                r.IsDBNull(10) ? "" : r.GetString(10), r.GetString(11)));
        return list;
    }

    public async Task<long?> VerifyChainAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT run_id, run_at, user_name, question, status, metric_id, sql_text, row_count, findings, duration_ms, prev_hash, row_hash FROM audit.reportrun ORDER BY run_id", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var prev = "";
        while (await r.ReadAsync(ct))
        {
            var entry = new AuditEntry(r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetInt32(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.GetInt32(7), r.GetString(8), r.GetInt32(9));
            var at = DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Utc);
            if (r.GetString(10) != prev || AuditHasher.Compute(prev, entry, at) != r.GetString(11)) return r.GetInt64(0);
            prev = r.GetString(11);
        }
        return null;
    }
}
