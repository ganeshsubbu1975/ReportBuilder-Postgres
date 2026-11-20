using Microsoft.Extensions.Options;
using Npgsql;

namespace ReportBuilder.Core.Security;

public sealed record CompanyAccessInfo(string Code, string Name, string Host, bool IsDefault);

public sealed class RbacRule
{
    public int Id { get; set; }
    public string? RoleName { get; set; }
    public string? ClaimType { get; set; }
    public string? ClaimValue { get; set; }
    public int? MenuId { get; set; }
    public string? AreaName { get; set; }
    public string ViewName { get; set; } = "";
    public bool AllowAllForViewAllFlags { get; set; } = true;
    public string BlockedColumns { get; set; } = "";
}

public sealed class ErpUserSession
{
    public string UserName { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool IsActive { get; init; }
    public bool ViewAllFlags { get; init; }
    public int UserType { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = [];
    public IReadOnlyList<(string Type, string Value)> RoleClaims { get; init; } = [];
    public IReadOnlyList<string> CompanyRoles { get; init; } = [];
    public IReadOnlyList<CompanyAccessInfo> AllowedCompanies { get; init; } = [];
    public IReadOnlyList<int> AllowedMenuIds { get; init; } = [];
    public IReadOnlySet<string> AllowedViews { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> BlockedColumns { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public bool AllowAllViews { get; init; }
    public bool IsAdmin { get; init; }

    public bool AllowsCompany(string companyCode) =>
        AllowedCompanies.Any(c => c.Code.Equals(companyCode, StringComparison.OrdinalIgnoreCase));

    public bool AllowsView(string viewName) =>
        AllowAllViews || AllowedViews.Contains("*") || AllowedViews.Contains(viewName);
}

public sealed record ErpUserSummary(string UserName, string DisplayName, bool IsActive, bool ViewAllFlags, int UserType);

/// <summary>
/// Resolves user identity, company access, and module/view RBAC from the ERP PostgreSQL tables
/// (common."AspNetUsers", "AspNetUserRoles", "AspNetRoles", "AspNetRoleClaims", "AspNetUserClaims",
/// common.consoleusermenuaccess, common.usercompany, common.company, and cat.module_view_map),
/// plus optional company-DB common.userrole/role/usermodule checks.
/// </summary>
public sealed class ErpSecurityProvider
{
    private readonly DatabaseOptions _db;
    private readonly PolicyOptions _policy;

    public ErpSecurityProvider(IOptions<DatabaseOptions> db, IOptions<PolicyOptions> policy)
    {
        _db = db.Value;
        _policy = policy.Value;
    }

    private string ConsoleConn => !string.IsNullOrWhiteSpace(_db.AppConnectionString)
        ? _db.AppConnectionString
        : _db.ReaderConnectionString;

    public async Task<IReadOnlyList<ErpUserSummary>> ListUsersAsync(int limit = 100, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(ConsoleConn);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT "UserName",
                   COALESCE(NULLIF(TRIM(COALESCE("FirstName", '') || ' ' || COALESCE("LastName", '')), ''), "UserName") AS display_name,
                   COALESCE("IsActive", true) AS is_active,
                   COALESCE("ViewAllFlags", false) AS view_all_flags,
                   COALESCE("UserType", 0) AS user_type
            FROM common."AspNetUsers"
            WHERE COALESCE("IsActive", true) = true
            ORDER BY "UserName"
            LIMIT @lim
            """, conn);
        cmd.Parameters.AddWithValue("lim", limit);
        var list = new List<ErpUserSummary>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new ErpUserSummary(r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetBoolean(3), r.GetInt32(4)));
        return list;
    }

    public async Task<ErpUserSession?> ResolveUserAsync(string userName, string? selectedCompanyConn = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userName)) return null;
        await using var conn = new NpgsqlConnection(ConsoleConn);
        await conn.OpenAsync(ct);

        string? userId = null;
        string actualUserName = userName;
        string displayName = userName;
        bool isActive = false, viewAllFlags = false;
        int userType = 0;

        await using (var cmd = new NpgsqlCommand("""
            SELECT "Id", "UserName",
                   COALESCE(NULLIF(TRIM(COALESCE("FirstName", '') || ' ' || COALESCE("LastName", '')), ''), "UserName"),
                   COALESCE("IsActive", true),
                   COALESCE("ViewAllFlags", false),
                   COALESCE("UserType", 0)
            FROM common."AspNetUsers"
            WHERE LOWER("UserName") = LOWER(@u)
            LIMIT 1
            """, conn))
        {
            cmd.Parameters.AddWithValue("u", userName.Trim());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            userId = r.GetString(0);
            actualUserName = r.GetString(1);
            displayName = r.GetString(2);
            isActive = r.GetBoolean(3);
            viewAllFlags = r.GetBoolean(4);
            userType = r.GetInt32(5);
        }

        if (!isActive)
        {
            return new ErpUserSession
            {
                UserName = actualUserName,
                DisplayName = displayName,
                IsActive = false
            };
        }

        var roles = new List<string>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT r."Name"
            FROM common."AspNetUserRoles" ur
            JOIN common."AspNetRoles" r ON r."Id" = ur."RoleId"
            WHERE ur."UserId" = @uid AND r."Name" IS NOT NULL
            """, conn))
        {
            cmd.Parameters.AddWithValue("uid", userId!);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) roles.Add(r.GetString(0));
        }

        var claims = new List<(string Type, string Value)>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT rc."ClaimType", rc."ClaimValue"
            FROM common."AspNetUserRoles" ur
            JOIN common."AspNetRoleClaims" rc ON rc."RoleId" = ur."RoleId"
            WHERE ur."UserId" = @uid AND rc."ClaimType" IS NOT NULL AND rc."ClaimValue" IS NOT NULL
            UNION
            SELECT uc."ClaimType", uc."ClaimValue"
            FROM common."AspNetUserClaims" uc
            WHERE uc."UserId" = @uid AND uc."ClaimType" IS NOT NULL AND uc."ClaimValue" IS NOT NULL
            """, conn))
        {
            cmd.Parameters.AddWithValue("uid", userId!);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) claims.Add((r.GetString(0), r.GetString(1)));
        }

        var menuIds = new List<int>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT DISTINCT menuid
            FROM common.consoleusermenuaccess
            WHERE LOWER(username) = LOWER(@u) AND NOT COALESCE(isarchived, false)
            ORDER BY menuid
            """, conn))
        {
            cmd.Parameters.AddWithValue("u", actualUserName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) menuIds.Add(r.GetInt32(0));
        }

        var companies = new List<CompanyAccessInfo>();
        var companySql = viewAllFlags
            ? """
              SELECT c.code, c.name, COALESCE(c.host, ''),
                     EXISTS(SELECT 1 FROM common.usercompany uc WHERE uc.companyid = c.id AND LOWER(uc.username) = LOWER(@u) AND COALESCE(uc.isdefault, false) AND NOT COALESCE(uc.isarchived, false)) AS is_default
              FROM common.company c
              WHERE NOT COALESCE(c.isarchived, false)
              ORDER BY is_default DESC, c.code
              """
            : """
              SELECT c.code, c.name, COALESCE(c.host, ''), COALESCE(uc.isdefault, false)
              FROM common.usercompany uc
              JOIN common.company c ON c.id = uc.companyid
              WHERE LOWER(uc.username) = LOWER(@u)
                AND NOT COALESCE(uc.isarchived, false)
                AND NOT COALESCE(c.isarchived, false)
              ORDER BY COALESCE(uc.isdefault, false) DESC, c.code
              """;
        await using (var cmd = new NpgsqlCommand(companySql, conn))
        {
            cmd.Parameters.AddWithValue("u", actualUserName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                companies.Add(new CompanyAccessInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3)));
        }

        var rules = await GetRbacRulesAsync(conn, ct);
        var companyRoles = string.IsNullOrWhiteSpace(selectedCompanyConn)
            ? new List<string>()
            : await TryLoadCompanyRolesAsync(selectedCompanyConn, actualUserName, ct);

        return ResolveFromData(actualUserName, displayName, isActive, viewAllFlags, userType,
            roles, claims, companyRoles, companies, menuIds, rules, _policy.BlockedColumns);
    }

    /// <summary>Deterministic pure RBAC evaluation over loaded DB rows (unit-testable without a database).</summary>
    public static ErpUserSession ResolveFromData(
        string userName,
        string displayName,
        bool isActive,
        bool viewAllFlags,
        int userType,
        IReadOnlyList<string> roles,
        IReadOnlyList<(string Type, string Value)> roleClaims,
        IReadOnlyList<string> companyRoles,
        IReadOnlyList<CompanyAccessInfo> companies,
        IReadOnlyList<int> menuIds,
        IReadOnlyList<RbacRule> rules,
        IEnumerable<string> globalBlockedColumns)
    {
        var allowedViews = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blocked = new HashSet<string>(globalBlockedColumns ?? [], StringComparer.OrdinalIgnoreCase);
        var allViews = false;

        var allRoles = roles.Concat(companyRoles).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var menuSet = menuIds.ToHashSet();

        foreach (var rule in rules)
        {
            var matches = false;

            if (!string.IsNullOrWhiteSpace(rule.RoleName) && allRoles.Contains(rule.RoleName))
                matches = true;
            if (!string.IsNullOrWhiteSpace(rule.ClaimType) && !string.IsNullOrWhiteSpace(rule.ClaimValue) &&
                roleClaims.Any(c => c.Type.Equals(rule.ClaimType, StringComparison.OrdinalIgnoreCase) &&
                                    c.Value.Equals(rule.ClaimValue, StringComparison.OrdinalIgnoreCase)))
                matches = true;
            if (rule.MenuId.HasValue && menuSet.Contains(rule.MenuId.Value))
                matches = true;
            if (viewAllFlags && rule.AllowAllForViewAllFlags &&
                (rule.ViewName == "*" || (rule.MenuId.HasValue && menuSet.Contains(rule.MenuId.Value))))
                matches = true;

            if (!matches) continue;

            if (rule.ViewName == "*") allViews = true;
            else if (!string.IsNullOrWhiteSpace(rule.ViewName)) allowedViews.Add(rule.ViewName.Trim());

            if (!string.IsNullOrWhiteSpace(rule.BlockedColumns))
            {
                foreach (var col in rule.BlockedColumns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    blocked.Add(col);
            }
        }

        if (viewAllFlags) allViews = true;

        var isAdmin = allRoles.Contains("Admin") || allRoles.Contains("ReportAdmin") ||
                      menuSet.Contains(4) || viewAllFlags;

        return new ErpUserSession
        {
            UserName = userName,
            DisplayName = displayName,
            IsActive = isActive,
            ViewAllFlags = viewAllFlags,
            UserType = userType,
            Roles = roles,
            RoleClaims = roleClaims,
            CompanyRoles = companyRoles,
            AllowedCompanies = companies,
            AllowedMenuIds = menuIds,
            AllowedViews = allowedViews,
            BlockedColumns = blocked,
            AllowAllViews = allViews,
            IsAdmin = isAdmin
        };
    }

    private static async Task<List<string>> TryLoadCompanyRolesAsync(string companyConn, string userName, CancellationToken ct)
    {
        var result = new List<string>();
        try
        {
            await using var conn = new NpgsqlConnection(companyConn);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("""
                SELECT DISTINCT r.name
                FROM common.userrole ur
                JOIN common.role r ON r.id = ur.roleid
                JOIN common."Users" u ON u.id = ur.userid
                WHERE LOWER(u.username) = LOWER(@u)
                  AND NOT COALESCE(ur.isarchived, false)
                  AND NOT COALESCE(r.isarchived, false)
                  AND r.name IS NOT NULL
                """, conn);
            cmd.Parameters.AddWithValue("u", userName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) result.Add(r.GetString(0));
        }
        catch
        {
            // Optional company-DB RBAC tables may not exist in all company DBs (e.g. ChefDB/capricorn).
        }
        return result;
    }

    public async Task<IReadOnlyList<RbacRule>> GetRbacRulesAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(ConsoleConn);
        await conn.OpenAsync(ct);
        return await GetRbacRulesAsync(conn, ct);
    }

    private static async Task<List<RbacRule>> GetRbacRulesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var list = new List<RbacRule>();
        await using var cmd = new NpgsqlCommand("""
            SELECT id, role_name, claim_type, claim_value, menuid, area_name, view_name,
                   allow_all_for_view_all_flags, COALESCE(blocked_columns, '')
            FROM cat.module_view_map
            ORDER BY id
            """, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new RbacRule
            {
                Id = r.GetInt32(0),
                RoleName = r.IsDBNull(1) ? null : r.GetString(1),
                ClaimType = r.IsDBNull(2) ? null : r.GetString(2),
                ClaimValue = r.IsDBNull(3) ? null : r.GetString(3),
                MenuId = r.IsDBNull(4) ? null : r.GetInt32(4),
                AreaName = r.IsDBNull(5) ? null : r.GetString(5),
                ViewName = r.GetString(6),
                AllowAllForViewAllFlags = r.GetBoolean(7),
                BlockedColumns = r.GetString(8)
            });
        }
        return list;
    }

    public async Task<int> UpsertRbacRuleAsync(RbacRule rule, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(ConsoleConn);
        await conn.OpenAsync(ct);
        if (rule.Id > 0)
        {
            await using var cmd = new NpgsqlCommand("""
                UPDATE cat.module_view_map
                SET role_name=@rn, claim_type=@ct, claim_value=@cv, menuid=@mid,
                    area_name=@an, view_name=@vn, allow_all_for_view_all_flags=@aaf, blocked_columns=@bc
                WHERE id=@id
                """, conn);
            BindRuleParams(cmd, rule);
            cmd.Parameters.AddWithValue("id", rule.Id);
            await cmd.ExecuteNonQueryAsync(ct);
            return rule.Id;
        }
        else
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO cat.module_view_map
                    (role_name, claim_type, claim_value, menuid, area_name, view_name, allow_all_for_view_all_flags, blocked_columns)
                VALUES (@rn, @ct, @cv, @mid, @an, @vn, @aaf, @bc)
                RETURNING id
                """, conn);
            BindRuleParams(cmd, rule);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }
    }

    public async Task DeleteRbacRuleAsync(int id, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(ConsoleConn);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM cat.module_view_map WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void BindRuleParams(NpgsqlCommand cmd, RbacRule r)
    {
        cmd.Parameters.AddWithValue("rn", (object?)NullIfEmpty(r.RoleName) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ct", (object?)NullIfEmpty(r.ClaimType) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("cv", (object?)NullIfEmpty(r.ClaimValue) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("mid", (object?)r.MenuId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("an", (object?)NullIfEmpty(r.AreaName) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("vn", r.ViewName.Trim());
        cmd.Parameters.AddWithValue("aaf", r.AllowAllForViewAllFlags);
        cmd.Parameters.AddWithValue("bc", r.BlockedColumns?.Trim() ?? "");
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
