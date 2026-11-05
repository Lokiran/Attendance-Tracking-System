"""initial schema

Revision ID: 0001
Revises:
Create Date: 2026-10-27 10:52:44.014549
"""
from collections.abc import Sequence

import sqlalchemy as sa
from alembic import context, op

revision: str = '0001'
down_revision: str | None = None
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

NO_OVERLAP_SQL = (
    "ALTER TABLE attendance_sessions ADD CONSTRAINT ex_attendance_no_overlap "
    "EXCLUDE USING gist (employee_id WITH =, "
    "tstzrange(check_in_at, COALESCE(check_out_at, check_in_at), '[)') WITH &&) "
    "WHERE (status <> 'VOIDED')"
)


def ensure_btree_gist() -> None:
    """btree_gist lets the exclusion constraint combine `employee_id WITH =` and a range `&&`."""
    if context.is_offline_mode():
        op.execute("CREATE EXTENSION IF NOT EXISTS btree_gist")
        return
    bind = op.get_bind()
    if bind.execute(sa.text("SELECT 1 FROM pg_extension WHERE extname = 'btree_gist'")).scalar():
        return
    if not bind.execute(sa.text("SELECT 1 FROM pg_available_extensions WHERE name = 'btree_gist'")).scalar():
        raise RuntimeError(
            "PostgreSQL extension 'btree_gist' is not available on this server. Install the PostgreSQL "
            "contrib modules (bundled with the official Windows installer and Docker images)."
        )
    try:
        op.execute("CREATE EXTENSION IF NOT EXISTS btree_gist")
    except sa.exc.DBAPIError as exc:
        raise RuntimeError(
            "Could not create extension 'btree_gist'. It is a trusted extension on PostgreSQL 13+, so the "
            "database owner can create it; otherwise run once as a superuser in this database: "
            "CREATE EXTENSION btree_gist;"
        ) from exc


def upgrade() -> None:
    is_pg = op.get_bind().dialect.name == "postgresql"
    if is_pg:
        ensure_btree_gist()

    # departments.department_head_id -> users.id is added after users exists (circular reference).
    op.create_table('departments',
    sa.Column('id', sa.Integer(), nullable=False),
    sa.Column('name', sa.String(length=100), nullable=False),
    sa.Column('description', sa.Text(), nullable=True),
    sa.Column('department_head_id', sa.Integer(), nullable=True),
    sa.Column('created_at', sa.DateTime(timezone=True), nullable=False),
    sa.Column('updated_at', sa.DateTime(timezone=True), nullable=False),
    sa.PrimaryKeyConstraint('id', name=op.f('pk_departments'))
    )
    op.create_table('login_attempts',
    sa.Column('id', sa.BigInteger().with_variant(sa.Integer(), 'sqlite'), nullable=False),
    sa.Column('email', sa.String(length=255), nullable=False),
    sa.Column('ip_address', sa.String(length=64), nullable=True),
    sa.Column('succeeded', sa.Boolean(), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), nullable=False),
    sa.PrimaryKeyConstraint('id', name=op.f('pk_login_attempts'))
    )
    with op.batch_alter_table('login_attempts', schema=None) as batch_op:
        batch_op.create_index('ix_login_attempts_email_time', ['email', 'created_at'], unique=False)
        batch_op.create_index('ix_login_attempts_ip_time', ['ip_address', 'created_at'], unique=False)

    op.create_table('holidays',
    sa.Column('id', sa.Integer(), nullable=False),
    sa.Column('name', sa.String(length=120), nullable=False),
    sa.Column('holiday_date', sa.Date(), nullable=False),
    sa.Column('department_id', sa.Integer(), nullable=True),
    sa.Column('created_at', sa.DateTime(timezone=True), nullable=False),
    sa.ForeignKeyConstraint(['department_id'], ['departments.id'], name=op.f('fk_holidays_department_id_departments'), ondelete='CASCADE'),
    sa.PrimaryKeyConstraint('id', name=op.f('pk_holidays'))
    )
    with op.batch_alter_table('holidays', schema=None) as batch_op:
        batch_op.create_index(batch_op.f('ix_holidays_holiday_date'), ['holiday_date'], unique=False)
    op.create_index('uq_holidays_date_department', 'holidays',
                    ['holiday_date', sa.text('COALESCE(department_id, 0)')], unique=True)

    op.create_table('users',
    sa.Column('id', sa.Integer(), nullable=False),
    sa.Column('employee_code', sa.String(length=32), nullable=False),
    sa.Column('full_name', sa.String(length=120), nullable=False),
    sa.Column('email', sa.String(length=255), nullable=False),
    sa.Column('password_hash', sa.String(length=255), nullable=False),
    sa.Column('role', sa.Enum('EMPLOYEE', 'MANAGER', 'ADMIN', name='role', native_enum=False, create_constraint=True, length=16), nullable=False),
    sa.Column('job_title', sa.String(length=120), nullable=True),
    sa.Column('department_id', sa.Integer(), nullable=True),
    sa.Column('manager_id', sa.Integer(), nullable=True),
    sa.Column('is_active', sa.Boolean(), nullable=False),
    sa.Column('attendance_tracked', sa.Boolean(), nullable=False),
    sa.Column('date_joined', sa.Date(), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), nullable=False),
    sa.Column('updated_at', sa.DateTime(timezone=True), nullable=False),
    sa.CheckConstraint('manager_id IS NULL OR manager_id <> id', name=op.f('ck_users_not_own_manager')),
    sa.ForeignKeyConstraint(['department_id'], ['departments.id'], name=op.f('fk_users_department_id_departments'), ondelete='SET NULL'),
    sa.ForeignKeyConstraint(['manager_id'], ['users.id'], name=op.f('fk_users_manager_id_users'), ondelete='SET NULL'),
    sa.PrimaryKeyConstraint('id', name=op.f('pk_users')),
    sa.UniqueConstraint('email', name=op.f('uq_users_email')),
    sa.UniqueConstraint('employee_code', name=op.f('uq_users_employee_code'))
    )
    with op.batch_alter_table('users', schema=None) as batch_op:
        batch_op.create_index('ix_users_department_id', ['department_id'], unique=False)
        batch_op.create_index('ix_users_manager_id', ['manager_id'], unique=False)
    with op.batch_alter_table('departments', schema=None) as batch_op:
        batch_op.create_foreign_key('fk_departments_head_users', 'users', ['department_head_id'], ['id'],
                                    ondelete='SET NULL')
    # Created after the batch step above, which rebuilds the table on SQLite.
    op.create_index('uq_departments_name_lower', 'departments', [sa.text('lower(name)')], unique=True)

    op.create_table('attendance_policy',
    sa.Column('id', sa.Integer(), nullable=False),
    sa.Column('min_present_minutes', sa.Integer(), nullable=False),
    sa.Column('count_incomplete_as_present', sa.Boolean(), nullable=False),
    sa.Column('max_session_minutes', sa.Integer(), nullable=False),
    sa.Column('late_grace_minutes', sa.Integer(), nullable=False),
    sa.Column('correction_window_days', sa.Integer(), nullable=False),
    sa.Column('manager_can_approve', sa.Boolean(), nullable=False),
    sa.Column('updated_at', sa.DateTime(timezone=True), nullable=False),
    sa.Column('updated_by_id', sa.Integer(), nullable=True),
    sa.Column('notes', sa.Text(), nullable=True),
    sa.CheckConstraint('id = 1', name=op.f('ck_attendance_policy_singleton')),
    sa.CheckConstraint('max_session_minutes BETWEEN 60 AND 1440', name=op.f('ck_attendance_policy_max_session_range')),
    sa.CheckConstraint('min_present_minutes >= 0', name=op.f('ck_attendance_policy_min_present_non_negative')),
    sa.ForeignKeyConstraint(['updated_by_id'], ['users.id'], name=op.f('fk_attendance_policy_updated_by_id_users'), ondelete='SET NULL'),
    sa.PrimaryKeyConstraint('id', name=op.f('pk_attendance_policy'))
    )
    op.create_table('attendance_sessions',
    sa.Column('id', sa.Integer(), nullable=False),
    sa.Column('employee_id', sa.Integer(), nullable=False),
    sa.Column('check_in_at', sa.DateTime(timezone=True), nullable=False),
    sa.Column('check_out_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('break_minutes', sa.Integer(), nullable=False),
    sa.Column('net_minutes', sa.Integer(), nullable=True),
    sa.Column('status', sa.Enum('OPEN', 'COMPLETED', 'MISSING_CHECKOUT', 'VOIDED', name='session_status', native_enum=False, create_constraint=True, length=20), nullable=False),
    sa.Column('source', sa.Enum('WEB', 'CORRECTION', 'ADMIN', 'SEED', name='session_source', native_enum=False, create_constraint=True, length=16), nullable=False),
    sa.Column('notes', sa.String(length=500), nullable=True),
    sa.Column('voided_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('void_reason', sa.Text(), nullable=True),
    sa.Column('created_at', sa.DateTime(timezone=True), nullable=False),
    sa.Column('updated_at', sa.DateTime(timezone=True), nullable=False),
    sa.CheckConstraint("status <> 'COMPLETED' OR (check_out_at IS NOT NULL AND net_minutes IS NOT NULL)", name=op.f('ck_attendance_sessions_completed_has_totals')),
    sa.CheckConstraint("status <> 'OPEN' OR check_out_at IS NULL", name=op.f('ck_attendance_sessions_open_has_no_checkout')),
    sa.CheckConstraint('break_minutes >= 0', name=op.f('ck_attendance_sessions_break_non_negative')),
    sa.CheckConstraint('check_out_at IS NULL OR check_out_at > check_in_at', name=op.f('ck_attendance_sessions_checkout_after_checkin')),
    sa.CheckConstraint('net_minutes IS NULL OR net_minutes >= 0', name=op.f('ck_attendance_sessions_net_non_negative')),
    sa.ForeignKeyConstraint(['employee_id'], ['users.id'], name=op.f('fk_attendance_sessions_employee_id_users'), ondelete='CASCADE'),
    sa.PrimaryKeyConstraint('id', name=op.f('pk_attendance_sessions'))
    )
    with op.batch_alter_table('attendance_sessions', schema=None) as batch_op:
        batch_op.create_index('ix_attendance_checkin', ['check_in_at'], unique=False)
        batch_op.create_index('ix_attendance_employee_checkin', ['employee_id', 'check_in_at'], unique=False)
        batch_op.create_index('uq_attendance_one_open_session', ['employee_id'], unique=True, postgresql_where=sa.text("status = 'OPEN'"), sqlite_where=sa.text("status = 'OPEN'"))
    if is_pg:
        op.execute(NO_OVERLAP_SQL)

    op.create_table('audit_logs',
    sa.Column('id', sa.BigInteger().with_variant(sa.Integer(), 'sqlite'), nullable=False),
    sa.Column('actor_user_id', sa.Integer(), nullable=True),
    sa.Column('action', sa.String(length=64), nullable=False),
    sa.Column('entity_type', sa.String(length=64), nullable=False),
    sa.Column('entity_id', sa.String(length=64), nullable=True),
    sa.Column('before_values', sa.JSON(), nullable=True),
    sa.Column('after_values', sa.JSON(), nullable=True),
    sa.Column('request_id', sa.String(length=64), nullable=True),
    sa.Column('ip_address', sa.String(length=64), nullable=True),
    sa.Column('created_at', sa.DateTime(timezone=True), nullable=False),
    sa.ForeignKeyConstraint(['actor_user_id'], ['users.id'], name=op.f('fk_audit_logs_actor_user_id_users'), ondelete='SET NULL'),
    sa.PrimaryKeyConstraint('id', name=op.f('pk_audit_logs'))
    )
    with op.batch_alter_table('audit_logs', schema=None) as batch_op:
        batch_op.create_index('ix_audit_actor', ['actor_user_id'], unique=False)
        batch_op.create_index('ix_audit_created_at', ['created_at'], unique=False)
        batch_op.create_index('ix_audit_entity', ['entity_type', 'entity_id'], unique=False)

    op.create_table('leave_records',
    sa.Column('id', sa.Integer(), nullable=False),
    sa.Column('employee_id', sa.Integer(), nullable=False),
    sa.Column('start_date', sa.Date(), nullable=False),
    sa.Column('end_date', sa.Date(), nullable=False),
    sa.Column('leave_type', sa.Enum('ANNUAL', 'SICK', 'CASUAL', 'UNPAID', 'OTHER', name='leave_type', native_enum=False, create_constraint=True, length=16), nullable=False),
    sa.Column('status', sa.Enum('PENDING', 'APPROVED', 'REJECTED', 'CANCELLED', name='request_status', native_enum=False, create_constraint=True, length=16), nullable=False),
    sa.Column('reason', sa.Text(), nullable=True),
    sa.Column('reviewed_by_id', sa.Integer(), nullable=True),
    sa.Column('reviewed_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('reviewer_comment', sa.Text(), nullable=True),
    sa.Column('created_at', sa.DateTime(timezone=True), nullable=False),
    sa.Column('updated_at', sa.DateTime(timezone=True), nullable=False),
    sa.CheckConstraint('end_date >= start_date', name=op.f('ck_leave_records_date_order')),
    sa.ForeignKeyConstraint(['employee_id'], ['users.id'], name=op.f('fk_leave_records_employee_id_users'), ondelete='CASCADE'),
    sa.ForeignKeyConstraint(['reviewed_by_id'], ['users.id'], name=op.f('fk_leave_records_reviewed_by_id_users'), ondelete='SET NULL'),
    sa.PrimaryKeyConstraint('id', name=op.f('pk_leave_records'))
    )
    with op.batch_alter_table('leave_records', schema=None) as batch_op:
        batch_op.create_index('ix_leave_employee_dates', ['employee_id', 'start_date', 'end_date'], unique=False)

    op.create_table('user_sessions',
    sa.Column('id', sa.BigInteger().with_variant(sa.Integer(), 'sqlite'), nullable=False),
    sa.Column('user_id', sa.Integer(), nullable=False),
    sa.Column('token_hash', sa.String(length=64), nullable=False),
    sa.Column('csrf_token', sa.String(length=64), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), nullable=False),
    sa.Column('last_seen_at', sa.DateTime(timezone=True), nullable=False),
    sa.Column('expires_at', sa.DateTime(timezone=True), nullable=False),
    sa.Column('revoked_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('ip_address', sa.String(length=64), nullable=True),
    sa.Column('user_agent', sa.String(length=255), nullable=True),
    sa.ForeignKeyConstraint(['user_id'], ['users.id'], name=op.f('fk_user_sessions_user_id_users'), ondelete='CASCADE'),
    sa.PrimaryKeyConstraint('id', name=op.f('pk_user_sessions')),
    sa.UniqueConstraint('token_hash', name=op.f('uq_user_sessions_token_hash'))
    )
    with op.batch_alter_table('user_sessions', schema=None) as batch_op:
        batch_op.create_index('ix_user_sessions_user', ['user_id'], unique=False)

    op.create_table('work_schedules',
    sa.Column('id', sa.Integer(), nullable=False),
    sa.Column('name', sa.String(length=100), nullable=False),
    sa.Column('employee_id', sa.Integer(), nullable=True),
    sa.Column('department_id', sa.Integer(), nullable=True),
    sa.Column('start_time', sa.Time(), nullable=False),
    sa.Column('end_time', sa.Time(), nullable=False),
    sa.Column('required_minutes', sa.Integer(), nullable=False),
    sa.Column('working_days', sa.JSON(), nullable=False),
    sa.Column('effective_from', sa.Date(), nullable=False),
    sa.Column('effective_to', sa.Date(), nullable=True),
    sa.Column('created_at', sa.DateTime(timezone=True), nullable=False),
    sa.Column('updated_at', sa.DateTime(timezone=True), nullable=False),
    sa.CheckConstraint('NOT (employee_id IS NOT NULL AND department_id IS NOT NULL)', name=op.f('ck_work_schedules_single_target')),
    sa.CheckConstraint('effective_to IS NULL OR effective_to >= effective_from', name=op.f('ck_work_schedules_effective_range')),
    sa.CheckConstraint('required_minutes > 0 AND required_minutes <= 1440', name=op.f('ck_work_schedules_required_minutes_range')),
    sa.ForeignKeyConstraint(['department_id'], ['departments.id'], name=op.f('fk_work_schedules_department_id_departments'), ondelete='CASCADE'),
    sa.ForeignKeyConstraint(['employee_id'], ['users.id'], name=op.f('fk_work_schedules_employee_id_users'), ondelete='CASCADE'),
    sa.PrimaryKeyConstraint('id', name=op.f('pk_work_schedules'))
    )
    with op.batch_alter_table('work_schedules', schema=None) as batch_op:
        batch_op.create_index('ix_work_schedules_department', ['department_id'], unique=False)
        batch_op.create_index('ix_work_schedules_employee', ['employee_id'], unique=False)

    op.create_table('attendance_corrections',
    sa.Column('id', sa.Integer(), nullable=False),
    sa.Column('employee_id', sa.Integer(), nullable=False),
    sa.Column('attendance_date', sa.Date(), nullable=False),
    sa.Column('request_type', sa.Enum('ADD_SESSION', 'MODIFY_SESSION', name='correction_type', native_enum=False, create_constraint=True, length=20), nullable=False),
    sa.Column('original_session_id', sa.Integer(), nullable=True),
    sa.Column('original_values', sa.JSON(), nullable=True),
    sa.Column('requested_check_in_at', sa.DateTime(timezone=True), nullable=False),
    sa.Column('requested_check_out_at', sa.DateTime(timezone=True), nullable=False),
    sa.Column('requested_break_minutes', sa.Integer(), nullable=False),
    sa.Column('reason', sa.String(length=1000), nullable=False),
    sa.Column('status', sa.Enum('PENDING', 'APPROVED', 'REJECTED', 'CANCELLED', name='request_status', native_enum=False, create_constraint=True, length=16), nullable=False),
    sa.Column('reviewed_by_id', sa.Integer(), nullable=True),
    sa.Column('reviewer_comment', sa.Text(), nullable=True),
    sa.Column('reviewed_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('resulting_session_id', sa.Integer(), nullable=True),
    sa.Column('created_at', sa.DateTime(timezone=True), nullable=False),
    sa.Column('updated_at', sa.DateTime(timezone=True), nullable=False),
    sa.CheckConstraint('requested_break_minutes >= 0', name=op.f('ck_attendance_corrections_requested_break_non_negative')),
    sa.CheckConstraint('requested_check_out_at > requested_check_in_at', name=op.f('ck_attendance_corrections_requested_order')),
    sa.ForeignKeyConstraint(['employee_id'], ['users.id'], name=op.f('fk_attendance_corrections_employee_id_users'), ondelete='CASCADE'),
    sa.ForeignKeyConstraint(['original_session_id'], ['attendance_sessions.id'], name=op.f('fk_attendance_corrections_original_session_id_attendance_sessions'), ondelete='SET NULL'),
    sa.ForeignKeyConstraint(['resulting_session_id'], ['attendance_sessions.id'], name=op.f('fk_attendance_corrections_resulting_session_id_attendance_sessions'), ondelete='SET NULL'),
    sa.ForeignKeyConstraint(['reviewed_by_id'], ['users.id'], name=op.f('fk_attendance_corrections_reviewed_by_id_users'), ondelete='SET NULL'),
    sa.PrimaryKeyConstraint('id', name=op.f('pk_attendance_corrections'))
    )
    with op.batch_alter_table('attendance_corrections', schema=None) as batch_op:
        batch_op.create_index('ix_corrections_employee_date', ['employee_id', 'attendance_date'], unique=False)
        batch_op.create_index('ix_corrections_status', ['status'], unique=False)



def downgrade() -> None:
    # The btree_gist extension is left installed: it is database-wide and may be used elsewhere.
    # ex_attendance_no_overlap is dropped together with attendance_sessions.
    with op.batch_alter_table('attendance_corrections', schema=None) as batch_op:
        batch_op.drop_index('ix_corrections_status')
        batch_op.drop_index('ix_corrections_employee_date')

    op.drop_table('attendance_corrections')
    with op.batch_alter_table('work_schedules', schema=None) as batch_op:
        batch_op.drop_index('ix_work_schedules_employee')
        batch_op.drop_index('ix_work_schedules_department')

    op.drop_table('work_schedules')
    with op.batch_alter_table('user_sessions', schema=None) as batch_op:
        batch_op.drop_index('ix_user_sessions_user')

    op.drop_table('user_sessions')
    with op.batch_alter_table('leave_records', schema=None) as batch_op:
        batch_op.drop_index('ix_leave_employee_dates')

    op.drop_table('leave_records')
    with op.batch_alter_table('audit_logs', schema=None) as batch_op:
        batch_op.drop_index('ix_audit_entity')
        batch_op.drop_index('ix_audit_created_at')
        batch_op.drop_index('ix_audit_actor')

    op.drop_table('audit_logs')
    with op.batch_alter_table('attendance_sessions', schema=None) as batch_op:
        batch_op.drop_index('uq_attendance_one_open_session', postgresql_where=sa.text("status = 'OPEN'"), sqlite_where=sa.text("status = 'OPEN'"))
        batch_op.drop_index('ix_attendance_employee_checkin')
        batch_op.drop_index('ix_attendance_checkin')

    op.drop_table('attendance_sessions')
    op.drop_table('attendance_policy')
    with op.batch_alter_table('users', schema=None) as batch_op:
        batch_op.drop_index('ix_users_manager_id')
        batch_op.drop_index('ix_users_department_id')

    op.drop_index('uq_departments_name_lower', table_name='departments')
    with op.batch_alter_table('departments', schema=None) as batch_op:
        batch_op.drop_constraint('fk_departments_head_users', type_='foreignkey')
    op.drop_table('users')
    with op.batch_alter_table('holidays', schema=None) as batch_op:
        batch_op.drop_index(batch_op.f('ix_holidays_holiday_date'))

    op.drop_index('uq_holidays_date_department', table_name='holidays')
    op.drop_table('holidays')
    with op.batch_alter_table('login_attempts', schema=None) as batch_op:
        batch_op.drop_index('ix_login_attempts_ip_time')
        batch_op.drop_index('ix_login_attempts_email_time')

    op.drop_table('login_attempts')
    op.drop_table('departments')
