using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReportBuilder.Core;
using ReportBuilder.Core.Catalog;
using ReportBuilder.Core.Generator;
using ReportBuilder.Core.Pipeline;

// Usage:
//   ReportBuilder.Cli generate [--questions questions.json] [--out out] [--load]
//   ReportBuilder.Cli ask "Show receivables aging by region for company 1000" [--user DOMAIN\name] [--role "CONTOSO\Finance-1000"]
//   ReportBuilder.Cli review                      (lists catalog entries and their status)

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Local.json", optional: true)
    .AddUserSecrets<Marker>(optional: true)
    .AddEnvironmentVariables("RB_")
    .Build();

var services = new ServiceCollection().AddReportBuilder(config).BuildServiceProvider();
if (args.Length == 0) { Console.WriteLine("Commands: generate | ask \"question\" | review"); return 1; }

string Opt(string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}
var json = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };

switch (args[0])
{
    case "generate":
    {
        var qFile = Opt("--questions", Path.Combine(AppContext.BaseDirectory, "questions.json"));
        var outDir = Opt("--out", "out");
        var questions = JsonSerializer.Deserialize<List<TestQuestion>>(await File.ReadAllTextAsync(qFile), json) ?? new();
        var gen = services.GetRequiredService<CatalogGenerator>();
        var report = await gen.RunAsync(questions, CancellationToken.None);

        Directory.CreateDirectory(outDir);
        await File.WriteAllTextAsync(Path.Combine(outDir, "review_pack.json"), JsonSerializer.Serialize(report, json));
        var lines = new List<string> { $"Questions: {report.Scores.Count}   Passed: {report.Passed}   Failed: {report.Scores.Count - report.Passed}", "" };
        lines.AddRange(report.Scores.Select(s => $"{s.Id,-4} {s.Category,-12} {s.Status,-20} {(s.Passed ? "PASS" : "FAIL"),-5} {s.Why}"));
        lines.Add("");
        lines.Add("Synonym collisions: " + (report.SynonymCollisions.Count == 0 ? "none" : string.Join("; ", report.SynonymCollisions.Select(c => $"{c.Key} -> {string.Join("/", c.Value)}"))));
        await File.WriteAllLinesAsync(Path.Combine(outDir, "scorecard.txt"), lines);
        lines.ForEach(Console.WriteLine);

        if (args.Contains("--load"))
        {
            var n = await services.GetRequiredService<ICatalogStore>().InsertUnverifiedAsync(CatalogGenerator.ToEntries(report), CancellationToken.None);
            Console.WriteLine($"Inserted {n} new entries as UNVERIFIED. An owner must approve them in the web app before they are certified.");
        }
        return report.Passed == report.Scores.Count ? 0 : 2;
    }
    case "ask":
    {
        if (args.Length < 2) { Console.WriteLine("ask needs a question"); return 1; }
        var user = new UserContext { UserName = Opt("--user", Environment.UserName), Roles = new[] { Opt("--role", "") }.Where(r => r.Length > 0).ToArray() };
        var resp = await services.GetRequiredService<ReportPipeline>().AskAsync(args[1], user);
        Console.WriteLine($"[{resp.Status}] {resp.Label}");
        Console.WriteLine(resp.Answer);
        if (resp.Clarifications.Length > 0) Console.WriteLine("Options: " + string.Join(" | ", resp.Clarifications));
        if (resp.Sql != null) Console.WriteLine("SQL: " + resp.Sql);
        if (resp.Result != null)
        {
            Console.WriteLine(string.Join("\t", resp.Result.Columns));
            foreach (var row in resp.Result.Rows.Take(25)) Console.WriteLine(string.Join("\t", row.Select(c => c?.ToString() ?? "")));
        }
        foreach (var f in resp.Findings) Console.WriteLine($"  {f.Severity}: {f.Message}");
        Console.WriteLine($"Audit run id: {resp.RunId}");
        return resp.Status == AskStatus.Failed ? 2 : 0;
    }
    case "review":
    {
        var all = await services.GetRequiredService<ICatalogStore>().GetMetricsAsync(CancellationToken.None);
        foreach (var m in all) Console.WriteLine($"{m.MetricId,3} {m.ReviewStatus,-10} {m.Name} (rpt.{m.ViewName}; dims: {string.Join(",", m.Dimensions)})");
        return 0;
    }
    default:
        Console.WriteLine("Unknown command.");
        return 1;
}

internal sealed class Marker { }
