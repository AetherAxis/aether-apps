# 交易所资金 Runbook（AQ 充值 / 提币）

> **定位**：交易所场内资金的操作手册——钱从哪来、怎么充、怎么提。
> 链自身运维（节点/种子/备份/排障）见 ① aether-ledger `doc/ledger-runbook.md`；服务器部署见本仓 `.github/workflows/deploy-*.yml` 与 `scripts/`（链 = `deploy-ledger`，2026-10-27 起本仓构建）。
> ⚠ 本文档不含真实种子/密钥/服务器地址——它们只存在于你的离线备份里。

## 0. 全景（30 秒）

**场内现金 = 链上 AQ 的托管凭证**（1 AQ = 1 现金）——交易所永不发钱，新账户初始资金恒 0。AQ 的源头是国库 treasury（创世 210 亿），由你分发。

```
treasury 国库（210 亿 AQ——AQ 唯一源头）
   │ ① transfer（treasury 签名）
   ▼
用户/运营钱包（种子 = 财富，本地生成零网络）
   │ ② deposit：转 AQ 到 custody + memo = 交易所账户（充值钱包签名）
   ▼
custody 托管地址（种子 = 交易所托管责任）
   │ 桥 500ms 轮询 + LedgerSpotLink 1s 对账（自动；1~2 秒到账）
   ▼
交易所账户场内现金（1 AQ = 1 现金；提币 withdraw 反向，custody 签名出金）
```

## 1. 前提与操作面

| 前提 | 确认方式 |
|---|---|
| 链节点 Running（:17805） | `wallet info`（高度正常推进） |
| 交易所桥已启用 | 交易所日志有 `[LEDGER-BRIDGE] 交易所桥就绪`——宿主 env 已设 `AETHER_LEDGER_CUSTODY_SEED`（**不设 = 桥不启动，充值无处归账**） |
| treasury 种子已离线备份 | 新链生成时打印在节点日志（纪律见 ① runbook） |
| custody 地址已知 | `wallet balance --seed <custody种子>` 输出的地址 |

**钱包 CLI**（`new` / `info` / `balance` / `transfer` / `deposit` / `withdraw`）：

- 服务器上 `C:\Aether\LedgerWallet\Aether.Ledger.Wallet.exe`（随链三件套由**本仓** `deploy-ledger.yml` 部署——2026-10-27 起源 = 本仓 `Aether.Ledger.Wallet` + ① 账本 + ④ RPC）；本地 `dotnet run --project ~/Desktop/AetherAxis/aether-apps/Aether.Ledger.Wallet -- <命令>`。下文简写 `wallet`。
- 端点：链侧 `--node`（默认 127.0.0.1:17805）；提币 `--exchange`（默认 127.0.0.1:17800）。服务器上直跑用默认值即可；本地远程操作加 `--node <服务器IP>:17805`。
- ⚠ **`transfer` / `deposit` 的 `--asset` 缺省是 coin（通道已退役）——AQ 操作必须显式 `--asset quote`**（`withdraw` 的缺省已是 quote）。

## 2. 给交易所账户充 AQ

```bash
# ① 国库先发 AQ 给充值的钱包（它还没有 AQ 时）
wallet transfer --seed @treasury.seed --to <钱包地址> --amount 50000 --asset quote --node <链节点>

# ② 充值（--account = 交易所账户键，必填）
wallet deposit --seed @运营钱包.seed --custody <custody地址> --account <userId> --amount 50000 --asset quote --node <链节点>

# ③ 1~2 秒后验证（交易所日志）
#    [LEDGER-BRIDGE] 充值（计价币）：账户 <userId> +50000.00000000（from=…）
#    [LEDGER-QUOTE]  现金入账 50000 → <userId>（AQ IOU …）
```

归账规则（① LedgerBridge 的诚实边界）：

- **memo（`--account`）必填**：漏了或写错 ⇒ 不归账（或归到写错的账户）——资产不丢（链上 custody 仍在），需 custody 侧链上划转处理
- **coin（AEX）充值跳过**（币通道 2026-10-10 退役）：误转 coin 到 custody 只留 ⚠ 日志、不进交易所——coin 变现走链上转账，不经过交易所
- 1 AQ = 1 现金，8 位小数原子级精确；不足 1 AQ 的原子零头不入账（目标值稳定，不反复触发对账）
- **console bot 的账户键 = 实例 config.json 里该 profile 的 `user` 字段**（如 `botmm`）——`--account botmm` 充值后 bot 的 Lean 风控即见可用资金

## 3. 提币（交易所 → 链上）

```bash
wallet withdraw --user <userId> --password <登录密码> --to <钱包地址> --amount 100 --exchange <交易所JSON-RPC>
```

- 闸门 = **静态可用现金**（锁定保证金/挂单冻结/浮盈不可提——浮盈须平仓落袋）
- 登录即鉴权（绑定账户 = 出金账户）。**双密码域**：前置登录（CTP 域）与提币（JSON-RPC 明文域）是两列独立密码——首次提币输入的密码即设密（不校验旧值），连错 5 次锁 60 秒
- 交易所 17800 若只绑回环：远程提币先开隧道 `ssh -L 17800:127.0.0.1:17800 <user>@<host>`

## 4. 本地带资金 E2E（不经服务器）

```bash
# 服务链（交易所带 custody seed 才接桥；seed = 任意 64 位 hex 随机，离线备份）
dotnet run --project aether-apps/Aether.Ledger.Node     # 新链种子打印在日志——当场抄录 treasury
AETHER_LEDGER_CUSTODY_SEED=<hex> dotnet run --project aether-apps/Aether.Exchange.Host
dotnet run --project aether-apps/Aether.CtpFront.Host

# 充值（本地 CLI = 本仓 Aether.Ledger.Wallet）
wallet=~/Desktop/AetherAxis/aether-apps/Aether.Ledger.Wallet
dotnet run --project $wallet -- balance --seed <custody种子hex>       # 输出 custody 地址
dotnet run --project $wallet -- transfer --seed @treasury.seed --to <钱包地址> --amount 1000000 --asset quote
dotnet run --project $wallet -- deposit --seed @运营钱包.seed --custody <custody地址> --account bot01 --amount 1000000 --asset quote

# 实例目录跑 bot（现金已就位，Lean 风控不再全单 Invalid）
cd <实例目录>
dotnet run --project ~/Desktop/AetherAxis/aether-apps/Aether.CtpConsole -- --profile bot01 --run-strategy market-maker zx001 --real --max-volume 1
```

## 5. 语义速查（重启 / 异常）

| 场景 | 行为 |
|---|---|
| 重启链节点 | 桥轮询自动重连——交易所不用动 |
| 重启交易所 | 桥全量重扫链重建 AQ IOU（链是唯一真相）；未成交挂单作废、铸造台账续用 |
| 充值后交易所崩溃 | 无损——IOU 从链重扫推导，对账自动补上 |
| 提现的崩溃窗口（扣现后链未确认） | 下一轮对账把现金补回（差额自愈） |
| 误充 coin | 跳过不归账，资产留链上 custody |
