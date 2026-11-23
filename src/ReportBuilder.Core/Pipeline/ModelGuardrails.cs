using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ReportBuilder.Core.Data;

namespace ReportBuilder.Core.Pipeline;

public sealed record GuardrailDecision(bool Allowed, string Category, string Reason);

public sealed record SpecVerificationResult(
    bool Ok,
    DynamicReportRequest? Sanitized,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Deterministic input & metadata boundary guardrails that run before any LLM call
/// to keep prompts strictly inside read-only TERO ERP reporting scope.
/// </summary>
public static class ScopeGuardrail
{
    public const int MaxQuestionLength = 1000;

    private static readonly Regex PromptInjectionRegex = new(
        @"\b(ignore\s+(?:all\s+|any\s+|the\s+|your\s+)?(?:previous|prior|above|system|earlier|preceding)\s+(?:instructions|rules|prompts|directives|constraints)|disregard\s+(?:all\s+|any\s+)?(?:previous|prior|above|system)|(?:reveal|show|print|dump|leak|output|repeat|display)\s+(?:me\s+|the\s+|your\s+)?(?:system\s+prompt|hidden\s+prompt|instructions|rules|api\s*key|connection\s*string)|(?:act|behave|pretend|roleplay)\s+as\s+(?!an?\s+erp\b)|jailbreak|developer\s+mode|dan\s+mode|override\s+(?:all\s+|your\s+)?(?:rules|guardrails|safety|constraints)|select\s+\*\s+from|pg_catalog|information_schema|sys\.tables|sys\.sql_logins|common\s*\.\s*""?aspnetusers""?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExplicitDdlDmlRegex = new(
        @"\b(?:(?:drop|truncate|alter)\s+(?:table|view|schema|database|index|role|user|column|function|procedure)|delete\s+(?:from|all)\b|insert\s+into\b|update\s+[A-Za-z_""][A-Za-z0-9_.""\s]{0,40}\s+set\b|merge\s+into\b|(?:grant|revoke)\s+[^;\n]{1,60}\s+on\b|create\s+(?:or\s+replace\s+)?(?:table|user|role|database|function|procedure|schema)\b)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ImperativeWriteRegex = new(
        @"^(?:please\s+|can\s+you\s+|could\s+you\s+|would\s+you\s+|kindly\s+)?(?:delete|remove|erase|drop|truncate|update|modify|edit|change|insert|post|reverse|cancel|approve|reject)\s+(?:all\s+|the\s+|every\s+|this\s+|that\s+)?(?:invoice|invoices|customer|customers|vendor|vendors|partner|partners|record|records|row|rows|journal|journals|order|orders|entry|entries|table|data|account|accounts|employee|employees)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ForecastOrAdviceRegex = new(
        @"\b(?:predict(?:ing|ion|s)?|forecast(?:ing|s)?|extrapolate|project\s+(?:next|future)|estimate\s+future|what\s+will\s+[a-z0-9\s]{1,40}\s+be\s+(?:next|in\s+20\d{2}|tomorrow)|(?:medical|legal|investment|stock\s+market|crypto(?:currency)?)\s+advice)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OutOfScopeTopicRegex = new(
        @"\b(?:(?:write|generate|compose|create)\s+(?:a\s+|an\s+|me\s+a\s+)?(?:poem|story|essay|song|lyrics|joke|cover\s+letter|resume|python|javascript|typescript|java|c#|c\+\+|sql\s+script|bash\s+script|powershell|html|react\s+app|code)\b|tell\s+(?:me\s+)?a\s+joke|recipe\s+for\b|weather\s+(?:in|at|for|today|tomorrow)\b|capital\s+of\b|who\s+(?:is|was)\s+the\s+(?:president|prime\s+minister|king|queen|ceo\s+of\s+(?!this|our))|who\s+won\s+the\b|translate\s+[^\n]{1,80}\s+into\b|solve\s+this\s+math|explain\s+(?:quantum|relativity|black\s+holes|evolution|history\s+of))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SensitiveDataRegex = new(
        @"\b(?:passwords?|passwordhash|api\s*keys?|secret\s*keys?|connection\s*strings?|private\s*keys?|access\s*tokens?|credit\s*cards?|card\s*numbers?|cvv|social\s*security|ssn|passport\s*numbers?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SensitiveColumnNameRegex = new(
        @"(password|secret|token|hash|salt|ssn|credit_card|card_no|card_number|cvv|passport|date_of_birth|\bdob\b|salary|bank_account|iban|tax_registration)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static GuardrailDecision InspectInput(string? question, IEnumerable<string>? blockedColumns = null)
    {
        if (string.IsNullOrWhiteSpace(question))
            return new(false, "InvalidInput", "Please enter a reporting question for your permitted TERO ERP views.");

        var q = question.Trim();
        if (q.Length > MaxQuestionLength)
            return new(false, "InvalidInput", $"Question is too long ({q.Length} characters; maximum {MaxQuestionLength}).");

        if (PromptInjectionRegex.IsMatch(q))
            return new(false, "PromptInjection", "Request blocked: prompt instructions, system probing, or direct base-table queries are not permitted.");

        if (ExplicitDdlDmlRegex.IsMatch(q) || ImperativeWriteRegex.IsMatch(q))
            return new(false, "WriteOrDdlAttempt", "Request blocked: this AI Report Engine is strictly read-only and cannot modify data or database objects.");

        if (SensitiveDataRegex.IsMatch(q))
            return new(false, "SensitiveData", "Request blocked: passwords, credentials, payment card details, and restricted personal identifiers cannot be queried.");

        if (blockedColumns is not null)
        {
            foreach (var col in blockedColumns.Where(c => !string.IsNullOrWhiteSpace(c)))
            {
                var trimmed = col.Trim();
                var spaced = Regex.Replace(trimmed.Replace('_', ' '), @"\s+", @"\s+");
                if (Regex.IsMatch(q, $@"\b{Regex.Escape(trimmed)}\b", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(q, $@"\b{spaced}\b", RegexOptions.IgnoreCase))
                {
                    return new(false, "SensitiveData", $"Request blocked: restricted column '{trimmed}' is protected by RBAC policy.");
                }
            }
        }

        if (ForecastOrAdviceRegex.IsMatch(q))
            return new(false, "ForecastOrAdvice", "Out of scope: I can only report historical/current figures from permitted TERO ERP views, not generate forecasts or advisory predictions.");

        if (OutOfScopeTopicRegex.IsMatch(q))
            return new(false, "OutOfScopeTopic", "Out of scope: this assistant only answers read-only reporting questions grounded in your permitted TERO ERP database views.");

        return new(true, "OK", "");
    }

    public static string WrapUntrustedUserInput(string question)
    {
        var sanitized = Regex.Replace(question ?? "", @"</?user_question>", "", RegexOptions.IgnoreCase).Trim();
        return $"<user_question>\n{sanitized}\n</user_question>";
    }

    public static bool IsSensitiveColumnName(string columnName) =>
        !string.IsNullOrWhiteSpace(columnName) && SensitiveColumnNameRegex.IsMatch(columnName);

    public static string FormatPermittedScopeHint(IEnumerable<ErpAreaViewMeta> permittedViews)
    {
        var areas = permittedViews
            .Select(v => v.AreaName)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return areas.Length == 0
            ? "No reporting views are currently permitted for this company and role."
            : $"I can only build read-only reports from your permitted TERO ERP areas: {string.Join(", ", areas)}.";
    }
}

/// <summary>
/// Deterministic post-LLM verification and sanitization for planned specs and natural-language summaries.
/// </summary>
public static class GuardrailOutputVerifier
{
    private static readonly string[] AllowedAggs = ["SUM", "COUNT", "AVG", "MIN", "MAX"];
    private static readonly string[] AllowedFilterOps = ["=", "<>", ">", ">=", "<", "<=", "ILIKE"];
    private static readonly string[] AllowedHavingOps = ["=", "<>", ">", ">=", "<", "<="];
    private static readonly string[] AllowedChartTypes = ["auto", "bar", "line", "pie", "table"];

    private static readonly Regex SafeIdent = new(@"^[a-z_][a-z0-9_]{0,63}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ScaledNumberRegex = new(
        @"(?<![A-Za-z0-9_])(\d[\d,]*(?:\.\d+)?)\s*([kmb])?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SummaryNumberRegex = new(
        @"(?<![A-Za-z0-9_])(?:(?<=^|[\s(:=,\[])-)?\d[\d,]*(?:\.\d+)?%?(?![A-Za-z0-9_])",
        RegexOptions.Compiled);
    private static readonly Regex PromptLeakOrMetaRegex = new(
        @"(```|https?://|www\.|\bsystem\s+prompt\b|\bas\s+an\s+ai\b|\blanguage\s+model\b|\bignore\s+previous\b|\bmy\s+instructions\b)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DateCueInQuestionRegex = new(
        @"\b(today|yesterday|ytd|year\s+to\s+date|quarter|month|week|year|date|since|after|before|from|until|till|between|jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?|20\d{2}|19\d{2})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsNumericType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType)) return false;
        var t = dataType.ToLowerInvariant();
        return t.Contains("int")
            || t.Contains("numeric")
            || t.Contains("decimal")
            || t.Contains("real")
            || t.Contains("double")
            || t.Contains("float")
            || t.Contains("money")
            || t.Contains("number");
    }

    public static bool IsSafeText(string? text, int maxLength = 350)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        return trimmed.Length <= maxLength && !PromptLeakOrMetaRegex.IsMatch(trimmed);
    }

    public static SpecVerificationResult VerifyDynamicReportSpec(
        string question,
        DynamicReportRequest req,
        ErpAreaViewMeta viewMeta,
        IReadOnlySet<string>? blockedColumns = null,
        int maxAllowedRows = 5000)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(req.ViewName) || !viewMeta.ViewName.Equals(req.ViewName.Trim(), StringComparison.OrdinalIgnoreCase))
            return new(false, null, [$"View '{req.ViewName}' is not permitted or does not exist."], warnings);

        var blocked = blockedColumns ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var colMap = viewMeta.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        // 1. Selected columns
        var cleanSelected = new List<string>();
        foreach (var rawCol in req.SelectedColumns.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            var col = rawCol.Trim();
            if (blocked.Contains(col))
            {
                errors.Add($"Column '{col}' is restricted by RBAC policy.");
                continue;
            }
            if (!colMap.TryGetValue(col, out var meta))
            {
                errors.Add($"Column '{col}' does not exist on rpt.{viewMeta.ViewName}.");
                continue;
            }
            if (!cleanSelected.Contains(meta.Name, StringComparer.OrdinalIgnoreCase))
                cleanSelected.Add(meta.Name);
        }

        // 2. Aggregations
        var cleanAggs = new List<AggregationSpec>();
        var aggAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var agg in req.Aggregations)
        {
            var fn = (agg.Function ?? "").Trim().ToUpperInvariant();
            if (!AllowedAggs.Contains(fn))
            {
                errors.Add($"Aggregation '{agg.Function}' is not allowed.");
                continue;
            }

            var rawCol = (agg.Column ?? "").Trim();
            string resolvedCol;
            if (fn == "COUNT" && (string.IsNullOrWhiteSpace(rawCol) || rawCol == "*"))
            {
                resolvedCol = rawCol == "*" ? "*" : "";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(rawCol))
                {
                    errors.Add($"Aggregation '{fn}' requires a column name.");
                    continue;
                }
                if (blocked.Contains(rawCol))
                {
                    errors.Add($"Column '{rawCol}' is restricted by RBAC policy.");
                    continue;
                }
                if (!colMap.TryGetValue(rawCol, out var colMeta))
                {
                    errors.Add($"Aggregation column '{rawCol}' does not exist on rpt.{viewMeta.ViewName}.");
                    continue;
                }
                if ((fn == "SUM" || fn == "AVG") && !IsNumericType(colMeta.DataType))
                {
                    errors.Add($"Aggregation '{fn}' requires a numeric column, but '{colMeta.Name}' is {colMeta.DataType}.");
                    continue;
                }
                resolvedCol = colMeta.Name;
            }

            var alias = string.IsNullOrWhiteSpace(agg.Alias)
                ? $"{fn.ToLowerInvariant()}_{(string.IsNullOrWhiteSpace(resolvedCol) || resolvedCol == "*" ? "rows" : resolvedCol)}"
                : agg.Alias.Trim();

            if (!SafeIdent.IsMatch(alias))
            {
                errors.Add($"Aggregation alias '{alias}' is not a valid snake_case identifier.");
                continue;
            }
            if (!aggAliases.Add(alias))
            {
                errors.Add($"Duplicate aggregation alias '{alias}'.");
                continue;
            }

            cleanAggs.Add(new AggregationSpec { Column = resolvedCol, Function = fn, Alias = alias });
        }

        // Drop aggregated columns if accidentally included in selected_columns
        if (cleanAggs.Count > 0)
        {
            var aggColSet = cleanAggs
                .Select(a => a.Column)
                .Where(c => !string.IsNullOrWhiteSpace(c) && c != "*")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            cleanSelected.RemoveAll(c => aggColSet.Contains(c));
        }

        // 3. Filters: validate columns/operators strictly, strip company_code & ungrounded filters with warnings
        var cleanFilters = new List<FilterSpec>();
        foreach (var f in req.Filters)
        {
            if (string.IsNullOrWhiteSpace(f.Column) || string.IsNullOrWhiteSpace(f.Value)) continue;
            var col = f.Column.Trim();
            if (col.Equals("company_code", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("Removed redundant filter on 'company_code' (company scope is enforced deterministically).");
                continue;
            }
            if (blocked.Contains(col))
            {
                errors.Add($"Filter column '{col}' is restricted by RBAC policy.");
                continue;
            }
            if (!colMap.TryGetValue(col, out var colMeta))
            {
                errors.Add($"Filter column '{col}' does not exist on rpt.{viewMeta.ViewName}.");
                continue;
            }
            var op = (f.Op ?? "=").Trim().ToUpperInvariant();
            if (!AllowedFilterOps.Contains(op))
            {
                errors.Add($"Filter operator '{f.Op}' is not allowed.");
                continue;
            }

            var val = f.Value.Trim();
            if (!IsFilterValueGrounded(question, colMeta, val))
            {
                warnings.Add($"Removed ungrounded filter '{colMeta.Name} {op} {val}' that was not stated in your question.");
                continue;
            }

            cleanFilters.Add(new FilterSpec { Column = colMeta.Name, Op = op, Value = val });
        }

        // 4. Having filters
        var cleanHaving = new List<HavingFilterSpec>();
        foreach (var h in req.HavingFilters)
        {
            if (string.IsNullOrWhiteSpace(h.Alias)) continue;
            var alias = h.Alias.Trim();
            if (!aggAliases.Contains(alias))
            {
                errors.Add($"HAVING alias '{alias}' must match a defined aggregation alias.");
                continue;
            }
            var op = (h.Op ?? ">").Trim();
            if (!AllowedHavingOps.Contains(op))
            {
                errors.Add($"HAVING operator '{h.Op}' is not allowed.");
                continue;
            }
            cleanHaving.Add(new HavingFilterSpec { Alias = alias, Op = op, Value = h.Value });
        }

        // 5. Relative period
        string? cleanPeriod = string.IsNullOrWhiteSpace(req.RelativePeriod) ? null : req.RelativePeriod.Trim();
        if (cleanPeriod is not null && !cleanPeriod.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            if (DynamicReportCompiler.ResolveRelativeStart(cleanPeriod, DateTime.UtcNow.Date) is null)
                errors.Add($"Relative period '{cleanPeriod}' is not supported.");
        }

        // 6. OrderBy
        var cleanOrder = new List<SortSpec>();
        foreach (var s in req.OrderBy)
        {
            if (string.IsNullOrWhiteSpace(s.Column)) continue;
            var col = s.Column.Trim();
            if (blocked.Contains(col))
            {
                errors.Add($"Sort column '{col}' is restricted by RBAC policy.");
                continue;
            }
            var canonicalCol = aggAliases.Contains(col)
                ? col
                : colMap.TryGetValue(col, out var m) ? m.Name : null;
            if (canonicalCol is null)
            {
                errors.Add($"Sort column '{col}' is not a valid column or aggregation alias on rpt.{viewMeta.ViewName}.");
                continue;
            }
            var dir = s.Direction.Equals("ASC", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";
            cleanOrder.Add(new SortSpec { Column = canonicalCol, Direction = dir });
        }

        if (errors.Count > 0)
            return new(false, null, errors, warnings);

        var clampedLimit = req.Limit <= 0 ? 100 : Math.Clamp(req.Limit, 1, Math.Max(1, maxAllowedRows));
        if (req.Limit > maxAllowedRows && maxAllowedRows > 0)
            warnings.Add($"Row limit capped to policy maximum of {maxAllowedRows}.");

        var chartType = (req.ChartType ?? "auto").Trim().ToLowerInvariant();
        if (!AllowedChartTypes.Contains(chartType))
        {
            warnings.Add($"Normalized unsupported chart type '{req.ChartType}' to 'auto'.");
            chartType = "auto";
        }

        var sanitized = new DynamicReportRequest
        {
            ViewName = viewMeta.ViewName,
            CompanyCode = req.CompanyCode,
            SelectedColumns = cleanSelected,
            Aggregations = cleanAggs,
            Filters = cleanFilters,
            HavingFilters = cleanHaving,
            RelativePeriod = cleanPeriod,
            DateColumn = req.DateColumn,
            GroupBy = req.GroupBy.ToList(),
            OrderBy = cleanOrder,
            Limit = clampedLimit,
            ChartType = chartType
        };

        return new(true, sanitized, errors, warnings);
    }

    public static string BuildDeterministicConfigSummary(DynamicReportRequest req, ErpAreaViewMeta viewMeta)
    {
        var sb = new StringBuilder();
        sb.Append($"Configured {viewMeta.DisplayName} (rpt.{viewMeta.ViewName})");
        if (req.SelectedColumns.Count > 0)
            sb.Append($" with columns [{string.Join(", ", req.SelectedColumns)}]");
        if (req.Aggregations.Count > 0)
            sb.Append($", aggregations [{string.Join(", ", req.Aggregations.Select(a => $"{a.Function}({(string.IsNullOrWhiteSpace(a.Column) ? "*" : a.Column)}) AS {a.Alias}"))}]");
        if (req.Filters.Count > 0)
            sb.Append($", filters [{string.Join(", ", req.Filters.Select(f => $"{f.Column} {f.Op} {f.Value}"))}]");
        if (req.HavingFilters.Count > 0)
            sb.Append($", having [{string.Join(", ", req.HavingFilters.Select(h => $"{h.Alias} {h.Op} {h.Value}"))}]");
        if (!string.IsNullOrWhiteSpace(req.RelativePeriod) && !req.RelativePeriod.Equals("all", StringComparison.OrdinalIgnoreCase))
            sb.Append($", period '{req.RelativePeriod}'");
        sb.Append($", limit {req.Limit}.");
        return sb.ToString();
    }

    public static bool VerifyGroundedSummary(
        string? llmText,
        IEnumerable<string> allowedFactsText,
        string? requiredLabel = null,
        string? forbiddenLabel = null)
    {
        if (string.IsNullOrWhiteSpace(llmText)) return false;
        var text = llmText.Trim();
        if (text.Length > 1000) return false;
        if (PromptLeakOrMetaRegex.IsMatch(text)) return false;

        var sentences = Regex.Matches(text, @"[.!?](?=\s|$)").Count;
        if (sentences > 6) return false;

        if (!string.IsNullOrWhiteSpace(requiredLabel) &&
            !Regex.IsMatch(text, $@"\b{Regex.Escape(requiredLabel)}\b", RegexOptions.IgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(forbiddenLabel) &&
            Regex.IsMatch(text, $@"\b{Regex.Escape(forbiddenLabel)}\b", RegexOptions.IgnoreCase))
            return false;

        var (allowedNumbers, allowedPercents) = ExtractNumbersAndPercents(allowedFactsText);
        foreach (Match m in SummaryNumberRegex.Matches(text))
        {
            var token = m.Value;
            var isPct = token.EndsWith('%');
            var clean = token.TrimEnd('%').Replace(",", "");
            if (!decimal.TryParse(clean, NumberStyles.Number, CultureInfo.InvariantCulture, out var num))
                continue;

            if (isPct)
            {
                if (!allowedPercents.Contains(num)) return false;
            }
            else if (!allowedNumbers.Contains(num))
            {
                return false;
            }
        }

        return true;
    }

    private static (HashSet<decimal> Numbers, HashSet<decimal> Percents) ExtractNumbersAndPercents(IEnumerable<string> texts)
    {
        var numbers = new HashSet<decimal>();
        var percents = new HashSet<decimal>();
        foreach (var raw in texts)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            foreach (Match m in SummaryNumberRegex.Matches(raw))
            {
                var token = m.Value;
                var isPct = token.EndsWith('%');
                var clean = token.TrimEnd('%').Replace(",", "");
                if (!decimal.TryParse(clean, NumberStyles.Number, CultureInfo.InvariantCulture, out var num))
                    continue;
                if (isPct) percents.Add(num);
                else numbers.Add(num);
            }
        }
        return (numbers, percents);
    }

    private static bool IsFilterValueGrounded(string question, ViewColumnMeta colMeta, string value)
    {
        var clean = value.Trim().Trim('%');
        if (clean.Length == 0) return false;

        if (question.Contains(clean, StringComparison.OrdinalIgnoreCase))
            return true;

        // Numeric grounding (handles 50,000 == 50k and 0 for overdue/open/unpaid/positive phrases)
        if (decimal.TryParse(clean.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var numVal))
        {
            foreach (Match m in ScaledNumberRegex.Matches(question))
            {
                if (!decimal.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var qNum))
                    continue;
                var mult = m.Groups[2].Value.ToLowerInvariant() switch
                {
                    "k" => 1_000m,
                    "m" => 1_000_000m,
                    "b" => 1_000_000_000m,
                    _ => 1m
                };
                if (qNum * mult == numVal) return true;
            }
            if (numVal == 0m && Regex.IsMatch(question, @"\b(overdue|open|unpaid|outstanding|positive|negative|zero|non-zero|greater|above|more\s+than|less\s+than)\b", RegexOptions.IgnoreCase))
                return true;
        }

        // Boolean grounding
        if (colMeta.DataType.Contains("bool", StringComparison.OrdinalIgnoreCase)
            || clean.Equals("true", StringComparison.OrdinalIgnoreCase)
            || clean.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            if (Regex.IsMatch(question, @"\b(active|inactive|enabled|disabled|open|closed|posted|unposted|approved)\b", RegexOptions.IgnoreCase))
                return true;
        }

        // Date / timestamp grounding: allowed only if question contains an explicit date/time cue
        if ((colMeta.DataType.Contains("date", StringComparison.OrdinalIgnoreCase) || colMeta.DataType.Contains("timestamp", StringComparison.OrdinalIgnoreCase))
            && (DateTime.TryParse(clean, out _) || DynamicReportCompiler.ResolveRelativeStart(clean, DateTime.UtcNow.Date) is not null))
        {
            return DateCueInQuestionRegex.IsMatch(question);
        }

        // Known ERP domain mappings
        if (colMeta.Name.Equals("payment_mode", StringComparison.OrdinalIgnoreCase))
        {
            if (clean.Equals("credit", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(question, @"\bcredit\b", RegexOptions.IgnoreCase)) return true;
            if (clean.Equals("cash", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(question, @"\bcash\b", RegexOptions.IgnoreCase)) return true;
        }
        if (colMeta.Name.Contains("aging_bucket", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(question, @"\b(aging|overdue|bucket|not\s+due|30|60|90|120)\b", RegexOptions.IgnoreCase))
            return true;
        if (colMeta.Name.Contains("partner_type", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(question, @"\b(customer|customers|vendor|vendors|supplier|suppliers)\b", RegexOptions.IgnoreCase))
            return true;

        return false;
    }
}
