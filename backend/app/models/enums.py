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
    # Allowed by the database since migration 0002; automatic attendance now writes attendance_daily
    # instead of sessions, so nothing creates these.
    AUTO_WIFI = "AUTO_WIFI"
    AUTO_TEAMS = "AUTO_TEAMS"


class WifiEventType(StrEnum):
    CONNECT = "CONNECT"
    DISCONNECT = "DISCONNECT"
    SEEN = "SEEN"  # periodic "still connected" (e.g. RADIUS interim update, controller poll)


class DailyStatus(StrEnum):
    """Status of an automatic attendance day (thresholds come from settings)."""
    PRESENT = "PRESENT"      # office (or WFH) minutes >= full-day minutes
    HALF_DAY = "HALF_DAY"    # >= half-day minutes
    ABSENT = "ABSENT"        # scheduled working day, below half-day minutes
    WFH = "WFH"              # no office Wi-Fi; Teams presence counted as working from home
    HOLIDAY = "HOLIDAY"
    WEEKEND = "WEEKEND"
    NO_DATA = "NO_DATA"      # a collector was down; never shown as Absent


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
