#Requires -Version 5.1
[CmdletBinding()]
param(
    # 服务器布局：$Root\publish\ = 程序本体（每次部署整替换）；$Root\<实例名>\ = 运行实例
    #（config.json + labs + lean-algo-cache + directbot.heartbeat.json——config.json 永不被部署覆盖）。
    [string]$Root = 'C:\Aether\direct-bot',
    [string[]]$Instances = @('botmm', 'botnoise'),
    # 凭据来源（可选）：CTP 版实例目录。直连实例缺 config.json 时从这里取 user/password/broker，
    # 避免「默认账户名=密码」的假设在改过密码的服务器上把人锁在门外。
    [string]$CtpInstanceRoot = 'C:\Aether\ctp-console',
    [string]$StagingDir = 'C:\Aether\direct-bot-staging',
    [string]$TarPath = 'C:\Aether\direct-bot-staging\direct-bot.tar.gz'
)

# Aether.DirectBot.Console 远端部署（**临时旁路客户端**：bot 直连交易所 :17800，绕开 CTP 前置）。
#   1) 解 tar 到 $Root\publish\（先删旧——程序本体整替换）
#   2) 实例目录幂等搭建：建目录 + labs\leanalgorithms 覆盖 + config.json 永不覆盖
#   3) 打印实例状态清单
# **不动任何计划任务**：停旧 bot / 起直连 bot 是切换动作，由操作者显式执行（见 doc/direct-bot-bypass.md），
# 部署脚本不替人决定市场何时停。
# 注意：本文件必须保持 UTF-8 带 BOM——Windows PowerShell 5.1 对无 BOM 的 UTF-8 按系统 ANSI/GBK 误读中文。

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

function Info($msg) { Write-Output "[DIRECT-BOT] $msg" }

if (-not (Test-Path $TarPath)) { throw "未找到部署包：$TarPath" }

# 1) 程序本体：publish\ 整替换
Info "解压新版本 → $Root\publish"
Remove-Item -Recurse -Force (Join-Path $Root 'publish') -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $Root 'publish') | Out-Null
tar -xzf $TarPath -C (Join-Path $Root 'publish')
if ($LASTEXITCODE -ne 0) { throw "tar 解压失败（exit $LASTEXITCODE）" }

# 2) 实例：幂等搭建
$algoSrc = Join-Path $Root 'publish\labs\leanalgorithms'
foreach ($name in $Instances) {
    $dir = Join-Path $Root $name
    $labs = Join-Path $dir 'labs\leanalgorithms'
    New-Item -ItemType Directory -Force $labs | Out-Null

    Copy-Item (Join-Path $algoSrc '*.lean.cs') $labs -Force
    Info "实例 $name ：labs 已同步（$((Get-ChildItem $labs -Filter *.lean.cs | Measure-Object).Count) 个算法）"

    $config = Join-Path $dir 'config.json'
    if (Test-Path $config) {
        Info "实例 $name ：config.json 已存在（部署不覆盖）"
        continue
    }

    # 优先沿用 CTP 版实例的凭据（同一账户，同一密码——切换直连不改账户语义）
    $ctpConfig = Join-Path (Join-Path $CtpInstanceRoot $name) 'config.json'
    $written = $false
    if ((Test-Path $ctpConfig)) {
        try {
            $ctp = Get-Content $ctpConfig -Raw -Encoding UTF8 | ConvertFrom-Json
            $profName = if ($ctp.defaultProfile) { $ctp.defaultProfile } elseif ($ctp.profiles.PSObject.Properties | Select-Object -First 1) { ($ctp.profiles.PSObject.Properties | Select-Object -First 1).Name } else { $null }
            if ($profName -and $ctp.profiles.$profName) {
                $prof = $ctp.profiles.$profName
                $payload = [ordered]@{
                    defaultProfile = $profName
                    labs           = $null
                    data           = $null
                    profiles       = [ordered]@{
                        $profName = [ordered]@{
                            user     = $prof.user
                            password = $prof.password
                            broker   = $(if ($prof.broker) { $prof.broker } else { '9999' })
                        }
                    }
                }
                $json = $payload | ConvertTo-Json -Depth 6
                [System.IO.File]::WriteAllText($config, $json, (New-Object System.Text.UTF8Encoding($false)))
                Info "实例 $name ：config.json 已从 $ctpConfig 的档案 '$profName' 派生（账户不变，仅去掉前置字段）"
                $written = $true
            }
        } catch { Info "实例 $name ：读 $ctpConfig 失败（$($_.Exception.Message)）——回退模板" }
    }
    if (-not $written -and (Test-Path (Join-Path $Root "publish\config.$name.json"))) {
        Copy-Item (Join-Path $Root "publish\config.$name.json") $config
        Info "实例 $name ：config.json 已按模板生成（默认凭据 $name/$name——改密编辑此文件）"
    }
    if (-not $written -and -not (Test-Path $config)) {
        Info "实例 $name ：无凭据可用——请手写 $config（user/password/broker）后再启动"
    }
}

# 3) 状态清单
$running = Get-Process Aether.DirectBot.Console -ErrorAction SilentlyContinue
Info $(if ($running) { "正在运行的直连实例进程：$($running.Count) 个（继续用旧内存程序；重启后用新版本）" } else { '当前无运行中的直连实例' })
$ctp = Get-Process Aether.CtpConsole -ErrorAction SilentlyContinue
if ($ctp) { Info "注意：仍有 $($ctp.Count) 个 Aether.CtpConsole（走前置）在跑——同账户不可与直连实例并存（OrderRef 撞号）" }
$version = Get-Content (Join-Path $Root 'publish\VERSION') -ErrorAction SilentlyContinue
Info "部署完成：publish 版本 $version"
Info "启动方式：cd $Root\botmm ; ..\publish\Aether.DirectBot.Console.exe --profile botmm --exchange 127.0.0.1:17800 --data . --run-strategy market-maker ax001 --real --max-volume 5"
