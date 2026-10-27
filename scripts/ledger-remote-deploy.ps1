# ── Aether 虚拟币链·远程部署脚本（在服务器上执行）──────────────────────────────────
# 由 .github/workflows/deploy-ledger.yml 经 SSH 调起：
#   powershell -NoProfile -ExecutionPolicy Bypass -File C:/Aether/ledger-tmp/ledger-remote-deploy.ps1
# 前提（一次性）：已以管理员运行 scripts/ledger-setup-server.ps1（AetherLedgerNode 服务 + env 注册表）。
# 2026-10-27：自 monorepo scripts/ 原样迁入 ⑧ aether-apps（链三件套 = 本仓 Node + Wallet + Verify）。
#   脚本本身零改动——服务器侧目录/staging/服务名沿用 10-20 上线的既有约定（切换部署只换「谁来构建」）。
# 流程：preflight → 干净解包 → 停节点 → robocopy /MIR 三目录 → 起节点（等 Running）→ RPC 探针。
# 链数据（ledger.chain + 节点日志）在 DataDir——三部署目录之外，重部署不动链。
# ⚠ 节点重启语义：读取 + 全量重放恢复（单号/nonce 序列续排）；交易所桥轮询自动重连（交易所不用动）。
# 兼容 Windows PowerShell 5.1（无 ?? / 三元 / &&）。

[CmdletBinding()]
param(
    [string]$NodeService = 'AetherLedgerNode',
    [string]$NodeDir = 'C:\Aether\LedgerNode',
    [string]$WalletDir = 'C:\Aether\LedgerWallet',
    [string]$VerifyDir = 'C:\Aether\LedgerVerify',
    [string]$StagingDir = 'C:\Aether\ledger-tmp',
    [string]$TarName = 'ledger.tar.gz',
    [string]$DataDir = 'C:\Aether\ledger-data'
)

$ErrorActionPreference = 'Stop'

# 输出统一 UTF-8（治 CI 端 GBK mojibake——同 ctpsim-remote-deploy 的修复）
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

function Fail([string]$msg) {
    [Console]::Error.WriteLine("[LEDGER-DEPLOY] $msg")
    exit 1
}
function Info([string]$msg) { Write-Host "[LEDGER-DEPLOY] $msg" }

# 1) Preflight：服务已注册（setup 没跑过 = 配置缺失——立即失败好过静默错配）
if (-not (Get-Service -Name $NodeService -ErrorAction SilentlyContinue)) {
    Fail "服务 $NodeService 不存在——请先在服务器上以管理员运行 scripts/ledger-setup-server.ps1（一次性）。"
}

# 2) 干净解包（tar 不清旧文件——不删 staging\out 的话，已删除的旧 DLL 会被 /MIR 带进部署目录）。
#    tar 布局：node/ + wallet/ + verify/（workflow 的三项目 publish 产物）。
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
foreach ($part in @('node', 'wallet', 'verify')) {
    if (-not (Test-Path (Join-Path $out $part))) {
        Fail "解包产物缺 $part\ 子目录（workflow 的 Bundle 布局已变？）"
    }
}

# 3) 停节点（等 Stopped——链文件逐块落盘，无需额外 flush）
try { Stop-Service -Name $NodeService -ErrorAction SilentlyContinue } catch { }
$deadline = [DateTime]::Now.AddSeconds(30)
while ((Get-Service -Name $NodeService).Status -ne 'Stopped' -and [DateTime]::Now -lt $deadline) {
    Start-Sleep -Milliseconds 500
}
if ((Get-Service -Name $NodeService).Status -ne 'Stopped') {
    Fail "停服务 $NodeService 超时（30s）——二进制未动，服务仍在跑旧版本，安全（手工排查后重跑）"
}

# 3b) 清理残留进程（2026-10-09 实测踩中：SCM 报 Stopped ≠ 进程已退出）。
#     AddWindowsService 缺失的旧版本启动后不向 SCM 上报 → 1053 超时 → SCM 认为服务 Stopped，
#     但进程可能仍在跑（或有人手工 .\Aether.Ledger.Node.exe 测试过）→ 它持有部署目录里的
#     dll/exe（含 .pdb 的 user-mapped section）→ robocopy 必失败（ERROR 32 / ERROR 1224）。
#     按名杀干净再换文件——比“重试”更根本（占用者不会自己松手）。
function KillStrays {
    $killed = $false
    foreach ($pn in @('Aether.Ledger.Node', 'Aether.Ledger.Wallet', 'Aether.Ledger.Verify')) {
        foreach ($proc in @(Get-Process -Name $pn -ErrorAction SilentlyContinue)) {
            Info "发现残留进程 $pn (PID $($proc.Id))——终止（服务已停，锁着部署目录的二进制）"
            try { $proc.Kill() } catch { }
            try { $proc.WaitForExit(10000) | Out-Null } catch { }
            $exited = $false
            try { $exited = $proc.HasExited } catch { }
            if (-not $exited) {
                # .NET Kill() 对某些残留不生效（2026-10-09 两轮部署实测：上轮杀过的 PID 数小时后
                # 仍在存活名单里）——taskkill 原生强杀兜底；失败必须喊出来，静默吞 = 下轮 robocopy 再挂
                $tkOut = $null; $tkExit = 0
                try { $tkOut = & taskkill /F /T /PID $proc.Id 2>&1; $tkExit = $LASTEXITCODE }
                catch { $tkExit = -1; $tkOut = $_.Exception.Message }
                if ($tkExit -ne 0) {
                    Write-Host "[LEDGER-DEPLOY] ⚠ PID $($proc.Id) taskkill 也失败（exit $tkExit）：$tkOut——手动处理：taskkill /F /T /IM $pn.exe"
                }
            }
            $killed = $true
        }
    }
    if ($killed) { Start-Sleep -Seconds 2 }
}
KillStrays

# 4) 换文件：robocopy /MIR 三目录（退出码 0-7 都是成功，>=8 才失败）。链数据在 DataDir（部署目录之外）。
#    失败（文件仍被占用 / 杀软正扫刚解包的文件）→ 3 秒后重试，三次后才判死 + 打印占用者。
function ReportLockers([string]$dir) {
    foreach ($p in @(Get-Process -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $p.Path } catch { }
        if ($path -and $path.StartsWith($dir, [StringComparison]::OrdinalIgnoreCase)) {
            Write-Host "[LEDGER-DEPLOY]   占用者：$($p.ProcessName) (PID $($p.Id)) → $path"
        }
    }
}
function CopyPart([string]$src, [string]$dst, [string]$label, [string]$failMsg) {
    for ($i = 1; $i -le 3; $i++) {
        robocopy $src $dst /MIR /R:2 /W:2 /NFL /NDL /NJH /NJS
        if ($LASTEXITCODE -lt 8) { return }
        Info "robocopy $label 第 $i 次失败（code $LASTEXITCODE）——3 秒后重试"
        Start-Sleep -Seconds 3
    }
    Write-Host "[LEDGER-DEPLOY] ── $dst / $StagingDir 下的进程 ──"
    ReportLockers $dst
    ReportLockers $StagingDir
    Fail "$failMsg（code $LASTEXITCODE）"
}
CopyPart (Join-Path $out 'node') $NodeDir '节点' "robocopy 节点失败——$NodeDir 里的文件仍被占用：在服务器上 Get-Process Aether.Ledger.Node 看是否还有进程，杀掉后重跑部署"
CopyPart (Join-Path $out 'wallet') $WalletDir '钱包 CLI' 'robocopy 钱包 CLI 失败——节点已更新，重跑一次部署即可恢复'
CopyPart (Join-Path $out 'verify') $VerifyDir '验证器' 'robocopy 验证器失败——节点已更新，重跑一次部署即可恢复'

# 5) 起节点（等 Running——首次启动生成新链：treasury/producer 种子打印进 $DataDir\node.log，务必抄录备份）
#    再清一次残留：换文件期间若被拉起过旧进程，会和 SCM 新起的进程抢 17805 端口（bind 失败 → 起不来）。
KillStrays
try { Start-Service -Name $NodeService } catch { }
$deadline = [DateTime]::Now.AddSeconds(60)
while ((Get-Service -Name $NodeService).Status -ne 'Running' -and [DateTime]::Now -lt $deadline) {
    Start-Sleep -Milliseconds 500
}
if ((Get-Service -Name $NodeService).Status -ne 'Running') {
    $log = Join-Path $DataDir 'node.log'
    if (Test-Path $log) {
        Write-Host "[LEDGER-DEPLOY] ── $log 尾部 30 行 ──"
        Get-Content $log -Tail 30 -Encoding UTF8 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $_" }
    }
    Fail "链节点未能启动（见上方日志尾部——二进制已就位，修复后重跑部署即可恢复）"
}

# 6) RPC 探针：钱包 CLI info（自包含 win-x64，服务器裸跑）——链就绪 + 高度可见即部署成功。
#    2026-10-09 实测踩中两个坑：
#    ① PS 5.1 native stderr 陷阱（同 2026-10-06 ctpsim 脚本 Start-Service 短路坑）：
#      $ErrorActionPreference=Stop 下钱包 CLI 往 stderr 写「目标计算机积极拒绝」直接把脚本炸成
#      NativeCommandError 收场（exit 1、零诊断输出）。修：TCP 连通性先用纯 .NET TcpClient 测
#      （零 native stderr），端口通了才跑钱包 CLI（再套 try/catch 兜底）。
#    ② 探针目标不能写死 127.0.0.1：LedgerNode.Start 对 host 做 IPAddress.TryParse 成功即只绑那一个
#      IP——服务 env 的 AETHER_LEDGER_ADDR 若被 aether-public-ports.ps1 配成绑公网 IP，节点健康
#      但回环拒连。修：探针地址读服务 env（0.0.0.0 → 回环；env 读不出回退 127.0.0.1），
#      绑非回环时显式警告（同机交易所桥默认连 127.0.0.1 会拒）。
$nodeEnv = @()
try { $nodeEnv = @((Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$NodeService").Environment) } catch { }
$nodeAddr = $null
foreach ($kv in $nodeEnv) { if ($kv -like 'AETHER_LEDGER_ADDR=*') { $nodeAddr = $kv.Substring($kv.IndexOf('=') + 1) } }
$probeHost = '127.0.0.1'
$probePort = 17805
if ($nodeAddr) {
    $parts = $nodeAddr -split ':'
    $probeHost = $parts[0]
    if ($probeHost -eq '0.0.0.0') { $probeHost = '127.0.0.1' }
    if ($parts.Count -ge 2) { [int]::TryParse($parts[-1], [ref]$probePort) | Out-Null }
}
function Test-Port([string]$host2, [int]$port2) {
    try {
        $tcp = New-Object Net.Sockets.TcpClient
        $tcp.Connect($host2, $port2)
        $ok = $tcp.Connected
        $tcp.Close()
        return $ok
    } catch { return $false }
}
$tcpOk = Test-Port $probeHost $probePort
if (-not $tcpOk) {
    # 回环兜底：env 解析出的 host 连不上时再试 127.0.0.1（节点绑 Any 而 env 形态解析异常等边缘）
    if (Test-Port '127.0.0.1' $probePort) { $tcpOk = $true; $probeHost = '127.0.0.1' }
}
if (-not $tcpOk) {
    Write-Host "[LEDGER-DEPLOY] ── 探针失败诊断（$probeHost`:$probePort 不可连）──"
    Get-Service -Name $NodeService | Format-Table Name,Status | Out-String | ForEach-Object { Write-Host $_.TrimEnd() }
    Write-Host "[LEDGER-DEPLOY] 服务 env："
    foreach ($kv in $nodeEnv) { Write-Host "    $kv" }
    Write-Host "[LEDGER-DEPLOY] netstat（17805）："
    netstat -ano | Select-String ':17805' | ForEach-Object { Write-Host "    $($_.Line.Trim())" }
    $log = Join-Path $DataDir 'node.log'
    if (Test-Path $log) {
        Write-Host "[LEDGER-DEPLOY] ── $log 尾部 30 行 ──"
        Get-Content $log -Tail 30 -Encoding UTF8 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $_" }
    }
    Info "提示：日志反复「链已加载」而无「链节点已就绪」= node.Start() 在生产者种子检查处抛出——服务 env 缺 AETHER_LEDGER_PRODUCER_SEED（重启已有链必须有它；种子在生成链时打印进 node.log；见 doc/ledger-runbook.md），节点此刻在 SCM 5s 崩溃自恢复循环里空转"
    Fail "链节点 Running 但 RPC 端口不可连（见上方 netstat/服务 env/日志——节点可能绑了非回环地址、监听失败或进程已退出）"
}

# 端口已通才跑钱包 CLI 语义探针（getInfo 走一圈 JSON-RPC）
$probe = $null
$probeExit = 0
try {
    $probe = & (Join-Path $WalletDir 'Aether.Ledger.Wallet.exe') info --node "$probeHost`:$probePort" 2>&1
    $probeExit = $LASTEXITCODE
} catch {
    $probeExit = -1
    $probe = "$($_.Exception.Message)（PS 5.1 native stderr——按探针失败处理）"
}
Write-Host "[LEDGER-DEPLOY] RPC 探针：$probe"
if ($probeExit -ne 0) {
    Fail "链节点端口已通但钱包 CLI info 失败（退出码 $probeExit）——见上方输出"
}
if ($nodeAddr -and (($nodeAddr -split ':')[0] -notin @('127.0.0.1', '0.0.0.0', 'localhost'))) {
    Write-Host "[LEDGER-DEPLOY] ⚠ 节点绑定 $nodeAddr（非回环/Any）——同机交易所桥默认连 127.0.0.1:17805 会被拒：交易所服务 env 须设 AETHER_LEDGER_NODE_ADDR=$nodeAddr，或把节点绑回 0.0.0.0（推荐，回环+公网都通）"
}

# 7) 汇报（显式 exit 0——robocopy 的非零退出码不能漏成本脚本退出码毒死 workflow step）
$version = Get-Content (Join-Path $NodeDir 'VERSION') -ErrorAction SilentlyContinue
Write-Host "[LEDGER-DEPLOY] ==> $NodeService 已部署并运行（版本 $version）"
Write-Host "[LEDGER-DEPLOY]     链文件: $DataDir\ledger.chain（重部署不动）| 节点日志: $DataDir\node.log"
Write-Host "[LEDGER-DEPLOY]     钱包 CLI: $WalletDir\Aether.Ledger.Wallet.exe（发币/查余额/提币）| 独立验证: $VerifyDir\Aether.Ledger.Verify.exe"
Write-Host "[LEDGER-DEPLOY]     ⚠ 首次生成链后从 node.log 抄录 treasury/producer 种子并离线备份"
exit 0
