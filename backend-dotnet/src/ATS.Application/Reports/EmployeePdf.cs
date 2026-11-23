using System.Globalization;
using Ats.Domain;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Ats.Application.Reports;

/// <summary>
/// One employee's attendance as a branded PDF (<c>attendance_export.to_pdf</c>, reportlab in Python). Same layout and content:
/// a coloured header band with the company or logo on every page, the facts, four key-figure tiles, the office days by
/// month (chart and table) and the daily table, with "Page n of N" in the footer. Written with PdfPig's builder in the
/// standard PDF fonts (Helvetica, ZapfDingbats), as reportlab does; not byte-identical to the Python file.
/// </summary>
public static class EmployeePdf
{
    private const double Mm = 72 / 25.4;
    private const double PageW = 595.2755905511812; // A4
    private const double PageH = 841.8897637795277;
    private const double Margin = 16 * Mm;
    private const double BandH = 24 * Mm;
    private const double FramePad = 6; // reportlab's Frame padding
    private const string Title = "Attendance report";
    private static readonly string[] MonthHeaders = ["Month", "Working days", "Office days", "Required", "WFO status", "WFH", "Leave", "Absent", "Attendance", "Avg hours"];
    private static readonly string[] DayHeaders = ["Date", "Day", "Status", "In", "Out", "Office hours", "Notes"];

    private enum Face
    {
        Regular,
        Bold,
        Dingbats,
    }

    private sealed record Rgb(byte R, byte G, byte B)
    {
        public static Rgb Of(string hex) => new(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
    }

    private static readonly Rgb Ink = Rgb.Of("0F172A");
    private static readonly Rgb Muted = Rgb.Of("64748B");
    private static readonly Rgb LineColor = Rgb.Of("E2E8F0");
    private static readonly Rgb Soft = Rgb.Of("F8FAFC");
    private static readonly Rgb White = Rgb.Of("FFFFFF");

    /// <summary>A paragraph: text in one face and size, wrapped to the cell width; an optional coloured bullet in front.</summary>
    private sealed record Para(string Text, Face Face, double Size, double Leading, Rgb Color, bool Right = false, Rgb? Bullet = null);

    private sealed record Row(List<List<Para>> Cells, Rgb? Background = null);

    private sealed record TableSpec(double[] Widths, double TopPad, double BottomPad, double LeftPad, double RightPad, bool Middle,
                                    double LineBelow, int RepeatRows, bool Box = false, double InnerGrid = 0, Rgb? Background = null,
                                    Rgb?[]? TopLines = null);

    /// <summary>Text measuring with the standard fonts' metrics (through a scratch page of PdfPig's builder).</summary>
    private sealed class Metrics : IDisposable
    {
        private readonly PdfDocumentBuilder _builder = new();
        private readonly PdfPageBuilder _page;
        private readonly Dictionary<Face, PdfDocumentBuilder.AddedFont> _fonts;
        private readonly Dictionary<(Face, char), double?> _widths = [];

        public Metrics()
        {
            _fonts = Fonts(_builder);
            _page = _builder.AddPage(PageW, PageH);
        }

        public static Dictionary<Face, PdfDocumentBuilder.AddedFont> Fonts(PdfDocumentBuilder b) => new()
        {
            [Face.Regular] = b.AddStandard14Font(Standard14Font.Helvetica),
            [Face.Bold] = b.AddStandard14Font(Standard14Font.HelveticaBold),
            [Face.Dingbats] = b.AddStandard14Font(Standard14Font.ZapfDingbats),
        };

        /// <summary>The width of one character at 1000 pt, or null when the font has no glyph for it.</summary>
        private double? Glyph(Face face, char c)
        {
            if (!_widths.TryGetValue((face, c), out var w))
            {
                try
                {
                    var letters = _page.MeasureText(c.ToString(), 1000, new PdfPoint(0, 0), _fonts[face]);
                    w = letters.Count == 0 ? 0 : letters[^1].EndBaseLine.X - letters[0].StartBaseLine.X;
                }
#pragma warning disable CA1031 // a character the standard font cannot show is replaced, whatever the reason
                catch (Exception)
#pragma warning restore CA1031
                {
                    w = null;
                }

                _widths[(face, c)] = w;
            }

            return w;
        }

        /// <summary>The text with characters the font cannot show replaced by '?' (Helvetica covers Western European text).</summary>
        public string Printable(string text, Face face) =>
            string.Concat(text.Select(c => Glyph(face, c) is null ? '?' : c));

        public double Width(string text, Face face, double size) => text.Sum(c => Glyph(face, c) ?? Glyph(face, '?') ?? 0) * size / 1000;

        public void Dispose()
        {
        }
    }

    /// <summary>Drawing commands of one page, replayed once the page count is known.</summary>
    private sealed class Canvas
    {
        public List<Action<PdfPageBuilder, Dictionary<Face, PdfDocumentBuilder.AddedFont>>> Ops { get; } = [];

        public void Text(double x, double y, string text, Face face, double size, Rgb color, bool right = false, Metrics? m = null)
        {
            if (text.Length == 0)
            {
                return;
            }

            var left = right && m is not null ? x - m.Width(text, face, size) : x;
            Ops.Add((p, f) =>
            {
                p.SetTextAndFillColor(color.R, color.G, color.B);
                p.AddText(text, size, new PdfPoint(left, y), f[face]);
            });
        }

        public void Rect(double x, double y, double w, double h, Rgb fill) =>
            Ops.Add((p, _) =>
            {
                p.SetTextAndFillColor(fill.R, fill.G, fill.B);
                p.SetStrokeColor(fill.R, fill.G, fill.B);
                p.DrawRectangle(new PdfPoint(x, y), w, h, 0.01, fill: true);
            });

        public void Line(double x1, double y1, double x2, double y2, double width, Rgb color) =>
            Ops.Add((p, _) =>
            {
                p.SetStrokeColor(color.R, color.G, color.B);
                p.DrawLine(new PdfPoint(x1, y1), new PdfPoint(x2, y2), width);
            });

        public void Dashed(double x1, double x2, double y, double on, double off, double width, Rgb color)
        {
            for (var x = x1; x < x2; x += on + off)
            {
                Line(x, y, Math.Min(x + on, x2), y, width, color);
            }
        }
    }

    private sealed class Layout(Metrics metrics)
    {
        private const double Top = PageH - (BandH + 9 * Mm) - FramePad;
        private const double Bottom = 18 * Mm + FramePad;
        public const double Left = Margin + FramePad;
        public const double Avail = PageW - 2 * Margin - 2 * FramePad;

        public List<Canvas> Pages { get; } = [new()];

        public double Y { get; private set; } = Top;

        private bool AtTop => Math.Abs(Y - Top) < 0.01;

        public Canvas Page => Pages[^1];

        public Metrics M { get; } = metrics;

        public void NewPage()
        {
            Pages.Add(new Canvas());
            Y = Top;
        }

        public void Space(double h)
        {
            if (!AtTop)
            {
                Y -= h;
            }
        }

        /// <summary>Make room for <paramref name="h"/> points, on a new page when this one is full.</summary>
        public void Need(double h)
        {
            if (Y - h < Bottom && !AtTop)
            {
                NewPage();
            }
        }

        public List<string> Wrap(Para p, double width)
        {
            var text = M.Printable(p.Text, p.Face);
            var lines = new List<string>();
            var current = "";
            foreach (var word in text.Split(' '))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length > 0 && M.Width(candidate, p.Face, p.Size) + (p.Bullet is null ? 0 : 9) > width)
                {
                    lines.Add(current);
                    current = word;
                }
                else
                {
                    current = candidate;
                }
            }

            lines.Add(current);
            return lines;
        }

        public double Height(List<Para> paras, double width) => paras.Sum(p => Wrap(p, width).Count * p.Leading);

        /// <summary>Draws the paragraphs from <paramref name="top"/> down within [x, x + width].</summary>
        public void DrawParas(List<Para> paras, double x, double top, double width)
        {
            var y = top;
            foreach (var p in paras)
            {
                foreach (var line in Wrap(p, width))
                {
                    var baseline = y - p.Leading + (p.Leading - p.Size) / 2 + p.Size * 0.22;
                    var textX = x;
                    if (p.Bullet is { } dot)
                    {
                        Page.Text(x, baseline + 1, "l", Face.Dingbats, 6, dot);
                        textX += 9;
                    }

                    if (p.Right)
                    {
                        Page.Text(x + width, baseline, line, p.Face, p.Size, p.Color, right: true, m: M);
                    }
                    else
                    {
                        Page.Text(textX, baseline, line, p.Face, p.Size, p.Color);
                    }

                    y -= p.Leading;
                }
            }
        }

        public void Paragraph(Para p, double spaceBefore = 0, double spaceAfter = 0)
        {
            Space(spaceBefore);
            var h = Height([p], Avail);
            Need(h);
            DrawParas([p], Left, Y, Avail);
            Y -= h + spaceAfter;
        }

        /// <summary>A table at the page margin (reportlab centres a table that is as wide as the frame plus its padding).</summary>
        public void Table(TableSpec spec, List<Row> rows)
        {
            var x0 = Margin;
            var total = spec.Widths.Sum();
            double RowHeight(Row r) => r.Cells.Select((c, i) => Height(c, spec.Widths[i] - spec.LeftPad - spec.RightPad)).DefaultIfEmpty(0).Max()
                                       + spec.TopPad + spec.BottomPad;
            void DrawRow(Row r, double h)
            {
                var top = Y;
                if ((r.Background ?? spec.Background) is { } bg)
                {
                    Page.Rect(x0, top - h, total, h, bg);
                }

                var x = x0;
                for (var i = 0; i < r.Cells.Count; i++)
                {
                    var inner = spec.Widths[i] - spec.LeftPad - spec.RightPad;
                    var contentH = Height(r.Cells[i], inner);
                    var cellTop = spec.Middle ? top - (h - contentH) / 2 : top - spec.TopPad;
                    DrawParas(r.Cells[i], x + spec.LeftPad, cellTop, inner);
                    if (spec.TopLines?[i] is { } lineColor)
                    {
                        Page.Line(x, top, x + spec.Widths[i], top, 2, lineColor);
                    }

                    if (spec.InnerGrid > 0 && i > 0)
                    {
                        Page.Line(x, top, x, top - h, spec.InnerGrid, LineColor);
                    }

                    x += spec.Widths[i];
                }

                if (spec.LineBelow > 0)
                {
                    Page.Line(x0, top - h, x0 + total, top - h, spec.LineBelow, LineColor);
                }

                if (spec.Box)
                {
                    Page.Line(x0, top, x0 + total, top, spec.InnerGrid, LineColor);
                    Page.Line(x0, top - h, x0 + total, top - h, spec.InnerGrid, LineColor);
                    Page.Line(x0, top, x0, top - h, spec.InnerGrid, LineColor);
                    Page.Line(x0 + total, top, x0 + total, top - h, spec.InnerGrid, LineColor);
                }

                Y -= h;
            }

            var header = rows.Take(spec.RepeatRows).ToList();
            var headerH = header.Sum(RowHeight);
            Need(headerH + (rows.Count > spec.RepeatRows ? RowHeight(rows[spec.RepeatRows]) : 0));
            header.ForEach(r => DrawRow(r, RowHeight(r)));
            foreach (var r in rows.Skip(spec.RepeatRows))
            {
                var h = RowHeight(r);
                if (Y - h < Bottom)
                {
                    NewPage();
                    header.ForEach(hr => DrawRow(hr, RowHeight(hr)));
                }

                DrawRow(r, h);
            }
        }
    }

    public static byte[] Build(EmployeeReport r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var b = r.Brand;
        var brand = Rgb.Of(b.Color);
        var accent = Rgb.Of(b.Accent);
        var width = PageW - 2 * Margin;
        using var metrics = new Metrics();
        var layout = new Layout(metrics);

        Para Label(string t) => new(t, Face.Regular, 7.5, 10, Muted);
        Para Small(string t) => new(t, Face.Regular, 7.5, 10, Muted);
        Para Cell(object t) => new(Convert.ToString(t, CultureInfo.InvariantCulture) ?? "", Face.Regular, 8, 10, Ink);
        Para CellR(object t) => new(Convert.ToString(t, CultureInfo.InvariantCulture) ?? "", Face.Regular, 8, 10, Ink, Right: true);
        Para Head(string t) => new(t, Face.Bold, 7.5, 9, White);
        Para Dot(string hex, string text, bool bold = false) => new(text, bold ? Face.Bold : Face.Regular, 8, 10, Ink, Bullet: Rgb.Of(hex));
        Para H2(string t) => new(t, Face.Bold, 11, 14, brand);

        // Who and when
        var facts = r.Facts;
        var infoRows = new List<Row>();
        for (var i = 0; i < 6; i += 3)
        {
            infoRows.Add(new Row([.. facts.Skip(i).Take(3).Select(f => new List<Para> { Label(f.Label), new(f.Value, Face.Bold, 10, 13, Ink) })]));
        }

        layout.Table(new TableSpec([width / 3, width / 3, width / 3], 3, 6, 0, 6, Middle: false, LineBelow: 0, RepeatRows: 0), infoRows);
        layout.Space(4);

        // Key figures
        var s = r.Summary;
        var bandHex = EmployeeReport.BandColor[r.Band];
        List<Para> Tile(string caption, string value, string note, string? color = null) =>
        [
            Label(caption),
            char.IsAsciiDigit(value.FirstOrDefault()) ? new(value, Face.Bold, 18, 21, color is null ? Ink : Rgb.Of(color))
                                                      : new(value, Face.Bold, 12, 21, color is null ? Ink : Rgb.Of(color)),
            Small(note),
        ];
        var tiles = new Row(
        [
            Tile("Office (WFO) days", $"{r.OfficeDays} of {r.RequiredDays}", r.TargetNote, bandHex),
            Tile("Attendance rate", EmployeeReport.Pct(s.AttendancePercentage), $"{s.PresentDays} of {s.CountedDays} working days"),
            Tile("Average office hours", s.HoursDays > 0 ? EmployeeReport.Hm(s.AverageNetMinutes) : "Not recorded",
                 s.HoursDays > 0 ? $"on {s.HoursDays} day(s) with hours" : "not in the timesheets"),
            Tile("Absent days", s.AbsentDays.ToString(CultureInfo.InvariantCulture), $"WFH {s.WfhDays} · Leave {s.LeaveDays}"),
        ]);
        layout.Table(new TableSpec([width / 4, width / 4, width / 4, width / 4], 7, 7, 8, 6, Middle: false, LineBelow: 0, RepeatRows: 0, Box: true,
                                   InnerGrid: 0.6, Background: Soft, TopLines: [Rgb.Of(bandHex), accent, accent, accent]), [tiles]);
        layout.Space(3);
        layout.Paragraph(Small(EmployeeReport.BandRule(r.RequiredDays)));

        if (r.Months.Count > 0)
        {
            layout.Paragraph(H2("Office days by month"), spaceBefore: 10, spaceAfter: 5);
            Chart(layout, r, width);
            layout.Space(6);
            layout.Paragraph(Small("Bar: office days in the month (green on target, amber close, red below). Dashed line: days required."));
            layout.Paragraph(H2("Summary by month"), spaceBefore: 10, spaceAfter: 5);
            var rows = new List<Row>
            {
                new([.. MonthHeaders.Select(h => new List<Para> { Head(h) })], brand),
            };
            var i = 0;
            foreach (var m in r.Months)
            {
                var f = m.Figures;
                rows.Add(new Row(
                [
                    [Cell(m.Label)], [CellR(f.WorkingDays)], [CellR(f.OfficeDays)], [CellR(f.RequiredDays)], [Dot(m.Color, m.Status)], [CellR(f.WfhDays)],
                    [CellR(f.LeaveDays)], [CellR(f.AbsentDays)], [CellR(EmployeeReport.Pct(f.AttendancePercentage))], [CellR(EmployeeReport.Hm(f.AverageNetMinutes))],
                ], i++ % 2 == 0 ? White : Soft));
            }

            double[] shares = [0.13, 0.1, 0.09, 0.09, 0.15, 0.07, 0.07, 0.08, 0.11, 0.11];
            layout.Table(new TableSpec([.. shares.Select(x => width * x)], 4, 4, 6, 6, Middle: true, LineBelow: 0.4, RepeatRows: 1), rows);
        }

        // Every day
        layout.Paragraph(H2("Daily attendance"), spaceBefore: 10, spaceAfter: 5);
        var daily = new List<Row> { new([.. DayHeaders.Select(h => new List<Para> { Head(h) })], brand) };
        foreach (var rec in r.Records)
        {
            var quiet = rec.Status is DayStatus.Weekend or DayStatus.NotScheduled or DayStatus.NoTimesheet;
            daily.Add(new Row(
            [
                [Cell(rec.Date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture))], [Cell(rec.Date.ToString("ddd", CultureInfo.InvariantCulture))],
                [Dot(EmployeeReport.StatusColor[rec.Status], EmployeeReport.StatusLabel[rec.Status], bold: rec.Status == DayStatus.Absent)],
                [Cell(r.Clock(rec.FirstIn))], [Cell(r.Clock(rec.LastOut))], [CellR(rec.HoursKnown ? EmployeeReport.Hm(rec.NetMinutes) : "")],
                [Small(EmployeeReport.Notes(rec))],
            ], quiet ? Soft : null));
        }

        if (daily.Count == 1)
        {
            daily.Add(new Row([[Small("No days in this period.")], [], [], [], [], [], []]));
        }

        double[] dayShares = [0.16, 0.08, 0.24, 0.09, 0.09, 0.13, 0.21];
        layout.Table(new TableSpec([.. dayShares.Select(x => width * x)], 2.5, 2.5, 6, 6, Middle: true, LineBelow: 0.3, RepeatRows: 1), daily);

        return Render(layout, r, brand, accent, metrics);
    }

    private static void Chart(Layout layout, EmployeeReport r, double width)
    {
        double chartH = 46 * Mm, baseLine = 12 * Mm, topPad = 6 * Mm;
        layout.Need(chartH);
        var x0 = Layout.Left;
        var y0 = layout.Y - chartH;
        var page = layout.Page;
        var m = layout.M;
        var months = r.Months;
        var peak = Math.Max(1, months.Max(x => Math.Max(x.Figures.OfficeDays, x.Figures.RequiredDays)));
        var scale = (chartH - baseLine - topPad) / peak;
        var slot = Math.Min(width / months.Count, 42 * Mm);
        var barW = Math.Min(slot * 0.5, 16 * Mm);
        var left = (width - slot * months.Count) / 2;
        page.Line(x0, y0 + baseLine, x0 + width, y0 + baseLine, 0.8, LineColor);
        void Centered(double cx, double y, string text, Face face, double size, Rgb color) =>
            page.Text(cx - m.Width(text, face, size) / 2, y, text, face, size, color);
        for (var i = 0; i < months.Count; i++)
        {
            var month = months[i];
            var cx = x0 + left + slot * i + slot / 2;
            var h = month.Figures.OfficeDays * scale;
            Centered(cx, y0 + baseLine - 10, month.Figures.Label, Face.Regular, 8, Ink);
            if (!month.Figures.Covered)
            {
                Centered(cx, y0 + baseLine + 4, "No timesheet", Face.Regular, 7, Muted);
                continue;
            }

            if (h > 0)
            {
                page.Rect(cx - barW / 2, y0 + baseLine, barW, h, Rgb.Of(month.Color));
            }

            var reqY = y0 + baseLine + month.Figures.RequiredDays * scale;
            page.Dashed(cx - barW / 2 - 5, cx + barW / 2 + 5, reqY, 3, 2, 1, Ink);
            // The number sits above the bar, or above the requirement line when that is just over the bar.
            var gap = reqY - (y0 + baseLine + h);
            var labelY = gap is >= 0 and < 12 ? reqY + 3 : y0 + baseLine + h + 3;
            Centered(cx, labelY, month.Figures.OfficeDays.ToString(CultureInfo.InvariantCulture), Face.Bold, 9, Ink);
            Centered(cx, y0 + baseLine - 19, $"{month.Status} · need {month.Figures.RequiredDays}", Face.Regular, 6.5, Muted);
        }

        layout.Space(chartH);
    }

    private static (int W, int H)? ImageSize(byte[] bytes, out bool png)
    {
        png = bytes.Length > 24 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
        if (png)
        {
            return ((bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19], (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23]);
        }

        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
        {
            return null;
        }

        for (var i = 2; i + 9 < bytes.Length;)
        {
            if (bytes[i] != 0xFF)
            {
                return null;
            }

            var marker = bytes[i + 1];
            var length = (bytes[i + 2] << 8) | bytes[i + 3];
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                return ((bytes[i + 7] << 8) | bytes[i + 8], (bytes[i + 5] << 8) | bytes[i + 6]);
            }

            i += 2 + length;
        }

        return null;
    }

    private static byte[] Render(Layout layout, EmployeeReport r, Rgb brand, Rgb accent, Metrics metrics)
    {
        var b = r.Brand;
        var builder = new PdfDocumentBuilder();
        builder.DocumentInformation.Title = $"{Title} – {r.Employee.FullName}";
        builder.DocumentInformation.Author = b.Company.Length > 0 ? b.Company : "Attendance";
        builder.DocumentInformation.Subject = EmployeeReport.Period(r.Start, r.End);
        var fonts = Metrics.Fonts(builder);
        byte[]? logo = null;
        (int W, int H)? logoSize = null;
        var logoPng = false;
        if (b.Logo is not null)
        {
            try
            {
                logo = File.ReadAllBytes(b.Logo);
                logoSize = ImageSize(logo, out logoPng);
            }
            catch (IOException)
            {
                logo = null;
            }
        }

        PdfPageBuilder.AddedImage? added = null;
        var footerLeft = string.Join(" · ", new[] { b.Company, Title, r.Employee.FullName }.Where(x => x.Length > 0));
        footerLeft = metrics.Printable(footerLeft.Length > 110 ? footerLeft[..110] : footerLeft, Face.Regular);
        var period = EmployeeReport.Period(r.Start, r.End);
        for (var i = 0; i < layout.Pages.Count; i++)
        {
            var page = builder.AddPage(PageW, PageH);
            var frame = new Canvas();
            frame.Rect(0, PageH - BandH, PageW, BandH, brand);
            frame.Rect(0, PageH - BandH - 1.2 * Mm, PageW, 1.2 * Mm, accent);
            var drewLogo = false;
            if (logo is not null && logoSize is { W: > 0, H: > 0 } size)
            {
                var h = 8 * Mm;
                var w = Math.Min(h * size.W / size.H, 60 * Mm);
                var rect = new PdfRectangle(Margin, PageH - BandH / 2 - h / 2, Margin + w, PageH - BandH / 2 + h / 2);
                try
                {
                    // Embedded once and drawn on every page (as reportlab does).
                    if (added is null)
                    {
                        added = logoPng ? page.AddPng(logo, rect) : page.AddJpeg(logo, rect);
                    }
                    else
                    {
                        page.AddImage(added, rect);
                    }

                    drewLogo = true;
                }
#pragma warning disable CA1031 // a broken logo must not break the report
                catch (Exception)
#pragma warning restore CA1031
                {
                    drewLogo = false;
                }
            }

            if (!drewLogo && b.Company.Length > 0)
            {
                frame.Text(Margin, PageH - BandH / 2 - 5, metrics.Printable(b.Company, Face.Bold), Face.Bold, 16, White);
            }

            frame.Text(PageW - Margin, PageH - BandH / 2 + 1, Title, Face.Bold, 13, White, right: true, m: metrics);
            frame.Text(PageW - Margin, PageH - BandH / 2 - 10, period, Face.Regular, 8.5, White, right: true, m: metrics);
            frame.Line(Margin, 13 * Mm, PageW - Margin, 13 * Mm, 1, LineColor);
            frame.Text(Margin, 9 * Mm, footerLeft, Face.Regular, 7.5, Muted);
            frame.Text(PageW - Margin, 9 * Mm, $"Generated {r.Generated} · Page {i + 1} of {layout.Pages.Count}", Face.Regular, 7.5, Muted, right: true, m: metrics);
            foreach (var op in frame.Ops.Concat(layout.Pages[i].Ops))
            {
                op(page, fonts);
            }
        }

        return builder.Build();
    }
}
