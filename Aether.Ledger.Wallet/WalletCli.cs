using Aether.Exchange;
using Aether.Exchange.Rpc;
using Aether.Ledger;

namespace Aether.Ledger.Wallet;

/// <summary>
/// 钱包 CLI 的命令实现（自 monorepo <c>apps/LedgerWallet</c> 迁入 ⑧，2026-10-27）。
/// <para><b>为什么不是 top-level 直接跑</b>：迁移后要有回归网——<see cref="RunAsync"/> 接受注入的
/// <see cref="TextWriter"/>，测试进程内起真链节点（① <c>LedgerNode</c> 临时端口/临时链文件）跑
/// <c>info</c>/<c>balance</c>/<c>new</c>，断言输出与退出码；不靠子进程 + stdout 嗅探。</para>
/// <para><b>异常边界</b>：交易所侧业务错 = ④ <see cref="ExchangeBusinessException"/>（ErrorId/ErrorMsg，
/// 含提币 90019——消息带具体原因），链节点业务错 = ① <see cref="LedgerClientException"/>；两者都以退出码 1
/// 收场，未知命令 = 2。</para>
/// </summary>
public static class WalletCli
{
    /// <summary>执行一条命令。<paramref name="output"/>/<paramref name="error"/> 缺省为控制台。</summary>
    public static async Task<int> RunAsync(string[] argv, TextWriter? output = null, TextWriter? error = null)
    {
        var @out = output ?? Console.Out;
        var err = error ?? Console.Error;
        var args = Args.Parse(argv);
        try
        {
            switch (args.Command)
            {
                case "new": await NewAsync(args, @out); break;
                case "info": await InfoAsync(args, @out); break;
                case "balance": await BalanceAsync(args, @out); break;
                case "transfer": await TransferAsync(args, @out); break;
                case "deposit": await DepositAsync(args, @out); break;
                case "withdraw": await WithdrawAsync(args, @out); break;
                case "help" or null or "": PrintHelp(@out); break;
                default:
                    await err.WriteLineAsync($"未知命令：{args.Command}");
                    PrintHelp(@out);
                    return 2;
            }
            return 0;
        }
        catch (ExchangeBusinessException ex)   // 交易所侧业务错（含提币 90019——消息带具体原因）
        {
            await err.WriteLineAsync($"失败：{ex.ErrorMsg}（code {ex.ErrorId}）");
            return 1;
        }
        catch (LedgerClientException ex)       // 链节点业务错（余额不足/验签拒绝等）
        {
            await err.WriteLineAsync($"失败：{ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            await err.WriteLineAsync($"失败：{ex.Message}");
            return 1;
        }
    }

    private static void PrintHelp(TextWriter @out) => @out.WriteLine("""
        链上钱包 CLI——命令：
          new      [--out <种子文件>]                       生成钱包（打印种子 + 地址）
          info     [--node <addr>]                          链信息（高度/chainId/总供给）
          balance  <address> | --seed <hex|@file> [--node]  余额查询
          transfer --seed <hex|@file> --to <addr> --amount <币数> [--memo <text>] [--node]
          deposit  --seed <hex|@file> --custody <addr> --account <userId> --amount <币数> [--node]
          withdraw --user <userId> --password <pw> --to <addr> --amount <币数> [--exchange <addr>]
        默认端点：node 127.0.0.1:17805 / exchange 127.0.0.1:17800（远程先开 SSH 隧道）
        """);

    private static async Task NewAsync(Args a, TextWriter @out)
    {
        var seed = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var wallet = new LedgerWallet(seed);
        await @out.WriteLineAsync($"种子（hex，⚠ 全部财富与身份，丢了没人能找回）：{Convert.ToHexString(seed).ToLowerInvariant()}");
        await @out.WriteLineAsync($"地址：{wallet.Address.Hex}");
        if (a.Has("out"))
        {
            var path = a.Get("out")!;
            await File.WriteAllTextAsync(path, Convert.ToHexString(seed).ToLowerInvariant() + Environment.NewLine);
            await @out.WriteLineAsync($"已写入 {path}（明文 hex——自行妥善保管，勿入 git/勿分发）");
        }
    }

    private static async Task InfoAsync(Args a, TextWriter @out)
    {
        using var client = NodeClient(a);
        var info = await client.GetInfoAsync();
        await @out.WriteLineAsync($"链就绪：高度 {info.Height} | chainId {info.ChainId} | 创世 {info.GenesisHash[..12]}…");
        await @out.WriteLineAsync($"币总供给：{LedgerSupply.Format(info.TotalSupplyAtoms)}（固定——不可增发）");
        await @out.WriteLineAsync($"计价币总供给：{LedgerSupply.Format(info.QuoteTotalSupplyAtoms)}（AQ——交易对的报价侧）");
    }

    private static async Task BalanceAsync(Args a, TextWriter @out)
    {
        // 位置参数 0 是命令词本身——地址在 Positional(1)；Parse 顺便做格式校验（40 位小写 hex）
        var address = a.Has("seed") ? (await LoadWallet(a)).Address.Hex
            : LedgerAddress.Parse(a.Positional(1) ?? throw new ArgumentException("balance 需要 <address> 或 --seed 参数")).Hex;
        using var client = NodeClient(a);
        var account = await client.GetBalanceAsync(address);
        await @out.WriteLineAsync($"{address}：币 {LedgerSupply.Format(account.BalanceAtoms)} | 计价币 {LedgerSupply.Format(account.QuoteBalanceAtoms)}（nonce {account.Nonce}）");
    }

    private static async Task TransferAsync(Args a, TextWriter @out)
    {
        var wallet = await LoadWallet(a);
        var to = LedgerAddress.Parse(a.Require("to"));
        var atoms = ParseCoins(a.Require("amount"));
        var asset = ParseAsset(a);
        var memo = a.Get("memo") ?? "";
        using var client = NodeClient(a);
        var info = await client.GetInfoAsync();
        var account = await client.GetBalanceAsync(wallet.Address.Hex);
        var tx = LedgerTransaction.Sign(info.ChainId, wallet, to, atoms, account.Nonce + 1, memo, asset);
        await client.SubmitTxAsync(tx);
        var assetName = asset == LedgerSupply.Assets.Coin ? "币" : "计价币";
        await @out.WriteLineAsync($"已提交（{assetName}）：{LedgerSupply.Format(atoms)} → {to.Hex}");
        await @out.WriteLineAsync($"交易哈希：{tx.Hash}（出块由节点异步完成——info 可查高度推进）");
    }

    private static async Task DepositAsync(Args a, TextWriter @out)
    {
        var wallet = await LoadWallet(a);
        var custody = LedgerAddress.Parse(a.Require("custody"));
        var accountKey = a.Require("account");
        var atoms = ParseCoins(a.Require("amount"));
        var asset = ParseAsset(a);
        using var client = NodeClient(a);
        await LedgerBridge.DepositAsync(client, wallet, custody, atoms, accountKey, asset);   // coin → 桥层拒（币通道已退役）
        await @out.WriteLineAsync($"已提交入金（AQ）：{LedgerSupply.Format(atoms)} → custody {custody.Hex[..12]}…（memo={accountKey}）");
        await @out.WriteLineAsync("桥 500ms 轮询 + 对账 1s 内自动入账场内现金（1 AQ = 1 现金）。");
    }

    private static async Task WithdrawAsync(Args a, TextWriter @out)
    {
        var user = a.Require("user");
        var password = a.Require("password");
        var to = a.Require("to");
        var atoms = ParseCoins(a.Require("amount"));
        var assetName = (a.Get("asset") ?? "quote").Trim().ToLowerInvariant() is "coin" ? "coin" : "quote";   // 默认 quote（币通道已退役）
        var (host, port) = ParseEndpoint(a.Get("exchange") ?? "127.0.0.1:17800");

        using var client = new SimRpcClient(host, port);
        await client.ConnectAsync();
        await client.LoginAsync(new LoginArg("SIM", user, password));   // 登录即鉴权——绑定账户 = 出金账户
        var txHash = await client.WithdrawAsync(to, atoms, assetName);   // coin → 服务端拒 90019（币通道已退役）
        await @out.WriteLineAsync($"提现已提交（AQ ← 场内可用现金）：{LedgerSupply.Format(atoms)} → {to}（交易所 {host}:{port}）");
        await @out.WriteLineAsync($"交易哈希：{txHash}");
    }

    // —— 内部：参数与工具 ——

    private static LedgerClient NodeClient(Args a)
    {
        var (host, port) = ParseEndpoint(a.Get("node") ?? "127.0.0.1:17805");
        return new LedgerClient(host, port);
    }

    private static (string Host, int Port) ParseEndpoint(string addr)
    {
        var colon = addr.LastIndexOf(':');
        if (colon > 0 && int.TryParse(addr[(colon + 1)..], out var port))
            return (addr[..colon], port);
        if (int.TryParse(addr, out var portOnly))
            return ("127.0.0.1", portOnly);
        throw new FormatException($"地址格式非法：{addr}（应为 [host:]port，如 127.0.0.1:17805）");
    }

    private static async Task<LedgerWallet> LoadWallet(Args a)
    {
        var seed = a.Require("seed");
        if (seed.StartsWith('@'))
        {
            var path = seed[1..];
            if (!File.Exists(path)) throw new FileNotFoundException($"种子文件不存在：{path}");
            seed = (await File.ReadAllTextAsync(path)).Trim();
        }
        return new LedgerWallet(Convert.FromHexString(seed));
    }

    /// <summary>--asset coin|quote（缺省 coin）→ 资产 id。</summary>
    private static uint ParseAsset(Args a)
    {
        var value = (a.Get("asset") ?? "coin").Trim().ToLowerInvariant();
        return value switch
        {
            "coin" => LedgerSupply.Assets.Coin,
            "quote" => LedgerSupply.Assets.Quote,
            _ => throw new ArgumentException($"--asset 非法：\"{value}\"（coin | quote）"),
        };
    }

    private static ulong ParseCoins(string coins)
    {
        if (!decimal.TryParse(coins, out var value) || value <= 0)
            throw new ArgumentException($"币数非法：{coins}（应为正数，支持 8 位小数，如 1.5）");
        var atoms = Math.Round(value * (decimal)LedgerSupply.AtomsPerCoin);
        if (atoms > (decimal)ulong.MaxValue) throw new ArgumentException($"币数超出范围：{coins}");
        return (ulong)atoms;
    }

    /// <summary>极简参数解析：首位位置参数 = 命令；其余 --key value / --flag（重复 key 后值覆盖前值）。</summary>
    internal sealed class Args
    {
        private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _positional = new();

        public string? Command => _positional.Count > 0 ? _positional[0] : null;

        public static Args Parse(string[] argv)
        {
            var a = new Args();
            for (var i = 0; i < argv.Length; i++)
            {
                var arg = argv[i];
                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    var key = arg[2..];
                    string? value = null;
                    if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal))
                        value = argv[++i];
                    a._options[key] = value ?? "true";
                }
                else a._positional.Add(arg);
            }
            return a;
        }

        public bool Has(string key) => _options.ContainsKey(key);
        public string? Get(string key) => _options.TryGetValue(key, out var v) && v != "true" ? v : null;
        public string Require(string key) => Get(key) ?? throw new ArgumentException($"缺少参数：--{key}");
        public string? Positional(int index) => _positional.Count > index ? _positional[index] : null;
    }
}
