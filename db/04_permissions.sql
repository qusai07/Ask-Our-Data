-- Least-privilege database principals.
--
-- Production: map these users to dedicated logins / managed identities
--   (CREATE USER rpt_reader FOR LOGIN ... / FROM EXTERNAL PROVIDER).
-- Local dev (Windows-auth-only server): they are users WITHOUT LOGIN and the
--   gateway impersonates them with EXECUTE AS USER ... WITH NO REVERT on a
--   non-pooled connection, then verifies its effective permissions at startup.

USE AskData_Reporting;
GO
CREATE USER rpt_reader WITHOUT LOGIN;
GO
-- The only grant: SELECT on the rpt views.
GRANT SELECT ON SCHEMA::rpt TO rpt_reader;
-- Explicit denies on everything else (views still work through ownership chaining).
DENY SELECT, INSERT, UPDATE, DELETE, EXECUTE, ALTER, REFERENCES, VIEW DEFINITION ON SCHEMA::core TO rpt_reader;
DENY SELECT, INSERT, UPDATE, DELETE, EXECUTE, ALTER, REFERENCES, VIEW DEFINITION ON SCHEMA::sec  TO rpt_reader;
DENY INSERT, UPDATE, DELETE, ALTER ON SCHEMA::rpt TO rpt_reader;
DENY CREATE TABLE, CREATE VIEW, CREATE PROCEDURE, CREATE FUNCTION, CREATE SCHEMA TO rpt_reader;
GO

USE AskData_Ops;
GO
CREATE USER ops_writer WITHOUT LOGIN;
GO
GRANT INSERT ON ops.audit_log      TO ops_writer;
GRANT INSERT ON ops.missing_report TO ops_writer;
GRANT SELECT ON SCHEMA::sec TO ops_writer;          -- read the security model (05_security_model.sql)
DENY  UPDATE, DELETE ON SCHEMA::ops TO ops_writer;   -- audit is append-only
DENY  INSERT, UPDATE, DELETE, ALTER ON SCHEMA::sec TO ops_writer;
DENY CREATE TABLE, CREATE VIEW, CREATE PROCEDURE, CREATE FUNCTION, CREATE SCHEMA TO ops_writer;
GO
