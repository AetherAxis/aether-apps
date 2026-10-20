# algorithms/

Lean 机器人算法的**仓库分发副本**——克隆本项目即可看到这两个交易所机器人策略。
「bots 即策略」路线：机器人不是服务端内嵌代码，而是普通客户端账户上跑的 Lean 算法（`.lean.cs` = `LeanAlgorithmBase`（`QCAlgorithm`）子类，纯 C# 无 front-matter，`NoteNodeType.LeanAlgorithm`，仅 Stockapp 租户）。

| 文件 | 说明 |
|---|---|
| `market-maker.lean.cs` | 做市商——围绕最新成交价上下各挂多档阶梯限价（带宽保留式维护）+ 预算式库存偏斜 + 超限市价减仓 |
| `noise-taker.lean.cs` | 噪声交易者——按节奏随机方向市价吃单 + 净持仓软钳 + 固定种子可复现 |

两者组合 = 交易所的市场活力：做市挂深度、噪声吃深度，互为对手。

## 怎么跑

运行时 Aether 从**本机 tenant 目录**的 `labs/leanalgorithms/*.lean.cs` 扫描 Lean 算法——**本目录不会被运行时扫描**（同 `apps/Stockapp/.../mcp-servers/desktop.json` 模板的做法）。要用这两个机器人，把 `.lean.cs` 文件复制到：

```
macOS:   ~/Library/Application Support/Aether/Desktop-Stockapp/labs/leanalgorithms/
Windows: %LOCALAPPDATA%\Aether\Desktop-Stockapp\labs\leanalgorithms\
```

然后：桌面端 Stockapp → 实盘 tab → 策略引擎选 `LeanAlgorithm` → 算法选 `market-maker` / `noise-taker` → 合约填 `zx001`（本地仿真柜台默认上市合约；自建交易所/服务器部署见 `apps/CtpSim` + `scripts/ctpsim-*.ps1`）→ 勾「真实下单」→ sim 前置登录后启动。

**双 bot = 两个桌面实例 + 不同账户**（互为对手撮合；勿两实例登同一账户——OrderRef 撞号）。行情/价格由订单簿真实撮合产生（挂单即推 tick、成交驱动最新价）。

## 维护

在资料库编辑器改了 labs 里的算法、想把改动记录进仓库时：把文件拷回本目录即可——`tests/Acme.Trading.Runtime.Tests/LeanBotStrategyCompileTests` 直接编译**本目录**的文件做锁定（拷回即被测试覆盖，无需再同步任何镜像副本）。

## post-only（可选，做市防误吃）

下单时给 Lean 订单的 `tag` 加 `post-only`（不区分大小写），该限价单只入簿不吃单——插入时会交叉（吃对手挂单）则交易所整单拒收（90018），保证永远付 maker 费率：

```csharp
LimitOrder(_symbol, volume, price, tag: "MM买 post-only");
```

仅限价单支持；市价单带此 tag 会被映射器拒单。手续费按品种：`zx` 系列为 maker 2bp / taker 5bp（BTC 对标），真实品种双边同费。
