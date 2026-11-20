using System.Globalization;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using ReportBuilder.Core;

namespace ReportEngine.Web.Services;

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
