using System.Text;
using System.Text.RegularExpressions;
using Ats.Application.Common;
using Ats.Application.Python;
using ExcelDataReader;

namespace Ats.Application.Uploads;

/// <summary>A workbook sheet (or a PDF) as the preview lists it.</summary>
public sealed record SheetInfo(string Name, bool Usable, int Rows);

/// <summary>Times copied from other sheets: which sheets, and for how many listed days.</summary>
public sealed record HoursFrom(IReadOnlyList<string> Sheets, int Days);

/// <summary>Rows of cells read from a file. Cells are Python-like values: string, double, bool, DateTime, TimeOnly, TimeSpan.</summary>
public sealed record TimesheetTable(List<List<object?>> Rows, IReadOnlyList<SheetInfo> Sheets, string? Sheet, string FileType,
                                    HoursFrom? HoursFrom);

/// <summary>
/// Reading timesheet files into rows of cells (<c>app/services/timesheet_files.py</c>): CSV / TSV / TXT, Excel (.xlsx,
/// .xlsm, .xls) and PDF.
/// <list type="bullet">
/// <item>A workbook may hold several sheets: the one named like a month that has an employee and a date column is read,
/// else the first sheet that has them (or the sheet the admin picks).</item>
/// <item>The header row can be any of the first <see cref="HeaderSearchRows"/> rows.</item>
/// <item>A PDF is read from its text (<see cref="PdfTimesheet"/>).</item>
/// </list>
/// Interpreting the cells is the job of the upload service.
/// </summary>
public static partial class TimesheetFiles
{
    public const int MaxUploadBytes = 5 * 1024 * 1024;
    public const int HeaderSearchRows = 20;
    public const string AcceptedText = "CSV, TSV, TXT, Excel (.xlsx, .xls) or PDF";

    private static readonly string[] TimeKeys = ["in_time", "out_time", "hours"];

    static TimesheetFiles() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // cp1252 and .xls code pages

    /// <summary>Column key -> index. With both an ID column ("Employee ID") and a plain "Employee" column, the plain one is the name.</summary>
    public static OrderedDictionary<string, int> MapHeader(IReadOnlyList<object?> header, WfoMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(mapping);
        var candidates = new OrderedDictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < header.Count; i++)
        {
            var key = mapping.Column(PyText.CellText(header[i]));
            if (key is not null)
            {
                if (!candidates.TryGetValue(key, out var list))
                {
                    candidates[key] = list = [];
                }

                list.Add(i);
            }
        }

        var columns = new OrderedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (k, v) in candidates)
        {
            columns[k] = v[0];
        }

        if (candidates.TryGetValue("employee", out var ids) && ids.Count > 1 && !columns.ContainsKey("name"))
        {
            var plain = ids.Where(i => WfoMapping.NormalizeHeader(PyText.CellText(header[i])) == "employee").ToList();
            var specific = ids.Where(i => !plain.Contains(i)).ToList();
            if (plain.Count > 0 && specific.Count > 0)
            {
                columns["employee"] = specific[0];
                columns["name"] = plain[0];
            }
        }

        return columns;
    }

    /// <summary>(row index, columns) of the first of the top rows that has an employee and a date column.</summary>
    public static (int Row, OrderedDictionary<string, int> Columns)? FindHeader(IReadOnlyList<List<object?>> rows, WfoMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(rows);
        for (var i = 0; i < Math.Min(rows.Count, HeaderSearchRows); i++)
        {
            var columns = MapHeader(rows[i], mapping);
            if (columns.ContainsKey("employee") && columns.ContainsKey("date"))
            {
                return (i, columns);
            }
        }

        return null;
    }

    /// <summary>
    /// Rows of the file (of the chosen sheet for a workbook). Throws <see cref="ValidationFailedException"/> for
    /// unsupported or broken files.
    /// </summary>
    public static TimesheetTable ReadWorkbook(string filename, byte[] content, WfoMapping mapping, string? sheet = null)
    {
        ArgumentNullException.ThrowIfNull(filename);
        ArgumentNullException.ThrowIfNull(content);
        var name = filename.ToLowerInvariant();
        if (content.Length > MaxUploadBytes)
        {
            throw new ValidationFailedException($"The file is larger than {MaxUploadBytes / (1024 * 1024)} MB");
        }

        if (content.Length == 0)
        {
            throw new ValidationFailedException("The file is empty");
        }

        if (name.EndsWith(".csv", StringComparison.Ordinal) || name.EndsWith(".tsv", StringComparison.Ordinal)
            || name.EndsWith(".txt", StringComparison.Ordinal))
        {
            return new TimesheetTable(PyCsv.Read(content), [], null, "csv", null);
        }

        if (name.EndsWith(".xlsx", StringComparison.Ordinal) || name.EndsWith(".xlsm", StringComparison.Ordinal))
        {
            return Choose(XlsxSheets(content), sheet, "excel", mapping);
        }

        if (name.EndsWith(".xls", StringComparison.Ordinal))
        {
            return Choose(XlsSheets(content), sheet, "excel", mapping);
        }

        if (name.EndsWith(".pdf", StringComparison.Ordinal))
        {
            return new TimesheetTable(PdfTimesheet.Read(content, mapping), [], null, "pdf", null);
        }

        if (name.EndsWith(".ods", StringComparison.Ordinal) || name.EndsWith(".numbers", StringComparison.Ordinal))
        {
            throw new ValidationFailedException("Save this spreadsheet as Excel (.xlsx) or CSV and upload that");
        }

        throw new ValidationFailedException($"Upload a {AcceptedText} file");
    }

    // ------------------------------------------------------------------ Excel

    /// <summary>Every worksheet of an .xlsx / .xlsm, as openpyxl reads it (<see cref="XlsxReader"/>).</summary>
    internal static OrderedDictionary<string, List<List<object?>>> XlsxSheets(byte[] content)
    {
        try
        {
            return XlsxReader.Read(content);
        }
#pragma warning disable CA1031 // any parse failure is a bad file for the user (as in Python)
        catch (Exception)
#pragma warning restore CA1031
        {
            throw new ValidationFailedException("This Excel file could not be read");
        }
    }

    /// <summary>Every sheet of an .xls (ExcelDataReader), with values as xlrd gives them: dates as datetime, below one day as time.</summary>
    internal static OrderedDictionary<string, List<List<object?>>> XlsSheets(byte[] content)
    {
        var sheets = new OrderedDictionary<string, List<List<object?>>>(StringComparer.Ordinal);
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var reader = ExcelReaderFactory.CreateBinaryReader(stream);
            do
            {
                var rows = new List<List<object?>>();
                while (reader.Read())
                {
                    var row = new List<object?>(reader.FieldCount);
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        row.Add(XlsValue(reader.GetValue(i)));
                    }

                    rows.Add(row);
                }

                sheets.TryAdd(reader.Name ?? $"Sheet{sheets.Count + 1}", rows);
            }
            while (reader.NextResult());
        }
#pragma warning disable CA1031 // any parse failure is a bad file for the user (as in Python)
        catch (Exception)
#pragma warning restore CA1031
        {
            throw new ValidationFailedException("This Excel (.xls) file could not be read");
        }

        return sheets;
    }

    private static object? XlsValue(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b ? 1.0 : 0.0,
        DateTime dt => ExcelFormats.XlrdDate(ExcelFormats.SerialOf(dt)),
        TimeSpan ts => ExcelFormats.XlrdDate(ts.TotalDays),
        double d => d,
        IConvertible c => c.ToDouble(System.Globalization.CultureInfo.InvariantCulture),
        _ => PyText.Str(value),
    };

    // ------------------------------------------------------------------ choosing the sheet

    private static TimesheetTable Choose(OrderedDictionary<string, List<List<object?>>> data, string? sheet, string fileType, WfoMapping mapping)
    {
        var info = new List<SheetInfo>();
        foreach (var (n, rows) in data)
        {
            var found = FindHeader(rows, mapping);
            info.Add(new SheetInfo(n, found is not null,
                                   found is { } f ? rows.Skip(f.Row + 1).Count(r => r.Any(c => PyText.CellText(c).Length > 0)) : 0));
        }

        var names = data.Keys.ToList();
        if (names.Count == 0)
        {
            throw new ValidationFailedException("This workbook has no sheets");
        }

        string chosen;
        if (sheet is not null)
        {
            if (!data.ContainsKey(sheet))
            {
                throw new ValidationFailedException($"There is no sheet {PyFormat.Repr(sheet)}. Sheets: {string.Join(", ", names)}");
            }

            chosen = sheet;
        }
        else
        {
            // A monthly workbook keeps raw extracts next to the month's sheet ("September 2026"): prefer that one.
            var usable = info.Where(i => i.Usable).Select(i => i.Name).ToList();
            chosen = usable.FirstOrDefault(n => MonthName().IsMatch(n)) ?? (usable.Count > 0 ? usable[0] : names[0]);
        }

        var (table, hoursFrom) = WithTimesFromOtherSheets(data, chosen, mapping);
        return new TimesheetTable(table, info, chosen, fileType, hoursFrom);
    }

    private static object DayKey(object? v) => v switch
    {
        DateTime dt => DateOnly.FromDateTime(dt),
        DateOnly d => d,
        TimeOnly t => t,
        _ => PyText.CellText(v),
    };

    private static List<(string Kind, string Value)> PersonKeys(object? reference, object? name)
    {
        var output = new List<(string, string)>();
        var refText = PyText.CellText(reference);
        if (refText.Length > 0)
        {
            output.Add(("ref", PyText.SplitJoin(refText).ToLowerInvariant()));
        }

        var nameText = PyText.CellText(name);
        if (nameText.Length > 0)
        {
            output.Add(("name", WfoMapping.Normalize(nameText)));
        }

        return output;
    }

    private static object? Cell(List<object?> r, OrderedDictionary<string, int> cols, string key) =>
        cols.TryGetValue(key, out var i) && i < r.Count ? r[i] : null;

    /// <summary>
    /// A monthly workbook often lists the days on the month's sheet and keeps the swipe times on another sheet ("TS Raw
    /// Data": Code, Employee, Date, Time In, Time Out, Total Hrs). When the chosen sheet has no times, copy them over for
    /// the same person (by ID, or by name when the ID is written differently) and date.
    /// </summary>
    private static (List<List<object?>> Rows, HoursFrom? HoursFrom) WithTimesFromOtherSheets(
        OrderedDictionary<string, List<List<object?>>> data, string chosen, WfoMapping mapping)
    {
        var rows = data[chosen];
        var found = FindHeader(rows, mapping);
        if (found is null || TimeKeys.Any(found.Value.Columns.ContainsKey))
        {
            return (rows, null);
        }

        var times = new Dictionary<((string, string) Person, object Day), Dictionary<string, object?>>();
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var sources = new List<string>();
        foreach (var (name, other) in data)
        {
            if (name == chosen)
            {
                continue;
            }

            var f = FindHeader(other, mapping);
            if (f is not { } hit || !TimeKeys.Any(hit.Columns.ContainsKey))
            {
                continue;
            }

            var (at, cols) = hit;
            sources.Add(name);
            foreach (var k in TimeKeys)
            {
                if (cols.TryGetValue(k, out var ci))
                {
                    labels.TryAdd(k, PyText.CellText(other[at][ci]));
                }
            }

            foreach (var r in other.Skip(at + 1))
            {
                var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var k in TimeKeys)
                {
                    if (PyText.CellText(Cell(r, cols, k)).Length > 0)
                    {
                        values[k] = Cell(r, cols, k);
                    }
                }

                var day = DayKey(Cell(r, cols, "date"));
                if (values.Count == 0 || PyText.CellText(day).Length == 0)
                {
                    continue;
                }

                foreach (var pk in PersonKeys(Cell(r, cols, "employee"), Cell(r, cols, "name")))
                {
                    times.TryAdd((pk, day), values); // the first sheet listing the day wins
                }
            }
        }

        if (times.Count == 0)
        {
            return (rows, null);
        }

        var (headerAt, columns) = found.Value;
        var keys = TimeKeys.Where(labels.ContainsKey).ToList();
        var width = rows[headerAt].Count;
        var output = rows.Take(headerAt).Select(r => r.ToList()).ToList();
        output.Add([.. rows[headerAt], .. keys.Select(k => (object?)labels[k])]);
        var matched = 0;
        foreach (var r in rows.Skip(headerAt + 1))
        {
            var day = DayKey(Cell(r, columns, "date"));
            Dictionary<string, object?>? hit = null;
            foreach (var pk in PersonKeys(Cell(r, columns, "employee"), Cell(r, columns, "name")))
            {
                if (times.TryGetValue((pk, day), out hit))
                {
                    break;
                }
            }

            matched += hit is null ? 0 : 1;
            var padded = r.Concat(Enumerable.Repeat<object?>(null, width)).Take(width).ToList();
            padded.AddRange(keys.Select(k => hit?.GetValueOrDefault(k)));
            output.Add(padded);
        }

        return matched == 0 ? (rows, null) : (output, new HoursFrom(sources, matched)); // nothing in common: read the sheet as it is
    }

    [GeneratedRegex(@"\b(jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\b|\b\d{4}-\d{2}\b", RegexOptions.IgnoreCase)]
    private static partial Regex MonthName();
}
