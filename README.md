# Ask our data

Managers ask a question in plain language and get a table plus a short explanation. The model
**only** calls approved, read-only, parameterized report tools. It never writes SQL.

See [docs/design.md](docs/design.md) for the trust boundaries.

## Status

| Part | State |
|---|---|
| Data model, `rpt` views, RLS, least-privilege DB users | Done |
| Deterministic seed (300 customers, about 16k invoices, injection probes) | Done |
| Tool catalog: 8 tools (YAML + SQL) | Done |
| Query Gateway: auth, RBAC, validation, scope, masking, caps, audit | Done, 93 tests |
| Agent host (Microsoft Agent Framework + local Ollama model) | Next |
| Eval suite (30+ golden, ambiguity, out-of-scope, permission, adversarial) | Next |
| 5-minute demo script | With the agent |

## Prerequisites

- .NET 10 SDK
- A local SQL Server 2022 instance with Windows auth (default instance `.`)
- `sqlcmd`
- Ollama with `qwen2.5:7b`, for the agent phase

## Setup

```powershell
.\scripts\setup-db.ps1 -Server "DESKTOP-N0VO2G5\SQL22"   # drop/create both DBs, schema, security model, seed
                                  # (use the same server as Gateway:*ConnectionString; tests read it from there)
dotnet test                       # unit + integration tests (DB tests skip if SQL Server is unavailable)
dotnet run --project src/AskOurData.Gateway --launch-profile http
```

## Try the gateway

Demo users (Development only). A dev token carries **identity only**. Roles, permissions, region scope
and the active flag come from the `AskData_Ops.sec.*` tables.

| User | Role (sec.user_roles) | Regions (sec.user_region_scope) | Notes |
|---|---|---|---|
| fiona | finance | all | |
| rita | finance | APAC | |
| sam | sales_manager | EMEA | |
| erin | executive | all | |
| nora | none | none | Registered, but has no roles |
| ian | finance | all | `is_active = 0`, so denied everything |
| ghost | none | none | Valid token, not in `sec.users`, so denied everything |
| tess | none | none | Reserved for tests |

## Managing access

Run these as a security admin. The gateway's own DB identity can read `sec.*` but never write it. Changes
take effect on the user's next request, with no new token needed. All `sec` tables are temporal, so the
full history is kept in `sec_history.*`.

```sql
USE AskData_Ops;
-- Register a user (oid = Entra object id from the token)
INSERT INTO sec.users (user_oid, display_name, upn) VALUES ('<oid>', N'Jane Doe', N'jane@contoso.com');
-- Assign a role and a region scope ('*' = all regions)
INSERT INTO sec.user_roles (user_oid, role_code) VALUES ('<oid>', 'sales_manager');
INSERT INTO sec.user_region_scope (user_oid, region_code) VALUES ('<oid>', 'EMEA');
-- Lock out immediately
UPDATE sec.users SET is_active = 0 WHERE user_oid = '<oid>';
-- Who can do what
SELECT * FROM sec.effective_access_v ORDER BY user_oid;
-- Who had which role last month
SELECT * FROM sec.user_roles FOR SYSTEM_TIME AS OF '2026-10-01' WHERE user_oid = '<oid>';
```

Permissions are tied to the catalog: a tool's `permission:` must exist in `sec.permissions`, or the
gateway refuses to start. Roles and role-permission grants are baseline config in `db/05_security_model.sql`.

```powershell
$base = "http://localhost:5247"   # see launchSettings.json
$t = (Invoke-RestMethod -Method Post "$base/dev/token/fiona").access_token
$h = @{ Authorization = "Bearer $t" }
Invoke-RestMethod "$base/catalog" -Headers $h
Invoke-RestMethod -Method Post "$base/tools/get_top_customers_by_late_payments" -Headers $h `
  -ContentType application/json -Body '{"period":"this_quarter","metric":"amount","limit":5}'
```

"Today" is pinned to **2026-11-06** in Development (`Gateway:FixedToday`), so answers are reproducible.

## Layout

```
catalog/            reviewed source of truth: tools/*.yaml, queries/*.sql, definitions.yaml
db/                 schema, RLS, permissions
src/AskOurData.Contracts   result envelope, fiscal calendar
src/AskOurData.Gateway     the only component with DB access
src/AskOurData.Seed        deterministic fake data
tests/                     unit + integration tests
```

## Adding a report

1. Add `catalog/queries/<name>.sql`. It must be a single `SELECT`/`WITH` over `rpt.*` only, filter
   `@region_filter`, and cap rows with `TOP (@limit_plus_one)`.
2. Add `catalog/tools/<name>.yaml` with typed parameters, the permission, definitions and output columns.
3. The gateway refuses to start if the catalog or the SQL breaks any rule (see `ToolCatalog.Check` / `LintSql`).
