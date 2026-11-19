"""Office sites and the device register (Wi-Fi presence, step 1)

* office_sites: the offices, each with its own Wi-Fi network name (reports are per office).
* devices: the register that turns a Wi-Fi device (MAC address) into a person. With one shared Wi-Fi
  password the network never sees people, only devices. Keyed by asset serial number (how the company tracks
  laptops); the MAC address can be filled in later, from an Intune export or a script on the laptops.
* wifi_events gains site_id and device_id (which office, which registered device).
* Existing users.wifi_identity values that are MAC addresses are copied into devices (source LEGACY); the
  column itself stays (unused from now on, nothing deleted).

Additive only.

Revision ID: 0006
Revises: 0005
Create Date: 2026-11-17
"""
import re
from collections.abc import Sequence

import sqlalchemy as sa
from alembic import context, op

revision: str = '0006'
down_revision: str | None = '0005'
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None

_MAC = re.compile(r'^[0-9a-f]{2}(:[0-9a-f]{2}){5}$')


def _ts(name: str, nullable: bool = False) -> sa.Column:
    return sa.Column(name, sa.DateTime(timezone=True), nullable=nullable)


def upgrade() -> None:
    op.create_table('office_sites',
        sa.Column('id', sa.Integer(), nullable=False),
        sa.Column('name', sa.String(length=100), nullable=False),
        sa.Column('code', sa.String(length=16), nullable=False),
        sa.Column('network_name', sa.String(length=64), nullable=True),
        sa.Column('address', sa.Text(), nullable=True),
        sa.Column('is_active', sa.Boolean(), nullable=False),
        _ts('created_at'),
        _ts('updated_at'),
        sa.PrimaryKeyConstraint('id', name=op.f('pk_office_sites')),
    )
    op.create_index('uq_office_sites_name_lower', 'office_sites', [sa.text('lower(name)')], unique=True)
    op.create_index('uq_office_sites_code_lower', 'office_sites', [sa.text('lower(code)')], unique=True)

    op.create_table('devices',
        sa.Column('id', sa.Integer(), nullable=False),
        sa.Column('serial_number', sa.String(length=64), nullable=True),
        sa.Column('asset_tag', sa.String(length=64), nullable=True),
        sa.Column('mac_address', sa.String(length=17), nullable=True),
        sa.Column('hostname', sa.String(length=64), nullable=True),
        sa.Column('model', sa.String(length=120), nullable=True),
        sa.Column('user_id', sa.Integer(), nullable=True),
        sa.Column('is_active', sa.Boolean(), nullable=False),
        sa.Column('source', sa.String(length=16), nullable=False),
        sa.Column('notes', sa.Text(), nullable=True),
        _ts('created_at'),
        _ts('updated_at'),
        sa.CheckConstraint('serial_number IS NOT NULL OR mac_address IS NOT NULL', name=op.f('ck_devices_has_identifier')),
        sa.ForeignKeyConstraint(['user_id'], ['users.id'], name=op.f('fk_devices_user_id_users'), ondelete='SET NULL'),
        sa.PrimaryKeyConstraint('id', name=op.f('pk_devices')),
    )
    op.create_index('uq_devices_serial_number', 'devices', ['serial_number'], unique=True)
    op.create_index('uq_devices_mac_address', 'devices', ['mac_address'], unique=True)
    op.create_index('ix_devices_user_id', 'devices', ['user_id'], unique=False)
    op.create_index('ix_devices_hostname_lower', 'devices', [sa.text('lower(hostname)')], unique=False)

    with op.batch_alter_table('wifi_events') as batch:
        batch.add_column(sa.Column('site_id', sa.Integer(), nullable=True))
        batch.add_column(sa.Column('device_id', sa.Integer(), nullable=True))
        batch.create_foreign_key(op.f('fk_wifi_events_site_id_office_sites'), 'office_sites', ['site_id'], ['id'],
                                 ondelete='SET NULL')
        batch.create_foreign_key(op.f('fk_wifi_events_device_id_devices'), 'devices', ['device_id'], ['id'],
                                 ondelete='SET NULL')

    if context.is_offline_mode():
        return
    # Carry over the one-device-per-person MAC addresses (demo seed / earlier setup) into the register.
    bind = op.get_bind()
    users = sa.table('users', sa.column('id', sa.Integer), sa.column('wifi_identity', sa.String))
    devices = sa.table('devices', sa.column('mac_address', sa.String), sa.column('user_id', sa.Integer),
                       sa.column('is_active', sa.Boolean), sa.column('source', sa.String),
                       sa.column('created_at', sa.DateTime(timezone=True)),
                       sa.column('updated_at', sa.DateTime(timezone=True)))
    now = sa.func.current_timestamp()
    for uid, ident in bind.execute(sa.select(users.c.id, users.c.wifi_identity)
                                   .where(users.c.wifi_identity.is_not(None))).all():
        mac = (ident or '').strip().lower().replace('-', ':')
        if _MAC.match(mac):
            bind.execute(devices.insert().values(mac_address=mac, user_id=uid, is_active=True, source='LEGACY',
                                                 created_at=now, updated_at=now))


def downgrade() -> None:
    with op.batch_alter_table('wifi_events') as batch:
        batch.drop_constraint(op.f('fk_wifi_events_device_id_devices'), type_='foreignkey')
        batch.drop_constraint(op.f('fk_wifi_events_site_id_office_sites'), type_='foreignkey')
        batch.drop_column('device_id')
        batch.drop_column('site_id')
    op.drop_index('ix_devices_hostname_lower', table_name='devices')
    op.drop_index('ix_devices_user_id', table_name='devices')
    op.drop_index('uq_devices_mac_address', table_name='devices')
    op.drop_index('uq_devices_serial_number', table_name='devices')
    op.drop_table('devices')
    op.drop_index('uq_office_sites_code_lower', table_name='office_sites')
    op.drop_index('uq_office_sites_name_lower', table_name='office_sites')
    op.drop_table('office_sites')
