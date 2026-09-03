-- mysql-init.sql — the database and probe account the MySQL monitor tests connect as.
--
-- Applied by install-targets.sh over the root unix socket, after __E2E_MYSQL_PASSWORD__ has been
-- substituted. The substitution is why this is a template rather than something executed directly:
-- passing the password on the mysql command line would put it in the process table, and writing it
-- into a file under engine/ would trip the publish gate.
--
-- Everything here is written to converge rather than to create, so the installer can be re-run.

CREATE DATABASE IF NOT EXISTS e2e CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;

-- ─────────────────────────────────────────────────────────────────────────────────────────────────
--  BOTH '127.0.0.1' AND 'localhost' ARE REQUIRED, AND THAT IS NOT BELT-AND-BRACES
--
--  MySQL resolves the client address back to a hostname unless skip_name_resolve is set, and a TCP
--  connection from 127.0.0.1 therefore matches the account 'e2e_probe'@'localhost' — not
--  'e2e_probe'@'127.0.0.1'. Create only the latter and every connection is refused with
--  "Access denied for user 'e2e_probe'@'localhost'", which reads as a wrong password.
--
--  Creating both means the account works whichever way the lookup resolves, including on a box where
--  someone has since set skip_name_resolve.
-- ─────────────────────────────────────────────────────────────────────────────────────────────────

-- caching_sha2_password is named explicitly rather than left to the server default, because the whole
-- point of the MySQL tests is the behaviour of THAT plugin over TLS and without it. A future server
-- default would silently change what is being tested.
CREATE USER IF NOT EXISTS 'e2e_probe'@'127.0.0.1'
    IDENTIFIED WITH caching_sha2_password BY '__E2E_MYSQL_PASSWORD__';
CREATE USER IF NOT EXISTS 'e2e_probe'@'localhost'
    IDENTIFIED WITH caching_sha2_password BY '__E2E_MYSQL_PASSWORD__';

-- CREATE USER IF NOT EXISTS leaves an existing account's password alone, so the ALTERs are what make
-- a re-run converge on the password in the current manifest. Without them, regenerating the manifest
-- would leave the tests holding a password the server no longer accepts.
ALTER USER 'e2e_probe'@'127.0.0.1'
    IDENTIFIED WITH caching_sha2_password BY '__E2E_MYSQL_PASSWORD__';
ALTER USER 'e2e_probe'@'localhost'
    IDENTIFIED WITH caching_sha2_password BY '__E2E_MYSQL_PASSWORD__';

-- SELECT only. The checker runs `SELECT 1` and nothing else, so anything more would be granting
-- privileges to prove a point no test makes.
GRANT SELECT ON e2e.* TO 'e2e_probe'@'127.0.0.1';
GRANT SELECT ON e2e.* TO 'e2e_probe'@'localhost';

FLUSH PRIVILEGES;
