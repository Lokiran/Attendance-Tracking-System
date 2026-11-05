"""Single source of the server's current time.

All attendance timestamps come from here; browser-supplied times are never used for
check-in/check-out. Tests replace ``utcnow`` to simulate specific moments.
"""

from datetime import UTC, datetime


def utcnow() -> datetime:
    return datetime.now(UTC)
