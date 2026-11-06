-- Security model in OpsDb: users, roles, permissions, role_permissions, user_roles, user_region_scope.
-- The database is the ONLY source of authorization; the token only proves identity (oid).
-- All tables are system-versioned (temporal): every grant/revoke is kept in sec_history.* automatically.
--
-- SAFE TO RE-RUN: creates only what is missing and inserts only missing baseline rows.
-- It never drops tables and never removes users, assignments or history.
SET QUOTED_IDENTIFIER ON;   -- required for the filtered unique index (sqlcmd defaults to OFF)
SET ANSI_NULLS ON;
GO
USE AskData_Ops;
GO
IF SCHEMA_ID(N'sec_history') IS NULL EXEC (N'CREATE SCHEMA sec_history');
GO

-- One-time migration: the pre-security-model sec.user_region_scope (non-temporal, no FK) is replaced.
IF OBJECT_ID(N'sec.user_region_scope') IS NOT NULL
   AND OBJECTPROPERTY(OBJECT_ID(N'sec.user_region_scope'), 'TableTemporalType') = 0
    DROP TABLE sec.user_region_scope;
GO

------------------------------------------------------------------------------
-- Permissions: code-coupled (tools reference them). The gateway refuses to start
-- if the catalog uses a permission that is not in this table.
------------------------------------------------------------------------------
IF OBJECT_ID(N'sec.permissions') IS NULL
CREATE TABLE sec.permissions (
    permission_code varchar(50)   NOT NULL CONSTRAINT PK_permissions PRIMARY KEY
                    CONSTRAINT CK_permissions_code CHECK (permission_code LIKE '[a-z]%.[a-z]%'
                                                          AND permission_code NOT LIKE '%[^a-z0-9._]%'),
    description     nvarchar(400) NOT NULL,
    valid_from datetime2(0) GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    valid_to   datetime2(0) GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (valid_from, valid_to)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = sec_history.permissions));
GO
IF OBJECT_ID(N'sec.roles') IS NULL
CREATE TABLE sec.roles (
    role_code   varchar(50)   NOT NULL CONSTRAINT PK_roles PRIMARY KEY
                CONSTRAINT CK_roles_code CHECK (role_code NOT LIKE '%[^a-z0-9_]%' AND LEN(role_code) >= 2),
    name        nvarchar(100) NOT NULL,
    description nvarchar(400) NULL,
    is_active   bit           NOT NULL CONSTRAINT DF_roles_active DEFAULT 1,
    valid_from datetime2(0) GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    valid_to   datetime2(0) GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (valid_from, valid_to)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = sec_history.roles));
GO
IF OBJECT_ID(N'sec.role_permissions') IS NULL
CREATE TABLE sec.role_permissions (
    role_code       varchar(50) NOT NULL CONSTRAINT FK_rp_role REFERENCES sec.roles(role_code),
    permission_code varchar(50) NOT NULL CONSTRAINT FK_rp_permission REFERENCES sec.permissions(permission_code),
    CONSTRAINT PK_role_permissions PRIMARY KEY (role_code, permission_code),
    valid_from datetime2(0) GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    valid_to   datetime2(0) GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (valid_from, valid_to)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = sec_history.role_permissions));
GO

------------------------------------------------------------------------------
-- Users: keyed by the token's oid (Entra object id). A valid token for a user who
-- is missing here or inactive gets no access at all.
------------------------------------------------------------------------------
IF OBJECT_ID(N'sec.users') IS NULL
BEGIN
    CREATE TABLE sec.users (
        user_oid     varchar(100)  NOT NULL CONSTRAINT PK_users PRIMARY KEY,
        display_name nvarchar(150) NOT NULL,
        upn          nvarchar(256) NULL,
        is_active    bit           NOT NULL CONSTRAINT DF_users_active DEFAULT 1,
        created_by   nvarchar(128) NOT NULL CONSTRAINT DF_users_created_by DEFAULT SUSER_SNAME(),
        valid_from datetime2(0) GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
        valid_to   datetime2(0) GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL,
        PERIOD FOR SYSTEM_TIME (valid_from, valid_to)
    ) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = sec_history.users));
    CREATE UNIQUE INDEX UX_users_upn ON sec.users(upn) WHERE upn IS NOT NULL;
END
GO
IF OBJECT_ID(N'sec.user_roles') IS NULL
CREATE TABLE sec.user_roles (
    user_oid   varchar(100)  NOT NULL CONSTRAINT FK_ur_user REFERENCES sec.users(user_oid),
    role_code  varchar(50)   NOT NULL CONSTRAINT FK_ur_role REFERENCES sec.roles(role_code),
    granted_by nvarchar(128) NOT NULL CONSTRAINT DF_ur_granted_by DEFAULT SUSER_SNAME(),
    CONSTRAINT PK_user_roles PRIMARY KEY (user_oid, role_code),
    valid_from datetime2(0) GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    valid_to   datetime2(0) GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (valid_from, valid_to)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = sec_history.user_roles));
GO
-- Region scope (row-level data access). '*' = all regions. Codes are validated against
-- ReportingDb regions by the gateway (cross-database FKs are not possible); unknown codes grant nothing.
IF OBJECT_ID(N'sec.user_region_scope') IS NULL
CREATE TABLE sec.user_region_scope (
    user_oid    varchar(100)  NOT NULL CONSTRAINT FK_urs_user REFERENCES sec.users(user_oid),
    region_code varchar(10)   NOT NULL CONSTRAINT CK_urs_code CHECK (region_code = '*' OR region_code NOT LIKE '%[^A-Z]%'),
    granted_by  nvarchar(128) NOT NULL CONSTRAINT DF_urs_granted_by DEFAULT SUSER_SNAME(),
    CONSTRAINT PK_user_region_scope PRIMARY KEY (user_oid, region_code),
    valid_from datetime2(0) GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    valid_to   datetime2(0) GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (valid_from, valid_to)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = sec_history.user_region_scope));
GO

------------------------------------------------------------------------------
-- Baseline permissions and roles (reviewed configuration, not demo data).
-- Inserted only if missing; existing rows (and admin edits to them) are left alone.
------------------------------------------------------------------------------
INSERT INTO sec.permissions (permission_code, description)
SELECT v.code, v.descr FROM (VALUES
    ('ar.read',          N'Accounts receivable reports: late payments, overdue invoices, aging, collections, customer balances.'),
    ('revenue.read',     N'Revenue reports.'),
    ('customer.read',    N'Customer search and profile (contacts always masked).'),
    ('bank.read_masked', N'Bank name and last 4 IBAN characters on the customer profile.')
) AS v(code, descr)
WHERE NOT EXISTS (SELECT 1 FROM sec.permissions p WHERE p.permission_code = v.code);

INSERT INTO sec.roles (role_code, name, description)
SELECT v.code, v.name, v.descr FROM (VALUES
    ('finance',       N'Finance',       N'AR, revenue and customer reports, including masked bank details.'),
    ('sales_manager', N'Sales manager', N'Revenue and customer reports. No AR.'),
    ('executive',     N'Executive',     N'All reports except bank details.')
) AS v(code, name, descr)
WHERE NOT EXISTS (SELECT 1 FROM sec.roles r WHERE r.role_code = v.code);

-- Baseline grants are added once per role, only when that role has no grants yet,
-- so an admin who deliberately revokes a permission does not get it back on re-run.
INSERT INTO sec.role_permissions (role_code, permission_code)
SELECT v.role_code, v.permission_code FROM (VALUES
    ('finance', 'ar.read'), ('finance', 'revenue.read'), ('finance', 'customer.read'), ('finance', 'bank.read_masked'),
    ('sales_manager', 'revenue.read'), ('sales_manager', 'customer.read'),
    ('executive', 'ar.read'), ('executive', 'revenue.read'), ('executive', 'customer.read')
) AS v(role_code, permission_code)
WHERE NOT EXISTS (SELECT 1 FROM sec.role_permissions rp WHERE rp.role_code = v.role_code);
GO

------------------------------------------------------------------------------
-- The gateway may read the security model, never change it.
-- (Administration is done by a security admin, outside the gateway.)
------------------------------------------------------------------------------
GRANT SELECT ON SCHEMA::sec TO ops_writer;
DENY INSERT, UPDATE, DELETE, ALTER ON SCHEMA::sec TO ops_writer;
DENY SELECT, INSERT, UPDATE, DELETE, ALTER ON SCHEMA::sec_history TO ops_writer;
GO

------------------------------------------------------------------------------
-- Read-only view for admins/auditors: effective access per user.
------------------------------------------------------------------------------
CREATE OR ALTER VIEW sec.effective_access_v AS
    SELECT u.user_oid, u.display_name, u.is_active,
           ur.role_code, r.is_active AS role_active, rp.permission_code
    FROM sec.users u
    LEFT JOIN sec.user_roles ur       ON ur.user_oid = u.user_oid
    LEFT JOIN sec.roles r             ON r.role_code = ur.role_code
    LEFT JOIN sec.role_permissions rp ON rp.role_code = ur.role_code;
GO
