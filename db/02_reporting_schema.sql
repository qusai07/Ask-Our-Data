-- ReportingDb: source tables live in [core] (never readable by the gateway),
-- the ONLY readable surface is the [rpt] schema of views.
USE AskData_Reporting;
GO
CREATE SCHEMA core;
GO
CREATE SCHEMA rpt;
GO
CREATE SCHEMA sec;
GO

------------------------------------------------------------------------------
-- core tables
------------------------------------------------------------------------------
CREATE TABLE core.regions (
    region_id   tinyint      NOT NULL CONSTRAINT PK_regions PRIMARY KEY,
    code        varchar(10)  NOT NULL CONSTRAINT UQ_regions_code UNIQUE,
    name        nvarchar(50) NOT NULL
);

CREATE TABLE core.segments (
    segment_id  tinyint      NOT NULL CONSTRAINT PK_segments PRIMARY KEY,
    code        varchar(20)  NOT NULL CONSTRAINT UQ_segments_code UNIQUE,
    name        nvarchar(50) NOT NULL
);

CREATE TABLE core.customers (
    customer_id         int            NOT NULL CONSTRAINT PK_customers PRIMARY KEY,
    customer_code       varchar(20)    NOT NULL CONSTRAINT UQ_customers_code UNIQUE,
    name                nvarchar(200)  NOT NULL,
    region_id           tinyint        NOT NULL CONSTRAINT FK_customers_region REFERENCES core.regions(region_id),
    segment_id          tinyint        NOT NULL CONSTRAINT FK_customers_segment REFERENCES core.segments(segment_id),
    payment_terms_days  smallint       NOT NULL,
    credit_limit        decimal(18,2)  NOT NULL,
    status              varchar(10)    NOT NULL CONSTRAINT CK_customers_status CHECK (status IN ('active','on_hold','closed')),
    created_at          date           NOT NULL
);
CREATE INDEX IX_customers_region ON core.customers(region_id) INCLUDE (name, segment_id);

-- SENSITIVE: personal contact data. Exposed only through a masking view.
CREATE TABLE core.customer_contacts (
    contact_id   int            NOT NULL IDENTITY CONSTRAINT PK_customer_contacts PRIMARY KEY,
    customer_id  int            NOT NULL CONSTRAINT FK_contacts_customer REFERENCES core.customers(customer_id),
    full_name    nvarchar(150)  NOT NULL,
    email        nvarchar(200)  NOT NULL,
    phone        varchar(40)    NOT NULL,
    is_primary   bit            NOT NULL
);
CREATE INDEX IX_contacts_customer ON core.customer_contacts(customer_id);

-- SENSITIVE: bank details. The full IBAN never leaves this table.
CREATE TABLE core.customer_bank_accounts (
    customer_id  int            NOT NULL CONSTRAINT PK_bank PRIMARY KEY
                                CONSTRAINT FK_bank_customer REFERENCES core.customers(customer_id),
    bank_name    nvarchar(100)  NOT NULL,
    iban         varchar(34)    NOT NULL,
    swift        varchar(11)    NOT NULL
);

CREATE TABLE core.invoices (
    invoice_id      int            NOT NULL CONSTRAINT PK_invoices PRIMARY KEY,
    invoice_number  varchar(20)    NOT NULL CONSTRAINT UQ_invoices_number UNIQUE,
    customer_id     int            NOT NULL CONSTRAINT FK_invoices_customer REFERENCES core.customers(customer_id),
    issue_date      date           NOT NULL,
    due_date        date           NOT NULL,
    amount          decimal(18,2)  NOT NULL CONSTRAINT CK_invoices_amount CHECK (amount > 0),
    status          varchar(10)    NOT NULL CONSTRAINT CK_invoices_status CHECK (status IN ('open','partial','paid','void','credited')),
    notes           nvarchar(500)  NULL,
    CONSTRAINT CK_invoices_dates CHECK (due_date >= issue_date)
);
CREATE INDEX IX_invoices_customer ON core.invoices(customer_id) INCLUDE (due_date, amount, status);
CREATE INDEX IX_invoices_due      ON core.invoices(due_date)    INCLUDE (customer_id, amount, status);
CREATE INDEX IX_invoices_issue    ON core.invoices(issue_date)  INCLUDE (customer_id, amount, status);

CREATE TABLE core.payments (
    payment_id    int            NOT NULL IDENTITY CONSTRAINT PK_payments PRIMARY KEY,
    invoice_id    int            NOT NULL CONSTRAINT FK_payments_invoice REFERENCES core.invoices(invoice_id),
    payment_date  date           NOT NULL,
    amount        decimal(18,2)  NOT NULL CONSTRAINT CK_payments_amount CHECK (amount > 0),
    method        varchar(20)    NOT NULL
);
CREATE INDEX IX_payments_invoice ON core.payments(invoice_id) INCLUDE (payment_date, amount);

CREATE TABLE core.fiscal_calendar (
    cal_date          date        NOT NULL CONSTRAINT PK_fiscal_calendar PRIMARY KEY,
    fiscal_year       smallint    NOT NULL,
    fiscal_quarter    tinyint     NOT NULL,
    fiscal_month      tinyint     NOT NULL,
    fiscal_year_label varchar(10) NOT NULL,
    quarter_label     varchar(12) NOT NULL
);

CREATE TABLE core.data_refresh_log (
    source        varchar(50) NOT NULL CONSTRAINT PK_data_refresh_log PRIMARY KEY,
    refreshed_at  datetime2(0) NOT NULL
);
GO

------------------------------------------------------------------------------
-- Row-level security (defence in depth). The gateway ALSO filters by region
-- in every query; this policy is the backstop if a query forgets.
-- Applies only to rpt_reader; fails closed when no scope is set.
-- Scope is a JSON array of region ids set read-only via sp_set_session_context.
------------------------------------------------------------------------------
CREATE FUNCTION sec.fn_region_predicate(@region_id tinyint)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
    SELECT 1 AS allowed
    WHERE DATABASE_PRINCIPAL_ID() <> ISNULL(DATABASE_PRINCIPAL_ID(N'rpt_reader'), -1)
       OR @region_id IN (
            SELECT TRY_CAST(j.[value] AS tinyint)
            FROM OPENJSON(CAST(SESSION_CONTEXT(N'region_scope') AS nvarchar(400))) AS j
          );
GO
CREATE SECURITY POLICY sec.region_scope_policy
    ADD FILTER PREDICATE sec.fn_region_predicate(region_id) ON core.customers
    WITH (STATE = ON);
GO

------------------------------------------------------------------------------
-- rpt views. Every customer-related view joins core.customers so the RLS
-- policy above applies to it.
------------------------------------------------------------------------------
CREATE VIEW rpt.region_v AS
    SELECT region_id, code AS region_code, name AS region_name FROM core.regions;
GO
CREATE VIEW rpt.segment_v AS
    SELECT segment_id, code AS segment_code, name AS segment_name FROM core.segments;
GO
CREATE VIEW rpt.customer_v AS
    SELECT c.customer_id, c.customer_code, c.name AS customer_name,
           c.region_id, r.code AS region_code, r.name AS region_name,
           c.segment_id, s.code AS segment_code,
           c.payment_terms_days, c.credit_limit, c.status, c.created_at
    FROM core.customers c
    JOIN core.regions  r ON r.region_id  = c.region_id
    JOIN core.segments s ON s.segment_id = c.segment_id;
GO
CREATE VIEW rpt.invoice_v AS
    SELECT i.invoice_id, i.invoice_number, i.customer_id, c.region_id, c.segment_id,
           i.issue_date, i.due_date, i.amount, i.status, i.notes
    FROM core.invoices i
    JOIN core.customers c ON c.customer_id = i.customer_id;
GO
CREATE VIEW rpt.payment_v AS
    SELECT p.payment_id, p.invoice_id, i.customer_id, c.region_id,
           p.payment_date, p.amount, p.method
    FROM core.payments p
    JOIN core.invoices  i ON i.invoice_id  = p.invoice_id
    JOIN core.customers c ON c.customer_id = i.customer_id;
GO
-- Masked at source: first name + last initial, j***@domain, last 4 phone digits.
CREATE VIEW rpt.customer_contact_v AS
    SELECT cc.contact_id, cc.customer_id, c.region_id, cc.is_primary,
           CASE WHEN CHARINDEX(N' ', cc.full_name) > 0
                THEN LEFT(cc.full_name, CHARINDEX(N' ', cc.full_name)) +
                     SUBSTRING(cc.full_name, CHARINDEX(N' ', cc.full_name) + 1, 1) + N'.'
                ELSE cc.full_name END                                            AS contact_name_masked,
           LEFT(cc.email, 1) + N'***' + SUBSTRING(cc.email, CHARINDEX(N'@', cc.email), 200) AS email_masked,
           '***-' + RIGHT(cc.phone, 4)                                           AS phone_masked
    FROM core.customer_contacts cc
    JOIN core.customers c ON c.customer_id = cc.customer_id;
GO
CREATE VIEW rpt.customer_bank_v AS
    SELECT b.customer_id, c.region_id, b.bank_name, RIGHT(b.iban, 4) AS iban_last4
    FROM core.customer_bank_accounts b
    JOIN core.customers c ON c.customer_id = b.customer_id;
GO
CREATE VIEW rpt.fiscal_calendar_v AS
    SELECT cal_date, fiscal_year, fiscal_quarter, fiscal_month, fiscal_year_label, quarter_label
    FROM core.fiscal_calendar;
GO
CREATE VIEW rpt.data_freshness_v AS
    SELECT source, refreshed_at FROM core.data_refresh_log;
GO
