namespace ReportBuilder.Core.Pipeline;

/// <summary>Step 6. Caps result size and run time so one question cannot hurt the ERP database.</summary>
public sealed class Optimizer
{
    public CompiledQuery Optimize(CompiledQuery q, int maxRows)
    {
        var sql = q.Sql;
        var parameters = new Dictionary<string, object?>(q.Parameters);
        var limit = 0;
        if (q.HasOrderBy)
        {
            limit = maxRows + 1;   // one extra row tells us whether the result was cut off
            sql += " LIMIT @maxRows";
            parameters["maxRows"] = limit;
        }
        return new CompiledQuery
        {
            Sql = sql, Parameters = parameters, ParameterColumns = q.ParameterColumns, ViewName = q.ViewName, Columns = q.Columns,
            HasOrderBy = q.HasOrderBy, OrderSpecs = q.OrderSpecs, CommandTimeoutSeconds = 30, FetchLimit = limit
        };
    }
}
