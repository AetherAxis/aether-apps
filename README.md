# Aether.Apps

组合根宿主仓——各核心库的可执行宿主 + console 算法 bot + 策略源存档。**不发 NuGet**（部署/验证单元，非被引用单元；跨仓依赖以本地 ProjectReference 组装）。

## 组成

| 宿主 | 说明 |
|---|---|
| `Aether.Exchange.Host` | 仿真交易所（:17800；`AETHER_CTP_SIM_BALANCE` 设新账户初始资金） |
| `Aether.CtpFront.Host` | CTP 线协议前置（交易 :17801 / 行情 :17802，连 :17800） |
| `Aether.Ledger.Node` | 链节点（:17805，链文件 ~/.aether/ledger.chain） |
| `Aether.Ledger.Verify` | 独立链验证器（退出码 0=有效 / 2=无效） |
| `Aether.CtpConsole` | console 算法 bot：Lean 实盘引擎 + CTP SDK 会话 + REPL——无 GUI 依赖，SSH 挂机/无人值守/轻量手动交易 |
| `algorithms/` | Lean 策略源存档（market-maker / noise-taker）——运行时从实例目录 `labs/leanalgorithms/` 加载 |

**一个目录一个完整实例**（console）：config.json / labs/ / ctp.db / lean-algo-cache 全部默认落当前工作目录——双 bot = 两个目录各跑一个，完全隔离；发布目录拷到哪就在哪跑。

## 本地 E2E（全链实跑）

```bash
# 服务链（交易所带初始资金；前置；链节点）
AETHER_CTP_SIM_BALANCE=1000000 dotnet run --project Aether.Exchange.Host   # :17800
dotnet run --project Aether.CtpFront.Host                                  # :17801/:17802
dotnet run --project Aether.Ledger.Node                                    # :17805

# console 直跑（在实例目录内运行——config.json 按 cwd 发现）
mkdir -p /tmp/e2e/labs/leanalgorithms && cp algorithms/*.lean.cs /tmp/e2e/labs/leanalgorithms/
cd /tmp/e2e
dotnet run --project ~/Desktop/AetherAxis/aether-apps/Aether.CtpConsole \
  -- --profile bot01 --run-strategy market-maker zx001 --real --max-volume 1
```

铁证链：SDK 认证/登录/结算确认 → Connected → Lean 算法 Roslyn 编译加载 → 引擎 Running → tick 流 → market-maker 阶梯挂单 → 交易所 sim_order 落库（排队中）→ Lean 订单回报 BrokerId 回填。订单真实穿越 Lean → CtpBrokerage → CtpSessionService → 官方 SDK → 前置 → 交易所。

## 来历

2026-10-19 建为 sim-apps（Front.Host + Sim.Host 验证宿主）；2026-10-20 改名 aether-apps 并归一组合根——吸收 ① aether-ledger 的 LedgerNode/LedgerVerify、新增 CtpConsole 宿主与 algorithms/ 存档。完整决策记录见工作区根 `MIGRATION.md`。
