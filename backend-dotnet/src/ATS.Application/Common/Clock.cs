namespace Ats.Application.Common;

/// <summary>
/// The single source of the server's current time (<c>app/core/clock.py</c>). Tests replace it to simulate
/// specific moments.
/// </summary>
public interface IClock
{
    /// <summary>Current UTC time, truncated to microseconds like Python's <c>datetime.now(UTC)</c>.</summary>
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => TruncateToMicroseconds(DateTimeOffset.UtcNow);

    /// <summary>Python datetimes (and PostgreSQL timestamptz) hold microseconds; .NET ticks are 100 ns.</summary>
    public static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - (value.Ticks % 10), value.Offset);
}
