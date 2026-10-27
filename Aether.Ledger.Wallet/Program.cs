using Aether.Ledger.Wallet;

// ── 链上钱包 CLI（自 monorepo apps/LedgerWallet 迁入，2026-10-27）──────────────────────
// 用法：dotnet run --project aether-apps/Aether.Ledger.Wallet -- <命令> [参数]
//   new [--out <file>]                    生成钱包：打印种子（hex 64 字符）+ 地址；--out 存种子文件
//   info    [--node <addr>]               链信息：高度 / chainId / 总供给
//   balance <address> [--node <addr>]     查任意地址余额（也可 --seed 看自己）
//   transfer --seed <hex|@file> --to <addr> --amount <币数> [--memo <text>] [--node <addr>]
//   deposit  --seed <hex|@file> --custody <addr> --account <userId> --amount <币数> [--node <addr>]
//         充值到交易所（AQ 入金 = transfer 到 custody 地址、memo=交易所账号——桥自动归账场内现金；币通道已退役）
//   withdraw --user <userId> --password <pw> --to <addr> --amount <币数> [--exchange <addr>]
//         从交易所提现（JSON-RPC：登录绑定账户 → 扣场内可用现金 → custody 签名出金 AQ）
//
// 地址约定：--node 默认 127.0.0.1:17805（链节点）；--exchange 默认 127.0.0.1:17800（交易所，只绑回环——
//   远程服务器先开 SSH 隧道 ssh -L 17800:127.0.0.1:17800 <user>@<host>）。
// 币数支持小数（如 1.5 = 1 币 5000 万原子——8 位小数，四舍五入到原子）。
// ⚠ 种子 = 全部财富与身份：--out 写的文件按明文 hex 保管，勿入 git（CLI 不做加密——保管等级 = 冷钱包）。
// ──────────────────────────────────────────────────────────────────────────────────────
// 命令实现在 WalletCli（可注入 TextWriter ⇒ 测试进程内跑真链回环，无需起子进程）。

Environment.ExitCode = await WalletCli.RunAsync(args);
