# Aether.CtpHosts

组合根宿主仓——各核心库的可执行宿主 + console 算法 bot + 策略源存档。**不发 NuGet**（部署/验证单元，非被引用单元；跨仓依赖以本地 ProjectReference 组装）。

## 组成

本仓是 **CTP 交易线的组合根**：三个宿主 + 策略源存档。

| 宿主 | 说明 |
|---|---|
| `Aether.Exchange.Host` | 仿真交易所（:17800；资金 = 链上 AQ 充值对账——见「资金机制」；`AETHER_CTP_SIM_BALANCE` 仅沙盒水龙头） |
| `Aether.CtpFront.Host` | CTP 线协议前置（交易 :17801 / 行情 :17802，连 :17800） |
| `Aether.CtpConsole` | console 算法 bot：Lean 实盘引擎 + CTP SDK 会话 + REPL——无 GUI 依赖，SSH 挂机/无人值守/轻量手动交易 |

> **虚拟币三宿主（链节点 / 钱包 CLI / 验证器）已于 2026-10-27 拆出到 ⑯ `aether-coin-hosts`。**
> 它们有自己的服务端、客户端、部署（`deploy-ledger`）和故障域，不该和 CTP 宿主共享同一个构建上下文——
> 拆出后本仓的 CI 与发布链只覆盖 CTP 一条线。交易所接链的桥（`LedgerCashRail`）**仍在本仓**：
> 它是跨域胶水，住组合根，不随虚拟币线走。

### 策略源

| 目录 | 说明 |
|---|---|
| `algorithms/` | Lean 策略源存档（market-maker / noise-taker）——运行时从实例目录 `labs/leanalgorithms/` 加载 |

## 目录布局（本仓是唯一例外：没有 `src/`）

仓根直接放宿主目录，和其余 13 仓的 `src/` 约定不一致——**有意为之**，不是历史遗留：

- 这个仓是**组合根**：每个 `<X>.Host` 就是一个可执行单元，发布 = `<X>.Host/bin/…` 整个目录；
  中间没有被引用的库需要归类，多一层 `src/` 只多一次路径跳转（部署脚本、CI、runbook 的路径都要跟着长）。
- 目录名 = 程序集名 = 发布单元名，`dotnet run --project Aether.Exchange.Host` 直接可读可敲。

约束：**新增宿主也放仓根、命名 `<域>.<角色>`**（Host / Console / Node / Wallet…）；仓根不要新增库项目，
真要抽公共库，它属于对应引擎仓（③④⑤⑥⑦），不属于这里。`scripts/`（远程部署）、
`tests/`（宿主级断言）、`algorithms/`（策略源存档）是仓级公共资产，平级放置。

**一个目录一个完整实例**（console）：config.json / labs/ / ctp.db / lean-algo-cache 全部默认落当前工作目录——双 bot = 两个目录各跑一个，完全隔离；发布目录拷到哪就在哪跑。

## 本地 E2E（全链实跑）

```bash
# 服务链（链桥 opt-in：交易所宿主设 AETHER_LEDGER_CUSTODY_SEED 才接链；不设 = 桥不启动照常跑。
# 链节点在 ⑯ aether-coin-hosts：dotnet run --project ../aether-coin-hosts/Aether.Ledger.Node   # :17805
# ⚠ 接了桥就必须先起节点——桥启动即连节点+全量扫块，连不上交易所宿主起不来）
dotnet run --project Aether.Exchange.Host   # :17800
dotnet run --project Aether.CtpFront.Host   # :17801/:17802

# console 直跑（在实例目录内运行——config.json 按 cwd 发现）
mkdir -p /tmp/e2e/labs/leanalgorithms && cp algorithms/*.lean.cs /tmp/e2e/labs/leanalgorithms/
cd /tmp/e2e
dotnet run --project ~/Desktop/AetherAxis/aether-ctp-hosts/Aether.CtpConsole \
  -- --profile bot01 --run-strategy market-maker zx001 --real --max-volume 1
```

铁证链：SDK 认证/登录/结算确认 → Connected → Lean 算法 Roslyn 编译加载 → 引擎 Running → tick 流 → market-maker 阶梯挂单 → 交易所 sim_order 落库（排队中）→ Lean 订单回报 BrokerId 回填。订单真实穿越 Lean → CtpBrokerage → CtpSessionService → 官方 SDK → 前置 → 交易所。

## 资金机制（比特币模型）

新账户初始资金默认 0——场内现金**只能从链上 AQ 充值进来**（1 AQ = 1 现金）。结算轨道 opt-in：`AETHER_SETTLEMENT_RAIL=ledger-aq`（不设时看 `AETHER_LEDGER_CUSTODY_SEED`——设了才接链，否则 `none`，交易所照常跑）+ `AETHER_LEDGER_NODE_ADDR`（默认 127.0.0.1:17805）；接上后 `LedgerCashRail` 每秒对账（链侧 AQ IOU ↔ sim_quote_minted 台账：充值入现金 / 提现扣现金）。`AETHER_CTP_SIM_BALANCE` 仅纯期货开发沙盒（不起链桥、测策略）的水龙头。操作步骤（充值 / 提币 / bot 实例充 AQ / 本地带资金 E2E）见 ⑯ [`aether-coin-hosts/doc/funding-runbook.md`](../aether-coin-hosts/doc/funding-runbook.md)。

## 服务器部署

- `deploy-ctpsim`：交易所 + 前置两 Windows 服务（AetherCtpExchange / AetherCtpFront），win-x64 自包含发布
- `deploy-ctp-console`：console bot——publish 整替换 + 实例目录形态（实例 config.json 永不覆盖；labs/leanalgorithms 每次覆盖，`algorithms/` 是权威）
- `deploy-ledger`：**2026-10-27 起随虚拟币线迁到 ⑯ `aether-coin-hosts`**（本仓不再构建链三件套）
- 均 workflow_dispatch 手动触发：多仓 paths 联动已断——**依赖仓 push 后须手动 Run workflow 重新部署**
- **上线走 `scripts/deploy-from-local.sh ctpsim|console`（本机直传）**：2026-10-28 实测 GitHub runner → 服务器上传 54MB 需 23 分钟以上（本机 34s），CI 的 deploy workflow 暂只当构建契约（`deploy-dryrun.yml` 每 push 验 publish/bundle 与远端契约），带宽恢复后可切回

## 构建与测试

```bash
dotnet test Aether.CtpHosts.slnx    # tests/Aether.CtpHosts.Tests：2 例策略源编译锁定（algorithms/*.lean.cs 对 Runtime.Lean）
                                # 钱包 CLI 的 10 例回归随虚拟币线在 ⑯ aether-coin-hosts/tests/Aether.Ledger.Wallet.Tests
```

## 来历

2026-10-19 建为 sim-apps（Front.Host + Sim.Host 验证宿主）；2026-10-20 改名 aether-apps 并归一组合根——吸收 ① aether-ledger 的 LedgerNode/LedgerVerify、新增 CtpConsole 宿主与 algorithms/ 存档；2026-10-27 上午收入 LedgerWallet CLI 与链部署脚本（`deploy-ledger`），同日拆出到 ⑯ `aether-coin`（现 `aether-coin-hosts`）——虚拟币有独立的服务端/客户端/部署/故障域，不该和 CTP 宿主共享构建上下文；2026-10-28 改名 `aether-ctp-hosts`（虚拟币宿主已拆走，仓里只剩 CTP 宿主：`apps` 名不副实，且与 ⑮ `aether-ai-app` 的 app 概念冲突；结构与部署不动）。完整决策记录见工作区根 `MIGRATION.md`。
