// Shapes returned by the FastAPI backend (see /api/docs).

export type Role = "EMPLOYEE" | "MANAGER" | "ADMIN";
export type DayStatus =
  | "PRESENT"
  | "ABSENT"
  | "HOLIDAY"
  | "WEEKEND"
  | "INCOMPLETE"
  | "NOT_YET_CHECKED_IN"
  | "NOT_SCHEDULED";
export type SessionStatus = "OPEN" | "COMPLETED" | "MISSING_CHECKOUT" | "VOIDED";
export type RequestStatus = "PENDING" | "APPROVED" | "REJECTED" | "CANCELLED";

export interface Page<T> {
  items: T[];
  total: number;
  page: number;
  page_size: number;
}

export interface Brief {
  id: number;
  full_name: string;
  employee_code: string;
}
export interface UserBrief extends Brief {
  role: Role;
  job_title: string | null;
}

export interface Profile extends Brief {
  email: string;
  role: Role;
  job_title: string | null;
  department: { id: number; name: string } | null;
  manager: Brief | null;
  date_joined: string;
  is_active: boolean;
  attendance_tracked: boolean;
}

/** Sign-in methods the backend has enabled (GET /auth/providers). */
export interface AuthProviders {
  password: boolean;
  entra: boolean;
  dev: boolean;
}

export interface MeResponse {
  user: Profile;
  csrf_token: string;
  office_timezone: string;
  has_reports: boolean;
}

export interface Session {
  id: number;
  employee_id: number;
  check_in_at: string;
  check_out_at: string | null;
  break_minutes: number;
  net_minutes: number | null;
  status: SessionStatus;
  source: string;
  notes: string | null;
  voided_at: string | null;
  void_reason: string | null;
  work_location?: WorkLocation | null;
}

export interface DayRecord {
  date: string;
  status: DayStatus;
  expected: boolean;
  scheduled_workday: boolean;
  required_minutes: number;
  net_minutes: number;
  session_count: number;
  completed_sessions: number;
  first_check_in: string | null;
  last_check_out: string | null;
  has_active_session: boolean;
  unresolved_sessions: number;
  holiday_name: string | null;
  exceptions: string[];
}

export interface Summary {
  start: string;
  end: string;
  expected_days: number;
  present_days: number;
  present_expected_days: number;
  extra_present_days: number;
  absent_days: number;
  holiday_days: number;
  weekend_days: number;
  not_scheduled_days: number;
  incomplete_days: number;
  not_yet_checked_in_days: number;
  net_minutes: number;
  required_minutes: number;
  variance_minutes: number;
  average_net_minutes: number | null;
  attendance_percentage: number | null;
  attended_days_for_average: number;
  unresolved_sessions: number;
  late_days: number;
}

export interface CurrentStatus {
  server_time: string;
  today: string;
  office_timezone: string;
  today_record: DayRecord | null;
  active_session: Session | null;
  unresolved_session: Session | null;
  schedule: {
    name: string;
    start_time: string;
    end_time: string;
    required_minutes: number;
    working_days: number[];
  } | null;
  max_session_minutes: number;
}

export interface DayDetail {
  record: DayRecord | null;
  sessions: Session[];
  voided_sessions: Session[];
}

export interface TrendBucket {
  start: string;
  end: string;
  label: string;
  expected: number;
  present_expected: number;
  present: number;
  absent: number;
  incomplete: number;
  net_minutes: number;
  required_minutes: number;
  attended: number;
  late: number;
  missing_checkout: number;
  short_hours: number;
  attendance_percentage: number | null;
  average_net_minutes: number | null;
}
export interface Trends {
  granularity: "day" | "week" | "month";
  headcount: number;
  items: TrendBucket[];
}

export interface Snapshot {
  present: number;
  absent: number;
  not_checked_in: number;
  incomplete: number;
  off: number;
  checked_in_now: number;
}
export interface PeriodStats {
  expected_days: number;
  present_expected_days: number;
  present_days: number;
  absent_days: number;
  incomplete_days: number;
  late_days: number;
  unresolved_sessions: number;
  net_minutes: number;
  required_minutes: number;
  variance_minutes: number;
  attended_days_for_average: number;
  average_net_minutes: number | null;
  attendance_percentage: number | null;
}
export interface Overview {
  generated_at: string;
  on_date: string;
  start: string;
  end: string;
  headcount: number;
  snapshot: Snapshot;
  period: PeriodStats;
  /** People per attendance band over the whole scope (never a sample). */
  attendance_bands: { label: string; count: number }[];
  pending_corrections: number;
}

export interface EmployeeRow {
  employee_id: number;
  employee_code: string;
  full_name: string;
  job_title: string | null;
  role: Role;
  is_active: boolean;
  department_id: number | null;
  department_name: string | null;
  manager_id: number | null;
  manager_name: string | null;
  day: {
    status: DayStatus | null;
    first_check_in: string | null;
    last_check_out: string | null;
    net_minutes: number;
    has_active_session: boolean;
    exceptions: string[];
  };
  period: PeriodStats;
  exception_count: number;
}

export interface ExceptionRow {
  employee_id: number;
  employee_code: string;
  full_name: string;
  department_name: string | null;
  date: string;
  status: DayStatus;
  exceptions: string[];
  first_check_in: string | null;
  last_check_out: string | null;
  net_minutes: number;
  required_minutes: number;
}
export interface ExceptionsResponse extends Page<ExceptionRow> {
  counts: Record<string, number>;
}

export interface DepartmentStats {
  department_id: number | null;
  department_name: string;
  head_name: string | null;
  headcount: number;
  snapshot: Snapshot;
  period: PeriodStats;
}
export interface ManagerStats {
  manager_id: number;
  manager_name: string;
  employee_code: string;
  department_name: string | null;
  team_size: number;
  direct_reports: number;
  snapshot: Snapshot;
  period: PeriodStats;
}

export interface HierarchyNode {
  id: number;
  employee_code: string;
  full_name: string;
  role: Role;
  job_title: string | null;
  is_active: boolean;
  direct_reports: number;
  children: HierarchyNode[];
}
export interface HierarchyDepartment {
  id: number | null;
  name: string;
  description: string | null;
  head: { id: number; full_name: string } | null;
  member_count: number;
  nodes: HierarchyNode[];
}

export interface Correction {
  id: number;
  employee: UserBrief;
  attendance_date: string;
  request_type: "ADD_SESSION" | "MODIFY_SESSION";
  original_session_id: number | null;
  original_values: Record<string, unknown> | null;
  requested_check_in_at: string;
  requested_check_out_at: string;
  requested_break_minutes: number;
  reason: string;
  status: RequestStatus;
  reviewed_by: UserBrief | null;
  reviewer_comment: string | null;
  reviewed_at: string | null;
  resulting_session_id: number | null;
  created_at: string;
}

export interface AdminUser extends Profile {
  created_at: string;
  updated_at: string;
}

export interface Department {
  id: number;
  name: string;
  description: string | null;
  department_head_id: number | null;
  head: Brief | null;
  member_count: number;
  created_at: string;
}

export interface Schedule {
  id: number;
  name: string;
  employee_id: number | null;
  department_id: number | null;
  start_time: string;
  end_time: string;
  required_minutes: number;
  working_days: number[];
  effective_from: string;
  effective_to: string | null;
  target_label: string;
}

export interface Holiday {
  id: number;
  name: string;
  holiday_date: string;
  department_id: number | null;
}

export interface Policy {
  min_present_minutes: number;
  count_incomplete_as_present: boolean;
  max_session_minutes: number;
  late_grace_minutes: number;
  correction_window_days: number;
  manager_can_approve: boolean;
  notes: string | null;
  updated_at: string;
}

export interface AuditEntry {
  id: number;
  actor_user_id: number | null;
  actor_name: string | null;
  action: string;
  entity_type: string;
  entity_id: string | null;
  before_values: Record<string, unknown> | null;
  after_values: Record<string, unknown> | null;
  request_id: string | null;
  ip_address: string | null;
  created_at: string;
}

export interface TeamMember extends UserBrief {
  department_name: string | null;
  manager_id: number | null;
  manager_name: string | null;
  is_active: boolean;
}

// ------------------------------------------------------------------ WFO (work from office)

export type WorkMode = "OFFICE" | "WFH" | "LEAVE" | "HOLIDAY" | "WEEKOFF";
export type WfoSource = "MANUAL_OVERRIDE" | "UPLOAD_COLUMN" | "BADGE" | "WIFI" | "CHECK_IN" | "INFERRED" | "CALENDAR";
export type Compliance = "COMPLIANT" | "NON_COMPLIANT";
export type WorkLocation = "OFFICE" | "WFH";

export interface WfoPolicy {
  min_days_per_week: number;
  min_days_per_month: number;
  min_office_minutes: number;
}

export interface WfoSummary {
  working_days: number;
  standard_days: number;
  wfo_days: number;
  wfh_days: number;
  office_days_below_minimum: number;
  leave_days: number;
  holiday_days: number;
  weekoff_days: number;
  required_days: number;
  shortfall_days: number;
  wfo_percentage: number | null;
  compliance: Compliance;
}

export interface WfoWeek {
  week_start: string;
  week_end: string;
  label: string;
  working_days: number;
  wfo_days: number;
  required: number;
  status: "MET" | "SHORT" | "IN_PROGRESS" | "UPCOMING";
  shortfall: number;
}

export interface WfoEmployee {
  id: number;
  employee_code: string;
  full_name: string;
  department_name: string | null;
  manager_id: number | null;
  manager_name: string | null;
}

export interface WfoDay {
  date: string;
  work_mode: WorkMode | null;
  wfo_source: WfoSource | null;
  is_wfo_day: boolean;
  office_minutes: number;
  wfh_minutes: number;
  conflicts: string[];
  scheduled: boolean;
  overridden: boolean;
  override_note: string | null;
  in_progress: boolean;
  future: boolean;
}

export interface EmployeeWfo {
  employee: WfoEmployee;
  month: string;
  start: string;
  end: string;
  today: string;
  policy: WfoPolicy;
  summary: WfoSummary;
  weekly_wfo_breakdown: WfoWeek[];
  days: WfoDay[];
}

export interface WfoRow extends WfoSummary {
  employee: WfoEmployee;
}

export interface WfoTotals {
  headcount: number;
  working_days: number;
  wfo_days: number;
  wfh_days: number;
  wfo_percentage: number | null;
  compliant: number;
  non_compliant: number;
}

export interface TeamWfo {
  month: string;
  start: string;
  end: string;
  policy: WfoPolicy;
  totals: WfoTotals;
  rows: WfoRow[];
}

export interface OrgWfo extends Omit<TeamWfo, "rows"> {
  groups: { manager_id: number | null; manager_name: string; department_name: string | null; totals: WfoTotals; rows: WfoRow[] }[];
}

export interface UploadConflict {
  employee_code: string;
  full_name: string;
  date: string;
  conflicts: string[];
  rows: number[];
}

export interface UploadSheet { name: string; usable: boolean; rows: number }
export interface UnknownEmployee {
  employee_ref: string;
  name: string | null;
  other_names: string[];
  rows: number;
  first_date: string | null;
  can_add: boolean;
  reason: string | null;
}
export interface NameMismatch { employee_ref: string; file_name: string; app_name: string }

export interface AttendanceUpload {
  id: number;
  filename: string;
  status: "PREVIEW" | "COMMITTED" | "DISCARDED";
  file_size: number;
  row_count: number;
  error_count: number;
  summary: {
    columns: Record<string, string>;
    rows: number;
    valid_rows: number;
    error_rows: number;
    badge_rows: number;
    duplicate_rows: number;
    employee_days: number;
    employees: number;
    new_days: number;
    by_work_mode: Record<string, number>;
    office: number;
    wfh: number;
    office_below_minimum: number;
    conflict_count: number;
    conflicts: UploadConflict[];
    protected_overrides: number;
    errors: { row: number; employee: string | null; error: string }[];
    date_from: string | null;
    date_to: string | null;
    // File details (uploads made before these existed do not have them)
    sheet?: string | null;
    sheets?: UploadSheet[];
    presence_list?: boolean;
    presence_mode?: "OFFICE" | "WFH" | null;
    unknown_employees?: UnknownEmployee[];
    name_mismatches?: NameMismatch[];
    date_notes?: string[];
    blank_rows?: number;
    employees_added?: number;
  } | null;
  commit_summary: { applied_days: number; created_days: number; skipped_overrides: number; overrides_overwritten: number; error_rows: number } | null;
  overwrite_overrides: boolean;
  uploaded_by_name: string | null;
  created_at: string;
  committed_at: string | null;
}
