# Database

* Engine: PostgreSQL 15+ (16 recommended). Required extension: `btree_gist` (trusted; created by the
  first Alembic migration) for the "no overlapping sessions" exclusion constraint.
* Schema lives in Alembic migrations: `backend/alembic/versions/`. Apply with `alembic upgrade head`
  from `backend/`.
* The migration checks for `btree_gist` and stops with an actionable error if it is not available
  (contrib not installed) or cannot be created (insufficient privilege). Downgrade keeps the
  extension because it is database-wide.
* Verify a server end-to-end with `scripts\test-postgres.ps1` (see the main README, Tests).
* `init/` is mounted into the Docker container and creates the `ats_test` database used by the
  backend tests on first start.

Manual (non-Docker) setup in `psql` as a superuser:

```sql
CREATE ROLE ats LOGIN PASSWORD 'choose-a-strong-password';
CREATE DATABASE ats OWNER ats;
CREATE DATABASE ats_test OWNER ats;
```
