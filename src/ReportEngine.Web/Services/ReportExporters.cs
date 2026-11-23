using System.Globalization;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using ReportBuilder.Core;

namespace ReportEngine.Web.Services;

public sealed record GroupPackSection(
    string Category,
    string Title,
    string Prompt,
    string ViewName,
    string ScopeLabel,
    QueryResult Result,
    string? Note = null);

public sealed record QueryCatalogEntry(
    string Category,
    string Title,
    string Prompt,
    string ViewName,
    string Scope,
    string RecommendedVisual,
    string SqlTemplate);

public static class ReportExporters
{
    static ReportExporters()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static byte[] ToCsv(QueryResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", result.Columns.Select(CsvEscape)));
        foreach (var row in result.Rows)
            sb.AppendLine(string.Join(",", row.Select(v => CsvEscape(FormatCell(v)))));
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    public static byte[] ToJson(QueryResult result, string title, string companyCode)
    {
        var rows = result.Rows.Select(r =>
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < result.Columns.Length; i++)
                dict[result.Columns[i]] = i < r.Length ? r[i] : null;
            return dict;
        });
        var payload = new
        {
            title,
            companyCode,
            generatedAtUtc = DateTime.UtcNow,
            rowCount = result.Rows.Count,
            truncated = result.Truncated,
            columns = result.Columns,
            rows
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    public static byte[] ToExcel(QueryResult result, string title, string companyCode)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Report");

        ws.Cell(1, 1).Value = title;
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(2, 1).Value = $"Company: {companyCode} | Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC | Rows: {result.Rows.Count}";
        ws.Cell(2, 1).Style.Font.Italic = true;

        var headerRow = 4;
        for (var c = 0; c < result.Columns.Length; c++)
        {
            var cell = ws.Cell(headerRow, c + 1);
            cell.Value = result.Columns[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1e3a8a");
            cell.Style.Font.FontColor = XLColor.White;
        }

        for (var r = 0; r < result.Rows.Count; r++)
        {
            var row = result.Rows[r];
            for (var c = 0; c < result.Columns.Length; c++)
            {
                var val = c < row.Length ? row[c] : null;
                var cell = ws.Cell(headerRow + 1 + r, c + 1);
                if (val is null) cell.Value = Blank.Value;
                else if (val is int or long or float or double or decimal)
                    cell.Value = Convert.ToDouble(val, CultureInfo.InvariantCulture);
                else if (val is DateTime dt)
                    cell.Value = dt;
                else if (val is bool b)
                    cell.Value = b;
                else
                    cell.Value = FormatCell(val);
            }
        }

        ws.Columns().AdjustToContents(1, 50);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public static byte[] ToPdf(QueryResult result, string title, string companyCode)
    {
        var maxCols = Math.Min(result.Columns.Length, 12);
        var cols = result.Columns.Take(maxCols).ToArray();
        var rows = result.Rows.Take(500).ToList();

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(24);
                page.DefaultTextStyle(x => x.FontSize(8.5f));

                page.Header().Column(col =>
                {
                    col.Item().Text(title).FontSize(15).Bold().FontColor(Colors.Blue.Darken3);
                    col.Item().Text($"Company: {companyCode}  |  Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC  |  Rows shown: {rows.Count}{(result.Rows.Count > 500 ? $" of {result.Rows.Count}" : "")}")
                        .FontSize(8).FontColor(Colors.Grey.Darken1);
                    col.Item().PaddingBottom(8);
                });

                page.Content().Table(table =>
                {
                    table.ColumnsDefinition(def =>
                    {
                        for (var i = 0; i < cols.Length; i++)
                            def.RelativeColumn();
                    });

                    table.Header(h =>
                    {
                        foreach (var c in cols)
                        {
                            h.Cell().Background(Colors.Blue.Darken3).Padding(4)
                                .Text(c).Bold().FontColor(Colors.White);
                        }
                    });

                    for (var r = 0; r < rows.Count; r++)
                    {
                        var bg = r % 2 == 0 ? Colors.White : Colors.Grey.Lighten4;
                        for (var c = 0; c < cols.Length; c++)
                        {
                            var text = FormatCell(c < rows[r].Length ? rows[r][c] : null);
                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3)
                                .Text(text);
                        }
                    }
                });

                page.Footer().AlignRight().Text(x =>
                {
                    x.Span("Page ");
                    x.CurrentPageNumber();
                    x.Span(" of ");
                    x.TotalPages();
                });
            });
        });

        return doc.GeneratePdf();
    }

    public static byte[] ToGroupPackPdf(
        IReadOnlyList<GroupPackSection> sections,
        string generatedBy,
        IReadOnlyList<string> companies,
        string? packTitle = null,
        string? scopeHeader = null)
    {
        const string TeroNavy = "#14233C";
        const string TeroOrange = "#F15A24";
        const string RowWhite = "#FFFFFF";
        const string SoftSlate = "#F1F5F9";
        const string BorderLight = "#CBD5E1";

        var timestamp = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC";
        var companySummary = companies.Count > 0 ? string.Join(", ", companies) : "None";
        var heading = string.IsNullOrWhiteSpace(packTitle)
            ? "TERO ERP — Cross-Company Group Report Pack"
            : packTitle;
        var scopeText = string.IsNullOrWhiteSpace(scopeHeader)
            ? $"Companies: {companySummary}"
            : scopeHeader;
        var totalRows = sections.Sum(s => s.Result.Rows.Count);

        var doc = Document.Create(container =>
        {
            // Cover & Executive Index Page
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(22);
                page.DefaultTextStyle(x => x.FontSize(8.5f).FontColor("#1E293B"));

                page.Header().Column(col =>
                {
                    col.Item().Height(4).Background(TeroOrange);
                    col.Item().PaddingTop(8).Text(heading)
                        .FontSize(18).Bold().FontColor(TeroNavy);
                    col.Item().PaddingTop(2).Text(
                            $"{scopeText}  |  Generated by: {generatedBy}  |  Generated: {timestamp}  |  Reports: {sections.Count}  |  Total Rows: {totalRows}")
                        .FontSize(8.5f).FontColor("#475569");
                    col.Item().PaddingBottom(10);
                });

                page.Content().Column(col =>
                {
                    col.Item().PaddingBottom(6).Text("Executive Report Index & Recommended AI Prompts")
                        .FontSize(11).Bold().FontColor(TeroNavy);

                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(def =>
                        {
                            def.ConstantColumn(22);
                            def.RelativeColumn(1.2f);
                            def.RelativeColumn(3.2f);
                            def.RelativeColumn(1.5f);
                            def.RelativeColumn(1.5f);
                            def.ConstantColumn(38);
                            def.RelativeColumn(1.8f);
                        });

                        table.Header(h =>
                        {
                            foreach (var hdr in new[] { "#", "Category", "Report & Recommended Prompt", "View", "Scope", "Rows", "Notes" })
                                h.Cell().Background(TeroNavy).Padding(4).Text(hdr).Bold().FontColor(Colors.White);
                        });

                        for (var i = 0; i < sections.Count; i++)
                        {
                            var s = sections[i];
                            var bg = i % 2 == 0 ? RowWhite : SoftSlate;
                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor(BorderLight).Padding(3).Text((i + 1).ToString(CultureInfo.InvariantCulture));
                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor(BorderLight).Padding(3).Text(s.Category).FontColor(TeroOrange).Bold();
                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor(BorderLight).Padding(3).Column(rc =>
                            {
                                rc.Item().Text(s.Title).Bold();
                                rc.Item().Text($"Prompt: \"{s.Prompt}\"").FontSize(7.2f).Italic().FontColor("#475569");
                            });
                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor(BorderLight).Padding(3).Text($"rpt.{s.ViewName}");
                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor(BorderLight).Padding(3).Text(s.ScopeLabel);
                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor(BorderLight).Padding(3).AlignRight().Text(s.Result.Rows.Count.ToString(CultureInfo.InvariantCulture));
                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor(BorderLight).Padding(3).Text(s.Note ?? "OK");
                        }
                    });
                });

                page.Footer().Row(row =>
                {
                    row.RelativeItem().Text("TERO ERP AI Report Engine — Read-Only Governed Export").FontSize(7.5f).FontColor("#64748B");
                    row.RelativeItem().AlignRight().Text(x =>
                    {
                        x.Span("Page ").FontSize(7.5f).FontColor("#64748B");
                        x.CurrentPageNumber().FontSize(7.5f).FontColor("#64748B");
                        x.Span(" of ").FontSize(7.5f).FontColor("#64748B");
                        x.TotalPages().FontSize(7.5f).FontColor("#64748B");
                    });
                });
            });

            // Individual Report Pages
            foreach (var sec in sections)
            {
                const int MaxPdfCols = 12;
                const int MaxPdfRows = 150;
                var cols = sec.Result.Columns.Take(MaxPdfCols).ToArray();
                var rows = sec.Result.Rows.Take(MaxPdfRows).ToList();

                container.Page(page =>
                {
                    page.Size(PageSizes.A4.Landscape());
                    page.Margin(22);
                    page.DefaultTextStyle(x => x.FontSize(8f).FontColor("#1E293B"));

                    page.Header().Column(col =>
                    {
                        col.Item().Height(3).Background(TeroOrange);
                        col.Item().PaddingTop(6).Text(sec.Category.ToUpperInvariant())
                            .FontSize(8).Bold().FontColor(TeroOrange);
                        col.Item().Text(sec.Title)
                            .FontSize(14).Bold().FontColor(TeroNavy);
                        col.Item().Text($"Prompt: \"{sec.Prompt}\"")
                            .FontSize(8).Italic().FontColor("#475569");
                        var noteSuffix = string.IsNullOrWhiteSpace(sec.Note) ? "" : $"  |  Note: {sec.Note}";
                        col.Item().PaddingBottom(6).Text(
                                $"View: rpt.{sec.ViewName}  |  Scope: {sec.ScopeLabel}  |  Rows shown: {rows.Count}{(sec.Result.Rows.Count > MaxPdfRows ? $" of {sec.Result.Rows.Count}" : "")}{noteSuffix}")
                            .FontSize(8).FontColor("#475569");
                    });

                    if (rows.Count == 0 || cols.Length == 0)
                    {
                        page.Content().Border(1).BorderColor(BorderLight).Background(SoftSlate).Padding(14)
                            .Text(sec.Note ?? "No records returned for this report and scope.")
                            .FontSize(9.5f).FontColor(TeroNavy);
                    }
                    else
                    {
                        page.Content().Table(table =>
                        {
                            table.ColumnsDefinition(def =>
                            {
                                for (var i = 0; i < cols.Length; i++)
                                    def.RelativeColumn();
                            });

                            table.Header(h =>
                            {
                                foreach (var c in cols)
                                    h.Cell().Background(TeroNavy).Padding(4).Text(c).Bold().FontColor(Colors.White);
                            });

                            for (var r = 0; r < rows.Count; r++)
                            {
                                var bg = r % 2 == 0 ? RowWhite : SoftSlate;
                                for (var c = 0; c < cols.Length; c++)
                                {
                                    var val = c < rows[r].Length ? rows[r][c] : null;
                                    var isNum = val is int or long or float or double or decimal;
                                    var formatted = FormatPdfNumberOrCell(val);
                                    var cell = table.Cell().Background(bg).BorderBottom(0.5f).BorderColor(BorderLight).Padding(3);
                                    if (isNum) cell.AlignRight().Text(formatted);
                                    else cell.Text(formatted);
                                }
                            }
                        });
                    }

                    page.Footer().Row(row =>
                    {
                        row.RelativeItem().Text("TERO ERP AI Report Engine — Read-Only Governed Export").FontSize(7.5f).FontColor("#64748B");
                        row.RelativeItem().AlignRight().Text(x =>
                        {
                            x.Span("Page ").FontSize(7.5f).FontColor("#64748B");
                            x.CurrentPageNumber().FontSize(7.5f).FontColor("#64748B");
                            x.Span(" of ").FontSize(7.5f).FontColor("#64748B");
                            x.TotalPages().FontSize(7.5f).FontColor("#64748B");
                        });
                    });
                });
            }
        });

        return doc.GeneratePdf();
    }

    public static byte[] ToQueryCatalogPdf(
        IReadOnlyList<QueryCatalogEntry> entries,
        string title,
        string subtitle)
    {
        const string TeroNavy = "#14233C";
        const string TeroOrange = "#F15A24";
        const string RowWhite = "#FFFFFF";
        const string SoftSlate = "#F1F5F9";
        const string CodeBg = "#F8FAFC";
        const string BorderLight = "#CBD5E1";

        var timestamp = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC";

        var doc = Document.Create(container =>
        {
            // Page 1+: Executive Summary Table (Quick Reference)
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(22);
                page.DefaultTextStyle(x => x.FontSize(8.2f).FontColor("#1E293B"));

                page.Header().Column(col =>
                {
                    col.Item().Height(4).Background(TeroOrange);
                    col.Item().PaddingTop(8).Text(title)
                        .FontSize(18).Bold().FontColor(TeroNavy);
                    col.Item().PaddingTop(2).Text(
                            $"{subtitle}  |  Query Catalog Only — No Live Data Rows  |  Queries: {entries.Count}  |  Generated: {timestamp}")
                        .FontSize(8.5f).FontColor("#475569");
                    col.Item().PaddingBottom(8);
                });

                page.Content().Column(col =>
                {
                    col.Item().PaddingBottom(6).Text("Quick Reference — Reports, Plain-English AI Prompts & Visuals")
                        .FontSize(11).Bold().FontColor(TeroNavy);

                    col.Item().Table(table =>
                    {
                        table.ColumnsDefinition(def =>
                        {
                            def.ConstantColumn(22);
                            def.RelativeColumn(1.2f);
                            def.RelativeColumn(2.1f);
                            def.RelativeColumn(3.1f);
                            def.RelativeColumn(1.4f);
                            def.RelativeColumn(1.4f);
                        });

                        table.Header(h =>
                        {
                            foreach (var hdr in new[] { "#", "Category", "Report Title", "Plain-English AI Prompt", "View", "Recommended Visual" })
                                h.Cell().Background(TeroNavy).Padding(4).Text(hdr).Bold().FontColor(RowWhite);
                        });

                        for (var i = 0; i < entries.Count; i++)
                        {
                            var e = entries[i];
                            var bg = i % 2 == 0 ? RowWhite : SoftSlate;
                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor(BorderLight).Padding(3).Text((i + 1).ToString(CultureInfo.InvariantCulture));
                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor(BorderLight).Padding(3).Text(e.Category).FontColor(TeroOrange).Bold();
                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor(BorderLight).Padding(3).Text(e.Title).Bold();
                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor(BorderLight).Padding(3).Text($"\"{e.Prompt}\"").Italic();
                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor(BorderLight).Padding(3).Text($"rpt.{e.ViewName}");
                            table.Cell().Background(bg).BorderBottom(0.5f).BorderColor(BorderLight).Padding(3).Text(e.RecommendedVisual);
                        }
                    });
                });

                page.Footer().Row(row =>
                {
                    row.RelativeItem().Text("TERO ERP AI Report Engine — Query Catalog (No Data Exported)").FontSize(7.5f).FontColor("#64748B");
                    row.RelativeItem().AlignRight().Text(x =>
                    {
                        x.Span("Page ").FontSize(7.5f).FontColor("#64748B");
                        x.CurrentPageNumber().FontSize(7.5f).FontColor("#64748B");
                        x.Span(" of ").FontSize(7.5f).FontColor("#64748B");
                        x.TotalPages().FontSize(7.5f).FontColor("#64748B");
                    });
                });
            });

            // Detailed Query Cards Pages (Prompts + Parameterized SQL Templates)
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(22);
                page.DefaultTextStyle(x => x.FontSize(8.2f).FontColor("#1E293B"));

                page.Header().Column(col =>
                {
                    col.Item().Height(3).Background(TeroOrange);
                    col.Item().PaddingTop(6).Text($"{title} — SQL Templates & Prompt Specifications")
                        .FontSize(13).Bold().FontColor(TeroNavy);
                    col.Item().PaddingBottom(6).Text(
                            $"{subtitle}  |  Read-only SELECT queries over governed rpt.* PostgreSQL views (zero row data exported)")
                        .FontSize(8).FontColor("#475569");
                });

                page.Content().Column(col =>
                {
                    for (var i = 0; i < entries.Count; i++)
                    {
                        var e = entries[i];
                        col.Item().PaddingBottom(8).Border(0.75f).BorderColor(BorderLight).ShowEntire().Column(card =>
                        {
                            card.Item().Background(SoftSlate).PaddingHorizontal(8).PaddingVertical(5).Row(row =>
                            {
                                row.RelativeItem(2).Column(left =>
                                {
                                    left.Item().Text($"#{i + 1}  |  {e.Category.ToUpperInvariant()}")
                                        .FontSize(7.5f).Bold().FontColor(TeroOrange);
                                    left.Item().Text(e.Title)
                                        .FontSize(10f).Bold().FontColor(TeroNavy);
                                });
                                row.RelativeItem(1).AlignRight().AlignMiddle().Text(
                                        $"rpt.{e.ViewName}  |  {e.Scope}  |  {e.RecommendedVisual}")
                                    .FontSize(7.8f).FontColor("#475569");
                            });

                            card.Item().PaddingHorizontal(8).PaddingTop(5).PaddingBottom(3).Text(txt =>
                            {
                                txt.Span("AI Prompt: ").Bold().FontColor(TeroNavy);
                                txt.Span($"\"{e.Prompt}\"").Italic();
                            });

                            card.Item().Background(CodeBg).PaddingHorizontal(8).PaddingVertical(5)
                                .Text(e.SqlTemplate)
                                .FontFamily(Fonts.Courier).FontSize(7.8f).FontColor("#0F172A");
                        });
                    }
                });

                page.Footer().Row(row =>
                {
                    row.RelativeItem().Text("TERO ERP AI Report Engine — Query Catalog (No Data Exported)").FontSize(7.5f).FontColor("#64748B");
                    row.RelativeItem().AlignRight().Text(x =>
                    {
                        x.Span("Page ").FontSize(7.5f).FontColor("#64748B");
                        x.CurrentPageNumber().FontSize(7.5f).FontColor("#64748B");
                        x.Span(" of ").FontSize(7.5f).FontColor("#64748B");
                        x.TotalPages().FontSize(7.5f).FontColor("#64748B");
                    });
                });
            });
        });

        return doc.GeneratePdf();
    }

    private static string FormatPdfNumberOrCell(object? v) => v switch
    {
        null => "",
        int or long => Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString("N0", CultureInfo.InvariantCulture),
        float or double or decimal => Convert.ToDecimal(v, CultureInfo.InvariantCulture).ToString("N2", CultureInfo.InvariantCulture),
        _ => FormatCell(v)
    };

    private static string FormatCell(object? v) => v switch
    {
        null => "",
        DateTime dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? ""
    };

    private static string CsvEscape(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r')
            ? $"\"{s.Replace("\"", "\"\"")}\""
            : s;
}
