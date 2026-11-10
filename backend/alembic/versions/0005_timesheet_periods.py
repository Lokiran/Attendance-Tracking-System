"""Timesheet periods: attendance comes from imported timesheets (no check-in)

* attendance_uploads gains period_start / period_end: the dates a committed timesheet covers. A scheduled
  day inside a covered period that is not in any timesheet is absent; a day outside every period has no
  timesheet yet (it is not counted as absent).
* A committed timesheet covers its period for the people listed in it (its valid rows), so
  attendance_upload_rows gains an index on (employee_id, upload_id).
* Existing committed uploads are backfilled from their preview summary (date_from / date_to) and commit
  time, with the same rule the app uses: from the 1st of the month of the first date to the end of the
  month of the last date, but not beyond the day before the commit.

Additive only. The check-in and correction tables stay as they are (unused, nothing deleted).

Revision ID: 0005
Revises: 0004
Create Date: 2026-11-10
"""
import calendar
import json
from collections.abc import Sequence
from datetime import date, datetime, timedelta

import sqlalchemy as sa
from alembic import context, op

revision: str = '0005'
down_revision: str | None = '0004'
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def _period(date_from: date, date_to: date, committed: date) -> tuple[date, date]:
    start = date_from.replace(day=1)
    month_end = date_to.replace(day=calendar.monthrange(date_to.year, date_to.month)[1])
    # Same rule as app.services.wfo_upload.timesheet_period (migrations do not import app code).
    end = max(min(month_end, committed - timedelta(days=1)), date_to)
    return start, max(end, start)


def upgrade() -> None:
    with op.batch_alter_table('attendance_uploads') as batch:
        batch.add_column(sa.Column('period_start', sa.Date(), nullable=True))
        batch.add_column(sa.Column('period_end', sa.Date(), nullable=True))
    op.create_index(op.f('ix_attendance_upload_rows_employee'), 'attendance_upload_rows', ['employee_id', 'upload_id'])
    if context.is_offline_mode():
        return
    bind = op.get_bind()
    uploads = sa.table('attendance_uploads', sa.column('id', sa.Integer), sa.column('status', sa.String),
                       sa.column('summary', sa.JSON), sa.column('committed_at', sa.DateTime(timezone=True)),
                       sa.column('period_start', sa.Date), sa.column('period_end', sa.Date))
    rows = bind.execute(sa.select(uploads.c.id, uploads.c.summary, uploads.c.committed_at)
                        .where(uploads.c.status == 'COMMITTED')).all()
    for uid, summary, committed_at in rows:
        if isinstance(summary, str):
            summary = json.loads(summary)
        if not summary or not summary.get('date_from') or not summary.get('date_to') or committed_at is None:
            continue
        if isinstance(committed_at, str):
            committed_at = datetime.fromisoformat(committed_at)
        start, end = _period(date.fromisoformat(summary['date_from']), date.fromisoformat(summary['date_to']),
                             committed_at.date())
        bind.execute(uploads.update().where(uploads.c.id == uid).values(period_start=start, period_end=end))


def downgrade() -> None:
    op.drop_index(op.f('ix_attendance_upload_rows_employee'), table_name='attendance_upload_rows')
    with op.batch_alter_table('attendance_uploads') as batch:
        batch.drop_column('period_end')
        batch.drop_column('period_start')
