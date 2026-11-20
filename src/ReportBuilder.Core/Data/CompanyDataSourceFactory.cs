using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Npgsql;
using ReportBuilder.Core.Catalog;
using ReportBuilder.Core.Security;

namespace ReportBuilder.Core.Data;

public sealed class TenantModuleConfig
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
}

public sealed class TenantEntryConfig
{
    public string Name { get; set; } = "";
    public string BaseCompany { get; set; } = "";
    public string Host { get; set; } = "";
    public string ConnectionString { get; set; } = "";
    public string ReportConnection { get; set; } = "";
    public List<TenantModuleConfig> Modules { get; set; } = [];
}

public sealed class TenantsConfig
{
    public string VansalesConnection { get; set; } = "";
    public string ConsoleConnection { get; set; } = "";
    public List<TenantEntryConfig> Tenants { get; set; } = [];
}

public sealed record CompanyDatabaseMapping(string CompanyCode, string DatabaseName, string? Host, int? Port, bool IsActive, string Source);

public sealed record ViewColumnMeta(string Name, string DataType);

public sealed record ErpAreaViewMeta(
    string ViewName,
    string AreaName,
    string DisplayName,
    string Description,
    string? DateColumn,
    string[] DefaultFields,
    string SynonymsHint,
    IReadOnlyList<ViewColumnMeta> Columns);

/// <summary>
/// Resolves each authorized company to its operational PostgreSQL database using
/// cat.company_database_map overrides and the ERP Tenants JSON configuration,
/// introspects live rpt.* views in that database, and executes parallel multi-company queries.
/// </summary>
public sealed class CompanyDataSourceFactory : IAsyncDisposable
{
    private readonly DatabaseOptions _db;
    private readonly TenantsConfig _tenants;
    private readonly ConcurrentDictionary<string, NpgsqlDataSource> _sources = new(StringComparer.OrdinalIgnoreCase);

    public CompanyDataSourceFactory(IOptions<DatabaseOptions> db, IOptions<TenantsConfig> tenants)
    {
        _db = db.Value;
        _tenants = tenants.Value;
    }

    private string ConsoleAppConn => !string.IsNullOrWhiteSpace(_db.AppConnectionString)
        ? _db.AppConnectionString
        : _db.ReaderConnectionString;

    public async Task<IReadOnlyList<CompanyDatabaseMapping>> GetDatabaseMappingsAsync(CancellationToken ct = default)
    {
        var list = new List<CompanyDatabaseMapping>();
        await using var conn = new NpgsqlConnection(ConsoleAppConn);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT company_code, database_name, host, port, is_active
            FROM cat.company_database_map
            ORDER BY company_code
            """, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new CompanyDatabaseMapping(
                r.GetString(0),
                r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetInt32(3),
                r.GetBoolean(4),
                "cat.company_database_map"));
        }
        return list;
    }

    public async Task UpsertDatabaseMappingAsync(CompanyDatabaseMapping m, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(ConsoleAppConn);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO cat.company_database_map (company_code, database_name, host, port, is_active)
            VALUES (@c, @d, @h, @p, @a)
            ON CONFLICT (company_code) DO UPDATE
            SET database_name = EXCLUDED.database_name,
                host = EXCLUDED.host,
                port = EXCLUDED.port,
                is_active = EXCLUDED.is_active
            """, conn);
        cmd.Parameters.AddWithValue("c", m.CompanyCode.Trim());
        cmd.Parameters.AddWithValue("d", m.DatabaseName.Trim());
        cmd.Parameters.AddWithValue("h", (object?)m.Host ?? DBNull.Value);
        cmd.Parameters.AddWithValue("p", (object?)m.Port ?? DBNull.Value);
        cmd.Parameters.AddWithValue("a", m.IsActive);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Resolves a connection string for a single authorized company.</summary>
    public async Task<string?> ResolveConnectionStringAsync(CompanyAccessInfo company, CancellationToken ct = default)
    {
        var dbMaps = await GetDatabaseMappingsAsync(ct);
        var dbHit = dbMaps.FirstOrDefault(m => m.IsActive && m.CompanyCode.Equals(company.Code, StringComparison.OrdinalIgnoreCase));
        if (dbHit is not null)
            return BuildReaderConnection(dbHit.DatabaseName, dbHit.Host, dbHit.Port);

        var tenant = MatchTenant(_tenants, company);
        if (tenant is not null)
        {
            var raw = !string.IsNullOrWhiteSpace(tenant.ReportConnection) ? tenant.ReportConnection : tenant.ConnectionString;
            if (!string.IsNullOrWhiteSpace(raw))
                return ApplyReaderCredentials(raw);
        }

        return null;
    }

    /// <summary>Pure helper to match a company against Tenants JSON by Host == company_code, Host == company.Host, or Name.</summary>
    public static TenantEntryConfig? MatchTenant(TenantsConfig tenants, CompanyAccessInfo company)
    {
        return tenants.Tenants.FirstOrDefault(t =>
                   t.Host.Equals(company.Code, StringComparison.OrdinalIgnoreCase))
               ?? tenants.Tenants.FirstOrDefault(t =>
                   !string.IsNullOrWhiteSpace(company.Host) &&
                   t.Host.Equals(company.Host, StringComparison.OrdinalIgnoreCase))
               ?? tenants.Tenants.FirstOrDefault(t =>
                   t.Name.Equals(company.Name, StringComparison.OrdinalIgnoreCase));
    }

    private string BuildReaderConnection(string databaseName, string? host, int? port)
    {
        var csb = new NpgsqlConnectionStringBuilder(_db.ReaderConnectionString)
        {
            Database = databaseName
        };
        if (!string.IsNullOrWhiteSpace(host)) csb.Host = host;
        if (port.HasValue && port.Value > 0) csb.Port = port.Value;
        return csb.ConnectionString;
    }

    private string ApplyReaderCredentials(string rawConn)
    {
        var target = new NpgsqlConnectionStringBuilder(rawConn);
        if (!string.IsNullOrWhiteSpace(_db.ReaderConnectionString))
        {
            var reader = new NpgsqlConnectionStringBuilder(_db.ReaderConnectionString);
            if (!string.IsNullOrWhiteSpace(reader.Username) &&
                !string.Equals(reader.Password, "CHANGE_ME", StringComparison.Ordinal))
            {
                target.Username = reader.Username;
                target.Password = reader.Password;
            }
        }
        return target.ConnectionString;
    }

    public NpgsqlDataSource GetOrCreateDataSource(string connectionString) =>
        _sources.GetOrAdd(connectionString, cs => new NpgsqlDataSourceBuilder(cs).Build());

    /// <summary>Introspects live rpt.* views in the selected company DB and merges friendly metadata from cat.erp_area, filtered by RBAC.</summary>
    public async Task<IReadOnlyList<ErpAreaViewMeta>> GetPermittedViewsAsync(
        ErpUserSession session,
        CompanyAccessInfo company,
        CancellationToken ct = default)
    {
        var companyConn = await ResolveConnectionStringAsync(company, ct);
        if (string.IsNullOrWhiteSpace(companyConn)) return [];

        var areaMeta = await LoadAreaMetadataAsync(ct);
        var viewCols = new Dictionary<string, List<ViewColumnMeta>>(StringComparer.OrdinalIgnoreCase);

        var ds = GetOrCreateDataSource(companyConn);
        await using (var conn = await ds.OpenConnectionAsync(ct))
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT table_name, column_name, data_type
                FROM information_schema.columns
                WHERE table_schema = 'rpt'
                ORDER BY table_name, ordinal_position
                """, conn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var v = r.GetString(0);
                var c = r.GetString(1);
                var t = r.GetString(2);
                if (!viewCols.TryGetValue(v, out var list)) viewCols[v] = list = new();
                if (!session.BlockedColumns.Contains(c))
                    list.Add(new ViewColumnMeta(c, t));
            }
        }

        var result = new List<ErpAreaViewMeta>();
        foreach (var (viewName, cols) in viewCols.OrderBy(k => k.Key))
        {
            if (!session.AllowsView(viewName)) continue;
            areaMeta.TryGetValue(viewName, out var m);
            result.Add(new ErpAreaViewMeta(
                ViewName: viewName,
                AreaName: m?.AreaName ?? InferAreaName(viewName),
                DisplayName: m?.DisplayName ?? viewName,
                Description: m?.Description ?? $"Reporting view rpt.{viewName}",
                DateColumn: m?.DateColumn,
                DefaultFields: m?.DefaultFields.Where(f => !session.BlockedColumns.Contains(f)).ToArray()
                               ?? cols.Take(6).Select(x => x.Name).ToArray(),
                SynonymsHint: m?.SynonymsHint ?? "",
                Columns: cols));
        }
        return result;
    }

    private async Task<Dictionary<string, ErpAreaViewMeta>> LoadAreaMetadataAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, ErpAreaViewMeta>(StringComparer.OrdinalIgnoreCase);
        await using var conn = new NpgsqlConnection(ConsoleAppConn);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT view_name, area_name, display_name, description, date_column, default_fields, COALESCE(synonyms_hint, '')
            FROM cat.erp_area
            """, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var vn = r.GetString(0);
            var defs = r.GetString(5).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            map[vn] = new ErpAreaViewMeta(
                vn, r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), defs, r.GetString(6), []);
        }
        return map;
    }

    private static string InferAreaName(string viewName)
    {
        var v = viewName.ToLowerInvariant();
        if (v.Contains("sales") || v.Contains("item")) return "Sales & Inventory";
        if (v.Contains("gl") || v.Contains("ledger") || v.Contains("trial") || v.Contains("tax") || v.Contains("bank") || v.Contains("fiscal") || v.Contains("currency")) return "Finance & GL";
        if (v.Contains("ar_")) return "Finance & AR";
        if (v.Contains("ap_")) return "Finance & AP";
        if (v.Contains("employee") || v.Contains("department") || v.Contains("designation")) return "HR & Employees";
        if (v.Contains("partner")) return "Business Partners";
        return "General";
    }

    /// <summary>Executes a compiled query against one company or fans out in parallel across all permitted companies and merges rows.</summary>
    public async Task<QueryResult> ExecuteCompiledAsync(
        ErpUserSession session,
        string companyCode,
        CompiledQuery query,
        IReadOnlyList<ViewColumnMeta> viewColumns,
        CancellationToken ct = default)
    {
        var targets = companyCode.Equals("ALL", StringComparison.OrdinalIgnoreCase)
            ? session.AllowedCompanies.ToList()
            : session.AllowedCompanies.Where(c => c.Code.Equals(companyCode, StringComparison.OrdinalIgnoreCase)).ToList();

        if (targets.Count == 0)
            throw new UnauthorizedAccessException($"Access denied for company {companyCode}.");

        var colTypes = viewColumns.ToDictionary(c => c.Name, c => c.DataType, StringComparer.OrdinalIgnoreCase);

        if (targets.Count == 1)
        {
            var cs = await ResolveConnectionStringAsync(targets[0], ct)
                ?? throw new InvalidOperationException($"No database mapping configured for company {targets[0].Code}.");
            return await RunSingleAsync(cs, targets[0].Code, query, colTypes, ct);
        }

        // Parallel C# Fan-Out & Merge across unique target databases
        var resolved = new List<(string CompanyCode, string ConnStr)>();
        foreach (var t in targets)
        {
            var cs = await ResolveConnectionStringAsync(t, ct);
            if (!string.IsNullOrWhiteSpace(cs)) resolved.Add((t.Code, cs));
        }

        var distinctDbs = resolved.GroupBy(x => x.ConnStr).Select(g => g.First()).ToList();
        var tasks = distinctDbs.Select(x => RunSingleAsync(x.ConnStr, x.CompanyCode, query, colTypes, ct)).ToArray();
        var results = await Task.WhenAll(tasks);

        var merged = new QueryResult
        {
            Columns = results.FirstOrDefault(r => r.Columns.Length > 0)?.Columns ?? query.Columns
        };
        foreach (var r in results)
        {
            merged.Rows.AddRange(r.Rows);
            merged.Truncated |= r.Truncated;
        }
        if (query.FetchLimit > 0 && merged.Rows.Count > query.FetchLimit)
        {
            merged.Rows = merged.Rows.Take(query.FetchLimit).ToList();
            merged.Truncated = true;
        }
        return merged;
    }

    private async Task<QueryResult> RunSingleAsync(
        string connStr,
        string companyCode,
        CompiledQuery query,
        IReadOnlyDictionary<string, string> colTypes,
        CancellationToken ct)
    {
        var ds = GetOrCreateDataSource(connStr);
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(query.Sql, conn)
        {
            CommandTimeout = query.CommandTimeoutSeconds
        };
        foreach (var kv in query.Parameters)
        {
            var val = kv.Value;
            if (val is string s && query.ParameterColumns.TryGetValue(kv.Key, out var col) && colTypes.TryGetValue(col, out var dt))
                val = CoerceParam(s, dt);
            cmd.Parameters.AddWithValue(kv.Key, val ?? DBNull.Value);
        }

        var res = new QueryResult();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        res.Columns = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToArray();
        while (await r.ReadAsync(ct))
        {
            if (query.FetchLimit > 0 && res.Rows.Count >= query.FetchLimit)
            {
                res.Truncated = true;
                break;
            }
            var row = new object?[r.FieldCount];
            for (var i = 0; i < r.FieldCount; i++)
                row[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            res.Rows.Add(row);
        }
        return res;
    }

    private static object CoerceParam(string value, string dataType)
    {
        var t = dataType.ToLowerInvariant();
        if (t.Contains("int") && long.TryParse(value, out var l)) return l;
        if ((t.Contains("numeric") || t.Contains("decimal") || t.Contains("real") || t.Contains("double")) && decimal.TryParse(value, out var d)) return d;
        if (t.Contains("bool") && bool.TryParse(value, out var b)) return b;
        if (t.Contains("date") || t.Contains("timestamp"))
        {
            if (DateTime.TryParse(value, out var dt))
                return t == "date" ? DateOnly.FromDateTime(dt) : dt;
            throw new ArgumentException($"Invalid date value '{value}'. Use YYYY-MM-DD format.");
        }
        return value;
    }

    public async Task<Dictionary<string, ViewColumn[]>> GetSourceTablesAsync(
        CompanyAccessInfo company,
        string[] allowedSchemas,
        CancellationToken ct = default)
    {
        var cs = await ResolveConnectionStringAsync(company, ct)
            ?? throw new InvalidOperationException($"No database mapped for {company.Code}.");
        var ds = GetOrCreateDataSource(ApplyDesignerCredentials(cs));
        var map = new Dictionary<string, List<ViewColumn>>(StringComparer.OrdinalIgnoreCase);

        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT table_schema || '.' || table_name AS src, column_name, data_type
            FROM information_schema.columns
            WHERE table_schema = ANY(@schemas)
            ORDER BY table_schema, table_name, ordinal_position
            """, conn);
        cmd.Parameters.AddWithValue("schemas", allowedSchemas);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var src = r.GetString(0);
            if (!map.TryGetValue(src, out var list)) map[src] = list = new();
            list.Add(new ViewColumn(r.GetString(1), r.GetString(2)));
        }
        return map.ToDictionary(k => k.Key, k => k.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    public async Task CreateApprovedViewAsync(
        CompanyAccessInfo company,
        string viewName,
        string selectSql,
        string areaName,
        string description,
        string defaultFields,
        CancellationToken ct = default)
    {
        var cs = await ResolveConnectionStringAsync(company, ct)
            ?? throw new InvalidOperationException($"No database mapped for {company.Code}.");
        var designerConn = ApplyDesignerCredentials(cs);

        await using (var conn = new NpgsqlConnection(designerConn))
        {
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand($"""
                CREATE SCHEMA IF NOT EXISTS rpt;
                CREATE OR REPLACE VIEW rpt."{viewName}" AS {selectSql};
                GRANT USAGE ON SCHEMA rpt TO rb_reader;
                GRANT SELECT ON rpt."{viewName}" TO rb_reader;
                """, conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using var appConn = new NpgsqlConnection(ConsoleAppConn);
        await appConn.OpenAsync(ct);
        await using (var areaCmd = new NpgsqlCommand("""
            INSERT INTO cat.erp_area (view_name, area_name, display_name, description, default_fields)
            VALUES (@vn, @an, @dn, @desc, @df)
            ON CONFLICT (view_name) DO UPDATE
            SET area_name = EXCLUDED.area_name,
                display_name = EXCLUDED.display_name,
                description = EXCLUDED.description,
                default_fields = EXCLUDED.default_fields
            """, appConn))
        {
            areaCmd.Parameters.AddWithValue("vn", viewName);
            areaCmd.Parameters.AddWithValue("an", areaName);
            areaCmd.Parameters.AddWithValue("dn", viewName.Replace("_v", "").Replace('_', ' '));
            areaCmd.Parameters.AddWithValue("desc", description);
            areaCmd.Parameters.AddWithValue("df", defaultFields);
            await areaCmd.ExecuteNonQueryAsync(ct);
        }

        await using (var mapCmd = new NpgsqlCommand("""
            INSERT INTO cat.module_view_map (role_name, menuid, area_name, view_name, allow_all_for_view_all_flags, blocked_columns)
            SELECT NULL, 4, @an, @vn, true, ''
            WHERE NOT EXISTS (SELECT 1 FROM cat.module_view_map WHERE view_name = @vn)
            """, appConn))
        {
            mapCmd.Parameters.AddWithValue("an", areaName);
            mapCmd.Parameters.AddWithValue("vn", viewName);
            await mapCmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task MarkViewDraftReviewedAsync(int draftId, bool approve, string reviewer, string note, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(ConsoleAppConn);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            UPDATE cat.viewdraft
            SET status = @st, reviewed_by = @rb, reviewed_at = (now() at time zone 'utc'), review_note = @note
            WHERE draft_id = @id
            """, conn);
        cmd.Parameters.AddWithValue("st", approve ? "APPROVED" : "REJECTED");
        cmd.Parameters.AddWithValue("rb", reviewer);
        cmd.Parameters.AddWithValue("note", note);
        cmd.Parameters.AddWithValue("id", draftId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private string ApplyDesignerCredentials(string rawConn)
    {
        var target = new NpgsqlConnectionStringBuilder(rawConn);
        if (!string.IsNullOrWhiteSpace(_db.DesignerConnectionString))
        {
            var d = new NpgsqlConnectionStringBuilder(_db.DesignerConnectionString);
            if (!string.IsNullOrWhiteSpace(d.Username)) target.Username = d.Username;
            if (!string.IsNullOrWhiteSpace(d.Password)) target.Password = d.Password;
        }
        return target.ConnectionString;
    }

    public ValueTask DisposeAsync()
    {
        foreach (var ds in _sources.Values) ds.Dispose();
        _sources.Clear();
        return ValueTask.CompletedTask;
    }
}
