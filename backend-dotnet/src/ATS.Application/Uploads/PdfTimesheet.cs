using System.Text;
using System.Text.RegularExpressions;
using Ats.Application.Common;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Ats.Application.Uploads;

/// <summary>
/// A PDF table read from its text (<c>timesheet_files._pdf_table</c>): words are grouped into rows by their height on the
/// page and into columns by the header's positions, so a table exported or printed to PDF reads like the sheet it came
/// from. Text outside the header's columns (a pivot table beside the data) is dropped, the header repeated on later pages
/// is skipped, glued header labels / dates / IDs are split. A scanned PDF (no text) cannot be read.
/// <para>
/// pdfminer's characters and text lines are rebuilt from PdfPig letters: a letter spans its advance width, its box is one
/// font size high from (baseline - 0.2 x size), and consecutive letters form a line as in pdfminer's
/// <c>group_objects</c> (line_overlap 0.5, char_margin 2.0).
/// </para>
/// </summary>
public static partial class PdfTimesheet
{
    private sealed record Glyph(double X0, double X1, double Y0, double Y1, double Size, string Text)
    {
        public double Width => X1 - X0;

        public double Height => Y1 - Y0;
    }

    private sealed record Word(double X0, double X1, double Y, string Text)
    {
        public double Center => (X0 + X1) / 2;
    }

    public static List<List<object?>> Read(byte[] content, WfoMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        List<List<Glyph>> pages;
        try
        {
            using var document = PdfDocument.Open(content);
            pages = [.. document.GetPages().Select(GlyphsOf)];
        }
#pragma warning disable CA1031 // a broken or encrypted file is a bad file for the user (as in Python)
        catch (Exception)
#pragma warning restore CA1031
        {
            throw new ValidationFailedException("This PDF could not be read (it may be damaged or password-protected)");
        }

        var output = new List<List<object?>>();
        List<string>? headerTexts = null;
        var bounds = new List<(double Lo, double Hi)>();
        int? dateAt = null, empAt = null, nameAt = null;
        var anyText = false;
        foreach (var page in pages)
        {
            var rows = RowsByHeight(PageWords(page));
            anyText = anyText || rows.Count > 0;
            foreach (var original in rows)
            {
                if (headerTexts is null)
                {
                    var row = SplitHeaderWords(original, mapping);
                    var texts = row.Select(w => w.Text).ToList();
                    var cols = TimesheetFiles.MapHeader(texts, mapping);
                    if (cols.TryGetValue("employee", out var emp) && cols.TryGetValue("date", out var date))
                    {
                        (headerTexts, bounds, dateAt) = (texts, Bounds(row), date);
                        (empAt, nameAt) = (emp, cols.TryGetValue("name", out var n) ? n : null);
                        output.Add([.. texts]);
                    }

                    continue;
                }

                var cells = new string?[headerTexts.Count];
                foreach (var w in original)
                {
                    for (var i = 0; i < bounds.Count; i++)
                    {
                        if (bounds[i].Lo <= w.Center && w.Center < bounds[i].Hi)
                        {
                            cells[i] = cells[i] is null ? w.Text : $"{cells[i]} {w.Text}";
                            break;
                        }
                    }
                }

                if (cells.SequenceEqual(headerTexts) || SplitHeaderWords(original, mapping).Select(w => w.Text).SequenceEqual(headerTexts))
                {
                    continue; // the header again on a new page
                }

                UnglueDate(cells, dateAt);
                UnglueId(cells, empAt, nameAt);
                if (cells.Any(c => c is not null))
                {
                    output.Add([.. cells]);
                }
            }
        }

        if (!anyText)
        {
            throw new ValidationFailedException("This PDF has no text to read (it looks like a scanned image). Upload the "
                                                + "Excel or CSV file it came from instead.");
        }

        if (headerTexts is null)
        {
            var first = pages.Count > 0 ? RowsByHeight(PageWords(pages[0])).FirstOrDefault() ?? [] : [];
            var found = string.Join(", ", first.Take(8).Select(w => w.Text));
            throw new ValidationFailedException("Could not find an employee and a date column in this PDF. Found: "
                                                + (found.Length > 0 ? found : "nothing"));
        }

        return output;
    }

    private static List<Glyph> GlyphsOf(Page page) =>
    [
        .. page.Letters.Select(l =>
        {
            var size = l.PointSize > 0 ? l.PointSize : l.FontSize;
            var x0 = Math.Min(l.StartBaseLine.X, l.EndBaseLine.X);
            var x1 = Math.Max(l.StartBaseLine.X, l.EndBaseLine.X);
            var y0 = l.StartBaseLine.Y - 0.2 * size;
            return new Glyph(x0, x1, y0, y0 + size, size, l.Value);
        }),
    ];

    /// <summary>pdfminer's text lines: maximal runs of consecutive characters that sit side by side.</summary>
    private static IEnumerable<List<Glyph>> Lines(List<Glyph> chars)
    {
        Glyph? obj0 = null;
        List<Glyph>? line = null;
        foreach (var obj1 in chars)
        {
            if (obj0 is not null)
            {
                var halign = VOverlaps(obj0, obj1) && Math.Min(obj0.Height, obj1.Height) * 0.5 < VOverlap(obj0, obj1)
                             && HDistance(obj0, obj1) < Math.Max(obj0.Width, obj1.Width) * 2.0;
                if (halign && line is not null)
                {
                    line.Add(obj1);
                }
                else if (line is not null)
                {
                    yield return line;
                    line = null;
                }
                else if (halign)
                {
                    line = [obj0, obj1];
                }
                else
                {
                    yield return [obj0];
                }
            }

            obj0 = obj1;
        }

        if (obj0 is not null)
        {
            if (line is null)
            {
                yield return [obj0];
            }
            else
            {
                yield return line;
            }
        }
    }

    private static bool VOverlaps(Glyph a, Glyph b) => b.Y0 <= a.Y1 && a.Y0 <= b.Y1;

    private static double VOverlap(Glyph a, Glyph b) => VOverlaps(a, b) ? Math.Min(Math.Abs(a.Y0 - b.Y1), Math.Abs(a.Y1 - b.Y0)) : 0;

    private static double HDistance(Glyph a, Glyph b) =>
        b.X0 <= a.X1 && a.X0 <= b.X1 ? 0 : Math.Min(Math.Abs(a.X0 - b.X1), Math.Abs(a.X1 - b.X0));

    /// <summary>
    /// Words of one page: a text line is split where the gap between characters is wider than a space, so cells that sit
    /// close together on one line still come out separately.
    /// </summary>
    private static List<Word> PageWords(List<Glyph> page)
    {
        var words = new List<Word>();
        foreach (var line in Lines(page))
        {
            var current = new List<Glyph>();
            foreach (var c in line)
            {
                if (current.Count > 0 && c.X0 - current[^1].X1 > Math.Max(0.8 * c.Size, 4))
                {
                    words.Add(WordFrom(current));
                    current = [];
                }

                if (PyText.Strip(c.Text).Length > 0 || current.Count > 0)
                {
                    current.Add(c);
                }
            }

            if (current.Count > 0)
            {
                words.Add(WordFrom(current));
            }
        }

        return [.. words.Where(w => w.Text.Length > 0)];
    }

    private static Word WordFrom(List<Glyph> chars) =>
        new(chars[0].X0, chars[^1].X1, (chars[0].Y0 + chars[0].Y1) / 2, PyText.Strip(string.Concat(chars.Select(c => c.Text))));

    private static List<List<Word>> RowsByHeight(List<Word> words)
    {
        var rows = new List<List<Word>>();
        foreach (var w in words.OrderBy(w => -w.Y).ThenBy(w => w.X0))
        {
            if (rows.Count > 0 && Math.Abs(rows[^1][0].Y - w.Y) <= 3)
            {
                rows[^1].Add(w);
            }
            else
            {
                rows.Add([w]);
            }
        }

        return [.. rows.Select(r => r.OrderBy(w => w.X0).ToList())];
    }

    /// <summary>Each header column owns the space halfway to its neighbours; the outer columns as far again outwards.</summary>
    private static List<(double Lo, double Hi)> Bounds(List<Word> header)
    {
        var centers = header.Select(w => w.Center).ToList();
        var output = new List<(double, double)>();
        for (var i = 0; i < centers.Count; i++)
        {
            var c = centers[i];
            double? left = i > 0 ? (centers[i - 1] + c) / 2 : null;
            double? right = i + 1 < centers.Count ? (c + centers[i + 1]) / 2 : null;
            var half = left is { } l ? c - l : right is { } r ? r - c : 200;
            output.Add((left ?? c - Math.Max(half, header[i].Center - header[i].X0 + 10),
                        right ?? c + Math.Max(half, header[i].X1 - header[i].Center + 10)));
        }

        return output;
    }

    /// <summary>When the date cell is empty and the cell before it ends with a date, move the date to its own cell.</summary>
    private static void UnglueDate(string?[] cells, int? dateAt)
    {
        if (dateAt is not { } at || at == 0 || at >= cells.Length || cells[at] is not null)
        {
            return;
        }

        var before = cells[at - 1];
        var m = before is null ? null : GluedDate().Match(before);
        if (m is { Success: true } && PyText.Strip(m.Groups[1].Value).Length > 0)
        {
            cells[at - 1] = PyText.Strip(m.Groups[1].Value);
            cells[at] = m.Groups[2].Value;
        }
    }

    /// <summary>When the ID cell is empty and the name cell starts with a number, move the number to the ID cell.</summary>
    private static void UnglueId(string?[] cells, int? empAt, int? nameAt)
    {
        if (empAt is not { } e || nameAt is not { } n || e >= cells.Length || n >= cells.Length || cells[e] is not null)
        {
            return;
        }

        var m = cells[n] is { } text ? GluedId().Match(text) : null;
        if (m is { Success: true })
        {
            cells[e] = m.Groups[1].Value;
            cells[n] = PyText.Strip(m.Groups[2].Value);
        }
    }

    /// <summary>
    /// Split a header word that is several column labels run together, when every piece is a known column (or a serial-number
    /// label, with at least one known column). Each piece gets a share of the word's width in proportion to its length.
    /// </summary>
    private static List<Word> SplitHeaderWords(List<Word> row, WfoMapping mapping)
    {
        var output = new List<Word>();
        foreach (var w in row)
        {
            var pieces = GluedLabels().Split(w.Text).Select(PyText.Strip).Where(p => p.Length > 0).ToList();
            var known = pieces.Select(p => mapping.Column(p) is not null).ToList();
            if (mapping.Column(w.Text) is not null || pieces.Count < 2 || !known.Any(k => k)
                || !known.Zip(pieces).All(x => x.First || SerialLabel().IsMatch(x.Second)))
            {
                output.Add(w);
                continue;
            }

            var total = pieces.Sum(p => p.Length);
            var at = w.X0;
            foreach (var p in pieces)
            {
                var width = (w.X1 - w.X0) * p.Length / total;
                output.Add(new Word(at, at + width, w.Y, p));
                at += width;
            }
        }

        return output;
    }

    /// <summary>"Some Long Name2026-06-01": the name fills its cell, so the PDF text runs straight into the date.</summary>
    [GeneratedRegex(@"^(.*?[^\W\d_].*?)\s*(\d{4}[-/.]\d{1,2}[-/.]\d{1,2}|\d{1,2}[-/.]\d{1,2}[-/.]\d{4})$")]
    private static partial Regex GluedDate();

    /// <summary>Header labels printed without a gap ("Time InTime OutTotal Hrs"): a capital straight after a letter or digit.</summary>
    [GeneratedRegex(@"(?<=[a-z0-9.)])(?=[A-Z])")]
    private static partial Regex GluedLabels();

    /// <summary>A serial-number label ("Sr No.", "S.No", "Sl. No", "#"): not read, but it can run into the ID label.</summary>
    [GeneratedRegex(@"^(?:s(?:r|l)?\.?\s*no\.?|serial(?:\s*no\.?)?|#|no\.?)$", RegexOptions.IgnoreCase)]
    private static partial Regex SerialLabel();

    /// <summary>
    /// "10001Asha Quill": a right-aligned ID runs straight into the left-aligned name next to it. An ID with a word after a
    /// hyphen ("10002-ExtAsha Quill") ends where the name's first capital starts.
    /// </summary>
    [GeneratedRegex(@"^(\d[\d/.]*-[A-Za-z0-9]*?[a-z0-9](?=[A-Z])|\d[\d\-/.]*)\s*([^\W\d_].*)$")]
    private static partial Regex GluedId();
}
