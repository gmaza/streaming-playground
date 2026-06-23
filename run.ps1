<#
  Demo runner for WINDOWS using PODMAN.
  (macOS / Linux + Docker: use run.sh instead.)

    .\run.ps1          -> start broker + both apps, send a sample update, tail logs
    .\run.ps1 stop     -> stop the apps and the broker

  Ctrl+C stops the two .NET apps (the broker keeps running so you can keep playing;
  use '.\run.ps1 stop' to tear the broker down too).

  If PowerShell blocks the script, run once:
    Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
#>
param([string]$Command = "start")

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$Api      = "http://localhost:5080"
$Ui       = "http://localhost:15672"
$NotifLog = Join-Path $env:TEMP "streams-notif.log"
$CustLog  = Join-Path $env:TEMP "streams-cust.log"
$PidFile  = Join-Path $env:TEMP "streams-demo-pids.txt"

# podman has a 'compose' subcommand on newer installs; some only ship the
# standalone 'podman-compose'. Use whichever is available.
function Invoke-Compose {
    param([Parameter(Mandatory = $true)][string[]]$ComposeArgs)
    & podman compose version *> $null
    if ($LASTEXITCODE -eq 0) { & podman compose @ComposeArgs }
    elseif (Get-Command podman-compose -ErrorAction SilentlyContinue) { & podman-compose @ComposeArgs }
    else { throw "Need 'podman compose' or 'podman-compose' on PATH." }
}

function Stop-DemoApps {
    if (Test-Path $PidFile) {
        foreach ($procId in Get-Content $PidFile) {
            $procId = $procId.Trim()
            # taskkill /T also kills the child app process that 'dotnet run' spawns.
            if ($procId) { & taskkill /PID $procId /T /F *> $null }
        }
        Remove-Item $PidFile -ErrorAction SilentlyContinue
    }
}

# --- stop mode --------------------------------------------------------------
if ($Command -eq "stop") {
    Write-Host "Stopping apps..."
    Stop-DemoApps
    Write-Host "Stopping broker..."
    Push-Location docker; try { Invoke-Compose @("down") } finally { Pop-Location }
    Write-Host "Done."
    return
}

# 1. Broker -------------------------------------------------------------------
Write-Host "==> Starting RabbitMQ (streams) with podman..."
Push-Location docker; try { Invoke-Compose @("up", "-d") } finally { Pop-Location }

# Readiness: ask the broker itself (engine-agnostic).
Write-Host -NoNewline "==> Waiting for broker"
for ($i = 0; $i -lt 60; $i++) {
    & podman exec rabbitmq-streams rabbitmq-diagnostics -q check_running *> $null
    $r1 = $LASTEXITCODE
    & podman exec rabbitmq-streams rabbitmq-diagnostics -q check_port_connectivity *> $null
    $r2 = $LASTEXITCODE
    if ($r1 -eq 0 -and $r2 -eq 0) { Write-Host " - ready"; break }
    Write-Host -NoNewline "."; Start-Sleep -Seconds 2
}

# 2. Consumer (declares the stream + waits) ----------------------------------
Write-Host "==> Starting Notification Service (consumer)..."
$notif = Start-Process dotnet `
    -ArgumentList "run", "--project", "notification-service/NotificationService" `
    -RedirectStandardOutput $NotifLog -RedirectStandardError "$NotifLog.err" `
    -WindowStyle Hidden -PassThru

# 3. Publisher API ------------------------------------------------------------
Write-Host "==> Starting Customer Service (publisher API)..."
$cust = Start-Process dotnet `
    -ArgumentList "run", "--project", "customer-service/CustomerService" `
    -RedirectStandardOutput $CustLog -RedirectStandardError "$CustLog.err" `
    -WindowStyle Hidden -PassThru

# Remember the PIDs so 'stop' / Ctrl+C can tear the apps down.
Set-Content -Path $PidFile -Value @($notif.Id, $cust.Id)

Write-Host -NoNewline "==> Waiting for the API"
for ($i = 0; $i -lt 40; $i++) {
    try {
        Invoke-WebRequest "$Api/customers" -UseBasicParsing -TimeoutSec 3 *> $null
        Write-Host " - ready"; break
    } catch { Write-Host -NoNewline "."; Start-Sleep -Seconds 2 }
}

# 4. Sample update ------------------------------------------------------------
Write-Host "==> Sending a sample email change for C-001..."
$body = '{"fullName":"Ada Lovelace","email":"ada.changed@example.com","phoneNumber":"+1-555-0100"}'
Invoke-RestMethod -Method Put -Uri "$Api/customers/C-001" -ContentType "application/json" -Body $body | Out-Null

Write-Host @"

------------------------------------------------------------------
Everything is running (engine: podman).

  Publisher API : $Api   (PUT /customers/{id} to trigger events)
  Management UI : $Ui   (user: app  /  pass: app-pass)
                  -> "Streams" tab -> customer.events

Try more updates, e.g. (PowerShell):
  Invoke-RestMethod -Method Put -Uri $Api/customers/C-002 ``
    -ContentType 'application/json' ``
    -Body '{"fullName":"Alan Turing","email":"alan@example.com","phoneNumber":"+1-555-9999"}'

Tailing the CONSUMER log below (Ctrl+C to stop the apps)...
------------------------------------------------------------------

"@

try {
    Get-Content $NotifLog -Wait
} finally {
    # On Ctrl+C, mirror run.sh: stop the apps, leave the broker up.
    Write-Host "`nStopping apps (broker stays up; run '.\run.ps1 stop' to remove it)..."
    Stop-DemoApps
}
