using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Npgsql;
using ReportBuilder.Core.Data;

namespace ReportBuilder.Core.Pipeline;

public sealed record AiPlanCacheStats(
    int MemoryEntries,
    long MemoryHits,
    long DatabaseHits,
    long Misses,
    long Stores);

/// <summary>
/// RBAC-aware, schema-versioned Two-Tier (In-Memory L1 + PostgreSQL L2) cache for validated AI query plans.
/// Cache keys include the normalized question, target company, max row limit, and a SHA-256 hash of the
/// user's exact permitted views and blocked columns so two users with different RBAC never share a cache key,
/// and schema updates automatically invalidate stale entries.
/// </summary>
public sealed class AiPlanCache
{
    private const int MaxMemoryEntries = 1000;
    private static readonly TimeSpan EntryTtl = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed record CachedEntry(AiConfigureResult Result, string SchemaHash, DateTime CreatedUtc);

    private readonly ConcurrentDictionary<string, CachedEntry> _memory = new(StringComparer.Ordinal);
    private readonly string? _appConnString;
    private long _memoryHits;
    private long _dbHits;
    private long _misses;
    private long _stores;

    public AiPlanCache(IOptions<DatabaseOptions>? dbOptions = null)
    {
        var cs = !string.IsNullOrWhiteSpace(dbOptions?.Value?.AppConnectionString)
            ? dbOptions.Value.AppConnectionString
            : dbOptions?.Value?.ReaderConnectionString;
        _appConnString = !string.IsNullOrWhiteSpace(cs) && !cs.Contains("Password=CHANGE_ME", StringComparison.OrdinalIgnoreCase)
            ? cs
            : null;
    }

    public static string NormalizeQuestion(string? question)
    {
        if (string.IsNullOrWhiteSpace(question)) return "";
        var collapsed = Regex.Replace(question.Trim().ToLowerInvariant(), @"\s+", " ");
        return collapsed.TrimEnd('?', '!', '.', ' ');
    }

    public static string ComputeSchemaHash(
        IReadOnlyList<ErpAreaViewMeta> permittedViews,
        IReadOnlySet<string>? blockedColumns = null)
    {
        var blocked = blockedColumns ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var viewParts = permittedViews
            .OrderBy(v => v.ViewName, StringComparer.OrdinalIgnoreCase)
            .Select(v =>
            {
                var cols = v.Columns
                    .Where(c => !blocked.Contains(c.Name))
                    .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(c => $"{c.Name.ToLowerInvariant()}:{c.DataType.ToLowerInvariant()}");
                return $"{v.ViewName.ToLowerInvariant()}({string.Join(",", cols)})";
            });
        var blockedPart = string.Join(",", blocked.Select(b => b.ToLowerInvariant()).OrderBy(b => b, StringComparer.Ordinal));
        var payload = string.Join("|", viewParts) + "|blocked:" + blockedPart;
        return Sha256Hex(payload);
    }

    public static string ComputeCacheKey(
        string question,
        string companyCode,
        IReadOnlyList<ErpAreaViewMeta> permittedViews,
        IReadOnlySet<string>? blockedColumns = null,
        int maxAllowedRows = 5000)
    {
        var normQ = NormalizeQuestion(question);
        var comp = (companyCode ?? "").Trim().ToUpperInvariant();
        var schemaHash = ComputeSchemaHash(permittedViews, blockedColumns);
        return Sha256Hex($"{normQ}|{comp}|{schemaHash}|{maxAllowedRows}");
    }

    public async Task<AiConfigureResult?> GetAsync(
        string question,
        string companyCode,
        IReadOnlyList<ErpAreaViewMeta> permittedViews,
        IReadOnlySet<string>? blockedColumns = null,
        int maxAllowedRows = 5000,
        CancellationToken ct = default)
    {
        var key = ComputeCacheKey(question, companyCode, permittedViews, blockedColumns, maxAllowedRows);
        var now = DateTime.UtcNow;

        if (_memory.TryGetValue(key, out var mem))
        {
            if (now - mem.CreatedUtc <= EntryTtl)
            {
                Interlocked.Increment(ref _memoryHits);
                return CloneAsCached(mem.Result, "Memory");
            }
            _memory.TryRemove(key, out _);
        }

        if (!string.IsNullOrWhiteSpace(_appConnString))
        {
            try
            {
                await using var conn = new NpgsqlConnection(_appConnString);
                await conn.OpenAsync(ct);

                string? json = null;
                string? schemaHash = null;
                await using (var cmd = new NpgsqlCommand("""
                    SELECT result_json, schema_hash, created_at_utc
                    FROM cat.ai_plan_cache
                    WHERE cache_key = @k
                    """, conn))
                {
                    cmd.Parameters.AddWithValue("k", key);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    if (await r.ReadAsync(ct))
                    {
                        var created = r.GetDateTime(2);
                        if (now - created <= EntryTtl)
                        {
                            json = r.GetString(0);
                            schemaHash = r.GetString(1);
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(json))
                {
                    var parsed = JsonSerializer.Deserialize<AiConfigureResult>(json, JsonOpts);
                    if (parsed is not null)
                    {
                        await using (var upd = new NpgsqlCommand("""
                            UPDATE cat.ai_plan_cache
                            SET hit_count = hit_count + 1,
                                last_hit_at_utc = (now() at time zone 'utc')
                            WHERE cache_key = @k
                            """, conn))
                        {
                            upd.Parameters.AddWithValue("k", key);
                            await upd.ExecuteNonQueryAsync(ct);
                        }

                        StoreInMemory(key, new CachedEntry(CloneForStorage(parsed), schemaHash ?? "", now));
                        Interlocked.Increment(ref _dbHits);
                        return CloneAsCached(parsed, "Database");
                    }
                }
            }
            catch (NpgsqlException)
            {
                // Best-effort L2: ignore if cat.ai_plan_cache isn't migrated yet or DB unavailable
            }
        }

        Interlocked.Increment(ref _misses);
        return null;
    }

    public async Task SetAsync(
        string question,
        string companyCode,
        IReadOnlyList<ErpAreaViewMeta> permittedViews,
        IReadOnlySet<string>? blockedColumns,
        int maxAllowedRows,
        AiConfigureResult result,
        CancellationToken ct = default)
    {
        if (result.IsOutOfScope || result.GuardrailBlocked || (result.Request is null && !result.IsReconcile))
            return;

        var schemaHash = ComputeSchemaHash(permittedViews, blockedColumns);
        var key = ComputeCacheKey(question, companyCode, permittedViews, blockedColumns, maxAllowedRows);
        var stored = CloneForStorage(result);
        StoreInMemory(key, new CachedEntry(stored, schemaHash, DateTime.UtcNow));
        Interlocked.Increment(ref _stores);

        if (!string.IsNullOrWhiteSpace(_appConnString))
        {
            try
            {
                var normQ = NormalizeQuestion(question);
                var json = JsonSerializer.Serialize(stored, JsonOpts);
                await using var conn = new NpgsqlConnection(_appConnString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand("""
                    INSERT INTO cat.ai_plan_cache (cache_key, normalized_question, company_code, schema_hash, result_json, hit_count, created_at_utc, last_hit_at_utc)
                    VALUES (@k, @q, @c, @h, @j, 0, (now() at time zone 'utc'), (now() at time zone 'utc'))
                    ON CONFLICT (cache_key) DO UPDATE
                    SET schema_hash = EXCLUDED.schema_hash,
                        result_json = EXCLUDED.result_json,
                        created_at_utc = (now() at time zone 'utc'),
                        last_hit_at_utc = (now() at time zone 'utc')
                    """, conn);
                cmd.Parameters.AddWithValue("k", key);
                cmd.Parameters.AddWithValue("q", normQ);
                cmd.Parameters.AddWithValue("c", (companyCode ?? "").Trim().ToUpperInvariant());
                cmd.Parameters.AddWithValue("h", schemaHash);
                cmd.Parameters.AddWithValue("j", json);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (NpgsqlException)
            {
                // Best-effort L2 write
            }
        }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        _memory.Clear();
        if (!string.IsNullOrWhiteSpace(_appConnString))
        {
            try
            {
                await using var conn = new NpgsqlConnection(_appConnString);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand("DELETE FROM cat.ai_plan_cache", conn);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (NpgsqlException)
            {
                // Best-effort L2 clear
            }
        }
    }

    public AiPlanCacheStats GetStats() => new(
        _memory.Count,
        Interlocked.Read(ref _memoryHits),
        Interlocked.Read(ref _dbHits),
        Interlocked.Read(ref _misses),
        Interlocked.Read(ref _stores));

    private void StoreInMemory(string key, CachedEntry entry)
    {
        if (_memory.Count >= MaxMemoryEntries)
        {
            var oldestKeys = _memory
                .OrderBy(kv => kv.Value.CreatedUtc)
                .Take(Math.Max(1, MaxMemoryEntries / 10))
                .Select(kv => kv.Key)
                .ToArray();
            foreach (var oldKey in oldestKeys)
                _memory.TryRemove(oldKey, out _);
        }
        _memory[key] = entry;
    }

    private static AiConfigureResult CloneForStorage(AiConfigureResult src) => new()
    {
        IsReconcile = src.IsReconcile,
        ReconcileAreas = src.ReconcileAreas.ToArray(),
        Request = CloneRequest(src.Request),
        Explanation = src.Explanation,
        MissingDataNote = src.MissingDataNote,
        IsOutOfScope = src.IsOutOfScope,
        GuardrailBlocked = src.GuardrailBlocked,
        GuardrailCategory = src.GuardrailCategory,
        GuardrailWarnings = src.GuardrailWarnings.ToArray(),
        FromCache = false,
        CacheSource = null
    };

    private static AiConfigureResult CloneAsCached(AiConfigureResult src, string source)
    {
        var copy = CloneForStorage(src);
        copy.FromCache = true;
        copy.CacheSource = source;
        return copy;
    }

    private static DynamicReportRequest? CloneRequest(DynamicReportRequest? r)
    {
        if (r is null) return null;
        return new DynamicReportRequest
        {
            ViewName = r.ViewName,
            CompanyCode = r.CompanyCode,
            SelectedColumns = r.SelectedColumns.ToList(),
            Aggregations = r.Aggregations.Select(a => new AggregationSpec { Column = a.Column, Function = a.Function, Alias = a.Alias }).ToList(),
            Filters = r.Filters.Select(f => new FilterSpec { Column = f.Column, Op = f.Op, Value = f.Value }).ToList(),
            HavingFilters = r.HavingFilters.Select(h => new HavingFilterSpec { Alias = h.Alias, Op = h.Op, Value = h.Value }).ToList(),
            RelativePeriod = r.RelativePeriod,
            DateColumn = r.DateColumn,
            GroupBy = r.GroupBy.ToList(),
            OrderBy = r.OrderBy.Select(o => new SortSpec { Column = o.Column, Direction = o.Direction }).ToList(),
            Limit = r.Limit,
            ChartType = r.ChartType
        };
    }

    private static string Sha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
