using Npgsql;
using Microsoft.Extensions.Options;

namespace ReportBuilder.Core.Catalog;

public sealed class SqlCatalogStore : ICatalogStore
{
    private readonly string _appCs;
    private readonly string _readerCs;

    public SqlCatalogStore(IOptions<DatabaseOptions> options)
    {
        _appCs = options.Value.AppConnectionString;
        _readerCs = options.Value.ReaderConnectionString;
    }

    public async Task<IReadOnlyList<MetricEntry>> GetMetricsAsync(CancellationToken ct)
    {
        var list = new List<MetricEntry>();
        await using var conn = new NpgsqlConnection(_appCs);
        await conn.OpenAsync(ct);

        await using (var cmd = new NpgsqlCommand(
            "SELECT metric_id, name, description, view_name, measure_expr, sql_template, control_areas, is_certified, review_status, source_question, reviewed_by, reviewed_at, review_note, chart_type FROM cat.metric ORDER BY metric_id", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                list.Add(new MetricEntry
                {
                    MetricId = r.GetInt32(0),
                    Name = r.GetString(1),
                    Description = r.IsDBNull(2) ? "" : r.GetString(2),
                    ViewName = r.GetString(3),
                    MeasureExpr = r.GetString(4),
                    SqlTemplate = r.GetString(5),
                    ControlAreas = r.IsDBNull(6) || r.GetString(6).Length == 0 ? [] : r.GetString(6).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    IsCertified = r.GetBoolean(7),
                    ReviewStatus = r.GetString(8),
                    SourceQuestion = r.IsDBNull(9) ? null : r.GetString(9),
                    ReviewedBy = r.IsDBNull(10) ? null : r.GetString(10),
                    ReviewedAt = r.IsDBNull(11) ? null : r.GetDateTime(11),
                    ReviewNote = r.IsDBNull(12) ? null : r.GetString(12),
                    ChartType = r.IsDBNull(13) ? "auto" : r.GetString(13)
                });
            }
        }

        var dims = new Dictionary<int, List<string>>();
        await using (var cmd = new NpgsqlCommand("SELECT metric_id, column_name FROM cat.dimension", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                AddTo(dims, r.GetInt32(0), r.GetString(1));

        var syns = new Dictionary<int, List<string>>();
        await using (var cmd = new NpgsqlCommand("SELECT metric_id, term FROM cat.synonym", conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                AddTo(syns, r.GetInt32(0), r.GetString(1));

        foreach (var m in list)
        {
            m.Dimensions = dims.TryGetValue(m.MetricId, out var d) ? d.ToArray() : [];
            m.Synonyms = syns.TryGetValue(m.MetricId, out var s) ? s.ToArray() : [];
        }
        return list;
    }

    public async Task<MetricEntry?> GetMetricAsync(int metricId, CancellationToken ct)
        => (await GetMetricsAsync(ct)).FirstOrDefault(m => m.MetricId == metricId);

    public async Task<IReadOnlyDictionary<string, string[]>> GetViewColumnsAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        await using var conn = new NpgsqlConnection(_readerCs);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT table_name::text, column_name::text FROM information_schema.columns WHERE table_schema = 'rpt' ORDER BY table_name, ordinal_position", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var t = r.GetString(0);
            if (!map.TryGetValue(t, out var l)) map[t] = l = new List<string>();
            l.Add(r.GetString(1));
        }
        return map.ToDictionary(k => k.Key, v => v.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyDictionary<string, ViewColumn[]>> GetViewSchemaAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, List<ViewColumn>>(StringComparer.OrdinalIgnoreCase);
        await using var conn = new NpgsqlConnection(_readerCs);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT table_name::text, column_name::text, data_type::text FROM information_schema.columns WHERE table_schema = 'rpt' ORDER BY table_name, ordinal_position", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var t = r.GetString(0);
            if (!map.TryGetValue(t, out var l)) map[t] = l = new List<ViewColumn>();
            l.Add(new ViewColumn(r.GetString(1), r.GetString(2).ToLowerInvariant()));
        }
        return map.ToDictionary(k => k.Key, v => v.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    public async Task<int> InsertUnverifiedAsync(IEnumerable<MetricEntry> entries, CancellationToken ct)
    {
        var existing = await GetMetricsAsync(ct);
        var keys = existing.Select(Key).ToHashSet();
        var nextId = existing.Count == 0 ? 1 : existing.Max(m => m.MetricId) + 1;
        var inserted = 0;

        await using var conn = new NpgsqlConnection(_appCs);
        await conn.OpenAsync(ct);
        await using var tx = (NpgsqlTransaction)await conn.BeginTransactionAsync(ct);
        foreach (var e in entries)
        {
            if (!keys.Add(Key(e))) continue;
            var id = nextId++;
            await using (var cmd = new NpgsqlCommand(
                "INSERT INTO cat.metric (metric_id, name, description, view_name, measure_expr, sql_template, control_areas, is_certified, review_status, source_question, chart_type) " +
                "VALUES (@id, @name, @desc, @view, @measure, @sql, @areas, false, 'UNVERIFIED', @q, @chart)", conn, tx))
            {
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@name", e.Name);
                cmd.Parameters.AddWithValue("@desc", e.Description);
                cmd.Parameters.AddWithValue("@view", e.ViewName);
                cmd.Parameters.AddWithValue("@measure", e.MeasureExpr);
                cmd.Parameters.AddWithValue("@sql", e.SqlTemplate);
                cmd.Parameters.AddWithValue("@areas", string.Join(',', e.ControlAreas));
                cmd.Parameters.AddWithValue("@q", (object?)e.SourceQuestion ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@chart", e.ChartType);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            foreach (var d in e.Dimensions.Distinct())
            {
                await using var cmd = new NpgsqlCommand("INSERT INTO cat.dimension (metric_id, column_name) VALUES (@id, @c)", conn, tx);
                cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@c", d);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            foreach (var s in e.Synonyms.Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct())
            {
                await using var cmd = new NpgsqlCommand("INSERT INTO cat.synonym (term, metric_id) VALUES (@t, @id)", conn, tx);
                cmd.Parameters.AddWithValue("@t", s); cmd.Parameters.AddWithValue("@id", id);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            inserted++;
        }
        await tx.CommitAsync(ct);
        return inserted;
    }

    public async Task UpdateMetricAsync(MetricEntry e, bool definitionChanged, CancellationToken ct)
    {
        var all = await GetMetricsAsync(ct);
        if (all.All(m => m.MetricId != e.MetricId)) throw new InvalidOperationException($"Metric {e.MetricId} not found.");
        if (all.Any(m => m.MetricId != e.MetricId && Key(m) == Key(e)))
            throw new InvalidOperationException("Another metric already has the same view, measure and group-by columns.");

        await using var conn = new NpgsqlConnection(_appCs);
        await conn.OpenAsync(ct);
        await using var tx = (NpgsqlTransaction)await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            "UPDATE cat.metric SET name = @name, description = @desc, view_name = @view, measure_expr = @measure, sql_template = @sql, chart_type = @chart" +
            (definitionChanged ? ", is_certified = false, review_status = 'UNVERIFIED', reviewed_by = NULL, reviewed_at = NULL, review_note = NULL" : "") +
            " WHERE metric_id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("@id", e.MetricId);
            cmd.Parameters.AddWithValue("@name", e.Name);
            cmd.Parameters.AddWithValue("@desc", e.Description);
            cmd.Parameters.AddWithValue("@view", e.ViewName);
            cmd.Parameters.AddWithValue("@measure", e.MeasureExpr);
            cmd.Parameters.AddWithValue("@sql", e.SqlTemplate);
            cmd.Parameters.AddWithValue("@chart", e.ChartType);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var table in new[] { "cat.dimension", "cat.synonym" })
        {
            await using var del = new NpgsqlCommand($"DELETE FROM {table} WHERE metric_id = @id", conn, tx);
            del.Parameters.AddWithValue("@id", e.MetricId);
            await del.ExecuteNonQueryAsync(ct);
        }
        foreach (var d in e.Dimensions.Distinct())
        {
            await using var cmd = new NpgsqlCommand("INSERT INTO cat.dimension (metric_id, column_name) VALUES (@id, @c)", conn, tx);
            cmd.Parameters.AddWithValue("@id", e.MetricId); cmd.Parameters.AddWithValue("@c", d);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var s in e.Synonyms.Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).Distinct())
        {
            await using var cmd = new NpgsqlCommand("INSERT INTO cat.synonym (term, metric_id) VALUES (@t, @id)", conn, tx);
            cmd.Parameters.AddWithValue("@t", s); cmd.Parameters.AddWithValue("@id", e.MetricId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task SetReviewAsync(int metricId, bool approve, string reviewer, string? note, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_appCs);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE cat.metric SET is_certified = @cert, review_status = @status, reviewed_by = @by, reviewed_at = (now() at time zone 'utc'), review_note = @note WHERE metric_id = @id", conn);
        cmd.Parameters.AddWithValue("@cert", approve);
        cmd.Parameters.AddWithValue("@status", approve ? "CERTIFIED" : "REJECTED");
        cmd.Parameters.AddWithValue("@by", reviewer);
        cmd.Parameters.AddWithValue("@note", (object?)note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", metricId);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new InvalidOperationException($"Metric {metricId} not found.");
    }

    private static void AddTo(Dictionary<int, List<string>> map, int key, string value)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<string>();
        list.Add(value);
    }

    private static string Key(MetricEntry m) => $"{m.ViewName}|{m.MeasureExpr}|{string.Join(',', m.Dimensions.OrderBy(x => x))}";
}
