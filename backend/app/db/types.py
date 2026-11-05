"""Portable column types."""

from datetime import UTC, datetime

from sqlalchemy import DateTime
from sqlalchemy.types import TypeDecorator


class UTCDateTime(TypeDecorator):
    """Timezone-aware timestamp that is always stored and returned in UTC.

    PostgreSQL stores ``timestamptz`` natively. Other backends (SQLite in tests) keep naive
    UTC values, which are re-tagged as UTC when loaded. Naive input is rejected so local
    times can never be stored by accident.
    """

    impl = DateTime(timezone=True)
    cache_ok = True

    def process_bind_param(self, value: datetime | None, dialect):
        if value is None:
            return None
        if value.tzinfo is None:
            raise ValueError("Naive datetimes are not allowed; use timezone-aware UTC values")
        value = value.astimezone(UTC)
        if dialect.name != "postgresql":
            value = value.replace(tzinfo=None)
        return value

    def process_result_value(self, value: datetime | None, dialect):
        if value is None:
            return None
        if value.tzinfo is None:
            return value.replace(tzinfo=UTC)
        return value.astimezone(UTC)
