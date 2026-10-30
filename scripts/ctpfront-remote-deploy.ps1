# ── Aether CTP 前置·单独部署脚本（在服务器上执行）──────────────────────────────────
# 由 scripts/deploy-from-local.sh front 经 SSH 调起：
#   powershell -NoProfile -ExecutionPolicy Bypass -File C:/Aether/ctpfront-tmp/ctpfront-remote-deploy.ps1
#
# 为什么单独有一个：ctpsim-remote-deploy.ps1 会**连交易所一起重启**（撮合中断数十秒），
# 而只改前置（帧模板/会话语义/对账）时交易所二进制根本没变——没必要把市场和 bot 一起打断。
# 本脚本只做前置那半边，其余步骤与 ctpsim 脚本同源同形：
#   preflight → 干净解包 → 停前置 → 清占用 → robocopy /MIR → 钉 binPath → 起前置 → 回读监听。
#
# 前提：ctpsim 脚本至少跑过一次（两个服务已注册、env 已钉定）。本脚本**不改**服务环境变量——
# 上游地址 AETHER_CTP_EXCHANGE_ADDR 由 ctpsim 部署钉定，这里改了反而会和交易所监听漂移。
#
# 兼容 Windows PowerShell 5.1（无 ?? / 三元 / &&）。

[CmdletBinding()]
param(
    [string]$FrontService = 'AetherCtpFront',
    [string]$FrontDir = 'C:\Aether\CtpFront',
    [string]$StagingDir = 'C:\Aether\ctpfront-tmp',
    [string]$TarName = 'ctpfront.tar.gz',
    [string]$DataDir = 'C:\Aether\ctpsim-data'
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

function Fail([string]$msg) {
    [Console]::Error.WriteLine("[CTPFRONT-DEPLOY] $msg")
    exit 1
}
function Info([string]$msg) { Write-Host "[CTPFRONT-DEPLOY] $msg" }

function DumpStartFailure([string]$svc, [string]$logPath) {
    if (Test-Path $logPath) {
        Write-Host "[CTPFRONT-DEPLOY] ── $logPath 尾部 30 行 ──"
        Get-Content $logPath -Tail 30 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $_" }
    }
    else {
        Write-Host "[CTPFRONT-DEPLOY] （日志文件不存在：$logPath）"
    }
    try {
        $events = Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Service Control Manager' } -MaxEvents 200 -ErrorAction SilentlyContinue |
            Where-Object { $_.Message -match $svc } | Select-Object -First 5
        if ($events) {
            Write-Host "[CTPFRONT-DEPLOY] ── SCM 事件（最近 5 条与 $svc 相关）──"
            foreach ($e in $events) { Write-Host ("  {0}  {1}" -f $e.TimeCreated, $e.Message) }
        }
    } catch { }
}

# 1) Preflight
if (-not (Get-Service -Name $FrontService -ErrorAction SilentlyContinue)) {
    Fail "服务 $FrontService 不存在——请先跑一次完整部署（scripts/deploy-from-local.sh ctpsim）。"
}
$exch = Get-Service -Name 'AetherCtpExchange' -ErrorAction SilentlyContinue
if (-not $exch -or $exch.Status -ne 'Running') {
    Fail "交易所服务不在 Running（前置起来也连不上）——先确认交易所，再部署前置。"
}

# 2) 干净解包（tar 不清旧文件，必须删 out）
$tar = Join-Path $StagingDir $TarName
$out = Join-Path $StagingDir 'out'
if (-not (Test-Path $tar)) { Fail "staging 缺少 $tar" }
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Path $out -Force | Out-Null
& tar.exe -xzf $tar -C $out
if ($LASTEXITCODE -ne 0) { Fail "解包 $tar 失败（exit $LASTEXITCODE）" }
$src = Join-Path $out 'front'
if (-not (Test-Path $src)) { Fail "解包产物缺 front\ 子目录" }

# 3) 停前置（**不动交易所**——bot 的行情/撮合不受影响）
try { Stop-Service -Name $FrontService -ErrorAction SilentlyContinue } catch { }
$deadline = [DateTime]::Now.AddSeconds(30)
while ((Get-Service -Name $FrontService).Status -ne 'Stopped' -and [DateTime]::Now -lt $deadline) {
    Start-Sleep -Milliseconds 500
}
if ((Get-Service -Name $FrontService).Status -ne 'Stopped') {
    Fail "停前置超时（30s）——二进制未动，仍在跑旧版本，安全（手工排查后重跑）"
}

# 3.5) 清占用：SCM 报 Stopped 后仍可能有孤儿进程压着文件（只认映像路径落在部署目录内的）
function Get-ProcessInDir([string]$dir) {
    if (-not (Test-Path $dir)) { return @() }
    $full = (Resolve-Path $dir).Path.TrimEnd('\') + '\'
    $hits = @()
    foreach ($p in (Get-Process -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $p.Path } catch { }
        if ($path -and $path.StartsWith($full, [StringComparison]::OrdinalIgnoreCase)) { $hits += $p }
    }
    $hits
}
$procs = @(Get-ProcessInDir $FrontDir)
if ($procs.Count -gt 0) {
    $names = ($procs | ForEach-Object { "$($_.ProcessName)($($_.Id))" }) -join ', '
    Info "前置目录仍有进程占用（$names）——按孤儿进程处理：结束"
    foreach ($p in $procs) { try { $p.Kill() } catch { } }
    $deadline = [DateTime]::Now.AddSeconds(15)
    while ([DateTime]::Now -lt $deadline) {
        if (@(Get-ProcessInDir $FrontDir).Count -eq 0) { break }
        Start-Sleep -Milliseconds 300
    }
    if (@(Get-ProcessInDir $FrontDir).Count -gt 0) { Fail "前置目录仍有进程占用，15s 内未退出" }
}

# 4) 换文件（robocopy 0-7 都是成功）
for ($i = 1; $i -le 3; $i++) {
    robocopy $src $FrontDir /MIR /R:2 /W:2 /NFL /NDL /NJH /NJS
    if ($LASTEXITCODE -lt 8) { break }
    Info "robocopy 第 $i 次失败（code $LASTEXITCODE）——清占用后重试"
    foreach ($p in @(Get-ProcessInDir $FrontDir)) { try { $p.Kill() } catch { } }
    Start-Sleep -Seconds 2
    if ($i -eq 3) { Fail "robocopy 前置失败（code $LASTEXITCODE，已重试 3 次）" }
}

# 4.5) binPath 自愈（幂等；拆仓后宿主 exe 改名过）
sc.exe config $FrontService binPath= (Join-Path $FrontDir 'Aether.CtpFront.Host.exe') | Out-Null
Info "前置 ImagePath 已校正 → $FrontDir\Aether.CtpFront.Host.exe"

# 5) 起前置并等 Running
try { Start-Service -Name $FrontService } catch { }
$deadline = [DateTime]::Now.AddSeconds(60)
while ((Get-Service -Name $FrontService).Status -ne 'Running' -and [DateTime]::Now -lt $deadline) {
    Start-Sleep -Milliseconds 500
}
if ((Get-Service -Name $FrontService).Status -ne 'Running') {
    DumpStartFailure $FrontService (Join-Path $DataDir 'ctpsim-front.log')
    Fail "前置未能启动（见上方日志尾部与 SCM 事件——交易所仍在跑，修复后重跑即可）"
}

# 6) 汇报 + 回读（Running ≠ 端口可用）
$version = Get-Content (Join-Path $FrontDir 'VERSION') -ErrorAction SilentlyContinue
$dll = Get-Item (Join-Path $FrontDir 'ctp-net-front.dll') -ErrorAction SilentlyContinue
$proc = Get-Process 'Aether.CtpFront.Host' -ErrorAction SilentlyContinue
Write-Host "[CTPFRONT-DEPLOY] ==> $FrontService 已部署并运行（版本 $version）"
if ($dll) { Write-Host "[CTPFRONT-DEPLOY]     dll 时间: $($dll.LastWriteTime)   进程启动: $($proc.StartTime)" }
else { Write-Host "[CTPFRONT-DEPLOY]     ⚠ 部署目录里没有 ctp-net-front.dll——检查 publish 产物" }
if ($proc -and $dll -and $proc.StartTime -lt $dll.LastWriteTime) {
    Write-Host "[CTPFRONT-DEPLOY]     ⚠ 进程启动早于 dll 时间——可能仍在跑旧版本，重跑一次部署"
}
$listen = (netstat -ano | findstr ':17801' | Select-String 'LISTENING' | Select-Object -First 1)
if ($listen) { Write-Host "[CTPFRONT-DEPLOY]     监听: $($listen.Line.Trim())" }
else { Write-Host "[CTPFRONT-DEPLOY]     ⚠ 未见 :17801 LISTENING —— 看 ctpsim-front.log" }
exit 0
