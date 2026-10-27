# Aether.Apps

组合根宿主仓——各核心库的可执行宿主 + console 算法 bot + 策略源存档。**不发 NuGet**（部署/验证单元，非被引用单元；跨仓依赖以本地 ProjectReference 组装）。

## 组成

六个宿主分属**两套系统**，两组之间零引用（依赖不同、部署不同、故障域不同）：

### CTP 交易线（3 个）

| 宿主 | 说明 |
|---|---|
| `Aether.Exchange.Host` | 仿真交易所（:17800；资金 = 链上 AQ 充值对账——见「资金机制」；`AETHER_CTP_SIM_BALANCE` 仅沙盒水龙头） |
| `Aether.CtpFront.Host` | CTP 线协议前置（交易 :17801 / 行情 :17802，连 :17800） |
| `Aether.CtpConsole` | console 算法 bot：Lean 实盘引擎 + CTP SDK 会话 + REPL——无 GUI 依赖，SSH 挂机/无人值守/轻量手动交易 |

### 虚拟币线（3 个：一个服务端 + 一个客户端 + 一个验证器）

| 宿主 | 形态 | 说明 |
|---|---|---|
| `Aether.Ledger.Node` | **服务端** | 链节点（:17805，链文件 `~/.aether/ledger.chain`）。链文件是唯一真相，重启 = 全量重放 |
| `Aether.Ledger.Wallet` | **客户端** | 钱包 CLI（`new`/`info`/`balance`/`transfer`/`deposit`/`withdraw`）——唯一的资金操作面，引 ① 账本 + ④ RPC；**种子 = 全部财富** |
| `Aether.Ledger.Verify` | 工具 | 独立链验证器（退出码 0=有效 / 2=无效）——不需要任何活进程即可验链 |

三个宿主只引 ① `aether-ledger`（钱包另引 ④ `Aether.Exchange.Rpc` 做提现），和上面 CTP 三宿主**互不引用**。
这两组如果将来发布节奏分化（虚拟币要独立发版/独立值守），可以整体迁到独立仓——现在合住是因为它们共享同一条
「组合根只引不产」的纪律和同一套部署脚本习惯。

### 策略源

| 目录 | 说明 |
|---|---|
| `algorithms/` | Lean 策略源存档（market-maker / noise-taker）——运行时从实例目录 `labs/leanalgorithms/` 加载 |

## 目录布局（本仓是唯一例外：没有 `src/`）

仓根直接放宿主目录，和其余 13 仓的 `src/` 约定不一致——**有意为之**，不是历史遗留：

- 这个仓是**组合根**：每个 `<X>.Host` 就是一个可执行单元，发布 = `<X>.Host/bin/…` 整个目录；
  中间没有被引用的库需要归类，多一层 `src/` 只多一次路径跳转（部署脚本、CI、runbook 的路径都要跟着长）。
- 目录名 = 程序集名 = 发布单元名，`dotnet run --project Aether.Exchange.Host` 直接可读可敲。

约束：**新增宿主也放仓根、命名 `<域>.<角色>`**（Host / Node / Verify / Console / Wallet）；仓根不要新增库项目，
真要抽公共库，它属于对应引擎仓（①④⑤⑥⑦），不属于这里。`doc/`（runbook）、`scripts/`（远程部署）、
`tests/`（宿主级断言）、`algorithms/`（策略源存档）是仓级公共资产，平级放置。

**一个目录一个完整实例**（console）：config.json / labs/ / ctp.db / lean-algo-cache 全部默认落当前工作目录——双 bot = 两个目录各跑一个，完全隔离；发布目录拷到哪就在哪跑。

## 本地 E2E（全链实跑）

```bash
# 服务链（链桥 opt-in：交易所宿主设 AETHER_LEDGER_CUSTODY_SEED 才接链；不设 = 桥不启动照常跑）
dotnet run --project Aether.Exchange.Host   # :17800
dotnet run --project Aether.CtpFront.Host                                  # :17801/:17802
dotnet run --project Aether.Ledger.Node                                    # :17805

# console 直跑（在实例目录内运行——config.json 按 cwd 发现）
mkdir -p /tmp/e2e/labs/leanalgorithms && cp algorithms/*.lean.cs /tmp/e2e/labs/leanalgorithms/
cd /tmp/e2e
dotnet run --project ~/Desktop/AetherAxis/aether-apps/Aether.CtpConsole \
  -- --profile bot01 --run-strategy market-maker zx001 --real --max-volume 1
```

铁证链：SDK 认证/登录/结算确认 → Connected → Lean 算法 Roslyn 编译加载 → 引擎 Running → tick 流 → market-maker 阶梯挂单 → 交易所 sim_order 落库（排队中）→ Lean 订单回报 BrokerId 回填。订单真实穿越 Lean → CtpBrokerage → CtpSessionService → 官方 SDK → 前置 → 交易所。

## 资金机制（比特币模型）

新账户初始资金默认 0——场内现金**只能从链上 AQ 充值进来**（1 AQ = 1 现金）。链桥 opt-in：交易所宿主设 `AETHER_LEDGER_CUSTODY_SEED`（hex 64；不设 = 桥不启动，交易所照常跑）+ `AETHER_LEDGER_NODE_ADDR`（默认 127.0.0.1:17805）；桥启动后 `LedgerSpotLink` 每秒对账（链侧 AQ IOU ↔ sim_quote_minted 台账：充值入现金 / 提现扣现金）。`AETHER_CTP_SIM_BALANCE` 仅纯期货开发沙盒（不起链桥、测策略）的水龙头。操作步骤（充值 / 提币 / bot 实例充 AQ / 本地带资金 E2E）见 [`doc/funding-runbook.md`](doc/funding-runbook.md)。

## 服务器部署

- `deploy-ctpsim`：交易所 + 前置两 Windows 服务（AetherCtpExchange / AetherCtpFront），win-x64 自包含发布
- `deploy-ctp-console`：console bot——publish 整替换 + 实例目录形态（实例 config.json 永不覆盖；labs/leanalgorithms 每次覆盖，`algorithms/` 是权威）
- `deploy-ledger`：链三件套（AetherLedgerNode 服务 + 钱包 CLI + 独立验证器），**2026-10-27 起由本仓构建**——
  源 = 本仓宿主 + ① `aether-ledger` + ④ `aether-exchange`（钱包 withdraw 的 RPC 客户端）；
  旧 monorepo `deploy-ledger.yml` 的 push 触发已停用（`①` 的改动从此随本 workflow 上线，不再是「改了到不了服务器」）
- 均 workflow_dispatch 手动触发：多仓 paths 联动已断——**依赖仓 push 后须手动 Run workflow 重新部署**
  （`deploy-ledger` 会重启链节点 = 资产层短暂停服数秒，交易所桥轮询自动重连）

## 构建与测试

```bash
dotnet test Aether.Apps.slnx    # tests/Aether.Apps.Tests：12 例——2 例策略源编译锁定（algorithms/*.lean.cs 对 Runtime.Lean）
                                # + 10 例钱包 CLI 回归（进程内起真链节点跑 WalletCli，锁住 ① 账本 + ④ RPC 的接线）
```

## 来历

2026-10-19 建为 sim-apps（Front.Host + Sim.Host 验证宿主）；2026-10-20 改名 aether-apps 并归一组合根——吸收 ① aether-ledger 的 LedgerNode/LedgerVerify、新增 CtpConsole 宿主与 algorithms/ 存档；2026-10-27 收入 LedgerWallet CLI 与链部署脚本（`deploy-ledger`），旧 monorepo 链部署链退场。完整决策记录见工作区根 `MIGRATION.md`。
