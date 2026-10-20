#Requires -Version 5.1
[CmdletBinding()]
param(
    # 服务器布局：$Root\publish\ = 程序本体（每次部署整替换）；$Root\<实例名>\ = 运行实例
    #（config.json + labs + ctp.db + lean-algo-cache——config.json 永不被部署覆盖，密码在里面）。
    [string]$Root = 'C:\Aether\ctp-console',
    [string[]]$Instances = @('botmm', 'botnoise'),
    [string]$StagingDir = 'C:\Aether\ctp-console-staging',
    [string]$TarPath = 'C:\Aether\ctp-console-staging\ctp-console.tar.gz'
)

# CTP console 客户端远端部署（GitHub Actions deploy-ctp-console.yml 的远端半边——
# 服务器上由 CI 经 SSH 执行；也可手动跑（把 tar 放到 $StagingDir 后执行本脚本））。
#   1) 解 tar 到 $Root\publish\（先删旧——程序本体整替换）
#   2) 实例目录幂等搭建：建目录 + labs\leanalgorithms 覆盖拷贝（algorithms/ 仓库分发副本是权威）
#      + config.json 永不覆盖（用户填的密码在里面；不存在则留给 console 首启生成模板）
#   3) 打印实例状态清单（config 是否已填/是否在跑）
# 无 Windows 服务：console 是交互/挂机进程——部署不停任何东西（跑着的实例继续用旧内存程序，
# 新实例/重启后用新程序）。
# 注意：本文件必须保持 UTF-8 带 BOM——Windows PowerShell 5.1 对无 BOM 的 UTF-8 按系统 ANSI/GBK 误读中文。

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

function Info($msg)  { Write-Output "[CTP-CONSOLE] $msg" }

if (-not (Test-Path $TarPath)) { throw "未找到部署包：$TarPath（CI 应已 scp 到 staging）" }

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

    # labs 算法：覆盖拷贝（algorithms/ 仓库分发副本是权威——push 即更新，下次启动策略生效）
    Copy-Item (Join-Path $algoSrc '*.lean.cs') $labs -Force
    Info "实例 $name ：labs 已同步（$(( Get-ChildItem $labs -Filter *.lean.cs | Measure-Object ).Count) 个算法）"

    # config.json：永不被部署覆盖（密码在里面）。不存在 → 从 CI 打包的实例模板拷贝
    #（config.<实例名>.json——预填默认凭据「账户名=密码」+ 本机前置，开箱即跑）。
    $config = Join-Path $dir 'config.json'
    if (Test-Path $config) {
        Info "实例 $name ：config.json 已存在（部署不覆盖——密码/档案保持不变）"
    } elseif (Test-Path (Join-Path $Root "publish\config.$name.json")) {
        Copy-Item (Join-Path $Root "publish\config.$name.json") $config
        Info "实例 $name ：config.json 已按模板生成（默认凭据 $name/$name——可直接启动；改密编辑此文件）"
    } else {
        Info "实例 $name ：无模板（publish\config.$name.json 缺失）——首次启动时自动生成配置模板"
    }
}

# 3) 状态清单
$running = Get-Process Acme.Ctp.Console -ErrorAction SilentlyContinue
if ($running) {
    Info "正在运行的实例进程：$($running.Count) 个（继续用旧内存程序；重启后用新版本）"
} else {
    Info "当前无运行中的实例"
}
$version = Get-Content (Join-Path $Root 'publish\VERSION') -ErrorAction SilentlyContinue
Info "部署完成：publish 版本 $version"
Info "启动方式：cd $Root\botmm ; ..\publish\Acme.Ctp.Console.exe"
