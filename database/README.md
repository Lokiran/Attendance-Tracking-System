# Database

* Engine: PostgreSQL 16+. Required extension: `btree_gist` (trusted; created by the baseline migration)
  for the "no overlapping sessions" exclusion constraint.
* Schema lives in EF Core migrations: `backend-dotnet/src/ATS.Infrastructure/Persistence/Migrations/`. Create a new
  database with `dotnet run --project src\ATS.Api -- migrate` from `backend-dotnet/`; a database created by the former
  Python backend (Alembic) is adopted once with `ef-baseline` instead (see `backend-dotnet/README.md`).
* The baseline migration runs `CREATE EXTENSION IF NOT EXISTS btree_gist`; it fails with PostgreSQL's error if the
  extension is not available (contrib not installed) or cannot be created (insufficient privilege).
* Verify a server end-to-end by running the API tests against it (see the main README, Tests).
* `init/` is mounted into the Docker container and creates the `ats_test` database used by the
  API tests on first start.

Manual (non-Docker) setup in `psql` as a superuser:

```sql
CREATE ROLE ats LOGIN PASSWORD 'choose-a-strong-password';
CREATE DATABASE ats OWNER ats;
CREATE DATABASE ats_test OWNER ats;
```
