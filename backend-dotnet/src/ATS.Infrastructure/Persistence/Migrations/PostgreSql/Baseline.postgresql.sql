-- Schema of the Python backend at Alembic head 0006, exactly as Alembic creates it on PostgreSQL
-- (pg_dump --schema-only --no-owner --no-privileges, psql-only lines removed). Regenerate only from a fresh `alembic upgrade head`, never by hand.
-- Enum CHECKs and the poll-minutes CHECK are written as SQLAlchemy emitted them (pg_dump's deparsed form would be
-- stored as a different expression). alembic_version is included so the Python backend also accepts a database created by .NET (until cutover).

CREATE EXTENSION IF NOT EXISTS btree_gist;

CREATE TABLE public.alembic_version (
    version_num character varying(32) NOT NULL
);

CREATE TABLE public.attendance_corrections (
    id integer NOT NULL,
    employee_id integer NOT NULL,
    attendance_date date NOT NULL,
    request_type character varying(20) NOT NULL,
    original_session_id integer,
    original_values json,
    requested_check_in_at timestamp with time zone NOT NULL,
    requested_check_out_at timestamp with time zone NOT NULL,
    requested_break_minutes integer NOT NULL,
    reason character varying(1000) NOT NULL,
    status character varying(16) NOT NULL,
    reviewed_by_id integer,
    reviewer_comment text,
    reviewed_at timestamp with time zone,
    resulting_session_id integer,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    CONSTRAINT ck_attendance_corrections_correction_type CHECK (request_type IN ('ADD_SESSION', 'MODIFY_SESSION')),
    CONSTRAINT ck_attendance_corrections_request_status CHECK (status IN ('PENDING', 'APPROVED', 'REJECTED', 'CANCELLED')),
    CONSTRAINT ck_attendance_corrections_requested_break_non_negative CHECK ((requested_break_minutes >= 0)),
    CONSTRAINT ck_attendance_corrections_requested_order CHECK ((requested_check_out_at > requested_check_in_at))
);

CREATE SEQUENCE public.attendance_corrections_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE public.attendance_corrections_id_seq OWNED BY public.attendance_corrections.id;

CREATE TABLE public.attendance_daily (
    id integer NOT NULL,
    employee_id integer NOT NULL,
    work_date date NOT NULL,
    first_in timestamp with time zone,
    last_out timestamp with time zone,
    office_minutes integer NOT NULL,
    wfh_minutes integer NOT NULL,
    status character varying(16) NOT NULL,
    corrected boolean NOT NULL,
    computed_at timestamp with time zone,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    work_mode character varying(16),
    wfo_source character varying(20),
    conflicts json DEFAULT '[]'::json NOT NULL,
    upload_work_mode character varying(16),
    upload_minutes integer,
    badge_minutes integer,
    wifi_minutes integer,
    upload_id integer,
    override_work_mode character varying(16),
    override_note character varying(500),
    overridden_by_id integer,
    overridden_at timestamp with time zone,
    CONSTRAINT ck_attendance_daily_daily_status CHECK (status IN ('PRESENT', 'HALF_DAY', 'ABSENT', 'LEAVE', 'HOLIDAY', 'WEEKEND', 'NO_DATA')),
    CONSTRAINT ck_attendance_daily_in_out_order CHECK (((last_out IS NULL) OR (first_in IS NULL) OR (last_out >= first_in))),
    CONSTRAINT ck_attendance_daily_minutes_non_negative CHECK (((office_minutes >= 0) AND (wfh_minutes >= 0))),
    CONSTRAINT ck_attendance_daily_override_work_mode CHECK (override_work_mode IN ('OFFICE', 'WFH', 'LEAVE', 'HOLIDAY', 'WEEKOFF')),
    CONSTRAINT ck_attendance_daily_upload_work_mode CHECK (upload_work_mode IN ('OFFICE', 'WFH', 'LEAVE', 'HOLIDAY', 'WEEKOFF')),
    CONSTRAINT ck_attendance_daily_wfo_source CHECK (wfo_source IN ('MANUAL_OVERRIDE', 'UPLOAD_COLUMN', 'BADGE', 'WIFI', 'CHECK_IN', 'INFERRED', 'CALENDAR')),
    CONSTRAINT ck_attendance_daily_work_mode CHECK (work_mode IN ('OFFICE', 'WFH', 'LEAVE', 'HOLIDAY', 'WEEKOFF'))
);

CREATE TABLE public.attendance_daily_corrections (
    id integer NOT NULL,
    employee_id integer NOT NULL,
    work_date date NOT NULL,
    requested_first_in timestamp with time zone,
    requested_last_out timestamp with time zone,
    requested_status character varying(16),
    reason character varying(1000) NOT NULL,
    original_values json,
    status character varying(16) NOT NULL,
    reviewed_by_id integer,
    reviewed_at timestamp with time zone,
    reviewer_comment text,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    CONSTRAINT ck_attendance_daily_corrections_daily_correction_status CHECK (status IN ('PENDING', 'APPROVED', 'REJECTED', 'CANCELLED')),
    CONSTRAINT ck_attendance_daily_corrections_requested_daily_status CHECK (requested_status IN ('PRESENT', 'HALF_DAY', 'ABSENT', 'LEAVE', 'HOLIDAY', 'WEEKEND', 'NO_DATA')),
    CONSTRAINT ck_attendance_daily_corrections_requested_order CHECK (((requested_last_out IS NULL) OR (requested_first_in IS NULL) OR (requested_last_out > requested_first_in)))
);

CREATE SEQUENCE public.attendance_daily_corrections_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE public.attendance_daily_corrections_id_seq OWNED BY public.attendance_daily_corrections.id;

CREATE SEQUENCE public.attendance_daily_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE public.attendance_daily_id_seq OWNED BY public.attendance_daily.id;

CREATE TABLE public.attendance_policy (
    id integer NOT NULL,
    min_present_minutes integer NOT NULL,
    count_incomplete_as_present boolean NOT NULL,
    max_session_minutes integer NOT NULL,
    late_grace_minutes integer NOT NULL,
    correction_window_days integer NOT NULL,
    manager_can_approve boolean NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    updated_by_id integer,
    notes text,
    auto_attendance_enabled boolean DEFAULT false NOT NULL,
    auto_shadow_mode boolean DEFAULT true NOT NULL,
    gap_minutes integer DEFAULT 30 NOT NULL,
    full_day_minutes integer DEFAULT 480 NOT NULL,
    half_day_minutes integer DEFAULT 240 NOT NULL,
    wifi_poll_minutes integer DEFAULT 3 NOT NULL,
    presence_poll_minutes integer DEFAULT 5 NOT NULL,
    no_data_alert_minutes integer DEFAULT 15 NOT NULL,
    raw_retention_days integer DEFAULT 90 NOT NULL,
    wfh_presence_states json DEFAULT '["Available", "Busy", "InAMeeting"]'::json NOT NULL,
    min_wfo_days_per_week integer DEFAULT 3 NOT NULL,
    min_wfo_days_per_month integer DEFAULT 12 NOT NULL,
    min_office_minutes_for_wfo_day integer DEFAULT 240 NOT NULL,
    CONSTRAINT ck_attendance_policy_full_day_minutes_range CHECK (((full_day_minutes >= 1) AND (full_day_minutes <= 1440))),
    CONSTRAINT ck_attendance_policy_gap_minutes_range CHECK (((gap_minutes >= 1) AND (gap_minutes <= 240))),
    CONSTRAINT ck_attendance_policy_half_day_minutes_range CHECK (((half_day_minutes >= 0) AND (half_day_minutes <= full_day_minutes))),
    CONSTRAINT ck_attendance_policy_max_session_range CHECK (((max_session_minutes >= 60) AND (max_session_minutes <= 1440))),
    CONSTRAINT ck_attendance_policy_min_office_minutes_range CHECK (((min_office_minutes_for_wfo_day >= 0) AND (min_office_minutes_for_wfo_day <= 1440))),
    CONSTRAINT ck_attendance_policy_min_present_non_negative CHECK ((min_present_minutes >= 0)),
    CONSTRAINT ck_attendance_policy_min_wfo_days_per_month_range CHECK (((min_wfo_days_per_month >= 0) AND (min_wfo_days_per_month <= 31))),
    CONSTRAINT ck_attendance_policy_min_wfo_days_per_week_range CHECK (((min_wfo_days_per_week >= 0) AND (min_wfo_days_per_week <= 7))),
    CONSTRAINT ck_attendance_policy_no_data_alert_minutes_range CHECK (((no_data_alert_minutes >= 5) AND (no_data_alert_minutes <= 1440))),
    CONSTRAINT ck_attendance_policy_poll_minutes_range CHECK (wifi_poll_minutes BETWEEN 1 AND 60 AND presence_poll_minutes BETWEEN 1 AND 60),
    CONSTRAINT ck_attendance_policy_raw_retention_days_range CHECK (((raw_retention_days >= 7) AND (raw_retention_days <= 3650))),
    CONSTRAINT ck_attendance_policy_singleton CHECK ((id = 1))
);

CREATE SEQUENCE public.attendance_policy_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE public.attendance_policy_id_seq OWNED BY public.attendance_policy.id;

CREATE TABLE public.attendance_sessions (
    id integer NOT NULL,
    employee_id integer NOT NULL,
    check_in_at timestamp with time zone NOT NULL,
    check_out_at timestamp with time zone,
    break_minutes integer NOT NULL,
    net_minutes integer,
    status character varying(20) NOT NULL,
    source character varying(16) NOT NULL,
    notes character varying(500),
    voided_at timestamp with time zone,
    void_reason text,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    work_location character varying(8),
    CONSTRAINT ck_attendance_sessions_break_non_negative CHECK ((break_minutes >= 0)),
    CONSTRAINT ck_attendance_sessions_checkout_after_checkin CHECK (((check_out_at IS NULL) OR (check_out_at > check_in_at))),
    CONSTRAINT ck_attendance_sessions_completed_has_totals CHECK ((((status)::text <> 'COMPLETED'::text) OR ((check_out_at IS NOT NULL) AND (net_minutes IS NOT NULL)))),
    CONSTRAINT ck_attendance_sessions_net_non_negative CHECK (((net_minutes IS NULL) OR (net_minutes >= 0))),
    CONSTRAINT ck_attendance_sessions_open_has_no_checkout CHECK ((((status)::text <> 'OPEN'::text) OR (check_out_at IS NULL))),
    CONSTRAINT ck_attendance_sessions_session_source CHECK (source IN ('WEB', 'CORRECTION', 'ADMIN', 'SEED', 'AUTO_WIFI', 'AUTO_TEAMS')),
    CONSTRAINT ck_attendance_sessions_session_status CHECK (status IN ('OPEN', 'COMPLETED', 'MISSING_CHECKOUT', 'VOIDED')),
    CONSTRAINT ck_attendance_sessions_work_location CHECK (work_location IN ('OFFICE', 'WFH'))
);

CREATE SEQUENCE public.attendance_sessions_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE public.attendance_sessions_id_seq OWNED BY public.attendance_sessions.id;

CREATE TABLE public.attendance_upload_rows (
    id bigint NOT NULL,
    upload_id integer NOT NULL,
    row_number integer NOT NULL,
    employee_ref character varying(255),
    employee_id integer,
    work_date date,
    raw_work_mode character varying(64),
    work_mode character varying(16),
    raw_source character varying(64),
    is_badge boolean NOT NULL,
    minutes integer,
    first_in timestamp with time zone,
    last_out timestamp with time zone,
    error character varying(255),
    CONSTRAINT ck_attendance_upload_rows_minutes_non_negative CHECK (((minutes IS NULL) OR (minutes >= 0))),
    CONSTRAINT ck_attendance_upload_rows_upload_row_work_mode CHECK (work_mode IN ('OFFICE', 'WFH', 'LEAVE', 'HOLIDAY', 'WEEKOFF'))
);

CREATE SEQUENCE public.attendance_upload_rows_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE public.attendance_upload_rows_id_seq OWNED BY public.attendance_upload_rows.id;

CREATE TABLE public.attendance_uploads (
    id integer NOT NULL,
    filename character varying(255) NOT NULL,
    file_sha256 character varying(64) NOT NULL,
    file_size integer NOT NULL,
    status character varying(16) NOT NULL,
    row_count integer NOT NULL,
    error_count integer NOT NULL,
    summary json,
    commit_summary json,
    overwrite_overrides boolean NOT NULL,
    uploaded_by_id integer,
    created_at timestamp with time zone NOT NULL,
    committed_by_id integer,
    committed_at timestamp with time zone,
    period_start date,
    period_end date,
    CONSTRAINT ck_attendance_uploads_upload_status CHECK (status IN ('PREVIEW', 'COMMITTED', 'DISCARDED'))
);

CREATE SEQUENCE public.attendance_uploads_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE public.attendance_uploads_id_seq OWNED BY public.attendance_uploads.id;

CREATE TABLE public.audit_logs (
    id bigint NOT NULL,
    actor_user_id integer,
    action character varying(64) NOT NULL,
    entity_type character varying(64) NOT NULL,
    entity_id character varying(64),
    before_values json,
    after_values json,
    request_id character varying(64),
    ip_address character varying(64),
    created_at timestamp with time zone NOT NULL
);

CREATE SEQUENCE public.audit_logs_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE public.audit_logs_id_seq OWNED BY public.audit_logs.id;

CREATE TABLE public.departments (
    id integer NOT NULL,
    name character varying(100) NOT NULL,
    description text,
    department_head_id integer,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL
);

CREATE SEQUENCE public.departments_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE public.departments_id_seq OWNED BY public.departments.id;

CREATE TABLE public.devices (
    id integer NOT NULL,
    serial_number character varying(64),
    asset_tag character varying(64),
    mac_address character varying(17),
    hostname character varying(64),
    model character varying(120),
    user_id integer,
    is_active boolean NOT NULL,
    source character varying(16) NOT NULL,
    notes text,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    CONSTRAINT ck_devices_has_identifier CHECK (((serial_number IS NOT NULL) OR (mac_address IS NOT NULL)))
);

CREATE SEQUENCE public.devices_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE public.devices_id_seq OWNED BY public.devices.id;

CREATE TABLE public.holidays (
    id integer NOT NULL,
    name character varying(120) NOT NULL,
    holiday_date date NOT NULL,
    department_id integer,
    created_at timestamp with time zone NOT NULL
);

CREATE SEQUENCE public.holidays_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE public.holidays_id_seq OWNED BY public.holidays.id;

CREATE TABLE public.leave_records (
    id integer NOT NULL,
    employee_id integer NOT NULL,
    start_date date NOT NULL,
    end_date date NOT NULL,
    leave_type character varying(16) NOT NULL,
    status character varying(16) NOT NULL,
    reason text,
    reviewed_by_id integer,
    reviewed_at timestamp with time zone,
    reviewer_comment text,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    CONSTRAINT ck_leave_records_date_order CHECK ((end_date >= start_date)),
    CONSTRAINT ck_leave_records_leave_type CHECK (leave_type IN ('ANNUAL', 'SICK', 'CASUAL', 'UNPAID', 'OTHER')),
    CONSTRAINT ck_leave_records_request_status CHECK (status IN ('PENDING', 'APPROVED', 'REJECTED', 'CANCELLED'))
);

CREATE SEQUENCE public.leave_records_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE public.leave_records_id_seq OWNED BY public.leave_records.id;

CREATE TABLE public.login_attempts (
    id bigint NOT NULL,
    email character varying(255) NOT NULL,
    ip_address character varying(64),
    succeeded boolean NOT NULL,
    created_at timestamp with time zone NOT NULL
);

CREATE SEQUENCE public.login_attempts_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE public.login_attempts_id_seq OWNED BY public.login_attempts.id;

CREATE TABLE public.office_sites (
    id integer NOT NULL,
    name character varying(100) NOT NULL,
    code character varying(16) NOT NULL,
    network_name character varying(64),
    address text,
    is_active boolean NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL
);

CREATE SEQUENCE public.office_sites_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE public.office_sites_id_seq OWNED BY public.office_sites.id;

CREATE TABLE public.presence_events (
    id bigint NOT NULL,
    employee_id integer NOT NULL,
    ts_bucket timestamp with time zone NOT NULL,
    polled_at timestamp with time zone NOT NULL,
    availability character varying(32) NOT NULL,
    activity character varying(32),
    source character varying(32) NOT NULL,
    created_at timestamp with time zone NOT NULL
);

CREATE SEQUENCE public.presence_events_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE public.presence_events_id_seq OWNED BY public.presence_events.id;

CREATE TABLE public.user_sessions (
    id bigint NOT NULL,
    user_id integer NOT NULL,
    token_hash character varying(64) NOT NULL,
    csrf_token character varying(64) NOT NULL,
    created_at timestamp with time zone NOT NULL,
    last_seen_at timestamp with time zone NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    revoked_at timestamp with time zone,
    ip_address character varying(64),
    user_agent character varying(255)
);

CREATE SEQUENCE public.user_sessions_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE public.user_sessions_id_seq OWNED BY public.user_sessions.id;

CREATE TABLE public.users (
    id integer NOT NULL,
    employee_code character varying(32) NOT NULL,
    full_name character varying(120) NOT NULL,
    email character varying(255) NOT NULL,
    password_hash character varying(255) NOT NULL,
    role character varying(16) NOT NULL,
    job_title character varying(120),
    department_id integer,
    manager_id integer,
    is_active boolean NOT NULL,
    attendance_tracked boolean NOT NULL,
    date_joined date NOT NULL,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    graph_user_id character varying(64),
    wifi_identity character varying(255),
    CONSTRAINT ck_users_not_own_manager CHECK (((manager_id IS NULL) OR (manager_id <> id))),
    CONSTRAINT ck_users_role CHECK (role IN ('EMPLOYEE', 'MANAGER', 'ADMIN'))
);

CREATE SEQUENCE public.users_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE public.users_id_seq OWNED BY public.users.id;

CREATE TABLE public.wifi_events (
    id bigint NOT NULL,
    employee_id integer NOT NULL,
    ts_bucket timestamp with time zone NOT NULL,
    first_seen_at timestamp with time zone NOT NULL,
    last_seen_at timestamp with time zone NOT NULL,
    event_type character varying(16) NOT NULL,
    source character varying(32) NOT NULL,
    access_point character varying(64),
    created_at timestamp with time zone NOT NULL,
    site_id integer,
    device_id integer,
    CONSTRAINT ck_wifi_events_seen_order CHECK ((last_seen_at >= first_seen_at)),
    CONSTRAINT ck_wifi_events_wifi_event_type CHECK (event_type IN ('CONNECT', 'DISCONNECT', 'SEEN'))
);

CREATE SEQUENCE public.wifi_events_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE public.wifi_events_id_seq OWNED BY public.wifi_events.id;

CREATE TABLE public.work_schedules (
    id integer NOT NULL,
    name character varying(100) NOT NULL,
    employee_id integer,
    department_id integer,
    start_time time without time zone NOT NULL,
    end_time time without time zone NOT NULL,
    required_minutes integer NOT NULL,
    working_days json NOT NULL,
    effective_from date NOT NULL,
    effective_to date,
    created_at timestamp with time zone NOT NULL,
    updated_at timestamp with time zone NOT NULL,
    CONSTRAINT ck_work_schedules_effective_range CHECK (((effective_to IS NULL) OR (effective_to >= effective_from))),
    CONSTRAINT ck_work_schedules_required_minutes_range CHECK (((required_minutes > 0) AND (required_minutes <= 1440))),
    CONSTRAINT ck_work_schedules_single_target CHECK ((NOT ((employee_id IS NOT NULL) AND (department_id IS NOT NULL))))
);

CREATE SEQUENCE public.work_schedules_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE public.work_schedules_id_seq OWNED BY public.work_schedules.id;

ALTER TABLE ONLY public.attendance_corrections ALTER COLUMN id SET DEFAULT nextval('public.attendance_corrections_id_seq'::regclass);

ALTER TABLE ONLY public.attendance_daily ALTER COLUMN id SET DEFAULT nextval('public.attendance_daily_id_seq'::regclass);

ALTER TABLE ONLY public.attendance_daily_corrections ALTER COLUMN id SET DEFAULT nextval('public.attendance_daily_corrections_id_seq'::regclass);

ALTER TABLE ONLY public.attendance_policy ALTER COLUMN id SET DEFAULT nextval('public.attendance_policy_id_seq'::regclass);

ALTER TABLE ONLY public.attendance_sessions ALTER COLUMN id SET DEFAULT nextval('public.attendance_sessions_id_seq'::regclass);

ALTER TABLE ONLY public.attendance_upload_rows ALTER COLUMN id SET DEFAULT nextval('public.attendance_upload_rows_id_seq'::regclass);

ALTER TABLE ONLY public.attendance_uploads ALTER COLUMN id SET DEFAULT nextval('public.attendance_uploads_id_seq'::regclass);

ALTER TABLE ONLY public.audit_logs ALTER COLUMN id SET DEFAULT nextval('public.audit_logs_id_seq'::regclass);

ALTER TABLE ONLY public.departments ALTER COLUMN id SET DEFAULT nextval('public.departments_id_seq'::regclass);

ALTER TABLE ONLY public.devices ALTER COLUMN id SET DEFAULT nextval('public.devices_id_seq'::regclass);

ALTER TABLE ONLY public.holidays ALTER COLUMN id SET DEFAULT nextval('public.holidays_id_seq'::regclass);

ALTER TABLE ONLY public.leave_records ALTER COLUMN id SET DEFAULT nextval('public.leave_records_id_seq'::regclass);

ALTER TABLE ONLY public.login_attempts ALTER COLUMN id SET DEFAULT nextval('public.login_attempts_id_seq'::regclass);

ALTER TABLE ONLY public.office_sites ALTER COLUMN id SET DEFAULT nextval('public.office_sites_id_seq'::regclass);

ALTER TABLE ONLY public.presence_events ALTER COLUMN id SET DEFAULT nextval('public.presence_events_id_seq'::regclass);

ALTER TABLE ONLY public.user_sessions ALTER COLUMN id SET DEFAULT nextval('public.user_sessions_id_seq'::regclass);

ALTER TABLE ONLY public.users ALTER COLUMN id SET DEFAULT nextval('public.users_id_seq'::regclass);

ALTER TABLE ONLY public.wifi_events ALTER COLUMN id SET DEFAULT nextval('public.wifi_events_id_seq'::regclass);

ALTER TABLE ONLY public.work_schedules ALTER COLUMN id SET DEFAULT nextval('public.work_schedules_id_seq'::regclass);

ALTER TABLE ONLY public.alembic_version
    ADD CONSTRAINT alembic_version_pkc PRIMARY KEY (version_num);

ALTER TABLE ONLY public.attendance_sessions
    ADD CONSTRAINT ex_attendance_no_overlap EXCLUDE USING gist (employee_id WITH =, tstzrange(check_in_at, COALESCE(check_out_at, check_in_at), '[)'::text) WITH &&) WHERE (((status)::text <> 'VOIDED'::text));

ALTER TABLE ONLY public.attendance_corrections
    ADD CONSTRAINT pk_attendance_corrections PRIMARY KEY (id);

ALTER TABLE ONLY public.attendance_daily
    ADD CONSTRAINT pk_attendance_daily PRIMARY KEY (id);

ALTER TABLE ONLY public.attendance_daily_corrections
    ADD CONSTRAINT pk_attendance_daily_corrections PRIMARY KEY (id);

ALTER TABLE ONLY public.attendance_policy
    ADD CONSTRAINT pk_attendance_policy PRIMARY KEY (id);

ALTER TABLE ONLY public.attendance_sessions
    ADD CONSTRAINT pk_attendance_sessions PRIMARY KEY (id);

ALTER TABLE ONLY public.attendance_upload_rows
    ADD CONSTRAINT pk_attendance_upload_rows PRIMARY KEY (id);

ALTER TABLE ONLY public.attendance_uploads
    ADD CONSTRAINT pk_attendance_uploads PRIMARY KEY (id);

ALTER TABLE ONLY public.audit_logs
    ADD CONSTRAINT pk_audit_logs PRIMARY KEY (id);

ALTER TABLE ONLY public.departments
    ADD CONSTRAINT pk_departments PRIMARY KEY (id);

ALTER TABLE ONLY public.devices
    ADD CONSTRAINT pk_devices PRIMARY KEY (id);

ALTER TABLE ONLY public.holidays
    ADD CONSTRAINT pk_holidays PRIMARY KEY (id);

ALTER TABLE ONLY public.leave_records
    ADD CONSTRAINT pk_leave_records PRIMARY KEY (id);

ALTER TABLE ONLY public.login_attempts
    ADD CONSTRAINT pk_login_attempts PRIMARY KEY (id);

ALTER TABLE ONLY public.office_sites
    ADD CONSTRAINT pk_office_sites PRIMARY KEY (id);

ALTER TABLE ONLY public.presence_events
    ADD CONSTRAINT pk_presence_events PRIMARY KEY (id);

ALTER TABLE ONLY public.user_sessions
    ADD CONSTRAINT pk_user_sessions PRIMARY KEY (id);

ALTER TABLE ONLY public.users
    ADD CONSTRAINT pk_users PRIMARY KEY (id);

ALTER TABLE ONLY public.wifi_events
    ADD CONSTRAINT pk_wifi_events PRIMARY KEY (id);

ALTER TABLE ONLY public.work_schedules
    ADD CONSTRAINT pk_work_schedules PRIMARY KEY (id);

ALTER TABLE ONLY public.attendance_daily
    ADD CONSTRAINT uq_attendance_daily_employee_date UNIQUE (employee_id, work_date);

ALTER TABLE ONLY public.presence_events
    ADD CONSTRAINT uq_presence_events_employee_bucket UNIQUE (employee_id, ts_bucket);

ALTER TABLE ONLY public.user_sessions
    ADD CONSTRAINT uq_user_sessions_token_hash UNIQUE (token_hash);

ALTER TABLE ONLY public.users
    ADD CONSTRAINT uq_users_email UNIQUE (email);

ALTER TABLE ONLY public.users
    ADD CONSTRAINT uq_users_employee_code UNIQUE (employee_code);

ALTER TABLE ONLY public.wifi_events
    ADD CONSTRAINT uq_wifi_events_employee_bucket UNIQUE (employee_id, ts_bucket);

CREATE INDEX ix_attendance_checkin ON public.attendance_sessions USING btree (check_in_at);

CREATE INDEX ix_attendance_daily_date_status ON public.attendance_daily USING btree (work_date, status);

CREATE INDEX ix_attendance_employee_checkin ON public.attendance_sessions USING btree (employee_id, check_in_at);

CREATE INDEX ix_attendance_upload_rows_employee ON public.attendance_upload_rows USING btree (employee_id, upload_id);

CREATE INDEX ix_attendance_upload_rows_upload ON public.attendance_upload_rows USING btree (upload_id, row_number);

CREATE INDEX ix_attendance_uploads_created ON public.attendance_uploads USING btree (created_at);

CREATE INDEX ix_audit_actor ON public.audit_logs USING btree (actor_user_id);

CREATE INDEX ix_audit_created_at ON public.audit_logs USING btree (created_at);

CREATE INDEX ix_audit_entity ON public.audit_logs USING btree (entity_type, entity_id);

CREATE INDEX ix_corrections_employee_date ON public.attendance_corrections USING btree (employee_id, attendance_date);

CREATE INDEX ix_corrections_status ON public.attendance_corrections USING btree (status);

CREATE INDEX ix_daily_corrections_employee_date ON public.attendance_daily_corrections USING btree (employee_id, work_date);

CREATE INDEX ix_daily_corrections_status ON public.attendance_daily_corrections USING btree (status);

CREATE INDEX ix_devices_hostname_lower ON public.devices USING btree (lower((hostname)::text));

CREATE INDEX ix_devices_user_id ON public.devices USING btree (user_id);

CREATE INDEX ix_holidays_holiday_date ON public.holidays USING btree (holiday_date);

CREATE INDEX ix_leave_employee_dates ON public.leave_records USING btree (employee_id, start_date, end_date);

CREATE INDEX ix_login_attempts_email_time ON public.login_attempts USING btree (email, created_at);

CREATE INDEX ix_login_attempts_ip_time ON public.login_attempts USING btree (ip_address, created_at);

CREATE INDEX ix_presence_events_ts_bucket ON public.presence_events USING btree (ts_bucket);

CREATE INDEX ix_user_sessions_user ON public.user_sessions USING btree (user_id);

CREATE INDEX ix_users_department_id ON public.users USING btree (department_id);

CREATE INDEX ix_users_manager_id ON public.users USING btree (manager_id);

CREATE INDEX ix_wifi_events_ts_bucket ON public.wifi_events USING btree (ts_bucket);

CREATE INDEX ix_work_schedules_department ON public.work_schedules USING btree (department_id);

CREATE INDEX ix_work_schedules_employee ON public.work_schedules USING btree (employee_id);

CREATE UNIQUE INDEX uq_attendance_one_open_session ON public.attendance_sessions USING btree (employee_id) WHERE ((status)::text = 'OPEN'::text);

CREATE UNIQUE INDEX uq_departments_name_lower ON public.departments USING btree (lower((name)::text));

CREATE UNIQUE INDEX uq_devices_mac_address ON public.devices USING btree (mac_address);

CREATE UNIQUE INDEX uq_devices_serial_number ON public.devices USING btree (serial_number);

CREATE UNIQUE INDEX uq_holidays_date_department ON public.holidays USING btree (holiday_date, COALESCE(department_id, 0));

CREATE UNIQUE INDEX uq_office_sites_code_lower ON public.office_sites USING btree (lower((code)::text));

CREATE UNIQUE INDEX uq_office_sites_name_lower ON public.office_sites USING btree (lower((name)::text));

CREATE UNIQUE INDEX uq_users_graph_user_id ON public.users USING btree (graph_user_id);

CREATE UNIQUE INDEX uq_users_wifi_identity ON public.users USING btree (wifi_identity);

ALTER TABLE ONLY public.attendance_corrections
    ADD CONSTRAINT fk_attendance_corrections_employee_id_users FOREIGN KEY (employee_id) REFERENCES public.users(id) ON DELETE CASCADE;

ALTER TABLE ONLY public.attendance_corrections
    ADD CONSTRAINT fk_attendance_corrections_original_session_id_attendanc_e605 FOREIGN KEY (original_session_id) REFERENCES public.attendance_sessions(id) ON DELETE SET NULL;

ALTER TABLE ONLY public.attendance_corrections
    ADD CONSTRAINT fk_attendance_corrections_resulting_session_id_attendan_ca71 FOREIGN KEY (resulting_session_id) REFERENCES public.attendance_sessions(id) ON DELETE SET NULL;

ALTER TABLE ONLY public.attendance_corrections
    ADD CONSTRAINT fk_attendance_corrections_reviewed_by_id_users FOREIGN KEY (reviewed_by_id) REFERENCES public.users(id) ON DELETE SET NULL;

ALTER TABLE ONLY public.attendance_daily_corrections
    ADD CONSTRAINT fk_attendance_daily_corrections_employee_id_users FOREIGN KEY (employee_id) REFERENCES public.users(id) ON DELETE CASCADE;

ALTER TABLE ONLY public.attendance_daily_corrections
    ADD CONSTRAINT fk_attendance_daily_corrections_reviewed_by_id_users FOREIGN KEY (reviewed_by_id) REFERENCES public.users(id) ON DELETE SET NULL;

ALTER TABLE ONLY public.attendance_daily
    ADD CONSTRAINT fk_attendance_daily_employee_id_users FOREIGN KEY (employee_id) REFERENCES public.users(id) ON DELETE CASCADE;

ALTER TABLE ONLY public.attendance_daily
    ADD CONSTRAINT fk_attendance_daily_overridden_by_id_users FOREIGN KEY (overridden_by_id) REFERENCES public.users(id) ON DELETE SET NULL;

ALTER TABLE ONLY public.attendance_daily
    ADD CONSTRAINT fk_attendance_daily_upload_id_attendance_uploads FOREIGN KEY (upload_id) REFERENCES public.attendance_uploads(id) ON DELETE SET NULL;

ALTER TABLE ONLY public.attendance_policy
    ADD CONSTRAINT fk_attendance_policy_updated_by_id_users FOREIGN KEY (updated_by_id) REFERENCES public.users(id) ON DELETE SET NULL;

ALTER TABLE ONLY public.attendance_sessions
    ADD CONSTRAINT fk_attendance_sessions_employee_id_users FOREIGN KEY (employee_id) REFERENCES public.users(id) ON DELETE CASCADE;

ALTER TABLE ONLY public.attendance_upload_rows
    ADD CONSTRAINT fk_attendance_upload_rows_employee_id_users FOREIGN KEY (employee_id) REFERENCES public.users(id) ON DELETE CASCADE;

ALTER TABLE ONLY public.attendance_upload_rows
    ADD CONSTRAINT fk_attendance_upload_rows_upload_id_attendance_uploads FOREIGN KEY (upload_id) REFERENCES public.attendance_uploads(id) ON DELETE CASCADE;

ALTER TABLE ONLY public.attendance_uploads
    ADD CONSTRAINT fk_attendance_uploads_committed_by_id_users FOREIGN KEY (committed_by_id) REFERENCES public.users(id) ON DELETE SET NULL;

ALTER TABLE ONLY public.attendance_uploads
    ADD CONSTRAINT fk_attendance_uploads_uploaded_by_id_users FOREIGN KEY (uploaded_by_id) REFERENCES public.users(id) ON DELETE SET NULL;

ALTER TABLE ONLY public.audit_logs
    ADD CONSTRAINT fk_audit_logs_actor_user_id_users FOREIGN KEY (actor_user_id) REFERENCES public.users(id) ON DELETE SET NULL;

ALTER TABLE ONLY public.departments
    ADD CONSTRAINT fk_departments_head_users FOREIGN KEY (department_head_id) REFERENCES public.users(id) ON DELETE SET NULL;

ALTER TABLE ONLY public.devices
    ADD CONSTRAINT fk_devices_user_id_users FOREIGN KEY (user_id) REFERENCES public.users(id) ON DELETE SET NULL;

ALTER TABLE ONLY public.holidays
    ADD CONSTRAINT fk_holidays_department_id_departments FOREIGN KEY (department_id) REFERENCES public.departments(id) ON DELETE CASCADE;

ALTER TABLE ONLY public.leave_records
    ADD CONSTRAINT fk_leave_records_employee_id_users FOREIGN KEY (employee_id) REFERENCES public.users(id) ON DELETE CASCADE;

ALTER TABLE ONLY public.leave_records
    ADD CONSTRAINT fk_leave_records_reviewed_by_id_users FOREIGN KEY (reviewed_by_id) REFERENCES public.users(id) ON DELETE SET NULL;

ALTER TABLE ONLY public.presence_events
    ADD CONSTRAINT fk_presence_events_employee_id_users FOREIGN KEY (employee_id) REFERENCES public.users(id) ON DELETE CASCADE;

ALTER TABLE ONLY public.user_sessions
    ADD CONSTRAINT fk_user_sessions_user_id_users FOREIGN KEY (user_id) REFERENCES public.users(id) ON DELETE CASCADE;

ALTER TABLE ONLY public.users
    ADD CONSTRAINT fk_users_department_id_departments FOREIGN KEY (department_id) REFERENCES public.departments(id) ON DELETE SET NULL;

ALTER TABLE ONLY public.users
    ADD CONSTRAINT fk_users_manager_id_users FOREIGN KEY (manager_id) REFERENCES public.users(id) ON DELETE SET NULL;

ALTER TABLE ONLY public.wifi_events
    ADD CONSTRAINT fk_wifi_events_device_id_devices FOREIGN KEY (device_id) REFERENCES public.devices(id) ON DELETE SET NULL;

ALTER TABLE ONLY public.wifi_events
    ADD CONSTRAINT fk_wifi_events_employee_id_users FOREIGN KEY (employee_id) REFERENCES public.users(id) ON DELETE CASCADE;

ALTER TABLE ONLY public.wifi_events
    ADD CONSTRAINT fk_wifi_events_site_id_office_sites FOREIGN KEY (site_id) REFERENCES public.office_sites(id) ON DELETE SET NULL;

ALTER TABLE ONLY public.work_schedules
    ADD CONSTRAINT fk_work_schedules_department_id_departments FOREIGN KEY (department_id) REFERENCES public.departments(id) ON DELETE CASCADE;

ALTER TABLE ONLY public.work_schedules
    ADD CONSTRAINT fk_work_schedules_employee_id_users FOREIGN KEY (employee_id) REFERENCES public.users(id) ON DELETE CASCADE;

INSERT INTO public.alembic_version (version_num) VALUES ('0006');
