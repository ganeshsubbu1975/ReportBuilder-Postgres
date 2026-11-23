using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using ReportBuilder.Core.Pipeline;
using ReportBuilder.Core.Security;

namespace ReportBuilder.Core.Data;

public sealed class SavedReportEntry
{
    public int ReportId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string OwnerUser { get; set; } = "";
    public bool IsShared { get; set; }
    public string CompanyCode { get; set; } = "";
    public string ViewName { get; set; } = "";
    public string PromptText { get; set; } = "";
    public string RequestJson { get; set; } = "";
    public int RunCount { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastRunAtUtc { get; set; }
}

public sealed record CreateSavedReportInput(
    string Name,
    string? Description,
    bool IsShared,
    string CompanyCode,
    string ViewName,
    string? PromptText,
    DynamicReportRequest Request);

public interface ISavedReportStore
{
    Task<IReadOnlyList<SavedReportEntry>> ListVisibleAsync(ErpUserSession session, CancellationToken ct = default);
    Task<SavedReportEntry?> GetByIdAsync(int reportId, CancellationToken ct = default);
    Task<SavedReportEntry> SaveAsync(CreateSavedReportInput input, string ownerUser, CancellationToken ct = default);
    Task RecordRunAsync(int reportId, CancellationToken ct = default);
    Task<bool> DeleteAsync(int reportId, string requestingUser, bool isAdmin, CancellationToken ct = default);
}

/// <summary>
/// Persists user-saved report specs in PostgreSQL (cat.saved_report) for zero-token deterministic replay,
/// falling back cleanly to local thread-safe JSON storage when DB table 60 is not applied yet or in offline unit tests.
/// </summary>
public sealed class SqlSavedReportStore : ISavedReportStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions RequestJsonOpts = new() { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string? _appConnString;
    private readonly string _fallbackPath;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    private SqlSavedReportStore(string? fallbackJsonPath)
    {
        _appConnString = null;
        _fallbackPath = fallbackJsonPath ?? Path.Combine(Directory.GetCurrentDirectory(), ".local", "saved_reports.json");
    }

    public static SqlSavedReportStore CreateFileBacked(string? fallbackJsonPath = null) => new(fallbackJsonPath);

    public SqlSavedReportStore(IOptions<DatabaseOptions> dbOptions)
    {
        var cs = !string.IsNullOrWhiteSpace(dbOptions.Value.AppConnectionString)
            ? dbOptions.Value.AppConnectionString
            : dbOptions.Value.ReaderConnectionString;
        _appConnString = !string.IsNullOrWhiteSpace(cs) && !cs.Contains("Password=CHANGE_ME", StringComparison.OrdinalIgnoreCase)
            ? cs
            : null;
        _fallbackPath = Path.Combine(Directory.GetCurrentDirectory(), ".local", "saved_reports.json");
    }

    public async Task<IReadOnlyList<SavedReportEntry>> ListVisibleAsync(ErpUserSession session, CancellationToken ct = default)
    {
        IReadOnlyList<SavedReportEntry> all;
        if (!string.IsNullOrWhiteSpace(_appConnString))
        {
            try
            {
                var rows = new List<SavedReportEntry>();
                await using var conn = new NpgsqlConnection(_appConnString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand("""
                    SELECT report_id, name, description, owner_user, is_shared, company_code, view_name, prompt_text, request_json, run_count, created_at_utc, last_run_at_utc
                    FROM cat.saved_report
                    WHERE LOWER(owner_user) = LOWER(@u) OR is_shared = true
                    ORDER BY COALESCE(last_run_at_utc, created_at_utc) DESC, report_id DESC
                    """, conn);
                cmd.Parameters.AddWithValue("u", session.UserName);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    rows.Add(ReadRow(r));
                return FilterByRbac(rows, session);
            }
            catch (NpgsqlException)
            {
                // Fall back to local JSON store
            }
        }

        all = await ReadFallbackAsync(ct);
        return FilterByRbac(
            all.Where(x => x.OwnerUser.Equals(session.UserName, StringComparison.OrdinalIgnoreCase) || x.IsShared)
               .OrderByDescending(x => x.LastRunAtUtc ?? x.CreatedAtUtc)
               .ThenByDescending(x => x.ReportId),
            session);
    }

    public async Task<SavedReportEntry?> GetByIdAsync(int reportId, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(_appConnString))
        {
            try
            {
                await using var conn = new NpgsqlConnection(_appConnString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand("""
                    SELECT report_id, name, description, owner_user, is_shared, company_code, view_name, prompt_text, request_json, run_count, created_at_utc, last_run_at_utc
                    FROM cat.saved_report
                    WHERE report_id = @id
                    """, conn);
                cmd.Parameters.AddWithValue("id", reportId);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                return await r.ReadAsync(ct) ? ReadRow(r) : null;
            }
            catch (NpgsqlException)
            {
                // Fall back to local JSON store
            }
        }

        var all = await ReadFallbackAsync(ct);
        return all.FirstOrDefault(x => x.ReportId == reportId);
    }

    public async Task<SavedReportEntry> SaveAsync(CreateSavedReportInput input, string ownerUser, CancellationToken ct = default)
    {
        var reqJson = JsonSerializer.Serialize(input.Request, RequestJsonOpts);
        var desc = (input.Description ?? "").Trim();
        var prompt = (input.PromptText ?? "").Trim();

        if (!string.IsNullOrWhiteSpace(_appConnString))
        {
            try
            {
                await using var conn = new NpgsqlConnection(_appConnString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand("""
                    INSERT INTO cat.saved_report (name, description, owner_user, is_shared, company_code, view_name, prompt_text, request_json, run_count, created_at_utc)
                    VALUES (@n, @d, @o, @s, @c, @v, @p, @rj, 0, (now() at time zone 'utc'))
                    RETURNING report_id, created_at_utc
                    """, conn);
                cmd.Parameters.AddWithValue("n", input.Name.Trim());
                cmd.Parameters.AddWithValue("d", desc);
                cmd.Parameters.AddWithValue("o", ownerUser);
                cmd.Parameters.AddWithValue("s", input.IsShared);
                cmd.Parameters.AddWithValue("c", input.CompanyCode.Trim());
                cmd.Parameters.AddWithValue("v", input.ViewName.Trim());
                cmd.Parameters.AddWithValue("p", prompt);
                cmd.Parameters.AddWithValue("rj", reqJson);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                await r.ReadAsync(ct);
                return new SavedReportEntry
                {
                    ReportId = r.GetInt32(0),
                    Name = input.Name.Trim(),
                    Description = desc,
                    OwnerUser = ownerUser,
                    IsShared = input.IsShared,
                    CompanyCode = input.CompanyCode.Trim(),
                    ViewName = input.ViewName.Trim(),
                    PromptText = prompt,
                    RequestJson = reqJson,
                    RunCount = 0,
                    CreatedAtUtc = r.GetDateTime(1)
                };
            }
            catch (NpgsqlException)
            {
                // Fall back to local JSON store
            }
        }

        await _fileLock.WaitAsync(ct);
        try
        {
            var list = await ReadFallbackUnlockedAsync(ct);
            var nextId = list.Count == 0 ? 1 : list.Max(x => x.ReportId) + 1;
            var entry = new SavedReportEntry
            {
                ReportId = nextId,
                Name = input.Name.Trim(),
                Description = desc,
                OwnerUser = ownerUser,
                IsShared = input.IsShared,
                CompanyCode = input.CompanyCode.Trim(),
                ViewName = input.ViewName.Trim(),
                PromptText = prompt,
                RequestJson = reqJson,
                RunCount = 0,
                CreatedAtUtc = DateTime.UtcNow
            };
            list.Add(entry);
            await WriteFallbackUnlockedAsync(list, ct);
            return entry;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task RecordRunAsync(int reportId, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(_appConnString))
        {
            try
            {
                await using var conn = new NpgsqlConnection(_appConnString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand("""
                    UPDATE cat.saved_report
                    SET run_count = run_count + 1,
                        last_run_at_utc = (now() at time zone 'utc')
                    WHERE report_id = @id
                    """, conn);
                cmd.Parameters.AddWithValue("id", reportId);
                await cmd.ExecuteNonQueryAsync(ct);
                return;
            }
            catch (NpgsqlException)
            {
                // Fall back to local JSON store
            }
        }

        await _fileLock.WaitAsync(ct);
        try
        {
            var list = await ReadFallbackUnlockedAsync(ct);
            var hit = list.FirstOrDefault(x => x.ReportId == reportId);
            if (hit is not null)
            {
                hit.RunCount++;
                hit.LastRunAtUtc = DateTime.UtcNow;
                await WriteFallbackUnlockedAsync(list, ct);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<bool> DeleteAsync(int reportId, string requestingUser, bool isAdmin, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(_appConnString))
        {
            try
            {
                await using var conn = new NpgsqlConnection(_appConnString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand("""
                    DELETE FROM cat.saved_report
                    WHERE report_id = @id AND (@admin = true OR LOWER(owner_user) = LOWER(@u))
                    """, conn);
                cmd.Parameters.AddWithValue("id", reportId);
                cmd.Parameters.AddWithValue("admin", isAdmin);
                cmd.Parameters.AddWithValue("u", requestingUser);
                var affected = await cmd.ExecuteNonQueryAsync(ct);
                return affected > 0;
            }
            catch (NpgsqlException)
            {
                // Fall back to local JSON store
            }
        }

        await _fileLock.WaitAsync(ct);
        try
        {
            var list = await ReadFallbackUnlockedAsync(ct);
            var hit = list.FirstOrDefault(x => x.ReportId == reportId);
            if (hit is null) return false;
            if (!isAdmin && !hit.OwnerUser.Equals(requestingUser, StringComparison.OrdinalIgnoreCase))
                return false;
            list.Remove(hit);
            await WriteFallbackUnlockedAsync(list, ct);
            return true;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private static IReadOnlyList<SavedReportEntry> FilterByRbac(IEnumerable<SavedReportEntry> rows, ErpUserSession session) =>
        rows.Where(r =>
            session.AllowsView(r.ViewName) &&
            (r.CompanyCode.Equals("ALL", StringComparison.OrdinalIgnoreCase) || session.AllowsCompany(r.CompanyCode)))
            .ToList();

    private static SavedReportEntry ReadRow(NpgsqlDataReader r) => new()
    {
        ReportId = r.GetInt32(0),
        Name = r.GetString(1),
        Description = r.GetString(2),
        OwnerUser = r.GetString(3),
        IsShared = r.GetBoolean(4),
        CompanyCode = r.GetString(5),
        ViewName = r.GetString(6),
        PromptText = r.GetString(7),
        RequestJson = r.GetString(8),
        RunCount = r.GetInt32(9),
        CreatedAtUtc = r.GetDateTime(10),
        LastRunAtUtc = r.IsDBNull(11) ? null : r.GetDateTime(11)
    };

    private async Task<List<SavedReportEntry>> ReadFallbackAsync(CancellationToken ct)
    {
        await _fileLock.WaitAsync(ct);
        try { return await ReadFallbackUnlockedAsync(ct); }
        finally { _fileLock.Release(); }
    }

    private async Task<List<SavedReportEntry>> ReadFallbackUnlockedAsync(CancellationToken ct)
    {
        if (!File.Exists(_fallbackPath)) return [];
        var json = await File.ReadAllTextAsync(_fallbackPath, ct);
        if (string.IsNullOrWhiteSpace(json)) return [];
        return JsonSerializer.Deserialize<List<SavedReportEntry>>(json, JsonOpts) ?? [];
    }

    private async Task WriteFallbackUnlockedAsync(List<SavedReportEntry> list, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_fallbackPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(list, JsonOpts);
        await File.WriteAllTextAsync(_fallbackPath, json, ct);
    }
}
