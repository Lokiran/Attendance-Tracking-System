using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Ats.Application.Common;
using Ats.Application.Python;
using Ats.Application.Uploads;
using FluentAssertions;

namespace Ats.Tests.Uploads;

/// <summary>
/// Replays fixtures produced by the Python backend's own readers (tools/make_upload_fixtures.py): every synthetic CSV,
/// Excel and PDF file must give exactly the same cells (or the same error), and the value parsers the same results.
/// </summary>
public sealed class UploadParityTests
{
    internal static readonly Lazy<WfoMapping> Mapping = new(() =>
        WfoMapping.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "config", "wfo_mapping.json"))));

    private static JsonElement Load()
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "uploads.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip).RootElement.Clone();
    }

    internal static string? Canon(object? v) => v switch
    {
        null => null,
        bool b => b ? "b:True" : "b:False",
        double d => "f:" + PyJson.FloatRepr(d),
        DateTime dt => "dt:" + PyText.DateTimeStr(dt).Replace(' ', 'T'),
        TimeOnly t => "t:" + PyFormat.IsoTime(t),
        TimeSpan ts => "td:" + PyJson.FloatRepr(ts.Ticks / 1e7),
        string s => "s:" + s,
        _ => "o:" + v,
    };

    private static string Describe(TimesheetTable t) => JsonSerializer.Serialize(new
    {
        rows = t.Rows.Select(r => r.Select(Canon).ToList()).ToList(),
        sheets = t.Sheets.Select(s => new { name = s.Name, usable = s.Usable, rows = s.Rows }).ToList(),
        sheet = t.Sheet,
        file_type = t.FileType,
        hours_from = t.HoursFrom is null ? null : new { sheets = t.HoursFrom.Sheets, days = t.HoursFrom.Days },
    });

    private static string Expected(JsonElement result) => JsonSerializer.Serialize(new
    {
        rows = result.GetProperty("rows").EnumerateArray().Select(r => r.EnumerateArray().Select(c => c.GetString()).ToList()).ToList(),
        sheets = result.GetProperty("sheets").EnumerateArray().Select(s => new
        {
            name = s.GetProperty("name").GetString(), usable = s.GetProperty("usable").GetBoolean(), rows = s.GetProperty("rows").GetInt32(),
        }).ToList(),
        sheet = result.GetProperty("sheet").GetString(),
        file_type = result.GetProperty("file_type").GetString(),
        hours_from = result.GetProperty("hours_from").ValueKind == JsonValueKind.Null ? null : new
        {
            sheets = result.GetProperty("hours_from").GetProperty("sheets").EnumerateArray().Select(x => x.GetString()).ToList(),
            days = result.GetProperty("hours_from").GetProperty("days").GetInt32(),
        },
    });

    internal static List<string> CompareFiles(IEnumerable<JsonElement> cases, Func<JsonElement, byte[]> content)
    {
        var problems = new List<string>();
        foreach (var c in cases)
        {
            var name = c.GetProperty("name").GetString()!;
            var sheet = c.GetProperty("sheet").ValueKind == JsonValueKind.Null ? null : c.GetProperty("sheet").GetString();
            string actual;
            try
            {
                actual = Describe(TimesheetFiles.ReadWorkbook(name, content(c), Mapping.Value, sheet));
            }
            catch (ValidationFailedException e)
            {
                actual = "error: " + e.Message;
            }

            var expected = c.TryGetProperty("error", out var error) ? "error: " + error.GetString() : Expected(c.GetProperty("result"));
            if (actual != expected)
            {
                problems.Add($"{name} (sheet {sheet ?? "-"}):\n  python {Clip(expected)}\n  dotnet {Clip(actual)}\n  first difference at {FirstDifference(expected, actual)}");
            }
        }

        return problems;
    }

    private static string Clip(string s) => s.Length > 600 ? s[..600] + "..." : s;

    private static string FirstDifference(string a, string b)
    {
        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i])
        {
            i++;
        }

        var from = Math.Max(0, i - 80);
        return $"{i}: python …{a[from..Math.Min(a.Length, i + 80)]}… dotnet …{b[from..Math.Min(b.Length, i + 80)]}…";
    }

    private static object? Typed(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var p = e.EnumerateObject().Single();
        return p.Name switch
        {
            "s" => p.Value.GetString(),
            "f" => p.Value.ValueKind == JsonValueKind.String ? double.NaN : p.Value.GetDouble(),
            "b" => p.Value.GetBoolean(),
            "dt" => DateTime.Parse(p.Value.GetString()!, CultureInfo.InvariantCulture),
            "t" => TimeOnly.Parse(p.Value.GetString()!, CultureInfo.InvariantCulture),
            "td" => TimeSpan.FromTicks((long)Math.Round(p.Value.GetDouble() * 1e7)),
            _ => throw new InvalidOperationException(p.Name),
        };
    }

    private static string Outcome(Func<object?, object?> fn, object? v)
    {
        try
        {
            return fn(v) switch
            {
                null => "value:null",
                DateOnly d => "value:" + PyFormat.IsoDate(d),
                TimeOnly t => "value:" + PyFormat.IsoTime(t),
                var other => "value:" + Convert.ToString(other, CultureInfo.InvariantCulture),
            };
        }
        catch (RowValueException e)
        {
            return "error:" + e.Message;
        }
    }

    private static string Outcome(JsonElement e) =>
        e.TryGetProperty("error", out var error) ? "error:" + error.GetString()
        : e.GetProperty("value").ValueKind == JsonValueKind.Null ? "value:null"
        : "value:" + (e.GetProperty("value").ValueKind == JsonValueKind.Number ? e.GetProperty("value").GetInt32().ToString(CultureInfo.InvariantCulture)
                      : e.GetProperty("value").GetString());

    [Fact]
    public void Files_read_like_python()
    {
        var files = Load().GetProperty("files").EnumerateArray().ToList();
        files.Should().HaveCountGreaterThan(100);
        var problems = CompareFiles(files, c => Convert.FromBase64String(c.GetProperty("content").GetString()!));
        problems.Should().BeEmpty(string.Join("\n", problems.Take(5)));
    }

    [Fact]
    public void Value_parsers_match_python()
    {
        var values = Load().GetProperty("values");
        var problems = new List<string>();
        foreach (var c in values.GetProperty("singles").EnumerateArray())
        {
            var v = Typed(c.GetProperty("input"));
            var label = c.GetProperty("input").GetRawText();
            void Check(string what, string expected, string actual)
            {
                if (expected != actual)
                {
                    problems.Add($"{what}({label}): python {expected} / dotnet {actual}");
                }
            }

            Check("parse_date", Outcome(c.GetProperty("date")), Outcome(x => UploadValues.ParseDate(x), v));
            Check("parse_hours", Outcome(c.GetProperty("hours")), Outcome(x => UploadValues.ParseHours(x), v));
            Check("parse_time", Outcome(c.GetProperty("time")), Outcome(x => UploadValues.ParseTime(x), v));
            var candidates = string.Join(";", UploadValues.DateCandidates(v).Select(x => $"{PyFormat.IsoDate(x.Date)}/{x.Swapped}"));
            var expectedCandidates = string.Join(";", c.GetProperty("candidates").EnumerateArray()
                .Select(x => $"{x[0].GetString()}/{(x[1].GetBoolean() ? "True" : "False")}"));
            Check("date_candidates", expectedCandidates, candidates);
        }

        foreach (var c in values.GetProperty("readers").EnumerateArray())
        {
            var seq = c.GetProperty("values").EnumerateArray().Select(Typed).ToList();
            var reader = DateReader.ForValues(seq);
            var reads = seq.Select(reader.Read).Select(d => d is { } x ? PyFormat.IsoDate(x) : null).ToList();
            var months = string.Join(";", reader.Months.OrderBy(m => m.Year).ThenBy(m => m.Month).Select(m => $"{m.Year}-{m.Month}"));
            var expectedMonths = string.Join(";", c.GetProperty("months").EnumerateArray().Select(m => $"{m[0].GetInt32()}-{m[1].GetInt32()}"));
            var expectedReads = c.GetProperty("reads").EnumerateArray().Select(x => x.GetString()).ToList();
            if (months != expectedMonths || !reads.SequenceEqual(expectedReads) || reader.MonthFirst != c.GetProperty("month_first").GetInt32()
                || reader.AmbiguousDayFirst != c.GetProperty("ambiguous_day_first").GetInt32())
            {
                problems.Add($"DateReader({c.GetProperty("values").GetRawText()}): months {months} vs {expectedMonths}, reads "
                             + $"{string.Join(",", reads)} vs {string.Join(",", expectedReads)}");
            }
        }

        problems.Should().BeEmpty(string.Join("\n", problems.Take(10)));
    }

    /// <summary>
    /// Opt-in: real timesheet files on this machine (never committed), recorded by
    /// <c>tools/make_upload_fixtures.py --local OUT.json FILE...</c>; ATS_UPLOAD_PARITY_FILE names that OUT.json.
    /// </summary>
    [Fact]
    public void Local_files_read_like_python()
    {
        var path = Environment.GetEnvironmentVariable("ATS_UPLOAD_PARITY_FILE");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var files = JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("files").EnumerateArray().ToList();
        var problems = CompareFiles(files, c => File.ReadAllBytes(c.GetProperty("path").GetString()!));
        problems.Should().BeEmpty(string.Join("\n", problems.Take(3)));
    }
}
