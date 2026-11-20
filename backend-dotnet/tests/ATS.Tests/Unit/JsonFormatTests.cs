using System.Text.Json;
using System.Text.Json.Serialization;
using Ats.Application.Common;
using Ats.Application.Json;
using FluentAssertions;

namespace Ats.Tests.Unit;

/// <summary>The Pydantic wire format, checked against values captured from the Python backend.</summary>
public sealed class JsonFormatTests
{
    private static readonly JsonSerializerOptions Options = AtsJson.Create();

    private enum Mode
    {
        [JsonStringEnumMemberName("OFFICE")]
        Office,
        [JsonStringEnumMemberName("WFH")]
        Wfh,
    }

    private sealed record Sample(DateTimeOffset At, DateTimeOffset? Missing, DateOnly Day, TimeOnly Start,
                                 double? Pct, Mode WorkMode, int RequiredMinutes);

    [Fact]
    public void Serializes_like_pydantic()
    {
        var sample = new Sample(
            new DateTimeOffset(2026, 11, 19, 8, 39, 37, TimeSpan.Zero).AddTicks(5774360),
            null, new DateOnly(2026, 9, 1), new TimeOnly(9, 30), 66.7, Mode.Office, 480);
        JsonSerializer.Serialize(sample, Options).Should().Be(
            """{"at":"2026-11-19T08:39:37.577436Z","missing":null,"day":"2026-09-01","start":"09:30:00","pct":66.7,"work_mode":"OFFICE","required_minutes":480}""");
    }

    [Fact]
    public void Datetimes_are_utc_with_microseconds_omitted_when_zero()
    {
        UtcDateTimeOffsetConverter.Format(new DateTimeOffset(2026, 11, 19, 8, 0, 0, TimeSpan.Zero))
            .Should().Be("2026-11-19T08:00:00Z");
        UtcDateTimeOffsetConverter.Format(new DateTimeOffset(2026, 11, 19, 13, 30, 0, TimeSpan.FromHours(5.5)).AddTicks(10))
            .Should().Be("2026-11-19T08:00:00.000001Z");
        PythonTimeConverter.Format(new TimeOnly(18, 30, 5)).Should().Be("18:30:05");
    }

    [Fact]
    public void Reads_iso_values_and_lax_numbers()
    {
        var s = JsonSerializer.Deserialize<Sample>(
            """{"at":"2026-11-19T14:09:37+05:30","missing":null,"day":"2026-09-01","start":"09:30","pct":"66.7","work_mode":"WFH","required_minutes":"480"}""",
            Options)!;
        s.At.Should().Be(new DateTimeOffset(2026, 11, 19, 8, 39, 37, TimeSpan.Zero));
        s.Start.Should().Be(new TimeOnly(9, 30));
        s.WorkMode.Should().Be(Mode.Wfh);
        s.RequiredMinutes.Should().Be(480);
    }

    [Fact]
    public void Clock_truncates_to_microseconds()
    {
        var v = SystemClock.TruncateToMicroseconds(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1234567));
        (v.Ticks % 10).Should().Be(0);
        new SystemClock().UtcNow.Offset.Should().Be(TimeSpan.Zero);
    }
}
