using System.Data;
using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using Microsoft.Extensions.Options;

namespace ReportBuilder.Core.Pipeline;

/// <summary>
/// Step 7. Connects with the rb_reader role (SELECT on rpt only) and stamps the end user into the session setting rb.user
/// so any row-level security in the ERP database can follow the user.
/// </summary>
public sealed class SqlQueryExecutor : IQueryExecutor, IReconciliationSource, IDraftPreview
{
    private readonly string _cs;

    public SqlQueryExecutor(IOptions<DatabaseOptions> options) => _cs = options.Value.ReaderConnectionString;

    public async Task<QueryResult> ExecuteAsync(CompiledQuery query, string userName, int maxRows, CancellationToken ct)
    {
        await using var conn = await OpenAsync(userName, ct);
        await using var cmd = new NpgsqlCommand(query.Sql, conn) { CommandTimeout = query.CommandTimeoutSeconds };
        var types = query.ParameterColumns.Count > 0 ? await ColumnTypesAsync(conn, query.ViewName, ct) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in query.Parameters) cmd.Parameters.Add(ToParameter(k, v, query, types));

        var result = new QueryResult();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        result.Columns = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToArray();
        while (await r.ReadAsync(ct))
        {
            if (result.Rows.Count >= maxRows) { result.Truncated = true; break; }
            var row = new object?[r.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            result.Rows.Add(row);
        }
        return result;
    }

    public async Task<IReadOnlyList<ControlTotal>> GetControlTotalsAsync(string company, CancellationToken ct)
    {
        var list = new List<ControlTotal>();
        await using var conn = await OpenAsync("system", ct);
        await using var cmd = new NpgsqlCommand("SELECT area, gl_balance, subledger_balance, difference FROM rpt.control_totals_v WHERE company_code = @c ORDER BY area", conn);
        cmd.Parameters.AddWithValue("@c", company);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new ControlTotal(r.GetString(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3)));
        return list;
    }

    public async Task<IReadOnlyList<ManualPosting>> GetManualPostingsAsync(string company, IReadOnlyCollection<string> glAccounts, CancellationToken ct)
    {
        var list = new List<ManualPosting>();
        if (glAccounts.Count == 0) return list;
        await using var conn = await OpenAsync("system", ct);
        var names = glAccounts.Select((_, i) => "@a" + i).ToArray();
        await using var cmd = new NpgsqlCommand(
            $"SELECT journal_no, posting_date, gl_account, net_amount, description FROM rpt.manual_postings_v WHERE company_code = @c AND gl_account IN ({string.Join(',', names)}) ORDER BY posting_date, journal_no", conn);
        cmd.Parameters.AddWithValue("@c", company);
        var i = 0;
        foreach (var a in glAccounts) cmd.Parameters.AddWithValue(names[i++], a);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ManualPosting(r.GetString(0), r.GetDateTime(1), r.GetString(2), r.GetDecimal(3), r.IsDBNull(4) ? "" : r.GetString(4)));
        return list;
    }

    public async Task<decimal> GetTrialBalanceNetAsync(string company, CancellationToken ct)
    {
        await using var conn = await OpenAsync("system", ct);
        await using var cmd = new NpgsqlCommand("SELECT COALESCE(SUM(balance), 0) FROM rpt.trial_balance_v WHERE company_code = @c", conn);
        cmd.Parameters.AddWithValue("@c", company);
        return Convert.ToDecimal(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    public async Task<DraftPreview> PreviewAsync(string sql, string company, CancellationToken ct)
    {
        await using var conn = await OpenAsync("catalog-generator", ct);
        // Wrap the draft so at most 5 rows come back, then count separately. The draft has already passed SqlGuard.
        await using (var cmd = new NpgsqlCommand($"SELECT * FROM ({sql}) AS q LIMIT 5", conn) { CommandTimeout = 30 })
        {
            cmd.Parameters.AddWithValue("@company", company);
            var rows = new List<string[]>();
            string[] cols;
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                cols = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToArray();
                while (await r.ReadAsync(ct))
                    rows.Add(Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "").ToArray());
            }
            await using var count = new NpgsqlCommand($"SELECT COUNT(*) FROM ({sql}) AS q", conn) { CommandTimeout = 30 };
            count.Parameters.AddWithValue("@company", company);
            var n = Convert.ToInt32(await count.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            return new DraftPreview(cols, rows, n);
        }
    }

    private static async Task<Dictionary<string, string>> ColumnTypesAsync(NpgsqlConnection conn, string view, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new NpgsqlCommand("SELECT column_name::text, data_type::text FROM information_schema.columns WHERE table_schema = 'rpt' AND table_name = @v", conn);
        cmd.Parameters.AddWithValue("@v", view);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) map[r.GetString(0)] = r.GetString(1).ToLowerInvariant();
        return map;
    }

    /// <summary>
    /// Filter values arrive as text. Npgsql sends parameters with a strict type, so convert here to the column's type
    /// (information_schema data_type) and reject values that do not fit.
    /// </summary>
    private static NpgsqlParameter ToParameter(string name, object? value, CompiledQuery q, IReadOnlyDictionary<string, string> types)
    {
        var pn = "@" + name;
        if (value is null) return new NpgsqlParameter(pn, DBNull.Value);
        if (name == "company") return new NpgsqlParameter(pn, NpgsqlDbType.Varchar) { Value = value };
        if (value is not string text || !q.ParameterColumns.TryGetValue(name, out var column) || !types.TryGetValue(column, out var type))
            return new NpgsqlParameter { ParameterName = pn, Value = value };

        text = text.Trim();
        var inv = CultureInfo.InvariantCulture;
        switch (type)
        {
            case "smallint": case "integer": case "bigint":
            {
                var (min, max) = type == "smallint" ? ((long)short.MinValue, (long)short.MaxValue) : type == "integer" ? (int.MinValue, int.MaxValue) : (long.MinValue, long.MaxValue);
                if (!long.TryParse(text, NumberStyles.Integer, inv, out var n) || n < min || n > max)
                    throw new PlanRejectedException($"'{text}' is not a valid value for {column}. It must be a whole number from {min} to {max}.");
                return type == "smallint" ? new NpgsqlParameter(pn, (short)n) : type == "integer" ? new NpgsqlParameter(pn, (int)n) : new NpgsqlParameter(pn, n);
            }
            case "numeric": case "real": case "double precision": case "money":
                if (!decimal.TryParse(text, NumberStyles.Number, inv, out var d)) throw new PlanRejectedException($"'{text}' is not a valid number for {column}.");
                return type == "real" ? new NpgsqlParameter(pn, (float)d) : type == "double precision" ? new NpgsqlParameter(pn, (double)d) : new NpgsqlParameter(pn, d);
            case "date":
                if (!DateTime.TryParse(text, inv, DateTimeStyles.None, out var day)) throw new PlanRejectedException($"'{text}' is not a valid date for {column}.");
                return new NpgsqlParameter(pn, NpgsqlDbType.Date) { Value = DateOnly.FromDateTime(day) };
            case "timestamp without time zone":
                if (!DateTime.TryParse(text, inv, DateTimeStyles.None, out var dt)) throw new PlanRejectedException($"'{text}' is not a valid date for {column}.");
                return new NpgsqlParameter(pn, NpgsqlDbType.Timestamp) { Value = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified) };
            case "boolean":
                var yes = text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1" || text.Equals("yes", StringComparison.OrdinalIgnoreCase);
                var no = text.Equals("false", StringComparison.OrdinalIgnoreCase) || text == "0" || text.Equals("no", StringComparison.OrdinalIgnoreCase);
                if (!yes && !no) throw new PlanRejectedException($"'{text}' is not a valid yes/no value for {column}.");
                return new NpgsqlParameter(pn, yes);
            default:
                return new NpgsqlParameter(pn, NpgsqlDbType.Text) { Value = text };
        }
    }

    private async Task<NpgsqlConnection> OpenAsync(string userName, CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_cs);
        await conn.OpenAsync(ct);
        await using var ctx = new NpgsqlCommand("SELECT set_config('rb.user', @u, false)", conn);
        ctx.Parameters.AddWithValue("@u", userName);
        await ctx.ExecuteNonQueryAsync(ct);
        return conn;
    }
}
