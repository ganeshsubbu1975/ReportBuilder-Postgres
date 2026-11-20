using Npgsql;
using Microsoft.Extensions.Options;
using ReportBuilder.Core.Catalog;

namespace ReportBuilder.Core.Views;

public sealed class SqlViewStore : IViewStore
{
    private readonly string _appCs, _designerCs;
    private readonly DesignerOptions _designer;

    public SqlViewStore(IOptions<DatabaseOptions> db, IOptions<DesignerOptions> designer)
    {
        _appCs = db.Value.AppConnectionString;
        _designerCs = db.Value.DesignerConnectionString;
        _designer = designer.Value;
    }

    public async Task<IReadOnlyDictionary<string, ViewColumn[]>> GetSourcesAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, List<ViewColumn>>(StringComparer.OrdinalIgnoreCase);
        await using var conn = new NpgsqlConnection(_designerCs);
        await conn.OpenAsync(ct);
        var names = _designer.Schemas.Select((_, i) => "@s" + i).ToArray();
        await using var cmd = new NpgsqlCommand(
            "SELECT c.table_schema::text, c.table_name::text, c.column_name::text, c.data_type::text FROM information_schema.columns c " +
            "JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name AND t.table_type = 'BASE TABLE' " +
            $"WHERE c.table_schema IN ({string.Join(",", names)}) ORDER BY c.table_schema, c.table_name, c.ordinal_position", conn);
        for (var i = 0; i < names.Length; i++) cmd.Parameters.AddWithValue(names[i], _designer.Schemas[i]);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var key = r.GetString(0) + "." + r.GetString(1);
            if (!map.TryGetValue(key, out var l)) map[key] = l = new();
            l.Add(new ViewColumn(r.GetString(2), r.GetString(3)));
        }
        return map.ToDictionary(k => k.Key, v => v.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<string>> GetExistingViewsAsync(CancellationToken ct)
    {
        var list = new List<string>();
        await using var conn = new NpgsqlConnection(_designerCs);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT viewname::text FROM pg_views WHERE schemaname = 'rpt'", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    public async Task<ViewPreview> PreviewAsync(string sql, string company, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_designerCs);
        await conn.OpenAsync(ct);
        // The SQL was assembled by ViewBuilder. The preview is limited to one company the caller may see.
        await using var cmd = new NpgsqlCommand($"SELECT * FROM ({sql}) AS p WHERE company_code = @company LIMIT 20", conn) { CommandTimeout = 20 };
        cmd.Parameters.AddWithValue("@company", company);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var cols = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToArray();
        var rows = new List<string?[]>();
        while (await r.ReadAsync(ct))
            rows.Add(Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)).ToArray());
        return new ViewPreview(cols, rows);
    }

    public async Task<int> SaveDraftAsync(string name, string description, string definitionJson, string sql, string createdBy, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_appCs);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO cat.viewdraft (name, description, definition_json, sql_text, status, created_by, created_at) " +
            "SELECT @name, @desc, @def, @sql, 'PENDING', @by, (now() at time zone 'utc') " +
            "WHERE NOT EXISTS (SELECT 1 FROM cat.viewdraft WHERE name = @name AND status = 'PENDING') RETURNING draft_id", conn);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@desc", description);
        cmd.Parameters.AddWithValue("@def", definitionJson);
        cmd.Parameters.AddWithValue("@sql", sql);
        cmd.Parameters.AddWithValue("@by", createdBy);
        var id = await cmd.ExecuteScalarAsync(ct);
        if (id is null) throw new InvalidOperationException("A draft with this name is already waiting for approval.");
        return Convert.ToInt32(id);
    }

    public async Task UpdateDraftAsync(int draftId, string name, string description, string definitionJson, string sql, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_appCs);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE cat.viewdraft SET name = @name, description = @desc, definition_json = @def, sql_text = @sql WHERE draft_id = @id AND status = 'PENDING' " +
            "AND NOT EXISTS (SELECT 1 FROM cat.viewdraft WHERE name = @name AND status = 'PENDING' AND draft_id <> @id)", conn);
        cmd.Parameters.AddWithValue("@id", draftId);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@desc", description);
        cmd.Parameters.AddWithValue("@def", definitionJson);
        cmd.Parameters.AddWithValue("@sql", sql);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new InvalidOperationException("That draft was already reviewed, or another pending draft has the same name.");
    }

    public async Task<IReadOnlyList<ViewDraft>> GetDraftsAsync(CancellationToken ct)
    {
        var list = new List<ViewDraft>();
        await using var conn = new NpgsqlConnection(_appCs);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT draft_id, name, description, definition_json, sql_text, status, created_by, created_at, reviewed_by, reviewed_at, review_note FROM cat.viewdraft ORDER BY draft_id DESC", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ViewDraft(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetDateTime(7),
                r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetDateTime(9), r.IsDBNull(10) ? null : r.GetString(10)));
        return list;
    }

    private static bool IsEdit(string definitionJson)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<ViewRequest>(definitionJson)?.ReplaceExisting == true; }
        catch (System.Text.Json.JsonException) { return false; }
    }

    public async Task ReviewAsync(int draftId, bool approve, string reviewer, string? note, CancellationToken ct)
    {
        var draft = (await GetDraftsAsync(ct)).FirstOrDefault(d => d.DraftId == draftId) ?? throw new InvalidOperationException("That draft no longer exists.");
        if (draft.Status != "PENDING") throw new InvalidOperationException("That draft has already been reviewed.");
        if (approve)
        {
            if (_designer.RequireSecondApprover && draft.CreatedBy.Equals(reviewer, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A different catalog owner must approve a view you designed.");
            if (!ViewBuilder.IsValidViewName(draft.Name)) throw new InvalidOperationException("The view name is not valid.");
            await using var conn = new NpgsqlConnection(_designerCs);
            await conn.OpenAsync(ct);
            // New views use CREATE VIEW (not OR ALTER) so an approval can never overwrite an existing reporting view by accident.
            // Only a draft the owner explicitly made as an edit of an existing view runs ALTER VIEW.
            // PostgreSQL has no ALTER VIEW ... AS; an edit uses CREATE OR REPLACE VIEW (column list must stay compatible).
            var verb = IsEdit(draft.DefinitionJson) ? "CREATE OR REPLACE" : "CREATE";
            await using var cmd = new NpgsqlCommand($"{verb} VIEW rpt.\"{draft.Name}\" AS {draft.SqlText}", conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using var app = new NpgsqlConnection(_appCs);
        await app.OpenAsync(ct);
        await using var upd = new NpgsqlCommand(
            "UPDATE cat.viewdraft SET status = @st, reviewed_by = @by, reviewed_at = (now() at time zone 'utc'), review_note = @note WHERE draft_id = @id AND status = 'PENDING'", app);
        upd.Parameters.AddWithValue("@st", approve ? "APPROVED" : "REJECTED");
        upd.Parameters.AddWithValue("@by", reviewer);
        upd.Parameters.AddWithValue("@note", (object?)note ?? DBNull.Value);
        upd.Parameters.AddWithValue("@id", draftId);
        await upd.ExecuteNonQueryAsync(ct);
    }
}
