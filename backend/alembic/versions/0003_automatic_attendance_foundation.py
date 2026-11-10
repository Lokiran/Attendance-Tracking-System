"""automatic attendance foundation

Replaces the unused auto-attendance groundwork of 0002 with the tables of the automatic attendance
design (TODO.md, Phase 1):

* wifi_events / presence_events: one row per person per time bucket (unique), so collectors can
  re-run safely. Presence and duration only.
* attendance_daily: one computed row per person per work date (unique), written by an upsert.
* attendance_daily_corrections: requests to change a day, with the original values.
* attendance_policy gains the rule settings (gap, full / half day, poll intervals, no-data window,
  retention, WFH presence states), each with a database CHECK.

The empty 0002 tables signals, auto_attendance_days, worker_jobs and ingest_api_keys are dropped.
The upgrade refuses to run if any of them has rows; the downgrade refuses to run if any of the new
tables has rows. Neither direction ever discards data silently.

Revision ID: 0003
Revises: 0002
Create Date: 2026-11-10
"""
from collections.abc import Sequence

import sqlalchemy as sa
from alembic import context, op

revision: str = '0003'
down_revision: str | None = '0002'
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

LEGACY_TABLES = ("signals", "auto_attendance_days", "worker_jobs", "ingest_api_keys")
NEW_TABLES = ("wifi_events", "presence_events", "attendance_daily", "attendance_daily_corrections")
DAILY_STATUSES = ('PRESENT', 'HALF_DAY', 'ABSENT', 'WFH', 'HOLIDAY', 'WEEKEND', 'NO_DATA')
REQUEST_STATUSES = ('PENDING', 'APPROVED', 'REJECTED', 'CANCELLED')

# name, server default, CHECK (name, expression) added for the settings row
POLICY_COLUMNS = (
    ('gap_minutes', '30'), ('full_day_minutes', '480'), ('half_day_minutes', '240'), ('wifi_poll_minutes', '3'),
    ('presence_poll_minutes', '5'), ('no_data_alert_minutes', '15'), ('raw_retention_days', '90'),
)
POLICY_CHECKS = (
    ('ck_attendance_policy_gap_minutes_range', 'gap_minutes BETWEEN 1 AND 240'),
    ('ck_attendance_policy_full_day_minutes_range', 'full_day_minutes BETWEEN 1 AND 1440'),
    ('ck_attendance_policy_half_day_minutes_range', 'half_day_minutes >= 0 AND half_day_minutes <= full_day_minutes'),
    ('ck_attendance_policy_poll_minutes_range',
     'wifi_poll_minutes BETWEEN 1 AND 60 AND presence_poll_minutes BETWEEN 1 AND 60'),
    ('ck_attendance_policy_no_data_alert_minutes_range', 'no_data_alert_minutes BETWEEN 5 AND 1440'),
    ('ck_attendance_policy_raw_retention_days_range', 'raw_retention_days BETWEEN 7 AND 3650'),
)
WFH_STATES_DEFAULT = '\'["Available", "Busy", "InAMeeting"]\''


def _refuse_if_rows(tables: tuple[str, ...], what: str) -> None:
    if context.is_offline_mode():
        return  # rendering SQL without a database (alembic --sql): nothing to inspect
    bind = op.get_bind()
    present = set(sa.inspect(bind).get_table_names())
    counts = {t: bind.execute(sa.text(f"SELECT count(*) FROM {t}")).scalar() for t in tables if t in present}
    non_empty = {t: n for t, n in counts.items() if n}
    if non_empty:
        detail = ", ".join(f"{t}: {n} row(s)" for t, n in non_empty.items())
        raise RuntimeError(f"Cannot {what}: these tables are not empty ({detail}). Nothing was changed. "
                           "Export or remove that data explicitly first.")


def _ts(name: str, nullable: bool) -> sa.Column:
    return sa.Column(name, sa.DateTime(timezone=True), nullable=nullable)


def _big_id() -> sa.Column:
    return sa.Column('id', sa.BigInteger().with_variant(sa.Integer(), 'sqlite'), nullable=False)


def upgrade() -> None:
    _refuse_if_rows(LEGACY_TABLES, "replace the 0002 auto-attendance tables")

    op.drop_index('ix_signals_seen_at', table_name='signals')
    op.drop_index('ix_signals_employee_seen', table_name='signals')
    op.drop_table('signals')
    op.drop_index('ix_auto_attendance_days_review', table_name='auto_attendance_days')
    op.drop_table('auto_attendance_days')
    op.drop_table('worker_jobs')
    op.drop_table('ingest_api_keys')

    op.create_table('wifi_events',
        _big_id(),
        sa.Column('employee_id', sa.Integer(), nullable=False),
        _ts('ts_bucket', False), _ts('first_seen_at', False), _ts('last_seen_at', False),
        sa.Column('event_type', sa.Enum('CONNECT', 'DISCONNECT', 'SEEN', name='wifi_event_type', native_enum=False,
                                        create_constraint=True, length=16), nullable=False),
        sa.Column('source', sa.String(length=32), nullable=False),
        sa.Column('access_point', sa.String(length=64), nullable=True),
        _ts('created_at', False),
        sa.CheckConstraint('last_seen_at >= first_seen_at', name=op.f('ck_wifi_events_seen_order')),
        sa.ForeignKeyConstraint(['employee_id'], ['users.id'], name=op.f('fk_wifi_events_employee_id_users'),
                                ondelete='CASCADE'),
        sa.PrimaryKeyConstraint('id', name=op.f('pk_wifi_events')),
        sa.UniqueConstraint('employee_id', 'ts_bucket', name='uq_wifi_events_employee_bucket'),
    )
    op.create_index('ix_wifi_events_ts_bucket', 'wifi_events', ['ts_bucket'], unique=False)

    op.create_table('presence_events',
        _big_id(),
        sa.Column('employee_id', sa.Integer(), nullable=False),
        _ts('ts_bucket', False), _ts('polled_at', False),
        sa.Column('availability', sa.String(length=32), nullable=False),
        sa.Column('activity', sa.String(length=32), nullable=True),
        sa.Column('source', sa.String(length=32), nullable=False),
        _ts('created_at', False),
        sa.ForeignKeyConstraint(['employee_id'], ['users.id'], name=op.f('fk_presence_events_employee_id_users'),
                                ondelete='CASCADE'),
        sa.PrimaryKeyConstraint('id', name=op.f('pk_presence_events')),
        sa.UniqueConstraint('employee_id', 'ts_bucket', name='uq_presence_events_employee_bucket'),
    )
    op.create_index('ix_presence_events_ts_bucket', 'presence_events', ['ts_bucket'], unique=False)

    op.create_table('attendance_daily',
        sa.Column('id', sa.Integer(), nullable=False),
        sa.Column('employee_id', sa.Integer(), nullable=False),
        sa.Column('work_date', sa.Date(), nullable=False),
        _ts('first_in', True), _ts('last_out', True),
        sa.Column('office_minutes', sa.Integer(), nullable=False),
        sa.Column('wfh_minutes', sa.Integer(), nullable=False),
        sa.Column('status', sa.Enum(*DAILY_STATUSES, name='daily_status', native_enum=False, create_constraint=True,
                                    length=16), nullable=False),
        sa.Column('corrected', sa.Boolean(), nullable=False),
        _ts('computed_at', True), _ts('created_at', False), _ts('updated_at', False),
        sa.CheckConstraint('last_out IS NULL OR first_in IS NULL OR last_out >= first_in',
                           name=op.f('ck_attendance_daily_in_out_order')),
        sa.CheckConstraint('office_minutes >= 0 AND wfh_minutes >= 0',
                           name=op.f('ck_attendance_daily_minutes_non_negative')),
        sa.ForeignKeyConstraint(['employee_id'], ['users.id'], name=op.f('fk_attendance_daily_employee_id_users'),
                                ondelete='CASCADE'),
        sa.PrimaryKeyConstraint('id', name=op.f('pk_attendance_daily')),
        sa.UniqueConstraint('employee_id', 'work_date', name='uq_attendance_daily_employee_date'),
    )
    op.create_index('ix_attendance_daily_date_status', 'attendance_daily', ['work_date', 'status'], unique=False)

    op.create_table('attendance_daily_corrections',
        sa.Column('id', sa.Integer(), nullable=False),
        sa.Column('employee_id', sa.Integer(), nullable=False),
        sa.Column('work_date', sa.Date(), nullable=False),
        _ts('requested_first_in', True), _ts('requested_last_out', True),
        sa.Column('requested_status', sa.Enum(*DAILY_STATUSES, name='requested_daily_status', native_enum=False,
                                              create_constraint=True, length=16), nullable=True),
        sa.Column('reason', sa.String(length=1000), nullable=False),
        sa.Column('original_values', sa.JSON(), nullable=True),
        sa.Column('status', sa.Enum(*REQUEST_STATUSES, name='daily_correction_status', native_enum=False,
                                    create_constraint=True, length=16), nullable=False),
        sa.Column('reviewed_by_id', sa.Integer(), nullable=True),
        _ts('reviewed_at', True),
        sa.Column('reviewer_comment', sa.Text(), nullable=True),
        _ts('created_at', False), _ts('updated_at', False),
        sa.CheckConstraint('requested_last_out IS NULL OR requested_first_in IS NULL '
                           'OR requested_last_out > requested_first_in',
                           name=op.f('ck_attendance_daily_corrections_requested_order')),
        sa.ForeignKeyConstraint(['employee_id'], ['users.id'],
                                name=op.f('fk_attendance_daily_corrections_employee_id_users'), ondelete='CASCADE'),
        sa.ForeignKeyConstraint(['reviewed_by_id'], ['users.id'],
                                name=op.f('fk_attendance_daily_corrections_reviewed_by_id_users'), ondelete='SET NULL'),
        sa.PrimaryKeyConstraint('id', name=op.f('pk_attendance_daily_corrections')),
    )
    op.create_index('ix_daily_corrections_employee_date', 'attendance_daily_corrections', ['employee_id', 'work_date'],
                    unique=False)
    op.create_index('ix_daily_corrections_status', 'attendance_daily_corrections', ['status'], unique=False)

    # Settings: new rule columns (the existing row gets the defaults), then their CHECKs.
    with op.batch_alter_table('attendance_policy', schema=None) as batch_op:
        for name, default in POLICY_COLUMNS:
            batch_op.add_column(sa.Column(name, sa.Integer(), server_default=sa.text(default), nullable=False))
        batch_op.add_column(sa.Column('wfh_presence_states', sa.JSON(), server_default=sa.text(WFH_STATES_DEFAULT),
                                      nullable=False))
    with op.batch_alter_table('attendance_policy', schema=None) as batch_op:
        for name, expression in POLICY_CHECKS:
            batch_op.create_check_constraint(op.f(name), expression)  # op.f: name is already final


def downgrade() -> None:
    _refuse_if_rows(NEW_TABLES, "downgrade 0003")

    with op.batch_alter_table('attendance_policy', schema=None) as batch_op:
        for name, _expression in POLICY_CHECKS:
            batch_op.drop_constraint(op.f(name), type_="check")
    with op.batch_alter_table('attendance_policy', schema=None) as batch_op:
        batch_op.drop_column('wfh_presence_states')
        for name, _default in reversed(POLICY_COLUMNS):
            batch_op.drop_column(name)

    op.drop_index('ix_daily_corrections_status', table_name='attendance_daily_corrections')
    op.drop_index('ix_daily_corrections_employee_date', table_name='attendance_daily_corrections')
    op.drop_table('attendance_daily_corrections')
    op.drop_index('ix_attendance_daily_date_status', table_name='attendance_daily')
    op.drop_table('attendance_daily')
    op.drop_index('ix_presence_events_ts_bucket', table_name='presence_events')
    op.drop_table('presence_events')
    op.drop_index('ix_wifi_events_ts_bucket', table_name='wifi_events')
    op.drop_table('wifi_events')

    # Re-create the 0002 tables exactly as 0002 defined them (they were empty when dropped).
    op.create_table('signals',
        _big_id(),
        sa.Column('employee_id', sa.Integer(), nullable=False),
        sa.Column('source', sa.Enum('WIFI', 'TEAMS', name='signal_source', native_enum=False, create_constraint=True,
                                    length=8), nullable=False),
        sa.Column('event', sa.String(length=32), nullable=False),
        sa.Column('detail', sa.String(length=255), nullable=True),
        _ts('seen_at', False), _ts('received_at', False),
        sa.ForeignKeyConstraint(['employee_id'], ['users.id'], name=op.f('fk_signals_employee_id_users'),
                                ondelete='CASCADE'),
        sa.PrimaryKeyConstraint('id', name=op.f('pk_signals')),
        sa.UniqueConstraint('employee_id', 'source', 'event', 'seen_at', name='uq_signals_dedupe'),
    )
    op.create_index('ix_signals_employee_seen', 'signals', ['employee_id', 'seen_at'], unique=False)
    op.create_index('ix_signals_seen_at', 'signals', ['seen_at'], unique=False)

    op.create_table('auto_attendance_days',
        sa.Column('id', sa.Integer(), nullable=False),
        sa.Column('employee_id', sa.Integer(), nullable=False),
        sa.Column('work_date', sa.Date(), nullable=False),
        _ts('check_in', True), _ts('check_out', True),
        sa.Column('location', sa.Enum('OFFICE', 'WFH', name='work_location', native_enum=False,
                                      create_constraint=True, length=8), nullable=True),
        sa.Column('source', sa.Enum('AUTO_WIFI', 'AUTO_TEAMS', name='auto_source', native_enum=False,
                                    create_constraint=True, length=16), nullable=True),
        sa.Column('active_minutes', sa.Integer(), nullable=False),
        sa.Column('signal_count', sa.Integer(), nullable=False),
        sa.Column('needs_review', sa.Boolean(), nullable=False),
        sa.Column('review_reasons', sa.JSON(), nullable=False),
        sa.Column('is_shadow', sa.Boolean(), nullable=False),
        sa.Column('superseded', sa.Boolean(), nullable=False),
        _ts('superseded_at', True),
        sa.Column('session_id', sa.Integer(), nullable=True),
        sa.Column('review_status', sa.Enum(*REQUEST_STATUSES, name='review_status', native_enum=False,
                                           create_constraint=True, length=16), nullable=True),
        sa.Column('reviewed_by_id', sa.Integer(), nullable=True),
        _ts('reviewed_at', True),
        sa.Column('review_comment', sa.Text(), nullable=True),
        _ts('computed_at', True), _ts('created_at', False), _ts('updated_at', False),
        sa.ForeignKeyConstraint(['employee_id'], ['users.id'], name=op.f('fk_auto_attendance_days_employee_id_users'),
                                ondelete='CASCADE'),
        sa.ForeignKeyConstraint(['reviewed_by_id'], ['users.id'],
                                name=op.f('fk_auto_attendance_days_reviewed_by_id_users'), ondelete='SET NULL'),
        sa.ForeignKeyConstraint(['session_id'], ['attendance_sessions.id'],
                                name=op.f('fk_auto_attendance_days_session_id_attendance_sessions'), ondelete='SET NULL'),
        sa.PrimaryKeyConstraint('id', name=op.f('pk_auto_attendance_days')),
        sa.UniqueConstraint('employee_id', 'work_date', name='uq_auto_attendance_days_employee_day'),
    )
    op.create_index('ix_auto_attendance_days_review', 'auto_attendance_days', ['needs_review', 'work_date'],
                    unique=False)

    op.create_table('worker_jobs',
        sa.Column('name', sa.String(length=64), nullable=False),
        _ts('last_run_at', True), _ts('last_success_at', True),
        sa.Column('last_error', sa.Text(), nullable=True),
        sa.Column('last_duration_ms', sa.Integer(), nullable=True),
        sa.Column('consecutive_failures', sa.Integer(), nullable=False),
        sa.Column('run_count', sa.Integer(), nullable=False),
        sa.Column('details', sa.JSON(), nullable=True),
        _ts('updated_at', False),
        sa.PrimaryKeyConstraint('name', name=op.f('pk_worker_jobs')),
    )

    op.create_table('ingest_api_keys',
        sa.Column('id', sa.Integer(), nullable=False),
        sa.Column('name', sa.String(length=64), nullable=False),
        sa.Column('key_hash', sa.String(length=64), nullable=False),
        sa.Column('key_prefix', sa.String(length=12), nullable=False),
        _ts('created_at', False),
        sa.Column('created_by_id', sa.Integer(), nullable=True),
        _ts('last_used_at', True), _ts('revoked_at', True),
        sa.ForeignKeyConstraint(['created_by_id'], ['users.id'], name=op.f('fk_ingest_api_keys_created_by_id_users'),
                                ondelete='SET NULL'),
        sa.PrimaryKeyConstraint('id', name=op.f('pk_ingest_api_keys')),
        sa.UniqueConstraint('key_hash', name=op.f('uq_ingest_api_keys_key_hash')),
    )
