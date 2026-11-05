-- Runs once when the Docker volume is first created.
-- A separate, disposable database for the backend test-suite (TEST_DATABASE_URL).
CREATE DATABASE ats_test;
