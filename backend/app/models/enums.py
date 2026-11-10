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
    """How much of a day was worked (thresholds from settings). Where it was worked is ``WorkMode``."""
    PRESENT = "PRESENT"      # office + WFH minutes >= full-day minutes
    HALF_DAY = "HALF_DAY"    # >= half-day minutes
    ABSENT = "ABSENT"        # scheduled working day, below half-day minutes
    LEAVE = "LEAVE"          # only from an imported file or an Admin override (no leave requests)
    HOLIDAY = "HOLIDAY"
    WEEKEND = "WEEKEND"
    NO_DATA = "NO_DATA"      # a collector was down; never shown as Absent


class WorkMode(StrEnum):
    """Where a day was worked (WFO tracking)."""
    OFFICE = "OFFICE"        # WFO
    WFH = "WFH"
    LEAVE = "LEAVE"          # only from an imported file or an Admin override
    HOLIDAY = "HOLIDAY"
    WEEKOFF = "WEEKOFF"


class WfoSource(StrEnum):
    """Which signal decided a day's work mode (highest priority first)."""
    MANUAL_OVERRIDE = "MANUAL_OVERRIDE"  # an Admin set it; always wins
    UPLOAD_COLUMN = "UPLOAD_COLUMN"      # work mode / location column of an imported file
    BADGE = "BADGE"                      # badge / swipe / office-access rows of an imported file
    WIFI = "WIFI"                        # office Wi-Fi (automatic attendance)
    CHECK_IN = "CHECK_IN"                # Office / WFH chosen by the employee at check-in
    INFERRED = "INFERRED"                # worked hours with no office signal: WFH
    CALENDAR = "CALENDAR"                # holiday or weekly off from schedules and holidays


class WorkLocation(StrEnum):
    """Office or WFH, picked by the employee when checking in."""
    OFFICE = "OFFICE"
    WFH = "WFH"


class UploadStatus(StrEnum):
    PREVIEW = "PREVIEW"      # parsed and validated; nothing applied yet
    COMMITTED = "COMMITTED"
    DISCARDED = "DISCARDED"


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
