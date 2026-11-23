using Ats.Domain;
using Ats.Domain.Rules;
using ClosedXML.Excel;

namespace Ats.Application.Reports;

/// <summary>One employee's attendance as an Excel workbook (<c>attendance_export.to_xlsx</c>): Summary, By month, Daily.</summary>
public static class EmployeeWorkbook
{
    private static XLColor Hex(string hex) => XLColor.FromHtml("#" + hex);

    public static byte[] Build(EmployeeReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var b = r.Brand;
        var muted = Hex("64748B");
        using var wb = new XLWorkbook();

        void Header(IXLWorksheet ws, int row, string[] titles)
        {
            for (var i = 0; i < titles.Length; i++)
            {
                var c = ws.Cell(row, i + 1);
                c.Value = titles[i];
                c.Style.Font.Bold = true;
                c.Style.Font.FontColor = XLColor.White;
                c.Style.Fill.BackgroundColor = Hex(b.Color);
                c.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                c.Style.Alignment.WrapText = true;
            }
        }

        static void Widths(IXLWorksheet ws, int[] values)
        {
            for (var i = 0; i < values.Length; i++)
            {
                ws.Column(i + 1).Width = values[i];
            }
        }

        var ws = wb.Worksheets.Add("Summary");
        ReportService.SetValue(ws.Cell("A1"), (b.Company.Length > 0 ? b.Company + " · " : "") + "Attendance report");
        ws.Cell("A1").Style.Font.Bold = true;
        ws.Cell("A1").Style.Font.FontSize = 14;
        ws.Cell("A1").Style.Font.FontColor = Hex(b.Color);
        var row = 3;
        foreach (var (label, value) in r.Facts)
        {
            ws.Cell(row, 1).Value = label;
            ws.Cell(row, 1).Style.Font.FontColor = muted;
            ReportService.SetValue(ws.Cell(row, 2), ReportService.SafeText(value));
            ws.Cell(row, 2).Style.Font.Bold = true;
            row++;
        }

        row++;
        var s = r.Summary;
        Header(ws, row, ["Measure", "Value", "Note"]);
        var figures = new (string Label, object Value, string Note)[]
        {
            ("Office (WFO) days", r.OfficeDays, $"of {r.RequiredDays} required · {r.TargetNote}"),
            ("Attendance rate", EmployeeReport.Pct(s.AttendancePercentage), $"{s.PresentDays} of {s.CountedDays} working days in the office"),
            ("Average office hours", EmployeeReport.Hm(s.AverageNetMinutes),
             s.HoursDays > 0 ? $"on {s.HoursDays} day(s) with recorded hours" : "not in the timesheets for this period"),
            ("Required hours", EmployeeReport.Hm(s.RequiredMinutes), $"{s.ExpectedDays} scheduled day(s)"),
            ("Absent days", s.AbsentDays, ""), ("Work from home days", s.WfhDays, ""), ("Leave days", s.LeaveDays, ""),
            ("Days without a timesheet", s.NoTimesheetDays, "not counted as absent"),
        };
        foreach (var (label, value, note) in figures)
        {
            row++;
            ws.Cell(row, 1).Value = label;
            ReportService.SetValue(ws.Cell(row, 2), value);
            ws.Cell(row, 2).Style.Font.Bold = true;
            ws.Cell(row, 3).Value = note;
            ws.Cell(row, 3).Style.Font.FontColor = muted;
        }

        ws.Cell(row - figures.Length + 1, 2).Style.Fill.BackgroundColor = Hex(EmployeeReport.BandSoft[r.Band]);
        row += 2;
        ws.Cell(row, 1).Value = EmployeeReport.BandRule(r.RequiredDays);
        ws.Cell(row, 1).Style.Font.FontColor = muted;
        Widths(ws, [28, 30, 58]);

        var mo = wb.Worksheets.Add("By month");
        Header(mo, 1, ["Month", "Working days", "Office days", "Required", "WFO status", "WFH", "Leave", "Absent", "No timesheet", "Attendance %",
                       "Average office hours"]);
        for (var i = 0; i < r.Months.Count; i++)
        {
            var m = r.Months[i];
            var f = m.Figures;
            object?[] values =
            [
                m.Label, f.WorkingDays, f.OfficeDays, f.RequiredDays, m.Status, f.WfhDays, f.LeaveDays, f.AbsentDays, m.NoTimesheetDays,
                f.AttendancePercentage is { } p ? PyMath.Round(p, 1) : null, f.AverageNetMinutes is { } a ? PyMath.Round(a / 60.0, 2) : null,
            ];
            for (var j = 0; j < values.Length; j++)
            {
                ReportService.SetValue(mo.Cell(i + 2, j + 1), values[j]);
            }

            mo.Cell(i + 2, 5).Style.Fill.BackgroundColor = Hex(m.Soft);
        }

        mo.SheetView.FreezeRows(1);
        Widths(mo, [16, 14, 12, 10, 14, 8, 8, 8, 14, 14, 22]);

        var da = wb.Worksheets.Add("Daily");
        Header(da, 1, ["Date", "Day", "Status", "In", "Out", "Office hours", "Required hours", "Notes"]);
        for (var i = 0; i < r.Records.Count; i++)
        {
            var rec = r.Records[i];
            object?[] values =
            [
                rec.Date, rec.Date.ToString("ddd", System.Globalization.CultureInfo.InvariantCulture), EmployeeReport.StatusLabel[rec.Status],
                r.Clock(rec.FirstIn), r.Clock(rec.LastOut), rec.HoursKnown ? PyMath.Round(rec.NetMinutes / 60.0, 2) : null,
                rec.RequiredMinutes != 0 ? PyMath.Round(rec.RequiredMinutes / 60.0, 2) : null, ReportService.SafeText(EmployeeReport.Notes(rec)),
            ];
            for (var j = 0; j < values.Length; j++)
            {
                ReportService.SetValue(da.Cell(i + 2, j + 1), values[j]);
            }

            da.Cell(i + 2, 1).Style.DateFormat.Format = "DD MMM YYYY";
            da.Cell(i + 2, 3).Style.Font.FontColor = Hex(EmployeeReport.StatusColor[rec.Status]);
            da.Cell(i + 2, 3).Style.Font.Bold = rec.Status is DayStatus.Present or DayStatus.Absent;
        }

        da.SheetView.FreezeRows(1);
        da.Range(1, 1, Math.Max(1, r.Records.Count + 1), 8).SetAutoFilter();
        Widths(da, [14, 7, 18, 8, 8, 13, 15, 40]);
        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        return stream.ToArray();
    }
}
