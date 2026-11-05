"""Office-timezone helpers. Timestamps are stored in UTC; workdays use office-local dates."""

from datetime import UTC, date, datetime, time, timedelta
from zoneinfo import ZoneInfo

from app.core import clock
from app.core.config import get_settings


def office_tz() -> ZoneInfo:
    return get_settings().tz


def to_local(dt: datetime, tz: ZoneInfo | None = None) -> datetime:
    return dt.astimezone(tz or office_tz())


def local_date(dt: datetime, tz: ZoneInfo | None = None) -> date:
    """Office-local calendar date of an aware timestamp (the workday a session belongs to)."""
    return dt.astimezone(tz or office_tz()).date()


def local_to_utc(d: date, t: time, tz: ZoneInfo | None = None) -> datetime:
    """Interpret a wall-clock time on a local date in the office timezone and return UTC.

    zoneinfo resolves DST gaps/folds deterministically (fold=0), so this never raises.
    """
    return datetime.combine(d, t, tzinfo=tz or office_tz()).astimezone(UTC)


def local_day_bounds(start: date, end: date | None = None, tz: ZoneInfo | None = None) -> tuple[datetime, datetime]:
    """UTC [start, end) covering local dates start..end inclusive (DST-safe: uses local midnights)."""
    end = end or start
    zone = tz or office_tz()
    lo = datetime.combine(start, time.min, tzinfo=zone).astimezone(UTC)
    hi = datetime.combine(end + timedelta(days=1), time.min, tzinfo=zone).astimezone(UTC)
    return lo, hi


def today_local(tz: ZoneInfo | None = None) -> date:
    return local_date(clock.utcnow(), tz)


def iter_dates(start: date, end: date):
    d = start
    while d <= end:
        yield d
        d += timedelta(days=1)
