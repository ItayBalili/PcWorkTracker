using System.Globalization;
using System.IO;
using System.Text;
using ClosedXML.Excel;

namespace WorkTimeTracker.App;

internal static class ReportExport
{
    private sealed record Row(string Type, string Date, string Start, string End, string Application, string Label, string Title, string Metric, TimeSpan Duration);

    public static DashboardReport VisibleGroups(DashboardReport report, IEnumerable<DashboardApplication> applications) =>
        report with { Applications = applications.ToArray() };

    public static void Write(string path, DashboardReport report)
    {
        var rows = CreateRows(report);
        if (string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase)) WriteCsv(path, rows);
        else WriteExcel(path, rows);
    }

    private static IReadOnlyList<Row> CreateRows(DashboardReport report)
    {
        var rows = new List<Row>();
        var period = $"{report.From:yyyy-MM-dd} – {report.To:yyyy-MM-dd}";
        void Add(string type, string date, string app, string title, string metric, TimeSpan duration, string start = "", string end = "", string label = "") =>
            rows.Add(new Row(type, date, start, end, app, label, title, metric, duration));
        foreach (var metric in new (string Name, Func<WorkTimeTracker.Core.Models.DayReport, TimeSpan> Value)[]
        {
            ("Span", x => x.TotalSpan), ("Active", x => x.TotalActiveTime), ("Displayed", x => x.TotalDisplayedTime),
            ("Away", x => x.AwayTime), ("NotTracked", x => x.NotTrackedTime)
        })
            Add(DashboardText.Get("Summary"), period, "", "", DashboardText.Get(metric.Name), report.Reports.Aggregate(TimeSpan.Zero, (a, x) => a + metric.Value(x)));
        foreach (var day in report.Days)
        {
            var date = day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            Add(DashboardText.Get("Days"), date, "", "", DashboardText.Get("Span"), day.SpanTime, day.Start, day.End);
            Add(DashboardText.Get("Days"), date, "", "", DashboardText.Get("Active"), day.ActiveTime, day.Start, day.End);
            Add(DashboardText.Get("Days"), date, "", "", DashboardText.Get("Displayed"), day.DisplayedTime, day.Start, day.End);
            Add(DashboardText.Get("Days"), date, "", "", DashboardText.Get("Away"), day.AwayTime, day.Start, day.End);
        }
        // Rows follow the dashboard grouping: App → Label → Title, Label → Title, or App only.
        var labelOnly = report.Grouping == DashboardGrouping.Label;
        foreach (var app in report.Applications)
        {
            var type = DashboardText.Get(labelOnly ? "Label" : "Application");
            var appName = labelOnly ? "" : app.Name;
            var groupLabel = labelOnly ? app.Name : "";
            Add(type, period, appName, "", DashboardText.Get("Active"), app.ActiveTime, label: groupLabel);
            Add(type, period, appName, "", DashboardText.Get("Displayed"), app.DisplayedTime, label: groupLabel);
            if (report.Grouping == DashboardGrouping.AppLabelTitle)
            {
                foreach (var label in app.Labels)
                {
                    Add(DashboardText.Get("Label"), period, app.Name, "", DashboardText.Get("Active"), label.ActiveTime, label: label.Name);
                    Add(DashboardText.Get("Label"), period, app.Name, "", DashboardText.Get("Displayed"), label.DisplayedTime, label: label.Name);
                    AddTitles(label.Titles, app.Name);
                }
            }
            else AddTitles(app.Titles, appName);
        }
        return rows;

        void AddTitles(IEnumerable<DashboardTitle> titles, string app)
        {
            foreach (var title in titles)
            {
                Add(DashboardText.Get("WindowTitle"), period, app, title.Name, DashboardText.Get("Active"), title.ActiveTime, label: title.Label);
                Add(DashboardText.Get("WindowTitle"), period, app, title.Name, DashboardText.Get("Displayed"), title.DisplayedTime, label: title.Label);
            }
        }
    }

    private static string[] Headers() => [DashboardText.Get("Type"), DashboardText.Get("Period"), DashboardText.Get("Start"), DashboardText.Get("End"), DashboardText.Get("Application"), DashboardText.Get("Label"), DashboardText.Get("WindowTitle"), DashboardText.Get("Group"), DashboardText.Get("Duration"), DashboardText.Get("Hours")];
    private static string Safe(string value)
    {
        var trimmed = value.TrimStart();
        return value.Length > 0 && ("\t\r\n".Contains(value[0]) || (trimmed.Length > 0 && "=+-@".Contains(trimmed[0]))) ? "'" + value : value;
    }
    private static string Quote(string value) => "\"" + Safe(value).Replace("\"", "\"\"") + "\"";
    private static void WriteCsv(string path, IReadOnlyList<Row> rows)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine(string.Join(",", Headers().Select(Quote)));
        foreach (var row in rows)
            writer.WriteLine(string.Join(",", new[] { Quote(row.Type), Quote(row.Date), Quote(row.Start), Quote(row.End), Quote(row.Application), Quote(row.Label), Quote(row.Title), Quote(row.Metric), Quote(DashboardViewModel.Duration(row.Duration)), row.Duration.TotalHours.ToString("0.########", CultureInfo.InvariantCulture) }));
    }

    private static void WriteExcel(string path, IReadOnlyList<Row> rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(DashboardText.Get("Summary"));
        sheet.RightToLeft = true;
        var headers = Headers();
        for (var i = 0; i < headers.Length; i++) sheet.Cell(1, i + 1).Value = headers[i];
        sheet.Row(1).Style.Font.Bold = true;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; var n = i + 2;
            sheet.Cell(n, 1).Value = row.Type;
            sheet.Cell(n, 2).Value = row.Date;
            sheet.Cell(n, 3).Value = row.Start;
            sheet.Cell(n, 4).Value = row.End;
            sheet.Cell(n, 5).Value = row.Application;
            sheet.Cell(n, 6).Value = row.Label;
            sheet.Cell(n, 7).Value = row.Title;
            sheet.Cell(n, 8).Value = row.Metric;
            sheet.Cell(n, 9).Value = DashboardViewModel.Duration(row.Duration);
            sheet.Cell(n, 10).Value = row.Duration.TotalHours;
            sheet.Cell(n, 10).Style.NumberFormat.Format = "0.00";
        }
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents();
        workbook.SaveAs(path);
    }
}
