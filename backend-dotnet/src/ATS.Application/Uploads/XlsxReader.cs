using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Ats.Application.Uploads;

/// <summary>
/// Cell values of every worksheet in an .xlsx / .xlsm, exactly as openpyxl's read-only, data-only reader returns them
/// (<c>load_workbook(read_only=True, data_only=True)</c> then <c>iter_rows(values_only=True)</c>):
/// <list type="bullet">
/// <item>rows run from row 1 to the last row element (missing rows are empty), cut at the sheet's declared dimension, and
/// every row is as wide as that dimension (without one: as far as the row's last cell element);</item>
/// <item>numbers in a date format are datetimes, times (below one day) or timedeltas (<c>[h]:mm</c> formats), using
/// openpyxl's own format rules and built-in format table; other numbers are floats; shared and inline strings are text
/// (rich-text runs joined); booleans; errors as their text; formulas give their cached value.</item>
/// </list>
/// </summary>
internal static class XlsxReader
{
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string DocRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private static readonly Dictionary<int, string> BuiltinFormats = new()
    {
        [0] = "General", [1] = "0", [2] = "0.00", [3] = "#,##0", [4] = "#,##0.00", [5] = "\"$\"#,##0_);(\"$\"#,##0)",
        [6] = "\"$\"#,##0_);[Red](\"$\"#,##0)", [7] = "\"$\"#,##0.00_);(\"$\"#,##0.00)", [8] = "\"$\"#,##0.00_);[Red](\"$\"#,##0.00)",
        [9] = "0%", [10] = "0.00%", [11] = "0.00E+00", [12] = "# ?/?", [13] = "# ??/??", [14] = "mm-dd-yy", [15] = "d-mmm-yy",
        [16] = "d-mmm", [17] = "mmm-yy", [18] = "h:mm AM/PM", [19] = "h:mm:ss AM/PM", [20] = "h:mm", [21] = "h:mm:ss",
        [22] = "m/d/yy h:mm", [37] = "#,##0_);(#,##0)", [38] = "#,##0_);[Red](#,##0)", [39] = "#,##0.00_);(#,##0.00)",
        [40] = "#,##0.00_);[Red](#,##0.00)", [41] = "_(* #,##0_);_(* \\(#,##0\\);_(* \"-\"_);_(@_)",
        [42] = "_(\"$\"* #,##0_);_(\"$\"* \\(#,##0\\);_(\"$\"* \"-\"_);_(@_)",
        [43] = "_(* #,##0.00_);_(* \\(#,##0.00\\);_(* \"-\"??_);_(@_)",
        [44] = "_(\"$\"* #,##0.00_)_(\"$\"* \\(#,##0.00\\)_(\"$\"* \"-\"??_)_(@_)", [45] = "mm:ss", [46] = "[h]:mm:ss",
        [47] = "mmss.0", [48] = "##0.0E+0", [49] = "@",
    };

    private sealed record Cell(int Column, object? Value);

    public static OrderedDictionary<string, List<List<object?>>> Read(byte[] content)
    {
        using var zip = new ZipArchive(new MemoryStream(content, writable: false), ZipArchiveMode.Read);
        var workbookPath = Relationships(zip, "_rels/.rels", "").FirstOrDefault(r => r.Type.EndsWith("/officeDocument", StringComparison.Ordinal)).Target
                           ?? "xl/workbook.xml";
        var workbookDir = workbookPath.Contains('/', StringComparison.Ordinal) ? workbookPath[..workbookPath.LastIndexOf('/')] : "";
        var relsPath = (workbookDir.Length > 0 ? workbookDir + "/" : "") + "_rels/" + workbookPath[(workbookPath.LastIndexOf('/') + 1)..] + ".rels";
        var rels = Relationships(zip, relsPath, workbookDir);
        var workbook = Load(zip, workbookPath) ?? throw new InvalidDataException("no workbook part");
        var main = XNamespace.Get(Main);
        var date1904 = workbook.Root?.Element(main + "workbookPr")?.Attribute("date1904")?.Value is "1" or "true";

        var sharedPath = rels.FirstOrDefault(r => r.Type.EndsWith("/sharedStrings", StringComparison.Ordinal)).Target;
        var shared = sharedPath is null ? [] : SharedStrings(zip, sharedPath);
        var stylesPath = rels.FirstOrDefault(r => r.Type.EndsWith("/styles", StringComparison.Ordinal)).Target;
        var (dateStyles, timedeltaStyles) = stylesPath is null ? ([], []) : DateStyles(Load(zip, stylesPath));

        var sheets = new OrderedDictionary<string, List<List<object?>>>(StringComparer.Ordinal);
        var byId = rels.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var sheet in workbook.Root?.Element(main + "sheets")?.Elements(main + "sheet") ?? [])
        {
            var name = sheet.Attribute("name")?.Value ?? "";
            var id = sheet.Attribute(XNamespace.Get(DocRel) + "id")?.Value ?? "";
            if (!byId.TryGetValue(id, out var rel) || rel.Type.Contains("chartsheet", StringComparison.Ordinal))
            {
                continue;
            }

            var entry = zip.GetEntry(rel.Target) ?? throw new InvalidDataException($"missing {rel.Target}");
            using var stream = entry.Open();
            sheets.TryAdd(name, SheetRows(stream, shared, dateStyles, timedeltaStyles, date1904));
        }

        return sheets;
    }

    private static XDocument? Load(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path);
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static List<(string Id, string Type, string Target)> Relationships(ZipArchive zip, string path, string baseDir)
    {
        var doc = Load(zip, path);
        if (doc?.Root is null)
        {
            return [];
        }

        return
        [
            .. doc.Root.Elements(XNamespace.Get(RelNs) + "Relationship").Select(r =>
                (r.Attribute("Id")?.Value ?? "", r.Attribute("Type")?.Value ?? "", Resolve(baseDir, r.Attribute("Target")?.Value ?? ""))),
        ];
    }

    private static string Resolve(string baseDir, string target)
    {
        var parts = new List<string>();
        var full = target.StartsWith('/') ? target[1..] : (baseDir.Length > 0 ? baseDir + "/" : "") + target;
        foreach (var p in full.Split('/'))
        {
            if (p == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (p is not ("." or ""))
            {
                parts.Add(p);
            }
        }

        return string.Join('/', parts);
    }

    /// <summary><c>read_string_table</c>: plain text plus rich-text runs, with "x005F_" removed.</summary>
    private static List<string> SharedStrings(ZipArchive zip, string path)
    {
        var output = new List<string>();
        var entry = zip.GetEntry(path);
        if (entry is null)
        {
            return output;
        }

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { IgnoreComments = true, DtdProcessing = DtdProcessing.Prohibit });
        reader.Read();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si" && reader.NamespaceURI == Main)
            {
                var si = (XElement)XNode.ReadFrom(reader); // leaves the reader on the next node
                output.Add(TextContent(si).Replace("x005F_", "", StringComparison.Ordinal));
                continue;
            }

            reader.Read();
        }

        return output;
    }

    /// <summary>openpyxl <c>Text.content</c>: the plain <c>t</c> then every run's <c>t</c> (phonetic runs ignored).</summary>
    private static string TextContent(XElement element)
    {
        var main = XNamespace.Get(Main);
        var sb = new StringBuilder();
        if (element.Element(main + "t") is { } plain)
        {
            sb.Append(plain.Value);
        }

        foreach (var run in element.Elements(main + "r"))
        {
            if (run.Element(main + "t") is { } t)
            {
                sb.Append(t.Value);
            }
        }

        return sb.ToString();
    }

    /// <summary>Stylesheet <c>_normalise_numbers</c>: which cell styles (cellXfs index) hold a date / a timedelta format.</summary>
    private static (HashSet<int> Dates, HashSet<int> Timedeltas) DateStyles(XDocument? styles)
    {
        var dates = new HashSet<int>();
        var timedeltas = new HashSet<int>();
        var main = XNamespace.Get(Main);
        if (styles?.Root is null)
        {
            return (dates, timedeltas);
        }

        var custom = new Dictionary<int, string>();
        foreach (var f in styles.Root.Element(main + "numFmts")?.Elements(main + "numFmt") ?? [])
        {
            if (int.TryParse(f.Attribute("numFmtId")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                custom[id] = f.Attribute("formatCode")?.Value ?? "";
            }
        }

        var index = 0;
        foreach (var xf in styles.Root.Element(main + "cellXfs")?.Elements(main + "xf") ?? [])
        {
            _ = int.TryParse(xf.Attribute("numFmtId")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id);
            var format = custom.TryGetValue(id, out var c) ? c : BuiltinFormats.GetValueOrDefault(id);
            if (ExcelFormats.IsDateFormat(format))
            {
                dates.Add(index);
            }

            if (ExcelFormats.IsTimedeltaFormat(format))
            {
                timedeltas.Add(index);
            }

            index++;
        }

        return (dates, timedeltas);
    }

    private static List<List<object?>> SheetRows(Stream stream, List<string> shared, HashSet<int> dateStyles, HashSet<int> timedeltaStyles,
                                                 bool date1904)
    {
        int? maxCol = null, maxRow = null;
        var rows = new List<List<object?>>();
        var counter = 1;
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { IgnoreComments = true, DtdProcessing = DtdProcessing.Prohibit });
        var rowCounter = 0;
        var inData = false;
        var stopped = false;
        reader.Read();
        while (!reader.EOF)
        {
            if (reader.NodeType != XmlNodeType.Element || reader.NamespaceURI != Main)
            {
                reader.Read();
                continue;
            }

            if (!inData && reader.LocalName == "dimension")
            {
                (maxCol, maxRow) = Boundaries(reader.GetAttribute("ref") ?? "");
            }
            else if (reader.LocalName == "sheetData")
            {
                inData = true;
            }
            else if (inData && reader.LocalName == "row")
            {
                var row = (XElement)XNode.ReadFrom(reader); // leaves the reader on the next node
                var r = row.Attribute("r")?.Value;
                rowCounter = r is null ? rowCounter + 1 : (int)double.Parse(r, CultureInfo.InvariantCulture);
                if (maxRow is { } limit && rowCounter > limit)
                {
                    stopped = true;
                    break;
                }

                var cells = ParseCells(row, shared, dateStyles, timedeltaStyles, date1904);
                for (; counter < rowCounter; counter++)
                {
                    rows.Add(maxCol is { } w ? [.. new object?[w]] : []);
                }

                if (counter <= rowCounter)
                {
                    rows.Add(RowValues(cells, maxCol));
                    counter++;
                }

                continue;
            }

            reader.Read();
        }

        if (stopped && maxRow is { } last)
        {
            for (; counter <= last; counter++)
            {
                rows.Add(maxCol is { } w ? [.. new object?[w]] : []);
            }
        }

        return rows;
    }

    private static List<object?> RowValues(List<Cell> cells, int? maxCol)
    {
        if (cells.Count == 0 && maxCol is null)
        {
            return [];
        }

        var width = maxCol ?? cells[^1].Column;
        var row = new object?[Math.Max(width, 0)];
        foreach (var c in cells)
        {
            if (c.Column >= 1 && c.Column <= width)
            {
                row[c.Column - 1] = c.Value;
            }
        }

        return [.. row];
    }

    private static List<Cell> ParseCells(XElement row, List<string> shared, HashSet<int> dateStyles, HashSet<int> timedeltaStyles, bool date1904)
    {
        var main = XNamespace.Get(Main);
        var output = new List<Cell>();
        var colCounter = 0;
        foreach (var c in row.Elements(main + "c"))
        {
            var type = c.Attribute("t")?.Value ?? "n";
            var style = int.TryParse(c.Attribute("s")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
            var coordinate = c.Attribute("r")?.Value;
            colCounter = coordinate is null ? colCounter + 1 : ColumnOf(coordinate);
            object? value = type == "inlineStr" ? null : NullIfEmpty(c.Element(main + "v")?.Value);
            if (value is string text)
            {
                value = type switch
                {
                    "n" => Number(text, style, dateStyles, timedeltaStyles, date1904),
                    "s" => shared[int.Parse(text, CultureInfo.InvariantCulture)],
                    "b" => int.Parse(text, CultureInfo.InvariantCulture) != 0,
                    "d" => FromIso(text),
                    _ => text,
                };
            }
            else if (type == "inlineStr" && c.Element(main + "is") is { } inline)
            {
                value = TextContent(inline);
            }

            output.Add(new Cell(colCounter, value));
        }

        return output;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    private static object Number(string text, int style, HashSet<int> dateStyles, HashSet<int> timedeltaStyles, bool date1904)
    {
        var value = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!dateStyles.Contains(style))
        {
            return value;
        }

        if (timedeltaStyles.Contains(style))
        {
            return ExcelFormats.TimeDelta(value);
        }

        return date1904 ? ExcelFormats.FromExcel1904(value) : ExcelFormats.FromExcel(value);
    }

    private static object? FromIso(string text)
    {
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
        {
            return text.Contains('T', StringComparison.Ordinal) || text.Contains('-', StringComparison.Ordinal) ? dt : TimeOnly.FromDateTime(dt);
        }

        return text;
    }

    private static int ColumnOf(string coordinate)
    {
        var col = 0;
        foreach (var ch in coordinate)
        {
            if (ch is >= 'A' and <= 'Z')
            {
                col = col * 26 + (ch - 'A' + 1);
            }
            else if (ch is >= 'a' and <= 'z')
            {
                col = col * 26 + (ch - 'a' + 1);
            }
            else if (ch != '$')
            {
                break;
            }
        }

        return col;
    }

    /// <summary><c>range_boundaries(ref)</c> → (max column, max row); "A1" alone is a one-cell sheet, as in openpyxl.</summary>
    private static (int? MaxCol, int? MaxRow) Boundaries(string reference)
    {
        var last = reference.Split(':')[^1].Replace("$", "", StringComparison.Ordinal);
        var letters = new string([.. last.TakeWhile(char.IsLetter)]);
        var digits = last[letters.Length..];
        int? col = letters.Length > 0 ? ColumnOf(letters) : null;
        int? row = int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? r : null;
        return (col, row);
    }
}
