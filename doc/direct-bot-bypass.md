# 直连交易所的 bot —— **临时旁路**，不是替代

> 面向：必须在前置「重连后不重新认证」缺陷修好之前，先让仿真市场有**真行情**的人。
> 一句话：两个 bot 现在直连交易所 `:17800` 取价/下单，绕开前置；**行情照旧由前置推给所有 CTP 客户端**，所以桌面端、其他客户端的行为一点没变。前置修好后本客户端整体退役（见 §8）。
> 相关：缺陷复盘 `../../ctp-net-client/doc/session-reauth-after-reconnect.md`；「为什么两个 bot 让行情动起来这么难」`two-bots-live-market.md`；装配边界 `../DESIGN.md`。
> 行情侧：投递契约与实测 `../../ctp-net-front/doc/md-tick-delivery.md`；客户端同值去重的判定与移除 `../../ctp-net-core/doc/md-tick-dedup.md`；tick 产生语义 `../../aether-exchange/doc/tick-emission.md`。

## 1. 为什么需要它

| 事实 | 后果 |
|---|---|
| 前置交易腿每 **240 帧**强制断连（`CtpFrontSession`），客户端重连后**不会重新认证**（`CtpSessionService` 的 `_hadLoggedIn && !_socketAlive` 闸门） | bot 静默失去下单能力（2026-10-29 实测 117 单被当作垃圾帧丢弃） |
| 行情由**交易所**产生（`SimMarketDataEngine.RecordTrade` / `OnBookChanged` → `EmitTick`），前置只做转发（`CtpFrontMdSession._rpc.TickPushed`） | 谁下单不重要，tick 一样推给所有订阅者 |
| 同一条断连也会吞掉**撤单请求** | 交易所里积下 54,738 张从未撤掉的 GFD 买单（106,192 手），盘口只有买盘、没有卖盘 ⇒ 永远不可能成交 ⇒ last 冻死 |

第三条是 2026-10-29 才查实的：**「行情不动」的一半根因不在策略，而在撤单丢失把盘口喂脏了**（处置见 §6）。

## 2. 形态

```
Aether.DirectBot.Console ──RemoteCtpSession──► SimRpcClient ──JSON-RPC──► 交易所 :17800
        │                                    (4B LE 长度前缀 + UTF-8 JSON，16MiB 帧上限)
        └─ Lean 引擎（同一套 CtpBrokerage / LeanAlgorithmBase / laptops 里的 .lean.cs 算法）
```

- **不引 ctp-net-client 的 native 依赖**：`ctp-net-front` 与 `Aether.Trading.Runtime.Lean` 都不带官方 CTP SDK/frameworks，所以自包含发布体积小、不需要 frameworks 拷贝。
- 复用前置**每个连接本来就在用**的适配器 `RemoteCtpSession : ICtpSession`（`ctp-net-front/src/Ctp.Net.Front/RemoteCtpSession.cs`），换一个单例注入即把整条 Lean 栈指向交易所：

```csharp
services.AddSingleton<ICtpSession>(_ => new RemoteCtpSession(exHost, exPort));
services.AddTradingRuntimeLean();
services.AddLeanAlgorithmServices(dataDir);
```

- **240 帧强断与重连认证这两条限制不经过旁路**：它们在 `CtpFrontSession`（前置交易腿）里，直连会话没有这一层，因此不会周期性掉线。
- 开户资金：**开户余额恒 0，没有任何开关**（2026-10-29 起「初始资金 / 开户赠金」`SimOptions.InitialBalance` 与 `AETHER_CTP_SIM_BALANCE` 已从代码删除）。账户里的钱只来自链上 AQ 真实转账——宿主 `LedgerCashRail` 对账后经 `CreditQuoteDeposit` 入账（1 AQ = 1 现金）。**新账户不充值就一分钱也没有，开不了仓**；这是刻意的，别再加回赠金。

## 3. 命名与共存边界（重要）

| 面向 | CTP 线（原路，暂时停用） | 直连线（本次） |
|---|---|---|
| 计划任务 | `AetherBot-botmm` / `AetherBot-botnoise`（**Disabled**，不删） | `AetherBotDirect-botmm` / `AetherBotDirect-botnoise` |
| 启动脚本 | `C:\Aether\ctp-console\run-bot{mm,noise}.cmd` | `C:\Aether\direct-bot\run-direct-{mm,noise}.cmd` |
| 程序 | `Aether.CtpConsole.exe`（走 CTP 二进制协议 → 前置） | `Aether.DirectBot.Console.exe`（走 JSON-RPC → 交易所） |
| 实例目录 | `C:\Aether\ctp-console\<实例>` | `C:\Aether\direct-bot\<实例>` |

- **只禁不删**：回退 = 停 `AetherBotDirect-*` + `Enable-ScheduledTask AetherBot-*` + 启动，不需要重新部署。
- **同账户两条线不可并存**：OrderRef 序号会撞。切换必须先停干净一条。
- 直连实例的 `config.json` 由部署脚本从 CTP 实例**派生**（只取 user/password/broker，去掉前置字段），账户语义不变；已存在的 `config.json` **永不被部署覆盖**。

## 4. 部署与切换

```bash
# 1) 本地构建 + 上传 + 远端解包（不动任何计划任务）
scripts/deploy-from-local.sh direct      # → C:\Aether\direct-bot\publish（整替换）
```
- 远端脚本 `scripts/directbot-remote-deploy.ps1`：解 tar → 每实例同步 `labs\leanalgorithms` → 打印状态。**它不替人决定市场何时停**。
- **部署前必须停 bot**：Windows 会锁住 `publish\` 里的 exe，停不掉就覆盖失败。

切换顺序（停旧 → 起新 → 等心跳）：

```powershell
foreach ($n in 'botmm','botnoise') { Stop-ScheduledTask "AetherBot-$n"; Disable-ScheduledTask "AetherBot-$n" }
Get-Process Aether.CtpConsole -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($n in 'botmm','botnoise') { Start-ScheduledTask "AetherBotDirect-$n" }
```

## 5. 运维信号

| 信号 | 位置 | 判读 |
|---|---|---|
| 心跳（每 10s 原子重写） | `C:\Aether\direct-bot\<实例>\directbot.heartbeat.json` | `account / engine / ticks / orders / trades / lastTickAtUtc / updatedAtUtc / pid / exchange` |
| 交易所日志 | `C:\Aether\ctpsim-data\ctpsim-exchange.log` | 启动横幅会打印账户数、上市合约数、**开户余额恒 0（现金只能来自链上 AQ 充值）** |
| watchdog v2 | `C:\Aether\build-tools\watchdog.ps1`（任务 `AetherBot-watchdog`） | 直连家族判活 = 心跳新鲜度 + `engine -ne 'Running'`；CTP 家族只在其任务 `Running` 时才管 |

**默认不写日志**（2026-10-29 教训：客户端 tick 风暴把 8.6GB/天 写进磁盘、拖死整机 I/O）。排查时临时把 launcher 的 `>NUL 2>&1` 改成 `>> 实例目录\probe.log 2>&1`，**用完改回**。编码陷阱：bot 的 stdout 是 **GBK**，`CtpConsole` 的探针输出是 **UTF-8**——解错编码只会得到乱码，不会报错。

## 6. 2026-10-29：重置 venue（授权后执行）

**症状**：`[Tick] ax001 last=98.45 bid=98.77/1607 ask=0/0`——last 一动不动，成交恒为 0。

**证据链**（探针 + 库直查）：

| 观测 | 值 |
|---|---|
| `sim_position` | botmm 多 11254 / **空 9226**；botnoise **空 2030** |
| `sim_order`（status='3' 未成交在队列） | botnoise **54,738 张 / 106,192 手**，botmm 1,415 张，**全是买单** |
| 做市商自述 | `[MM] 仓位视图 Portfolio=-9226 预算 买9426/卖-9026` |
| 结果 | 卖侧补挂预算 `仓限 − 敞口 = 200 + (−9226) ≤ 0` ⇒ **卖侧一行都不挂**；同时又以为超限，每轮市价买 5 手想拉回（`[Brokerage] 手数封顶: 9026 → 5 手`），而买回需要卖盘 ⇒ **死锁** |

**处置**：`scripts/reset-venue.ps1`（会先备份、备份 <100KB 拒绝删除）：

1. 交易所服务环境里**不设任何资金变量**（`AETHER_CTP_SIM_BALANCE` 已废；重跑 reset 脚本会顺手把它从服务环境里剔掉）——重置后老账户的现金随库一起清空，新账户开户即 0，谁要开仓谁先真实转 AQ。**bot 不需要人工充值**：它们在链上有真实 AQ（各 300 万），宿主第一轮对账会按 per-account IOU 差额自动 `CreditQuoteDeposit` 入账（`LedgerCashRail`，1 s 一轮）；
2. 停直连 bot → 停前置 → 停交易所；
3. 备份 `venue\ctp-sim.db` + `-wal` + `-shm` 到 `venue\backup-<时间戳>`，校验体积后删除；
4. 起交易所（等 `:17800` 就绪）→ 起前置 → 起 bot（心跳每实例 150s 内必须出现）。

**重置后 2 分钟**：成交 68 笔，成交价 99.97–100.03 真实走动；双边挂单同时在簿；持仓有界（botnoise 多 15；botmm 多 6 / 空 21）。

> 计划任务 `AetherBot-watchdog` 里的 `watchdog.hold` 会抑制看护 30 分钟（部署/排障窗口）；超过 30 分钟按「忘记删」处理，自动失效。

## 7. 顺带查实、尚未修的三件事

| # | 事实 | 为什么重要 |
|---|---|---|
| 1 | **前置报的持仓是错的**：经前置查 botmm 得「多 43 / 空 36」，交易所真值是「多 11254 / 空 9226」 | 桌面端看到的持仓不可信；直连客户端自己查交易所是对的，所以不是交易所的错 |
| 2 | **撤单在断连时静默丢失**（54,738 张僵尸单的来源） | 与 240 帧强断同族：断连瞬间仍在飞的撤单请求随 socket 一起消失，客户端以为撤成功（票据已终态），交易所里订单还活着 |
| 3 | 经前置拿到的回报/行情字段有乱数（成交回报 `0@1.59E-317`、成 `1079574528`） | F001 模板对部分 push 形状仍有解码缺口；直连路径不受影响 |
| 4 | ~~客户端把「内容相同、时间不同」的 tick 当重复丢掉~~ **已修（2026-10-29）**：移除 `ctp-net-core` 的同值去重，回调逐条投递 | 同一帧**并没有**被重复投递（前置按逐字节去重，实测 0 次），是客户端判据错，还会自造「时间戳不动」的假死。修后端点实测：前置推 11 帧 / 客户端交付 12 次（11 + 订阅快照），零丢失。详见 `../../ctp-net-core/doc/md-tick-dedup.md` |

## 8. 退役条件

同时满足即退役本客户端，回到「bot 连前置」：

1. 前置重连后**重新认证**（P0，见 `session-reauth-after-reconnect.md`）；
2. 断连不再丢撤单（撤单要能幂等重发或在重连后对账）；
3. 240 帧强制断连被解除或客户端不再被它影响。

退役动作：停 `AetherBotDirect-*` 并删除任务 → `Enable-ScheduledTask AetherBot-*` + 启动 → `deploy-from-local.sh console` 重推 console bot（可选）→ 删除 `C:\Aether\direct-bot` 与 `Aether.DirectBot.Console` 工程。

> 上面三条怎么落地、按什么顺序做，见 `../../ctp-net-client/doc/session-lifecycle.md`（会话状态机 + epoch、在途账本与对账、240 断连的三种消除路径、对真柜台的待实测清单）。

## 9. 自检命令

```powershell
# 心跳（两个实例都该是 engine=Running，且 trades 在长）
Get-Content C:\Aether\direct-bot\botmm\directbot.heartbeat.json -Raw -Encoding UTF8
Get-Content C:\Aether\direct-bot\botnoise\directbot.heartbeat.json -Raw -Encoding UTF8

# 任务与进程（应为 2 个 Aether.DirectBot.Console，且 0 个 Aether.CtpConsole）
Get-ScheduledTask -TaskName 'AetherBotDirect-*' | Select-Object TaskName, State
Get-Process | Where-Object { $_.ProcessName -like 'Aether.*Console' } | Select-Object ProcessName, Id

# 盘口真值（TcpClient 直连交易所 JSON-RPC，不经过前置）
#   op=quotes 回 bid/ask/last；比经前置读到的字段可信（§7 第 3 条）
```

前置侧验证（桌面端等价物）：最硬的证据是**前置日志**里的行情推送行——

```
[CTP-Front-MD] → 推送 tick: ax001 last=99.92 bid=99.92 ask=99.95
[CTP-Front-MD] → 推送 tick: ax001 last=99.95 bid=99.93 ask=99.95
```

last 前后不同，就说明订阅者拿到的是活行情。**别拿 `CtpConsole` 的 `[tick]` 行当验收工具**：交易腿被乱帧刷屏（§7 第 3 条）时控制台不打印 tick，前置其实已经推了。
