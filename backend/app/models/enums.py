from enum import StrEnum


class Role(StrEnum):
    EMPLOYEE = "EMPLOYEE"
    MANAGER = "MANAGER"
    ADMIN = "ADMIN"


class SessionStatus(StrEnum):
    OPEN = "OPEN"                          # checked in, not yet checked out
    COMPLETED = "COMPLETED"                # checked out; net_minutes final
    MISSING_CHECKOUT = "MISSING_CHECKOUT"  # never checked out; excluded from totals until corrected
    VOIDED = "VOIDED"                      # superseded by an approved correction (kept for history)


class SessionSource(StrEnum):
    WEB = "WEB"
    CORRECTION = "CORRECTION"
    ADMIN = "ADMIN"
    SEED = "SEED"
    AUTO_WIFI = "AUTO_WIFI"    # created by the auto-attendance engine from office Wi-Fi signals
    AUTO_TEAMS = "AUTO_TEAMS"  # created by the auto-attendance engine from Teams presence


# Sources a person (or reviewer) produced; these always win over AUTO_* sessions.
MANUAL_SOURCES = frozenset({SessionSource.WEB, SessionSource.ADMIN, SessionSource.CORRECTION})
AUTO_SOURCES = frozenset({SessionSource.AUTO_WIFI, SessionSource.AUTO_TEAMS})


class SignalSource(StrEnum):
    WIFI = "WIFI"
    TEAMS = "TEAMS"


class WorkLocation(StrEnum):
    OFFICE = "OFFICE"
    WFH = "WFH"


class AutoSource(StrEnum):
    AUTO_WIFI = "AUTO_WIFI"
    AUTO_TEAMS = "AUTO_TEAMS"


class DayStatus(StrEnum):
    PRESENT = "PRESENT"
    ABSENT = "ABSENT"
    HOLIDAY = "HOLIDAY"
    WEEKEND = "WEEKEND"
    INCOMPLETE = "INCOMPLETE"
    NOT_YET_CHECKED_IN = "NOT_YET_CHECKED_IN"
    NOT_SCHEDULED = "NOT_SCHEDULED"


class LeaveType(StrEnum):
    """Retired with the leave feature; kept only because the legacy leave_records table uses it."""

    ANNUAL = "ANNUAL"
    SICK = "SICK"
    CASUAL = "CASUAL"
    UNPAID = "UNPAID"
    OTHER = "OTHER"


class RequestStatus(StrEnum):
    PENDING = "PENDING"
    APPROVED = "APPROVED"
    REJECTED = "REJECTED"
    CANCELLED = "CANCELLED"


class CorrectionType(StrEnum):
    ADD_SESSION = "ADD_SESSION"        # attendance was not recorded at all
    MODIFY_SESSION = "MODIFY_SESSION"  # fix times/break of an existing session (incl. missing check-out)
