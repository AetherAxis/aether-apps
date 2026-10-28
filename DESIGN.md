# Aether.CtpHosts 设计说明

> 写「怎么设计 / 为什么这样设计 / 还能往哪升级」。工作区边界与迁移决策以根 `MIGRATION.md` 为准。

## 1. 定位与边界

组合根仓：把兄弟仓的库组装成进程，并存 bot 策略源。**不发 NuGet、不定义契约、不做业务抽象**——跨仓依赖一律本地 `ProjectReference`，按 `<dep>/` 并排 checkout。归本仓的只有 CTP 三个宿主、`algorithms/`、部署脚本（2026-10-27 起虚拟币三宿主与 `deploy-ledger` 迁到 ⑯ `aether-coin-hosts`）。

## 2. 项目组成

| 宿主 | 职责 | 关键类型 | 出向依赖 |
|---|---|---|---|
| Exchange.Host | 交易所 :17800；可选结算轨道（默认 `ledger-aq`） | `SimTradingCore` / `SimRpcServer` / `SimStore` / `SettlementRailFactory` / `LedgerCashRail : IExternalCashRail` | aether-exchange、contracts、aether-ledger |
| CtpFront.Host | 前置 :17801/:17802 | `CtpFrontServer` / `CtpFrontMdServer` / `FrontHostOptions` | ctp-net-front、exchange.Rpc、contracts |
| Ledger.Node | 链节点 :17805 | `LedgerNode` / `LedgerNodeOptions` | aether-ledger |
| Ledger.Verify | 离线验证器（0/2） | `LedgerVerifier` | aether-ledger |
| Ledger.Wallet | 钱包 CLI（资金操作面） | `WalletCli` / `Args` | aether-ledger、exchange.Rpc |
| CtpConsole | bot + 交互客户端 | `ICtpSession` / `ILiveTradingRuntime` / `LeanAlgorithmRegistry` | ctp-net-client、exchange.Rpc、aether-trading |
| tests | 策略编译锁定（2 例）+ 钱包 CLI 回环（10 例） | `LeanAlgorithmScriptCompiler` / `WalletCli` | aether-trading（Runtime.Lean）、Ledger.Wallet |

## 3. 关键设计决策

### 3.1 组合根只引不产
- **决策**：不打包、不被引用，只用 `ProjectReference` 组装。
- **理由**：契约正典在各仓，持副本必漂移；门禁在本仓验证装配。
- **代价**：需 7 仓就位；依赖仓改动不触发 CI。

### 3.2 一个目录一个实例
- **决策**：console 的 `config.json`/`labs`/`ctp.db`/`lean-algo-cache` 全按 cwd 解析，不碰 `~/.aether`。
- **理由**：双 bot = 两目录各跑一份。部署同构：`publish/` 整替换，实例 config 永不覆盖，`labs` 每次覆盖。
- **代价**：状态散落，无配置中心。

### 3.3 资金 = 链上充值，桥 opt-in
- **决策**：场内现金只能由链上 AQ 派生；设 `AETHER_LEDGER_CUSTODY_SEED` 才起桥与 1s 对账；`AETHER_CTP_SIM_BALANCE` 仅沙盒水龙头。
- **理由**：链是唯一真相，重启重建 IOU，崩溃窗口自愈；资金可选，开发不必先起链。
- **代价**：memo 写错不归账；需种子 + 链节点。

### 3.4 三进程分离，形态镜像真实世界
- **决策**：交易所/前置/链节点独立进程；前置只做协议适配，永不直连撮合；交易所默认只绑回环（**生产服务器实际配 `0.0.0.0:17800`**——外部前置/桌面要直连，靠登录鉴权；2026-10-28 核对）。服务宿主统一 `Host` + `AddWindowsService()`，console 例外手写 `ServiceCollection`。
- **理由**：客户端填 `tcp://ip:17801` 与 SimNow 同形，可直换真前置；启动即监听；SCM 与本地零差异。
- **代价**：四端口、进程级排障；两套生命周期，漏调即 1053 超时（已两次踩过）。

### 3.5 策略源同仓锁定 + 构建期拷入资产
- **决策**：`algorithms/*.lean.cs` 既是部署源也是测试输入；`LeanData` 用 Content glob、macOS frameworks 用 post-build 拷入。
- **理由**：改一处即被测试覆盖；Lean 的 `<exe>/LeanData` 回退与 `@rpath` 要求资产在输出目录。
- **代价**：测试依赖仓根 `slnx`；输出变大。

## 4. 运行与验证

- 服务链：Node → Exchange（带 seed 才接桥）→ Front；充值/提币见 ⑯ `aether-coin-hosts/doc/funding-runbook.md`。
- bot：实例内 `--profile <名> --run-strategy market-maker <合约> --real --max-volume 1`；先 JSON-RPC 清场再等会话≤90s。
- 验证：`dotnet test Aether.CtpHosts.slnx` = 2 例（编译 + `IsAssignableFrom QCAlgorithm`）；CI 先 build 再 `--no-build` test。
- 部署：`deploy-ctpsim`（两服务）、`deploy-ctp-console`（publish + 实例），均手动。链三件套的 `deploy-ledger` 随虚拟币线迁到 ⑯ `aether-coin-hosts`（2026-10-27）。
- 部署脚本是**服务 env 的唯一权威**（步骤 4.6，每次运行钉定）：交易所 `AETHER_CTP_SIM_ADDR=0.0.0.0:17800`
  （外部前置可直连，本地调试用）、前置 `AETHER_CTP_EXCHANGE_ADDR=127.0.0.1:17800`（同机走回环）。
  此前 env 只在 setup 时写一次、部署不碰，两边漂移后的症状是：17801/17802 正常，但任何登录回
  `90012 无法连接：Connection refused`，外部看跟「交易所没开」一模一样（2026-10-24 实测）。

## 5. 升级记录（2026-10-24）

| 项 | 变更前 | 变更后 | 依据 / 验证 |
|---|---|---|---|
| Extensions.Hosting / Hosting.WindowsServices / Logging.Console | 10.0.10 | 10.0.13 | 三宿主统一；assets 已 10.0.13 |
| NET.Test.Sdk | 17.14.1 | 18.11.0 | 测试栈上 18 |
| xunit.runner.visualstudio | 3.1.4 | 4.0.1 | 配套 Test.Sdk 18 |
| coverlet.collector | 6.0.4 | 10.1.0 | 未启用覆盖率 |
| 中央传递钉 | 未启用 | `CentralPackageTransitivePinningEnabled=true` | 覆盖传递链版本 |
| ServiceModel.Primitives/Private | 4.4.0（NetMQ 传递） | 4.10.3 | GHSA-6xh7 / GHSA-jc8g |
| Drawing.Common | 4.7.0（Windows.Extensions 传递） | 4.7.3 | GHSA-p9wx |
| Cryptography.Pkcs | 6.0.1（Xml 传递） | 6.0.5 | GHSA-555c |
| WinHttpHandler | 未进图 | 4.7.2（声明） | GHSA-rxg9；net10.0 图不含 |

**为什么必须各钉一份**：NuGet 审计按**每个项目自己的还原图**算，`ProjectReference` 是不透明边界——⑥ 钉过不会进入本仓图，本仓仍被审计出同名漏洞。命中 `CtpConsole` 与 `tests`（`Runtime.Lean` → Lean.Engine 2.5.18193 带入 2018 年系统包）；服务宿主图不含 Lean，CPM 后续生效。只钉同主线末位、不跳 8.x/10.x（同线只换实现不换契约）。本轮未跑 build/test。

## 6. 待升级项

| # | 优先级 | 项目 | 现状证据 | 建议 | 代价 / 风险 |
|---|---|---|---|---|---|
| 1 | ~~P1~~ 已关闭 | LedgerWallet 未迁入 | 2026-10-27 收入本仓，**同日随虚拟币线整体拆出到 ⑯ `aether-coin-hosts`**（含 10 例回环测试与 `deploy-ledger`） | 完成（落点是 ⑯） | — |
| 2 | P2 | 部署脚本旧名 | `ctp-console-remote-deploy.ps1:61,69` 用 `Acme.*` | 改为 `Aether.CtpConsole` | 恒报无实例，启动命令错 |
| 3 | P2 | 无 SIGTERM 处理 | `Program.cs:477-509` 只挂 `CancelKeyPress` | 加 `PosixSignalRegistration` | 断连/停服不走撤单腿 |
| 4 | P2 | CI 无审计步 | `ci.yml` 仅 build + test | 加 `--vulnerable` 审计 | 漏钉不会红 |
| 5 | P3 | WinHttpHandler 惰性 | 图无此包，仅中央表有名 | 核对 TFM 后取舍 | 误导「已修」 |
| 6 | P3 | 测试仅 2 例 | 只覆盖策略编译 | 补 HostOptions env 用例 | 装配无回归网 |
| 7 | P3 | 部署仅手动 | `deploy-*.yml` 只有 `workflow_dispatch` | 加 `repository_dispatch` | 易漏部署 |
| 8 | P4 | 文档漂移 | `readme.md:28` 旧路径；两 props 重复属性 | 更正、去重 | 纯维护性 |

## 升级补记（2026-10-26）：console `[tick]` 逐笔打印默认关

日志降噪（用户「之前调试才有的日志，真实部署不需要那么多——日志文件太大」）：`[tick]` 逐笔行
默认不打印，env `AETHER_CTP_TICK_TRACE=1` 开（与 ② `CtpMarketData` 的 `[ctp-md] Tick` 同一开关）；
REPL `tick on` 运行期开不受影响。报单/成交/状态/连接照常打印（bot 运行证据面）。实测动机：
bot 直连远程前置（旧构建无同值去重）行情活跃 ~2900 笔/s，逐笔双路日志单 bot 半天 300MB+。

## 迁移补记（2026-10-27）：链部署与钱包 CLI 收进本仓（同日拆出）

> **2026-10-27 晚修正**：本节记录的「收进本仓」当天就回退了——虚拟币三宿主（Node/Wallet/Verify）、
> `deploy-ledger` 与 10 例钱包测试整体拆到 ⑯ `aether-coin-hosts`。理由见该仓 README：虚拟币有自己的
> 服务端/客户端/部署/故障域，不该和 CTP 宿主共享构建上下文。下面保留当时的过程记录，结论以 ⑯ 为准。

生产链节点 :17805 此前由 **monorepo** `deploy-ledger.yml` 构建（`src/Aether.Ledger` + `apps/LedgerNode|LedgerWallet|LedgerVerify`
的仓内副本，push 自动触发）——① aether-ledger 拆分后，① 的改动**到不了服务器**，monorepo 副本继续演进的
风险是「两边都像正典」。本次收口：

| 项 | 变更前 | 变更后 |
|---|---|---|
| 构建源 | monorepo 副本 | 本仓宿主 + ① `aether-ledger` + ④ `aether-exchange`（`deploy-ledger.yml` 多仓 checkout） |
| 触发 | `push` 触及链路径自动 | `workflow_dispatch`（与另两个 deploy 同纪律；链重启 = 资产层短暂停服，不做无人值守） |
| 钱包 CLI | monorepo `apps/LedgerWallet`（引 `Acme.Ctp.Sim`） | 本仓 `Aether.Ledger.Wallet`（引 ④ `Aether.Exchange.Rpc`；`CtpBusinessException` → `ExchangeBusinessException`） |
| 版本可追溯 | `VERSION` = 单一 sha | `VERSION` = `<apps-sha>+<ledger-sha>`——「① 的改动上没上线」有直接证据 |
| 回归网 | 无（靠手工） | `LedgerWalletCliTests` 10 例：进程内真链节点跑 CLI（info/balance/transfer/deposit/参数校验/断连） |

**为什么 CLI 拆出 `WalletCli`**：monorepo 版是 top-level 语句，测不了；迁仓是「换依赖接线」的动作，
最容易错的就是异常类型与登录载荷——留一个能注入 `TextWriter` 的入口，测试才能在同进程真跑。
`withdraw` 仍不在单测里（要起交易所宿主 + custody 桥），属 `doc/funding-runbook.md` §4 的集成验收。
