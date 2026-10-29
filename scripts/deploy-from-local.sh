#!/usr/bin/env bash
# ── 本机直传部署（CTP 线：交易所+前置 / console bot）──────────────────────────────
# 为什么需要它：GitHub 托管 runner 到这台服务器的上传带宽极差——2026-10-28 实测同一次部署，
#   runner 传 54MB 跑了 23 分钟仍未完（job 被 25 分钟硬超时杀掉），本机同一条命令 34 秒。
#   所以 CI 的 deploy-*.yml 现在只当「构建契约的 dry-run 看门」用（deploy-dryrun.yml），
#   真要上线走本脚本。artifact 与 CI 完全一致（同 publish 命令、同 tar 布局、同远端脚本）。
#
# 用法：
#   scripts/deploy-from-local.sh ctpsim     # 交易所 + 前置（停两个服务，撮合中断数十秒）
#   scripts/deploy-from-local.sh console    # console bot（不停任何服务；实例 config 不被覆盖）
#   scripts/deploy-from-local.sh direct     # **临时旁路**直连 bot（bot 直连交易所绕开前置；不动任何任务）
#
# 环境变量（都有默认值）：
#   AETHER_DEPLOY_HOST  默认 117.72.186.128
#   AETHER_DEPLOY_USER  默认 administrator
#   AETHER_DEPLOY_KEY   默认 ~/.ssh/ctpsim_deploy
#   DOTNET              默认 dotnet
#
# 前置：本机工作区 16 仓并排（跨仓 ProjectReference 要解析得到）；服务器已按 setup 脚本准备过。
set -euo pipefail

what=${1:-}
case "$what" in
    ctpsim|console|direct) ;;
    *) echo "用法: $0 ctpsim|console|direct" >&2; exit 2 ;;
esac

HOST=${AETHER_DEPLOY_HOST:-117.72.186.128}
DEPLOY_USER=${AETHER_DEPLOY_USER:-administrator}
KEY=${AETHER_DEPLOY_KEY:-$HOME/.ssh/ctpsim_deploy}
DOTNET=${DOTNET:-dotnet}

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)   # 本仓根
WS=$(dirname "$ROOT")                                    # 工作区根（兄弟仓并排）
for d in ctp-net-client aether-trading aether-exchange; do
    [ -d "$WS/$d" ] || { echo "缺兄弟仓 $d（本机工作区须 16 仓并排）" >&2; exit 1; }
done

SSH_OPTS=(-i "$KEY" -o BatchMode=yes -o StrictHostKeyChecking=accept-new
          -o ConnectTimeout=20 -o ServerAliveInterval=15 -o ServerAliveCountMax=4)
TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT
VERSION=$(git -C "$ROOT" rev-parse --short HEAD)

publish() { # publish <项目名> <输出子目录>
    "$DOTNET" publish "$1" -c Release --self-contained true --runtime win-x64 -o "$TMP/$2" >/dev/null
    echo "  publish $1 → $2"
}

case "$what" in
ctpsim)
    STAGING='C:/Aether/ctpsim-tmp'; TAR_NAME=ctpsim.tar.gz; REMOTE_SCRIPT=ctpsim-remote-deploy.ps1
    echo "[deploy] 构建 win-x64 自包含（版本 $VERSION）"
    ( cd "$ROOT" && publish Aether.Exchange.Host exchange && publish Aether.CtpFront.Host front )
    printf '%s\n' "$VERSION" > "$TMP/exchange/VERSION"
    printf '%s\n' "$VERSION" > "$TMP/front/VERSION"
    tar -czf "$TMP/$TAR_NAME" -C "$TMP" exchange front
    echo "  停服影响：交易所+前置重启，撮合中断数十秒"
    ;;
console)
    STAGING='C:/Aether/ctp-console-staging'; TAR_NAME=ctp-console.tar.gz; REMOTE_SCRIPT=ctp-console-remote-deploy.ps1
    echo "[deploy] 构建 win-x64 自包含（版本 $VERSION）"
    ( cd "$ROOT" && publish Aether.CtpConsole console )
    # 以下与 deploy-ctp-console.yml 的 Bundle 步骤逐字一致（labs/LeanData/config 是现场生成的）
    mkdir -p "$TMP/console/LeanData/market-hours" "$TMP/console/LeanData/symbol-properties" \
             "$TMP/console/labs/leanalgorithms"
    cp -r "$WS/aether-trading/LeanData/market-hours/." "$TMP/console/LeanData/market-hours/"
    cp -r "$WS/aether-trading/LeanData/symbol-properties/." "$TMP/console/LeanData/symbol-properties/"
    cp "$ROOT"/algorithms/*.lean.cs "$TMP/console/labs/leanalgorithms/"
    for inst in botmm botnoise; do
        printf '{"defaultProfile":"%s","labs":null,"data":null,"profiles":{"%s":{"front":"tcp://127.0.0.1:17801","mdFront":"tcp://127.0.0.1:17802","user":"%s","password":"%s","broker":"9999","appId":"simnow_client_test","authCode":"0000000000000000"}}}' \
            "$inst" "$inst" "$inst" "$inst" > "$TMP/console/config.$inst.json"
    done
    printf '%s\n' "$VERSION" > "$TMP/console/VERSION"
    # tar 根 = console 内容的平铺（多套一层远端实例同步就找不到路径）
    tar -czf "$TMP/$TAR_NAME" -C "$TMP/console" .
    echo "  停服影响：无（不留驻服务；正在跑的实例继续用旧内存程序）"
    ;;
direct)
    # **临时旁路**（2026-10-29）：bot 直连交易所 :17800，绕开前置。前置重连认证修好后本分支退役。
    # 与 console 的差异：不引 ctp-net-client（无官方 CTP SDK / 无 frameworks 拷贝），程序本体更小。
    STAGING='C:/Aether/direct-bot-staging'; TAR_NAME=direct-bot.tar.gz; REMOTE_SCRIPT=directbot-remote-deploy.ps1
    echo "[deploy] 构建 win-x64 自包含（版本 $VERSION）"
    ( cd "$ROOT" && publish Aether.DirectBot.Console direct )
    mkdir -p "$TMP/direct/LeanData/market-hours" "$TMP/direct/LeanData/symbol-properties" \
             "$TMP/direct/labs/leanalgorithms"
    cp -r "$WS/aether-trading/LeanData/market-hours/." "$TMP/direct/LeanData/market-hours/"
    cp -r "$WS/aether-trading/LeanData/symbol-properties/." "$TMP/direct/LeanData/symbol-properties/"
    cp "$ROOT"/algorithms/*.lean.cs "$TMP/direct/labs/leanalgorithms/"
    # 实例模板：只带账户/密码/broker（直连实例不读前置字段）。远端优先沿用 CTP 版实例的凭据。
    for inst in botmm botnoise; do
        printf '{"defaultProfile":"%s","labs":null,"data":null,"profiles":{"%s":{"user":"%s","password":"%s","broker":"9999"}}}' \
            "$inst" "$inst" "$inst" "$inst" > "$TMP/direct/config.$inst.json"
    done
    printf '%s\n' "$VERSION" > "$TMP/direct/VERSION"
    tar -czf "$TMP/$TAR_NAME" -C "$TMP/direct" .
    echo "  停服影响：无（不动任何计划任务——切换 bot 是显式动作）"
    ;;
esac

echo "[deploy] 上传 → $HOST:$STAGING（$(du -h "$TMP/$TAR_NAME" | cut -f1)）"
t0=$(date +%s)
scp "${SSH_OPTS[@]}" "$TMP/$TAR_NAME" "$ROOT/scripts/$REMOTE_SCRIPT" "$DEPLOY_USER@$HOST:$STAGING/" >/dev/null
echo "  上传耗时 $(( $(date +%s) - t0 ))s"

echo "[deploy] 远端执行 $REMOTE_SCRIPT"
ssh "${SSH_OPTS[@]}" "$DEPLOY_USER@$HOST" \
    "powershell -NoProfile -ExecutionPolicy Bypass -File ${STAGING}/${REMOTE_SCRIPT}"
