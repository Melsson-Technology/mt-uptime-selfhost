-- postgres-init.sql — the role and database the PostgreSQL monitor tests connect as.
--
-- Applied by install-targets.sh as `runuser -u postgres -- psql`, from / rather than from the
-- invoking directory (psql warns loudly about not being able to chdir out of /root otherwise, which
-- looks like an error and is not).
--
-- __E2E_POSTGRES_PASSWORD__ is substituted before this runs. Same reasoning as the MySQL template:
-- not on a command line, not in a file under engine/.

-- CREATE ROLE has no IF NOT EXISTS, so the guard is a DO block. The ALTER outside it is what makes a
-- re-run converge on the current manifest's password — the same reasoning as the MySQL ALTERs.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'e2e_probe') THEN
        CREATE ROLE e2e_probe LOGIN;
    END IF;
END
$$;

ALTER ROLE e2e_probe LOGIN PASSWORD '__E2E_POSTGRES_PASSWORD__';

-- The database is created by install-targets.sh with `createdb -O e2e_probe e2e` when absent, because
-- CREATE DATABASE cannot run inside a transaction block and psql -f wraps this file in one.

-- Connect privilege is all the checker needs; it runs `SELECT 1`, which requires no table rights.
-- Granted here rather than relying on PUBLIC, because a hardened template1 may have revoked it.
GRANT CONNECT ON DATABASE e2e TO e2e_probe;
