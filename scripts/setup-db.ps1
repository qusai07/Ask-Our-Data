# Recreates AskData_Reporting + AskData_Ops on a local SQL Server and loads the deterministic seed.
# Usage: .\scripts\setup-db.ps1 [-Server .]
param([string]$Server = ".")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

foreach ($f in "01_create_databases.sql", "02_reporting_schema.sql", "03_ops_schema.sql", "04_permissions.sql", "05_security_model.sql") {
    Write-Host "== $f"
    sqlcmd -S $Server -E -b -I -i (Join-Path $root "db\$f")
    if ($LASTEXITCODE -ne 0) { throw "Failed: $f" }
}

$reporting = "Server=$Server;Database=AskData_Reporting;Integrated Security=true;TrustServerCertificate=true"
$ops = "Server=$Server;Database=AskData_Ops;Integrated Security=true;TrustServerCertificate=true"
dotnet run --project (Join-Path $root "src\AskOurData.Seed") -- --reporting $reporting --ops $ops
if ($LASTEXITCODE -ne 0) { throw "Seed failed" }
