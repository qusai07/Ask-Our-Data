-- OpsDb: audit trail, missing-report requests, and the security mapping
-- (who may see which regions). Never reachable from the model.
USE AskData_Ops;
GO
CREATE SCHEMA ops;
GO
CREATE SCHEMA sec;
GO

-- Security model (users, roles, permissions, region scope) lives in 05_security_model.sql.

-- One row per gateway tool call. Never stores result values; parameters are
-- stored after redaction (see catalog: parameters marked redact: true are hashed).
CREATE TABLE ops.audit_log (
    audit_id        bigint           NOT NULL IDENTITY CONSTRAINT PK_audit_log PRIMARY KEY,
    request_id      uniqueidentifier NOT NULL,
    conversation_id varchar(64)      NULL,
    ts_utc          datetime2(3)     NOT NULL CONSTRAINT DF_audit_ts DEFAULT SYSUTCDATETIME(),
    user_oid        varchar(100)     NOT NULL,
    tool            varchar(80)      NOT NULL,
    params_redacted nvarchar(2000)   NULL,
    row_count       int              NULL,
    truncated       bit              NULL,
    latency_ms      int              NOT NULL,
    outcome         varchar(40)      NOT NULL,   -- ok | invalid_argument | permission_denied | not_found_or_out_of_scope | timeout | ...
    detail          nvarchar(300)    NULL
);
CREATE INDEX IX_audit_user_ts ON ops.audit_log(user_oid, ts_utc);

-- Agent-level outcome per question (written by the agent host via the gateway, later phase).
CREATE TABLE ops.missing_report (
    missing_id        bigint           NOT NULL IDENTITY CONSTRAINT PK_missing_report PRIMARY KEY,
    conversation_id   varchar(64)      NULL,
    ts_utc            datetime2(3)     NOT NULL CONSTRAINT DF_missing_ts DEFAULT SYSUTCDATETIME(),
    user_oid          varchar(100)     NOT NULL,
    question_redacted nvarchar(1000)   NOT NULL,
    nearest_tool      varchar(80)      NULL
);
GO
