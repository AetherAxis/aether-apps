param([string]$Balance = '1000000', [switch]$DryRun)
# Venue reset (2026-10-29, authorized by the operator).
# WHY: the sim venue's stored state was corrupt -- 5.4e4 zombie GFD buy orders (106k lots) left behind by
# cancels that were lost when the CTP front force-closed the trader socket every 240 pushes, plus two bot
# accounts stuck deep short (botmm -9226, botnoise -2030). Nobody could sell, so no trade could ever happen
# and the last price was frozen. A fresh venue is the only way back to a market that actually trades.
# WHAT IS LOST: sim orders / positions / trades / account rows (all experimental artifacts).
# WHAT IS KEPT:    everything else on the box; the old DB is backed up first (db + wal + shm).
# SAFETY: refuses to delete unless the backup is at least 100 KB.
$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
function I($m) { Write-Output "[reset] $m" }
function PortOpen([int]$p) {
    try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', $p); $c.Close(); return $true }
    catch { return $false }
}

$dbPath = 'C:\Aether\ctpsim-data\venue\ctp-sim.db'
$dbDir  = 'C:\Aether\ctpsim-data\venue'
$stamp  = Get-Date -Format 'yyyyMMdd-HHmmss'
$bakDir = Join-Path $dbDir "backup-$stamp"

# 1) exchange service env: add AETHER_CTP_SIM_BALANCE so NEW accounts are funded (default is 0).
#    Existing entries are carried over verbatim -- the list also holds the ledger seed; we never print it.
$key = 'HKLM:\SYSTEM\CurrentControlSet\Services\AetherCtpExchange'
$cur = (Get-ItemProperty -Path $key).Environment
if ($null -eq $cur) { $cur = @() }
$new = New-Object System.Collections.Generic.List[string]
$has = $false
foreach ($e in $cur) {
    if ($e -like 'AETHER_CTP_SIM_BALANCE=*') { $new.Add("AETHER_CTP_SIM_BALANCE=$Balance"); $has = $true }
    else { $new.Add([string]$e) }
}
if (-not $has) { $new.Add("AETHER_CTP_SIM_BALANCE=$Balance") }
if ($DryRun) { I ("would write {0} env entries (balance {1})" -f $new.Count, $Balance) }
else {
    Set-ItemProperty -Path $key -Name Environment -Value ([string[]]$new.ToArray())
    $check = (Get-ItemProperty -Path $key).Environment
    I ("service env rewritten: {0} entries, AETHER_CTP_SIM_BALANCE present = {1}" -f `
        $check.Count, [bool]($check -like 'AETHER_CTP_SIM_BALANCE=*'))
}

# 2) stop the direct bots (they would reconnect to a half-reset venue otherwise)
foreach ($n in @('botmm', 'botnoise')) {
    Stop-ScheduledTask -TaskName "AetherBotDirect-$n" -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 1
Get-CimInstance Win32_Process -Filter "Name='Aether.DirectBot.Console.exe'" -ErrorAction SilentlyContinue |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
I 'direct bots stopped'

# 3) stop front, then exchange (front holds a session to the exchange)
if (-not $DryRun) {
    Stop-Service -Name AetherCtpFront -Force -ErrorAction SilentlyContinue
    Stop-Service -Name AetherCtpExchange -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 4
    I ("stopped: exchange={0} front={1}" -f (Get-Service AetherCtpExchange).Status, (Get-Service AetherCtpFront).Status)
}

# 4) backup all three SQLite files, verify size, then remove so the venue starts empty
New-Item -ItemType Directory -Force $bakDir | Out-Null
foreach ($f in @($dbPath, "$dbPath-wal", "$dbPath-shm")) {
    if (Test-Path $f) { Copy-Item $f (Join-Path $bakDir (Split-Path $f -Leaf)) -Force }
}
$bakBytes = (Get-ChildItem $bakDir | Measure-Object -Property Length -Sum).Sum
I ("backup -> {0} ({1:N0} bytes)" -f $bakDir, $bakBytes)
if ($bakBytes -lt 100000) { throw "backup is only $bakBytes bytes -- refusing to delete the live DB" }
if ($DryRun) { I 'dry run: would delete db/-wal/-shm now'; return }
foreach ($f in @($dbPath, "$dbPath-wal", "$dbPath-shm")) { Remove-Item $f -Force -ErrorAction SilentlyContinue }
I 'venue DB removed (fresh start)'

# 5) start exchange, wait for the listener
Start-Service -Name AetherCtpExchange
$up = $false
for ($i = 0; $i -lt 45; $i++) {
    Start-Sleep -Seconds 2
    if (PortOpen 17800) { $up = $true; break }
}
I ("exchange listening on 17800 = {0}" -f $up)
if (-not $up) { throw 'exchange did not listen on 17800 after 90s -- check C:\Aether\ctpsim-data\ctpsim-exchange.log' }

# 6) start front
Start-Service -Name AetherCtpFront
Start-Sleep -Seconds 5
I ("front listening on 17801 = {0}" -f (PortOpen 17801))

# 7) start the bots, clear stale heartbeats first so we read fresh ones
$dirs = @{ 'botmm' = 'C:\Aether\direct-bot\botmm'; 'botnoise' = 'C:\Aether\direct-bot\botnoise' }
foreach ($n in @('botmm', 'botnoise')) { Remove-Item (Join-Path $dirs[$n] 'directbot.heartbeat.json') -Force -ErrorAction SilentlyContinue }
foreach ($n in @('botmm', 'botnoise')) { Start-ScheduledTask -TaskName "AetherBotDirect-$n" }

# 8) wait for heartbeats and report counters
foreach ($n in @('botmm', 'botnoise')) {
    $hb = Join-Path $dirs[$n] 'directbot.heartbeat.json'
    $deadline = (Get-Date).AddSeconds(150)
    $got = $null
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $hb) { try { $got = Get-Content $hb -Raw -Encoding UTF8 | ConvertFrom-Json; break } catch { } }
        Start-Sleep -Seconds 2
    }
    if ($got) { I ("{0}: account={1} engine={2} ticks={3} orders={4} trades={5}" -f $n, $got.account, $got.engine, $got.ticks, $got.orders, $got.trades) }
    else { I "${n}: NO heartbeat within 150s" }
}
I 'done'
