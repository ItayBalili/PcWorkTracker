using System.Globalization;
using System.IO;
using System.Text;
using ClosedXML.Excel;

namespace WorkTimeTracker.App;

internal static class ApplicationUsageExport
{
    public static void Write(string path, string application, DashboardReport period, ApplicationUsageReport report,
        IReadOnlyList<ApplicationUsageDocument> documents, IReadOnlyList<ApplicationUsageDay> days, string? chartLabel)
    {
        var headers = new[] { DashboardText.Get("Type"), DashboardText.Get("Period"), DashboardText.Get("Application"),
            DashboardText.Get("Label"), DashboardText.Get("WindowTitle"), DashboardText.Get("Active"),
            DashboardText.Get("Displayed"), DashboardText.Get("ActivePercent"), DashboardText.Get("DaysUsed"),
            DashboardText.Get("FirstUse"), DashboardText.Get("LastUse") };
        var range = $"{period.From:yyyy-MM-dd} – {period.To:yyyy-MM-dd}";
        var rows = new List<string[]>
        {
            new[] { DashboardText.Get("Summary"), range, application, "", "", DashboardViewModel.Duration(report.Active),
                DashboardViewModel.Duration(report.Displayed), "", report.DaysUsed.ToString(UiCulture.Current),
                FormatTime(report.FirstUsed), FormatTime(report.LastUsed) }
        };
        foreach (var document in documents)
        {
            rows.Add([DashboardText.Get("Label"), range, application, document.Label, "", document.ActiveText,
                document.DisplayedText, document.PercentText, document.DaysUsed.ToString(UiCulture.Current), "", document.LastUsedText]);
            rows.AddRange(document.Titles.Select(title => new[] { DashboardText.Get("WindowTitle"), range, application,
                document.Label, title.Title, title.ActiveText, title.DisplayedText, "", "", "", "" }));
        }
        rows.AddRange(days.Select(day => new[] { DashboardText.Get("DailyActivity"),
            day.Date.ToString("ddd yyyy-MM-dd", UiCulture.Current), application, chartLabel ?? "", "", day.ActiveText,
            DashboardViewModel.Duration(day.Displayed), "", "", "", "" }));
        if (string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase))
        {
            using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
            writer.WriteLine(string.Join(",", headers.Select(Quote)));
            foreach (var row in rows) writer.WriteLine(string.Join(",", row.Select(Quote)));
            return;
        }
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(DashboardText.Get("ApplicationsPage"));
        sheet.RightToLeft = UiCulture.IsRightToLeft;
        for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
        sheet.Row(1).Style.Font.Bold = true;
        for (var i = 0; i < rows.Count; i++)
            for (var j = 0; j < headers.Length; j++) sheet.Cell(i + 2, j + 1).Value = rows[i][j];
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents();
        workbook.SaveAs(path);
    }

    private static string FormatTime(DateTimeOffset? value) => value?.ToLocalTime().ToString("g", UiCulture.Current) ?? "";
    private static string Quote(string value)
    {
        var trimmed = value.TrimStart();
        if (value.Length > 0 && ("\t\r\n".Contains(value[0]) || (trimmed.Length > 0 && "=+-@".Contains(trimmed[0]))))
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
