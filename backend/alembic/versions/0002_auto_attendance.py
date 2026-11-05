"""auto attendance (additive)

Adds signals, auto_attendance_days, worker_jobs and ingest_api_keys; nullable identity columns on
users; two policy switches (off / shadow by default); and the AUTO_WIFI / AUTO_TEAMS session sources.

Nothing is dropped or rewritten on upgrade. The only change to an existing constraint is widening
the attendance_sessions source CHECK to accept the two new values; existing rows are untouched.
On SQLite that requires rebuilding attendance_sessions (copying every row), so foreign keys are
switched off by alembic/env.py and the partial unique index is re-created explicitly.

Revision ID: 0002
Revises: 0001
Create Date: 2026-11-04
"""
from collections.abc import Sequence

import sqlalchemy as sa
from alembic import op

revision: str = '0002'
down_revision: str | None = '0001'
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

SOURCE_CHECK = 'ck_attendance_sessions_session_source'
OLD_SOURCES = ("WEB", "CORRECTION", "ADMIN", "SEED")
NEW_SOURCES = OLD_SOURCES + ("AUTO_WIFI", "AUTO_TEAMS")


def _in_list(values: tuple[str, ...]) -> str:
    return "source IN (" + ", ".join(f"'{v}'" for v in values) + ")"


def _set_session_sources(values: tuple[str, ...]) -> None:
    if op.get_bind().dialect.name == "sqlite":
        # SQLite cannot alter a CHECK: rebuild the table. The partial unique index is dropped and
        # re-created around the rebuild so its WHERE clause cannot be lost by reflection.
        op.drop_index('uq_attendance_one_open_session', table_name='attendance_sessions')
        with op.batch_alter_table('attendance_sessions', recreate='always') as batch_op:
            batch_op.drop_constraint(op.f(SOURCE_CHECK), type_='check')
            batch_op.create_check_constraint(op.f(SOURCE_CHECK), _in_list(values))
        op.create_index('uq_attendance_one_open_session', 'attendance_sessions', ['employee_id'], unique=True,
                        sqlite_where=sa.text("status = 'OPEN'"))
    else:
        op.drop_constraint(op.f(SOURCE_CHECK), 'attendance_sessions', type_='check')
        op.create_check_constraint(op.f(SOURCE_CHECK), 'attendance_sessions', _in_list(values))


def upgrade() -> None:
    with op.batch_alter_table('users', schema=None) as batch_op:
        batch_op.add_column(sa.Column('graph_user_id', sa.String(length=64), nullable=True))
        batch_op.add_column(sa.Column('wifi_identity', sa.String(length=255), nullable=True))
    op.create_index('uq_users_graph_user_id', 'users', ['graph_user_id'], unique=True)
    op.create_index('uq_users_wifi_identity', 'users', ['wifi_identity'], unique=True)

    with op.batch_alter_table('attendance_policy', schema=None) as batch_op:
        batch_op.add_column(sa.Column('auto_attendance_enabled', sa.Boolean(), server_default=sa.false(),
                                      nullable=False))
        batch_op.add_column(sa.Column('auto_shadow_mode', sa.Boolean(), server_default=sa.true(), nullable=False))

    _set_session_sources(NEW_SOURCES)

    op.create_table('signals',
    sa.Column('id', sa.BigInteger().with_variant(sa.Integer(), 'sqlite'), nullable=False),
    sa.Column('employee_id', sa.Integer(), nullable=False),
    sa.Column('source', sa.Enum('WIFI', 'TEAMS', name='signal_source', native_enum=False, create_constraint=True, length=8), nullable=False),
    sa.Column('event', sa.String(length=32), nullable=False),
    sa.Column('detail', sa.String(length=255), nullable=True),
    sa.Column('seen_at', sa.DateTime(timezone=True), nullable=False),
    sa.Column('received_at', sa.DateTime(timezone=True), nullable=False),
    sa.ForeignKeyConstraint(['employee_id'], ['users.id'], name=op.f('fk_signals_employee_id_users'), ondelete='CASCADE'),
    sa.PrimaryKeyConstraint('id', name=op.f('pk_signals')),
    sa.UniqueConstraint('employee_id', 'source', 'event', 'seen_at', name='uq_signals_dedupe')
    )
    op.create_index('ix_signals_employee_seen', 'signals', ['employee_id', 'seen_at'], unique=False)
    op.create_index('ix_signals_seen_at', 'signals', ['seen_at'], unique=False)

    op.create_table('auto_attendance_days',
    sa.Column('id', sa.Integer(), nullable=False),
    sa.Column('employee_id', sa.Integer(), nullable=False),
    sa.Column('work_date', sa.Date(), nullable=False),
    sa.Column('check_in', sa.DateTime(timezone=True), nullable=True),
    sa.Column('check_out', sa.DateTime(timezone=True), nullable=True),
    sa.Column('location', sa.Enum('OFFICE', 'WFH', name='work_location', native_enum=False, create_constraint=True, length=8), nullable=True),
    sa.Column('source', sa.Enum('AUTO_WIFI', 'AUTO_TEAMS', name='auto_source', native_enum=False, create_constraint=True, length=16), nullable=True),
    sa.Column('active_minutes', sa.Integer(), nullable=False),
    sa.Column('signal_count', sa.Integer(), nullable=False),
    sa.Column('needs_review', sa.Boolean(), nullable=False),
    sa.Column('review_reasons', sa.JSON(), nullable=False),
    sa.Column('is_shadow', sa.Boolean(), nullable=False),
    sa.Column('superseded', sa.Boolean(), nullable=False),
    sa.Column('superseded_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('session_id', sa.Integer(), nullable=True),
    sa.Column('review_status', sa.Enum('PENDING', 'APPROVED', 'REJECTED', 'CANCELLED', name='review_status', native_enum=False, create_constraint=True, length=16), nullable=True),
    sa.Column('reviewed_by_id', sa.Integer(), nullable=True),
    sa.Column('reviewed_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('review_comment', sa.Text(), nullable=True),
    sa.Column('computed_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('created_at', sa.DateTime(timezone=True), nullable=False),
    sa.Column('updated_at', sa.DateTime(timezone=True), nullable=False),
    sa.ForeignKeyConstraint(['employee_id'], ['users.id'], name=op.f('fk_auto_attendance_days_employee_id_users'), ondelete='CASCADE'),
    sa.ForeignKeyConstraint(['reviewed_by_id'], ['users.id'], name=op.f('fk_auto_attendance_days_reviewed_by_id_users'), ondelete='SET NULL'),
    sa.ForeignKeyConstraint(['session_id'], ['attendance_sessions.id'], name=op.f('fk_auto_attendance_days_session_id_attendance_sessions'), ondelete='SET NULL'),
    sa.PrimaryKeyConstraint('id', name=op.f('pk_auto_attendance_days')),
    sa.UniqueConstraint('employee_id', 'work_date', name='uq_auto_attendance_days_employee_day')
    )
    op.create_index('ix_auto_attendance_days_review', 'auto_attendance_days', ['needs_review', 'work_date'], unique=False)

    op.create_table('worker_jobs',
    sa.Column('name', sa.String(length=64), nullable=False),
    sa.Column('last_run_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('last_success_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('last_error', sa.Text(), nullable=True),
    sa.Column('last_duration_ms', sa.Integer(), nullable=True),
    sa.Column('consecutive_failures', sa.Integer(), nullable=False),
    sa.Column('run_count', sa.Integer(), nullable=False),
    sa.Column('details', sa.JSON(), nullable=True),
    sa.Column('updated_at', sa.DateTime(timezone=True), nullable=False),
    sa.PrimaryKeyConstraint('name', name=op.f('pk_worker_jobs'))
    )

    op.create_table('ingest_api_keys',
    sa.Column('id', sa.Integer(), nullable=False),
    sa.Column('name', sa.String(length=64), nullable=False),
    sa.Column('key_hash', sa.String(length=64), nullable=False),
    sa.Column('key_prefix', sa.String(length=12), nullable=False),
    sa.Column('created_at', sa.DateTime(timezone=True), nullable=False),
    sa.Column('created_by_id', sa.Integer(), nullable=True),
    sa.Column('last_used_at', sa.DateTime(timezone=True), nullable=True),
    sa.Column('revoked_at', sa.DateTime(timezone=True), nullable=True),
    sa.ForeignKeyConstraint(['created_by_id'], ['users.id'], name=op.f('fk_ingest_api_keys_created_by_id_users'), ondelete='SET NULL'),
    sa.PrimaryKeyConstraint('id', name=op.f('pk_ingest_api_keys')),
    sa.UniqueConstraint('key_hash', name=op.f('uq_ingest_api_keys_key_hash'))
    )


def downgrade() -> None:
    # Refuse rather than silently discard attendance created by the engine.
    auto_rows = op.get_bind().execute(sa.text(
        "SELECT count(*) FROM attendance_sessions WHERE source IN ('AUTO_WIFI', 'AUTO_TEAMS')")).scalar()
    if auto_rows:
        raise RuntimeError(
            f"Cannot downgrade 0002: {auto_rows} attendance session(s) were created by automatic attendance. "
            "Review and convert or remove them explicitly first."
        )

    op.drop_table('ingest_api_keys')
    op.drop_table('worker_jobs')
    op.drop_index('ix_auto_attendance_days_review', table_name='auto_attendance_days')
    op.drop_table('auto_attendance_days')
    op.drop_index('ix_signals_seen_at', table_name='signals')
    op.drop_index('ix_signals_employee_seen', table_name='signals')
    op.drop_table('signals')

    _set_session_sources(OLD_SOURCES)

    with op.batch_alter_table('attendance_policy', schema=None) as batch_op:
        batch_op.drop_column('auto_shadow_mode')
        batch_op.drop_column('auto_attendance_enabled')

    op.drop_index('uq_users_wifi_identity', table_name='users')
    op.drop_index('uq_users_graph_user_id', table_name='users')
    with op.batch_alter_table('users', schema=None) as batch_op:
        batch_op.drop_column('wifi_identity')
        batch_op.drop_column('graph_user_id')
