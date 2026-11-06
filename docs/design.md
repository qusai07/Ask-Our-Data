# Ask our data: design and trust boundaries

## Core rule

The model never writes or runs SQL. It can only pick a tool from a reviewed catalog and fill in its arguments.
The **Query Gateway** treats those arguments as hostile input. It validates them, adds the caller's identity
and data scope itself, and runs a fixed parameterized query.

## Components

| Component | Process | Holds DB credentials | Trusts |
|---|---|---|---|
| Agent Host (`AskOurData.Agent`, later phase) | 1 | **No** | The user's token; nothing from the model |
| Query Gateway (`AskOurData.Gateway`) | 2 | Yes, least-privilege | The bearer token only |
| ReportingDb (`AskData_Reporting`) | SQL | n/a | `rpt_reader`: SELECT on `rpt.*` views only |
| OpsDb (`AskData_Ops`) | SQL | n/a | `ops_writer`: INSERT into the audit tables, SELECT on the `sec` security model |

## Trust boundaries

```
 ┌──────────┐  Entra ID  ┌─────────────────────────────── TB1: authN ───────────────────────────────┐
 │ Manager  │──token────►│  AGENT HOST                                                               │
 │ (web UI) │◄─table+txt─│   • renders TABLE from tool JSON (model never retypes numbers)            │
 └──────────┘            │   • numeric grounding check on the explanation                           │
                         │   • logs outcome / missing-report (model has no write tool)               │
                         │        │  ▲ tool_call{name,args}  ◄── TB2: model output is UNTRUSTED     │
                         │   ┌────▼──┴────┐                                                          │
                         │   │ local LLM  │  tool results wrapped as untrusted data ◄── TB5          │
                         │   └────────────┘                                                          │
                         └────────┬──────────────────────────────────────────────────────────────────┘
                                  │ POST /tools/{name}  body = model args ONLY, Authorization = user token
                ══════════════════╪════════════ TB3: gateway trusts nothing but the token ════════════
                         ┌────────▼──────────────────────────────────────────────────────────────────┐
                         │  QUERY GATEWAY                                                             │
                         │   tool allowlist → RBAC → strict validation → period resolution →          │
                         │   region scope (server-injected) → customer scope → fixed SQL →           │
                         │   column masking → row/size caps → server totals → audit (fail-closed)    │
                         └────────┬───────────────────────────────────┬──────────────────────────────┘
                ══════════════════╪═══ TB4: DB permissions ═══════════╪═══════════════════════════════
                         ┌────────▼────────────┐            ┌─────────▼─────────────┐
                         │ ReportingDb          │            │ OpsDb                 │
                         │ rpt_reader: rpt.*    │            │ ops_writer: INSERT    │
                         │ RLS on region via    │            │ audit (append-only)   │
                         │ SESSION_CONTEXT      │            │ + scope mapping read  │
                         └──────────────────────┘            └───────────────────────┘
```

| Boundary | Guarantee | Enforced by |
|---|---|---|
| TB1 | Identity comes only from a validated token. | JwtBearer: Entra in production, an HS256 dev issuer in Development only |
| TB2 | A tool call is a request, not a command. | The gateway re-checks everything; the agent host forwards arguments as they are |
| TB3 | No parameter can choose identity, role or scope. | Catalog check: forbidden parameter names, `additionalProperties: false` |
| TB4 | Even buggy SQL can't read `core.*`, write anything, or see another region. | DB grants + DENY, RLS policy, startup self-check |
| TB5 | Data can't become instructions. | Untrusted columns flagged, system prompt, no write/send/export tools |

## Agent host safeguards (`AskOurData.Web`)

The agent host sits on the untrusted side of TB3. It has no database credentials, and every rule below
is a usability or correctness aid. None of them is a security control the gateway relies on.

| Safeguard | What it does |
|---|---|
| Tools come from the gateway | The model's tools are built from `GET /catalog`, so it only sees tools for the user's role. Reports the user may not run are offered as stubs; calling one makes a real gateway call that returns **403 and is audited**, so "not available for your role" is decided by the server. |
| Customer by name | Reports that take `customer_id` also accept `customer_name`. The host resolves it through the gateway's `find_customer`, using the same token and scope. One match is used; several produce a clarifying question with the names; none produces "not found". An id the model invents (one not seen in a result or typed by the user) is refused before reaching the gateway. |
| Untrusted data | The model sees results inside `<tool_result>` tags, JSON-encoded so that `<`, `>` and quotes are escaped. Planted text such as `</tool_result> SYSTEM: …` can't close the wrapper. Untrusted columns are listed by name. |
| Table rendered by the host | The table, totals, period, scope, freshness and definitions shown to the user come straight from the gateway result, never from model text. |
| Grounding check | Every number in the explanation must appear in a tool result or in the question. Rounding and K/M abbreviations are accepted; sums and percentages the model works out itself are not. If a number fails, the host does one rewrite pass with no tools; if it still fails, the explanation is withheld and only the table is shown. |
| No write tools | The only side effect is the missing-report log, which the host writes through the gateway, never the model. CSV and copy happen in the browser from the gateway result, with formula injection neutralized. |
| Limits | At most 6 tool calls per question, the last 4 turns of history (questions and parameters only, not data), temperature 0, and a Stop button. |

## Authorization model (OpsDb `sec` schema)

```
sec.users ──< sec.user_roles >── sec.roles ──< sec.role_permissions >── sec.permissions
    └──< sec.user_region_scope        (region_code or '*')
```

- **The token proves identity only** (`oid`). Any `roles` claim is ignored.
- **Users:** a user who is missing from `sec.users`, or has `is_active = 0`, is denied every call, including
  the catalog. The denial is audited.
- **Roles:** a role with `is_active = 0` grants nothing.
- **Permissions:** these are code-coupled. At startup the gateway checks that every permission used by a tool
  or a gated column exists in `sec.permissions`.
- **Region codes:** they can't have a foreign key to ReportingDb, so they are matched against the region
  allowlist. Unknown codes grant nothing.
- **Gateway identity:** `ops_writer` has SELECT only on `sec`. The startup check refuses to run if it can
  insert or update anything there.
- **History:** every `sec` table is system-versioned, so `FOR SYSTEM_TIME AS OF` answers "who had access on
  date X". `granted_by` defaults to the admin's login.
- **Effect of changes:** access is resolved on every request, so a grant or revoke applies to the next call
  without a new token.

## Defence in depth for region scope

1. **Gateway:** the effective regions are the user's scope, or the requested region if it's inside that
   scope. Asking for a region outside it returns `403`.
2. **Query:** every fixed query filters on `@region_filter`. The catalog check rejects any query that doesn't.
3. **Database:** an RLS filter predicate on `core.customers` keyed on a read-only
   `SESSION_CONTEXT('region_scope')`. It fails closed: no scope means no rows. Every `rpt` view joins
   `core.customers`, so invoices, payments, contacts and bank rows inherit the filter.

## Database identity

- **Production:** `rpt_reader` and `ops_writer` map to dedicated logins or managed identities.
- **Local dev (this SQL Server allows only Windows auth):** both are users without a login. On every connection
  the gateway runs `EXECUTE AS USER = '<user>' WITH NO REVERT` as an ad-hoc batch, on a **non-pooled**
  connection, so the session can never return to the service account's rights.
- **Both:** at startup the gateway proves its effective permissions. If it is db_owner, a data writer, can
  read `core.*` or bank accounts, can insert into `rpt`, or can create tables, it refuses to start. The same
  applies if the ops identity can update or delete the audit log. This is covered by `StartupSafetyTests`.

## Validation policy (server-side)

- Unknown, duplicate or mistyped parameters are rejected. `"5"` is not an integer, and `5.0` isn't either.
- Values are rejected, never clamped. `limit=1000000` returns `400` and is audited.
- Named periods are resolved on the server with the fiscal calendar (fiscal year starts 1 April, named by the
  year it ends in) and the company time zone. Periods that include today are cut at today and labelled
  "(to date)".
- Custom ranges: at most 24 months, no future dates, nothing before 2020-01-01.
- LIKE wildcards in search text are escaped. Control characters are rejected.
- Error messages never echo raw input. Unknown parameter names are stripped to `[A-Za-z0-9_]`.

## Correctness

- Every result carries the resolved period, the business definitions (verbatim from `catalog/definitions.yaml`),
  the scope applied, data freshness, and totals computed by the server.
- `GatewayApiTests.Top_late_customers_matches_an_independent_recomputation` recomputes the late-payments
  ranking in C# from the raw tables and compares it with the fixed SQL.

## Audit

`ops.audit_log` gets one row per tool call: user, tool, parameters (redactable per parameter), row count,
whether it was truncated, latency, outcome and a safe detail. No result values are ever logged. If the audit
row can't be written, the caller gets an error instead of data.
