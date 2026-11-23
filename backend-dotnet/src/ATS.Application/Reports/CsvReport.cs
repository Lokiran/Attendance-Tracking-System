using System.Globalization;
using System.Text;
using Ats.Application.Uploads;
using Ats.Domain.Rules;

namespace Ats.Application.Reports;

/// <summary>
/// CSV files as <c>app/services/reports.py</c> writes them: a UTF-8 BOM, CRLF line ends, minimal quoting (the <c>csv</c>
/// module) and <c>safe_cell</c> for every value.
/// </summary>
public static class CsvReport
{
    private static readonly char[] FormulaPrefixes = ['=', '+', '-', '@', '\t', '\r', '\n'];

    /// <summary>
    /// <c>safe_cell</c>: booleans as Yes / No, datetimes as office-local <c>YYYY-MM-DD HH:MM</c>, dates as ISO, numbers as
    /// Python prints them; text that a spreadsheet could run as a formula gets a leading apostrophe (CWE-1236).
    /// </summary>
    public static string SafeCell(object? value, TimeZoneInfo zone) => value switch
    {
        null => "",
        bool b => b ? "Yes" : "No",
        int or long => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        double d => PyText.Str(d),
        DateTimeOffset t => TimeZoneInfo.ConvertTime(t, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        string s => s.Length > 0 && FormulaPrefixes.Contains(s[0]) ? "'" + s : s,
        _ => SafeCell(Convert.ToString(value, CultureInfo.InvariantCulture), zone),
    };

    /// <summary><c>_hours</c>: minutes as hours with 2 decimals ("" for none). It is text, so a negative value gets the apostrophe too.</summary>
    public static string Hours(int? minutes) =>
        minutes is { } m ? (m / 60.0).ToString("F2", CultureInfo.InvariantCulture) : "";

    private static string Field(string s) =>
        s.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;

    public static byte[] ToCsv(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<object?>> rows, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(rows);
        var sb = new StringBuilder("﻿"); // BOM so Excel detects UTF-8
        sb.Append(string.Join(',', header.Select(Field))).Append("\r\n");
        foreach (var row in rows)
        {
            sb.Append(string.Join(',', row.Select(v => Field(SafeCell(v, zone))))).Append("\r\n");
        }

        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }

    /// <summary>Python <c>round(x, n)</c> as a double (half to even on the exact value).</summary>
    internal static double Round(double x, int digits) => PyMath.Round(x, digits);
}
