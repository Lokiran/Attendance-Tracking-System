using System.Text;

namespace Ats.Application.Uploads;

/// <summary>
/// <c>timesheet_files._csv_rows</c>: UTF-8 (with or without BOM), else cp1252 with replacement characters; the delimiter is
/// whichever of <c>, ; TAB |</c> is most frequent in the first 4096 characters (ties go to the first); then Python's
/// <c>csv.reader</c> (excel dialect: <c>"</c> quotes, doubled quotes, blank lines as empty rows).
/// </summary>
public static class PyCsv
{
    private static readonly char[] Delimiters = [',', ';', '\t', '|'];

    public static List<List<object?>> Read(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var text = Decode(content);
        var head = HeadByCodePoints(text, 4096);
        var delimiter = Delimiters[0];
        var best = -1;
        foreach (var d in Delimiters)
        {
            var n = head.Count(c => c == d);
            if (n > best)
            {
                (best, delimiter) = (n, d);
            }
        }

        return Parse(text, delimiter);
    }

    internal static string Decode(byte[] content)
    {
        try
        {
            var start = content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF ? 3 : 0;
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content, start, content.Length - start);
        }
        catch (DecoderFallbackException)
        {
            // Python's cp1252 has no character for 0x81, 0x8D, 0x8F, 0x90 and 0x9D: errors="replace" gives U+FFFD.
            var cp1252 = Encoding.GetEncoding(1252);
            var sb = new StringBuilder(content.Length);
            foreach (var b in content)
            {
                sb.Append(b is 0x81 or 0x8D or 0x8F or 0x90 or 0x9D ? '�' : cp1252.GetString([b])[0]);
            }

            return sb.ToString();
        }
    }

    private static string HeadByCodePoints(string text, int n)
    {
        var i = 0;
        for (var count = 0; i < text.Length && count < n; count++)
        {
            i += char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
        }

        return text[..i];
    }

    private enum State
    {
        StartRecord,
        StartField,
        InField,
        InQuotedField,
        QuoteInQuotedField,
        EatCrnl,
    }

    /// <summary>The C <c>csv</c> reader's state machine, fed line by line (lines end after '\n', as io.StringIO splits them).</summary>
    internal static List<List<object?>> Parse(string text, char delimiter)
    {
        var rows = new List<List<object?>>();
        var fields = new List<object?>();
        var field = new StringBuilder();
        var fieldLength = 0;
        var state = State.StartRecord;

        void Save()
        {
            fields.Add(field.ToString());
            field.Clear();
            fieldLength = 0;
        }

        void Add(char c)
        {
            field.Append(c);
            fieldLength++;
        }

        const int Eol = -2;

        void Process(int c)
        {
            var isNewline = c is '\n' or '\r';
            switch (state)
            {
                case State.StartRecord:
                    if (c == Eol)
                    {
                        return; // empty line: []
                    }

                    if (isNewline)
                    {
                        state = State.EatCrnl;
                        return;
                    }

                    state = State.StartField;
                    goto case State.StartField;
                case State.StartField:
                    if (isNewline || c == Eol)
                    {
                        Save();
                        state = c == Eol ? State.StartRecord : State.EatCrnl;
                    }
                    else if (c == '"')
                    {
                        state = State.InQuotedField;
                    }
                    else if (c == delimiter)
                    {
                        Save();
                    }
                    else
                    {
                        Add((char)c);
                        state = State.InField;
                    }

                    return;
                case State.InField:
                    if (isNewline || c == Eol)
                    {
                        Save();
                        state = c == Eol ? State.StartRecord : State.EatCrnl;
                    }
                    else if (c == delimiter)
                    {
                        Save();
                        state = State.StartField;
                    }
                    else
                    {
                        Add((char)c);
                    }

                    return;
                case State.InQuotedField:
                    if (c == Eol)
                    {
                        return;
                    }

                    if (c == '"')
                    {
                        state = State.QuoteInQuotedField;
                    }
                    else
                    {
                        Add((char)c);
                    }

                    return;
                case State.QuoteInQuotedField:
                    if (c == '"')
                    {
                        Add('"');
                        state = State.InQuotedField;
                    }
                    else if (c == delimiter)
                    {
                        Save();
                        state = State.StartField;
                    }
                    else if (isNewline || c == Eol)
                    {
                        Save();
                        state = c == Eol ? State.StartRecord : State.EatCrnl;
                    }
                    else
                    {
                        Add((char)c);
                        state = State.InField;
                    }

                    return;
                case State.EatCrnl:
                    if (c == Eol)
                    {
                        state = State.StartRecord;
                    }
                    else if (!isNewline)
                    {
                        // TODO(parity): Python raises "new-line character seen in unquoted field" (a server error); a lone '\r'
                        // ends the record here instead.
                        rows.Add(fields);
                        fields = [];
                        state = State.StartField;
                        Process(c);
                    }

                    return;
            }
        }

        var pos = 0;
        while (pos < text.Length)
        {
            var end = text.IndexOf('\n', pos);
            end = end < 0 ? text.Length : end + 1;
            for (var i = pos; i < end; i++)
            {
                Process(text[i]);
            }

            Process(Eol);
            pos = end;
            if (state == State.StartRecord)
            {
                rows.Add(fields);
                fields = [];
            }
        }

        if (fieldLength != 0 || state == State.InQuotedField || state != State.StartRecord)
        {
            // End of input inside a record (a quoted field left open): Python saves the field and returns the row.
            if (state is State.InQuotedField || fieldLength != 0)
            {
                Save();
            }

            if (fields.Count > 0)
            {
                rows.Add(fields);
            }
        }

        return rows;
    }
}
