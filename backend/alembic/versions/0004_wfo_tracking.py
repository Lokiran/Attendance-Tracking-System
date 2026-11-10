"""WFO tracking

* attendance_daily gains the work mode (OFFICE / WFH / LEAVE / HOLIDAY / WEEKOFF), the source that
  decided it, a conflicts list, one evidence column per source (upload column, badge, Wi-Fi), the
  upload that last wrote it, and an Admin override (who / when / note; old and new in the audit log).
* attendance_daily.status: WFH moves to work_mode, LEAVE is added (imported files / overrides only).
* attendance_sessions.work_location: Office or WFH chosen at check-in.
* attendance_uploads + attendance_upload_rows: imported attendance / badge files and their parsed rows.
* attendance_policy: WFO policy (minimum WFO days per week / month, minimum office minutes per WFO day).

Every change is additive except the two status CHECKs; the upgrade refuses if any row still uses the
retired WFH status, and the downgrade refuses if any row uses LEAVE or the new tables hold data.

Revision ID: 0004
Revises: 0003
Create Date: 2026-11-10
"""
from collections.abc import Sequence

import sqlalchemy as sa
from alembic import context, op

revision: str = '0004'
down_revision: str | None = '0003'
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

WORK_MODES = ('OFFICE', 'WFH', 'LEAVE', 'HOLIDAY', 'WEEKOFF')
LOCATIONS = ('OFFICE', 'WFH')
WFO_SOURCES = ('MANUAL_OVERRIDE', 'UPLOAD_COLUMN', 'BADGE', 'WIFI', 'CHECK_IN', 'INFERRED', 'CALENDAR')
OLD_STATUSES = ('PRESENT', 'HALF_DAY', 'ABSENT', 'WFH', 'HOLIDAY', 'WEEKEND', 'NO_DATA')
NEW_STATUSES = ('PRESENT', 'HALF_DAY', 'ABSENT', 'LEAVE', 'HOLIDAY', 'WEEKEND', 'NO_DATA')
POLICY_COLUMNS = (('min_wfo_days_per_week', '3'), ('min_wfo_days_per_month', '12'),
                  ('min_office_minutes_for_wfo_day', '240'))
POLICY_CHECKS = (
    ('ck_attendance_policy_min_wfo_days_per_week_range', 'min_wfo_days_per_week BETWEEN 0 AND 7'),
    ('ck_attendance_policy_min_wfo_days_per_month_range', 'min_wfo_days_per_month BETWEEN 0 AND 31'),
    ('ck_attendance_policy_min_office_minutes_range', 'min_office_minutes_for_wfo_day BETWEEN 0 AND 1440'),
)


def _in(column: str, values: tuple[str, ...]) -> str:
    return f"{column} IN (" + ", ".join(f"'{v}'" for v in values) + ")"


def _count(sql: str) -> int:
    if context.is_offline_mode():
        return 0
    return op.get_bind().execute(sa.text(sql)).scalar() or 0


def _mode(name: str) -> sa.Enum:
    return sa.Enum(*WORK_MODES, name=name, native_enum=False, create_constraint=True, length=16)


def _ts(name: str, nullable: bool = True) -> sa.Column:
    return sa.Column(name, sa.DateTime(timezone=True), nullable=nullable)


def upgrade() -> None:
    wfh_rows = _count("SELECT count(*) FROM attendance_daily WHERE status = 'WFH'") + \
        _count("SELECT count(*) FROM attendance_daily_corrections WHERE requested_status = 'WFH'")
    if wfh_rows:
        raise RuntimeError(f"Cannot upgrade to 0004: {wfh_rows} row(s) use the retired WFH status. "
                           "Nothing was changed; convert them to a work mode first.")

    op.create_table('attendance_uploads',
        sa.Column('id', sa.Integer(), nullable=False),
        sa.Column('filename', sa.String(length=255), nullable=False),
        sa.Column('file_sha256', sa.String(length=64), nullable=False),
        sa.Column('file_size', sa.Integer(), nullable=False),
        sa.Column('status', sa.Enum('PREVIEW', 'COMMITTED', 'DISCARDED', name='upload_status', native_enum=False,
                                    create_constraint=True, length=16), nullable=False),
        sa.Column('row_count', sa.Integer(), nullable=False),
        sa.Column('error_count', sa.Integer(), nullable=False),
        sa.Column('summary', sa.JSON(), nullable=True),
        sa.Column('commit_summary', sa.JSON(), nullable=True),
        sa.Column('overwrite_overrides', sa.Boolean(), nullable=False),
        sa.Column('uploaded_by_id', sa.Integer(), nullable=True),
        _ts('created_at', False),
        sa.Column('committed_by_id', sa.Integer(), nullable=True),
        _ts('committed_at'),
        sa.ForeignKeyConstraint(['uploaded_by_id'], ['users.id'], name=op.f('fk_attendance_uploads_uploaded_by_id_users'),
                                ondelete='SET NULL'),
        sa.ForeignKeyConstraint(['committed_by_id'], ['users.id'],
                                name=op.f('fk_attendance_uploads_committed_by_id_users'), ondelete='SET NULL'),
        sa.PrimaryKeyConstraint('id', name=op.f('pk_attendance_uploads')),
    )
    op.create_index('ix_attendance_uploads_created', 'attendance_uploads', ['created_at'], unique=False)

    op.create_table('attendance_upload_rows',
        sa.Column('id', sa.BigInteger().with_variant(sa.Integer(), 'sqlite'), nullable=False),
        sa.Column('upload_id', sa.Integer(), nullable=False),
        sa.Column('row_number', sa.Integer(), nullable=False),
        sa.Column('employee_ref', sa.String(length=255), nullable=True),
        sa.Column('employee_id', sa.Integer(), nullable=True),
        sa.Column('work_date', sa.Date(), nullable=True),
        sa.Column('raw_work_mode', sa.String(length=64), nullable=True),
        sa.Column('work_mode', _mode('upload_row_work_mode'), nullable=True),
        sa.Column('raw_source', sa.String(length=64), nullable=True),
        sa.Column('is_badge', sa.Boolean(), nullable=False),
        sa.Column('minutes', sa.Integer(), nullable=True),
        _ts('first_in'), _ts('last_out'),
        sa.Column('error', sa.String(length=255), nullable=True),
        sa.CheckConstraint('minutes IS NULL OR minutes >= 0', name=op.f('ck_attendance_upload_rows_minutes_non_negative')),
        sa.ForeignKeyConstraint(['upload_id'], ['attendance_uploads.id'],
                                name=op.f('fk_attendance_upload_rows_upload_id_attendance_uploads'), ondelete='CASCADE'),
        sa.ForeignKeyConstraint(['employee_id'], ['users.id'], name=op.f('fk_attendance_upload_rows_employee_id_users'),
                                ondelete='CASCADE'),
        sa.PrimaryKeyConstraint('id', name=op.f('pk_attendance_upload_rows')),
    )
    op.create_index('ix_attendance_upload_rows_upload', 'attendance_upload_rows', ['upload_id', 'row_number'],
                    unique=False)

    with op.batch_alter_table('attendance_daily', schema=None) as batch_op:
        batch_op.add_column(sa.Column('work_mode', _mode('work_mode'), nullable=True))
        batch_op.add_column(sa.Column('wfo_source', sa.Enum(*WFO_SOURCES, name='wfo_source', native_enum=False,
                                                            create_constraint=True, length=20), nullable=True))
        batch_op.add_column(sa.Column('conflicts', sa.JSON(), server_default=sa.text("'[]'"), nullable=False))
        batch_op.add_column(sa.Column('upload_work_mode', _mode('upload_work_mode'), nullable=True))
        batch_op.add_column(sa.Column('upload_minutes', sa.Integer(), nullable=True))
        batch_op.add_column(sa.Column('badge_minutes', sa.Integer(), nullable=True))
        batch_op.add_column(sa.Column('wifi_minutes', sa.Integer(), nullable=True))
        batch_op.add_column(sa.Column('upload_id', sa.Integer(), nullable=True))
        batch_op.add_column(sa.Column('override_work_mode', _mode('override_work_mode'), nullable=True))
        batch_op.add_column(sa.Column('override_note', sa.String(length=500), nullable=True))
        batch_op.add_column(sa.Column('overridden_by_id', sa.Integer(), nullable=True))
        batch_op.add_column(_ts('overridden_at'))
        batch_op.create_foreign_key(op.f('fk_attendance_daily_upload_id_attendance_uploads'), 'attendance_uploads',
                                    ['upload_id'], ['id'], ondelete='SET NULL')
        batch_op.create_foreign_key(op.f('fk_attendance_daily_overridden_by_id_users'), 'users',
                                    ['overridden_by_id'], ['id'], ondelete='SET NULL')
        batch_op.drop_constraint(op.f('ck_attendance_daily_daily_status'), type_='check')
        batch_op.create_check_constraint(op.f('ck_attendance_daily_daily_status'), _in('status', NEW_STATUSES))

    with op.batch_alter_table('attendance_daily_corrections', schema=None) as batch_op:
        batch_op.drop_constraint(op.f('ck_attendance_daily_corrections_requested_daily_status'), type_='check')
        batch_op.create_check_constraint(op.f('ck_attendance_daily_corrections_requested_daily_status'),
                                         _in('requested_status', NEW_STATUSES))

    # A nullable column with a column-level CHECK is added in place on both databases, so
    # attendance_sessions (with its partial index and exclusion constraint) is never rebuilt.
    if op.get_bind().dialect.name == 'sqlite':
        op.execute("ALTER TABLE attendance_sessions ADD COLUMN work_location VARCHAR(8) "
                   "CONSTRAINT ck_attendance_sessions_work_location CHECK (" + _in('work_location', LOCATIONS) + ")")
    else:
        op.add_column('attendance_sessions', sa.Column('work_location', sa.String(length=8), nullable=True))
        op.create_check_constraint(op.f('ck_attendance_sessions_work_location'), 'attendance_sessions',
                                   _in('work_location', LOCATIONS))

    with op.batch_alter_table('attendance_policy', schema=None) as batch_op:
        for name, default in POLICY_COLUMNS:
            batch_op.add_column(sa.Column(name, sa.Integer(), server_default=sa.text(default), nullable=False))
    with op.batch_alter_table('attendance_policy', schema=None) as batch_op:
        for name, expression in POLICY_CHECKS:
            batch_op.create_check_constraint(op.f(name), expression)


def downgrade() -> None:
    blocking = {
        "attendance rows with status LEAVE": _count("SELECT count(*) FROM attendance_daily WHERE status = 'LEAVE'"),
        "attendance rows with WFO data": _count("SELECT count(*) FROM attendance_daily WHERE work_mode IS NOT NULL "
                                                "OR override_work_mode IS NOT NULL OR upload_id IS NOT NULL"),
        "uploads": _count("SELECT count(*) FROM attendance_uploads"),
        "check-ins with a work location": _count("SELECT count(*) FROM attendance_sessions "
                                                 "WHERE work_location IS NOT NULL"),
    }
    blocking = {k: v for k, v in blocking.items() if v}
    if blocking:
        detail = ", ".join(f"{n} {k}" for k, n in blocking.items())
        raise RuntimeError(f"Cannot downgrade 0004: {detail}. Nothing was changed; export or remove that data first.")

    with op.batch_alter_table('attendance_policy', schema=None) as batch_op:
        for name, _expression in POLICY_CHECKS:
            batch_op.drop_constraint(op.f(name), type_='check')
    with op.batch_alter_table('attendance_policy', schema=None) as batch_op:
        for name, _default in reversed(POLICY_COLUMNS):
            batch_op.drop_column(name)

    if op.get_bind().dialect.name != 'sqlite':
        op.drop_constraint(op.f('ck_attendance_sessions_work_location'), 'attendance_sessions', type_='check')
    # SQLite >= 3.35 drops a column (with its column-level CHECK) in place.
    op.drop_column('attendance_sessions', 'work_location')

    with op.batch_alter_table('attendance_daily_corrections', schema=None) as batch_op:
        batch_op.drop_constraint(op.f('ck_attendance_daily_corrections_requested_daily_status'), type_='check')
        batch_op.create_check_constraint(op.f('ck_attendance_daily_corrections_requested_daily_status'),
                                         _in('requested_status', OLD_STATUSES))

    with op.batch_alter_table('attendance_daily', schema=None) as batch_op:
        batch_op.drop_constraint(op.f('ck_attendance_daily_daily_status'), type_='check')
        batch_op.create_check_constraint(op.f('ck_attendance_daily_daily_status'), _in('status', OLD_STATUSES))
        for check in ('work_mode', 'wfo_source', 'upload_work_mode', 'override_work_mode'):
            batch_op.drop_constraint(op.f(f'ck_attendance_daily_{check}'), type_='check')
        batch_op.drop_constraint(op.f('fk_attendance_daily_overridden_by_id_users'), type_='foreignkey')
        batch_op.drop_constraint(op.f('fk_attendance_daily_upload_id_attendance_uploads'), type_='foreignkey')
        for column in ('overridden_at', 'overridden_by_id', 'override_note', 'override_work_mode', 'upload_id',
                       'wifi_minutes', 'badge_minutes', 'upload_minutes', 'upload_work_mode', 'conflicts',
                       'wfo_source', 'work_mode'):
            batch_op.drop_column(column)

    op.drop_index('ix_attendance_upload_rows_upload', table_name='attendance_upload_rows')
    op.drop_table('attendance_upload_rows')
    op.drop_index('ix_attendance_uploads_created', table_name='attendance_uploads')
    op.drop_table('attendance_uploads')
