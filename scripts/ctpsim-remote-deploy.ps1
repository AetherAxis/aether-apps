# ── Aether CTP 仿真栈·远程部署脚本（在服务器上执行）──────────────────────────────────
# 由 .github/workflows/deploy-ctpsim.yml 经 SSH 调起：
#   powershell -NoProfile -ExecutionPolicy Bypass -File C:/Aether/ctpsim-tmp/ctpsim-remote-deploy.ps1
# 两服务形态（2026-10-05 进程分离）：AetherCtpExchange（交易所）+ AetherCtpFront（前置）。
# 前提（一次性）：已以管理员运行 scripts/ctpsim-setup-server.ps1（两服务 + env 注册表 + 防火墙）。
# 流程：preflight → 干净解包 → 停前置再停交易所 → 清部署目录残留进程 → robocopy /MIR 两目录 → 起交易所（等 Running）
#       → 起前置（等 Running）→ 汇报。兼容 Windows PowerShell 5.1（无 ?? / 三元 / &&）。
# 注意：参数默认值与 workflow 的 env.CTPSIM_STAGING / setup 脚本的目录约定是同值常量——改一处须同步另一处。

[CmdletBinding()]
param(
    [string]$ExchangeService = 'AetherCtpExchange',
    [string]$ExchangeDir = 'C:\Aether\CtpExchange',
    [string]$FrontService = 'AetherCtpFront',
    [string]$FrontDir = 'C:\Aether\CtpFront',
    [string]$StagingDir = 'C:\Aether\ctpsim-tmp',
    [string]$TarName = 'ctpsim.tar.gz',
    [string]$DataDir = 'C:\Aether\ctpsim-data'
)

$ErrorActionPreference = 'Stop'

# 输出统一 UTF-8：SSH 会话里 PowerShell 5.1 默认按系统 ANSI/GBK 写 stdout，GitHub Actions 按 UTF-8 读 → 全乱码
# （2026-10-06 实测：CI 里 Start-Service 的中文错误全是 mojibake）。设置失败不致命（headless 无控制台句柄时可能抛）。
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

function Fail([string]$msg) {
    [Console]::Error.WriteLine("[CTPSIM-DEPLOY] $msg")
    exit 1
}
function Info([string]$msg) { Write-Host "[CTPSIM-DEPLOY] $msg" }

# 服务起不来的统一诊断：① host 自己的日志尾部（AETHER_*_LOG 落盘）② SCM 事件日志（1053 超时/即崩各有事件——
# 服务进程不 connect SCM 的经典症状是「服务未及时响应启动或控制请求」）。
function DumpStartFailure([string]$svc, [string]$logPath) {
    if (Test-Path $logPath) {
        Write-Host "[CTPSIM-DEPLOY] ── $logPath 尾部 30 行 ──"
        Get-Content $logPath -Tail 30 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $_" }
    }
    else {
        Write-Host "[CTPSIM-DEPLOY] （日志文件不存在：$logPath——host 的 Log 未写或服务 env 未设）"
    }
    try {
        $events = Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Service Control Manager' } -MaxEvents 200 -ErrorAction SilentlyContinue |
            Where-Object { $_.Message -match $svc } | Select-Object -First 5
        if ($events) {
            Write-Host "[CTPSIM-DEPLOY] ── SCM 事件（System 日志，最近 5 条与 $svc 相关）──"
            foreach ($e in $events) { Write-Host ("  {0}  {1}" -f $e.TimeCreated, $e.Message) }
        }
    } catch { }
}

# 1) Preflight：两服务都已注册（setup 没跑过 = 配置缺失——立即失败好过静默错配）
foreach ($svc in @($ExchangeService, $FrontService)) {
    if (-not (Get-Service -Name $svc -ErrorAction SilentlyContinue)) {
        Fail "服务 $svc 不存在——请先在服务器上以管理员运行 scripts/ctpsim-setup-server.ps1（一次性）。"
    }
}

# 2) 干净解包（tar 不清旧文件——不删 staging\out 的话，已删除的旧 DLL 会被 /MIR 带进部署目录）。
#    tar 布局：exchange/（交易所二进制）+ front/（前置二进制）——workflow 的两项目 publish 产物。
$tar = Join-Path $StagingDir $TarName
$out = Join-Path $StagingDir 'out'
if (-not (Test-Path $tar)) {
    Fail "staging 缺少 $tar（workflow 的 scp 步骤未送达？）"
}
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Path $out -Force | Out-Null
& tar.exe -xzf $tar -C $out
if ($LASTEXITCODE -ne 0) {
    Fail "解包 $tar 失败（exit $LASTEXITCODE）"
}
foreach ($part in @('exchange', 'front')) {
    if (-not (Test-Path (Join-Path $out $part))) {
        Fail "解包产物缺 $part\ 子目录（workflow 的 Bundle 布局已变？）"
    }
}

# 3) 停服务（先前置后交易所——前置依赖交易所；账本 flush 在交易所停止时同步落盘）
try { Stop-Service -Name $FrontService -ErrorAction SilentlyContinue } catch { }
try { Stop-Service -Name $ExchangeService -ErrorAction SilentlyContinue } catch { }
foreach ($svc in @($FrontService, $ExchangeService)) {
    $deadline = [DateTime]::Now.AddSeconds(30)
    while ((Get-Service -Name $svc).Status -ne 'Stopped' -and [DateTime]::Now -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
    if ((Get-Service -Name $svc).Status -ne 'Stopped') {
        Fail "停服务 $svc 超时（30s）——二进制未动，服务仍在跑旧版本，安全（手工排查后重跑）"
    }
}

# 3.5) 停服兜底：SCM 报 Stopped 之后部署目录里仍可能有进程压着文件（2026-10-24 实测：两服务都已 Stopped，
#      robocopy 仍对整目录报 ERROR 32「文件正被另一进程使用」，交易所部署中断）。来源是 SCM 管不到的进程——
#      RDP 里手工起过同一个 exe，或上一次部署半途失败留下的孤儿。按「映像路径落在部署目录内」精确识别：
#      只结束自己人，不碰机器上其它进程（同名但不在部署目录的实例不认）。
function Get-ProcessInDir([string]$dir) {
    if (-not (Test-Path $dir)) { return @() }
    $full = (Resolve-Path $dir).Path.TrimEnd('\') + '\'
    $hits = @()
    foreach ($p in (Get-Process -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $p.Path } catch { }   # 无权限读 Path 的进程直接跳过（不是我们的）
        if ($path -and $path.StartsWith($full, [StringComparison]::OrdinalIgnoreCase)) { $hits += $p }
    }
    $hits
}

function Clear-LingeringProcesses([string]$dir, [string]$label) {
    $procs = @(Get-ProcessInDir $dir)
    if ($procs.Count -eq 0) { return }
    $names = ($procs | ForEach-Object { "$($_.ProcessName)($($_.Id))" }) -join ', '
    Info "$label 目录仍有进程占用（$names）——SCM 已停，按孤儿进程处理：结束"
    foreach ($p in $procs) { try { $p.Kill() } catch { } }
    $deadline = [DateTime]::Now.AddSeconds(15)
    while ([DateTime]::Now -lt $deadline) {
        if (@(Get-ProcessInDir $dir).Count -eq 0) {
            Info "$label 目录已无进程占用"
            return
        }
        Start-Sleep -Milliseconds 300
    }
    Fail "$label 目录仍有进程占用（$names）15s 内未退出——手工结束后重跑部署"
}

# 4) 换文件：robocopy /MIR（退出码 0-7 都是成功，>=8 才失败）。账本与日志在 DataDir（两部署目录之外）。
#    失败时先清掉刚冒出来的占用进程再重试（杀进程与句柄释放之间有间隙，单次重试不够）。
function Copy-DeployDir([string]$src, [string]$dst, [string]$label, [string]$hint) {
    for ($i = 1; $i -le 3; $i++) {
        robocopy $src $dst /MIR /R:2 /W:2 /NFL /NDL /NJH /NJS
        if ($LASTEXITCODE -lt 8) { return }
        Info "robocopy $label 第 $i 次失败（code $LASTEXITCODE）——清占用后重试"
        Clear-LingeringProcesses $dst $label
        Start-Sleep -Seconds 2
    }
    Fail "robocopy $label 失败（code $LASTEXITCODE，已重试 3 次）$hint"
}
Clear-LingeringProcesses $ExchangeDir '交易所'
Clear-LingeringProcesses $FrontDir '前置'
Copy-DeployDir (Join-Path $out 'exchange') $ExchangeDir '交易所' ''
Copy-DeployDir (Join-Path $out 'front') $FrontDir '前置' '——交易所已更新，重跑一次部署即可恢复'

# 4.5) 服务 ImagePath 自愈（2026-10-20 拆仓切换，幂等）：AetherAxis 独立仓的宿主 exe 改名
#      （Acme.Ctp.Sim.Host.exe → Aether.Exchange.Host.exe / Acme.Ctp.Front.Host.exe → Aether.CtpFront.Host.exe），
#      服务器注册的 binPath 仍指向旧名——/MIR 后旧 exe 已不存在，服务必然 1053。每次部署无条件校正 binPath
#      到本次部署目录的新 exe（路径无空格免引号；服务名/目录约定自 setup 脚本以来未变）。
sc.exe config $ExchangeService binPath= (Join-Path $ExchangeDir 'Aether.Exchange.Host.exe') | Out-Null
sc.exe config $FrontService binPath= (Join-Path $FrontDir 'Aether.CtpFront.Host.exe') | Out-Null
Info "服务 ImagePath 已校正 → $ExchangeDir\Aether.Exchange.Host.exe / $FrontDir\Aether.CtpFront.Host.exe"

# 5) 起交易所（等 Running——前置探活会自动连上）；再起前置（等 Running）。
#    Start-Service 失败（1053 超时 / 进程即崩）不在此抛——吞掉进等待循环，统一走 DumpStartFailure 诊断收尾
#    （2026-10-06 实测：$ErrorActionPreference=Stop 下 Start-Service 直接抛会把诊断输出全部短路掉，CI 只剩一句 mojibake）。
try { Start-Service -Name $ExchangeService } catch { }
$deadline = [DateTime]::Now.AddSeconds(60)
while ((Get-Service -Name $ExchangeService).Status -ne 'Running' -and [DateTime]::Now -lt $deadline) {
    Start-Sleep -Milliseconds 500
}
if ((Get-Service -Name $ExchangeService).Status -ne 'Running') {
    DumpStartFailure $ExchangeService (Join-Path $DataDir 'ctpsim-exchange.log')
    Fail "交易所未能启动（见上方日志尾部与 SCM 事件——二进制已就位，修复后重跑部署即可恢复）"
}
try { Start-Service -Name $FrontService } catch { }
$deadline = [DateTime]::Now.AddSeconds(60)
while ((Get-Service -Name $FrontService).Status -ne 'Running' -and [DateTime]::Now -lt $deadline) {
    Start-Sleep -Milliseconds 500
}
if ((Get-Service -Name $FrontService).Status -ne 'Running') {
    DumpStartFailure $FrontService (Join-Path $DataDir 'ctpsim-front.log')
    Fail "前置未能启动（见上方日志尾部与 SCM 事件——交易所已在跑，重跑部署即可恢复）"
}

# 6) 汇报（显式 exit 0——robocopy 的非零退出码不能漏成本脚本退出码毒死 workflow step）
$version = Get-Content (Join-Path $FrontDir 'VERSION') -ErrorAction SilentlyContinue
Write-Host "[CTPSIM-DEPLOY] ==> $ExchangeService + $FrontService 已部署并运行（版本 $version）"
Write-Host "[CTPSIM-DEPLOY]     日志: $DataDir\ctpsim-exchange.log / ctpsim-front.log    账本: $DataDir\ctp-sim.db"
Write-Host "[CTPSIM-DEPLOY]     客户端前置: tcp://<本机IP>:17801（交易）/ tcp://<本机IP>:17802（行情）"
exit 0
