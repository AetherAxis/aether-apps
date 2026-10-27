using System.Text.RegularExpressions;
using Aether.Ledger;
using Aether.Ledger.Wallet;

namespace Aether.Apps.Tests;

/// <summary>
/// 钱包 CLI 回归网（2026-10-27 自 monorepo <c>apps/LedgerWallet</c> 迁入 ⑧ 时补）：
/// 进程内起真链节点（临时端口 + 临时链文件）→ 同进程跑 <see cref="WalletCli.RunAsync"/>。
/// <para>为什么值得有：这个 CLI 是**资金操作面**（国库分发/充值/提币唯一的命令行入口），
/// 迁仓最容易出错的地方正是「引了哪份账本库 / 引了哪份交易所 RPC」——monorepo 版引 Acme.Ctp.Sim，
/// 本仓引 ④ <c>Aether.Exchange.Rpc</c>，两者异常类型与登录载荷都不同。这里的用例跑真节点，
/// 引用错立刻红。</para>
/// <para><c>withdraw</c> 不在覆盖内：它要起交易所宿主（含 custody 桥），属集成验收而非 CLI 单测——
/// 见 <c>doc/funding-runbook.md</c> §4 的本地 E2E 步骤。</para>
/// </summary>
public class LedgerWalletCliTests
{
    /// <summary>回环夹具：进程内节点 + 新链（临时端口/临时链文件），并回收日志里的 treasury 种子。</summary>
    private sealed class WalletNode : IDisposable
    {
        private readonly LedgerNode _node;
        private readonly string _chainFile;

        private WalletNode(LedgerNode node, string chainFile, int port, string treasurySeed)
        {
            _node = node;
            _chainFile = chainFile;
            Port = port;
            TreasurySeed = treasurySeed;
        }

        public int Port { get; }

        /// <summary>生成链时打印进日志的 treasury 种子（新链国库满额——转账用例的资金来源）。</summary>
        public string TreasurySeed { get; }

        public string NodeArg => $"127.0.0.1:{Port}";

        public static async Task<WalletNode> StartAsync()
        {
            var chainFile = Path.Combine(Path.GetTempPath(), $"wallet-cli-{Guid.NewGuid():N}.chain");
            var logFile = chainFile + ".log";
            var node = new LedgerNode(new LedgerNodeOptions("127.0.0.1", 0, chainFile, 77, 60), logFile);
            node.Start();
            var port = node.LocalEndpoint?.Port ?? throw new InvalidOperationException("节点未绑定端口");
            var log = await File.ReadAllTextAsync(logFile);
            var seed = Regex.Match(log, @"treasury 种子（hex[^：]*）：([0-9a-f]{64})");
            if (!seed.Success) throw new InvalidOperationException($"节点日志中未找到 treasury 种子：{logFile}");
            return new WalletNode(node, chainFile, port, seed.Groups[1].Value);
        }

        /// <summary>轮询节点余额（出块是 500ms tick 内的异步动作——CLI 输出断言前先等账实相符）。</summary>
        public async Task<LedgerAccountState> WaitForQuoteAsync(string address, ulong expectedAtoms)
        {
            using var client = new LedgerClient("127.0.0.1", Port);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            LedgerAccountState state = default!;
            while (DateTime.UtcNow < deadline)
            {
                state = await client.GetBalanceAsync(address);
                if (state.QuoteBalanceAtoms == expectedAtoms) return state;
                await Task.Delay(100);
            }
            return state;   // 超时由调用方断言（保留最后一次读数，失败信息才可读）
        }

        public void Dispose()
        {
            try { _node.StopAsync().GetAwaiter().GetResult(); } catch { }
            try { _node.Dispose(); } catch { }
            try { File.Delete(_chainFile); } catch { }
            try { File.Delete(_chainFile + ".log"); } catch { }
        }
    }

    /// <summary>一次 CLI 调用的输出捕获（命令实现在同进程，TextWriter 注入即可——不起子进程）。</summary>
    private sealed class CliRun
    {
        private readonly StringWriter _out = new();
        private readonly StringWriter _err = new();

        public string Stdout => _out.ToString();
        public string Stderr => _err.ToString();

        public Task<int> RunAsync(params string[] args) => WalletCli.RunAsync(args, _out, _err);
    }

    [Fact]
    public async Task 无参打印用法_退出码0()
    {
        var run = new CliRun();
        Assert.Equal(0, await run.RunAsync());
        // 用法串是这个 CLI 唯一的发现入口（`--help` 与裸跑同一条路径）
        Assert.Contains("链上钱包 CLI——命令", run.Stdout);
        Assert.Contains("withdraw --user", run.Stdout);
    }

    [Fact]
    public async Task 未知命令_退出码2且指向用法()
    {
        var run = new CliRun();
        Assert.Equal(2, await run.RunAsync("frobnicate"));
        Assert.Contains("未知命令：frobnicate", run.Stderr);
        Assert.Contains("链上钱包 CLI——命令", run.Stdout);
    }

    [Fact]
    public async Task new_打印种子与地址_并写出种子文件()
    {
        var run = new CliRun();
        var seedFile = Path.Combine(Path.GetTempPath(), $"wallet-{Guid.NewGuid():N}.seed");
        try
        {
            Assert.Equal(0, await run.RunAsync("new", "--out", seedFile));

            var printed = Regex.Match(run.Stdout, @"：([0-9a-f]{64})");
            Assert.True(printed.Success, $"输出里没有 64 位 hex 种子：{run.Stdout}");
            // 落盘内容 = 打印的种子（明文 hex 一行，勿入 git）——两者漂移等于让人备份了错的钥匙
            Assert.Equal(printed.Groups[1].Value + Environment.NewLine, await File.ReadAllTextAsync(seedFile));
            // 地址由种子确定性派生：CLI 打印的地址必须与同一颗种子复原的钱包一致
            var expected = new LedgerWallet(Convert.FromHexString(printed.Groups[1].Value)).Address.Hex;
            Assert.Contains($"地址：{expected}", run.Stdout);
        }
        finally
        {
            try { File.Delete(seedFile); } catch { }
        }
    }

    [Fact]
    public async Task info_读到真链_高度与供给()
    {
        using var node = await WalletNode.StartAsync();
        var run = new CliRun();
        Assert.Equal(0, await run.RunAsync("info", "--node", node.NodeArg));
        Assert.Contains("链就绪：高度 0", run.Stdout);
        Assert.Contains("chainId 77", run.Stdout);
        Assert.Contains("计价币总供给", run.Stdout);
        Assert.Contains("AQ", run.Stdout);
    }

    [Fact]
    public async Task balance_按地址查询_读到国库计价币()
    {
        using var node = await WalletNode.StartAsync();
        var treasury = new LedgerWallet(Convert.FromHexString(node.TreasurySeed));

        var run = new CliRun();
        Assert.Equal(0, await run.RunAsync("balance", treasury.Address.Hex, "--node", node.NodeArg));
        Assert.Contains(treasury.Address.Hex, run.Stdout);
        // 千分位随文化（"N0"），只断言首尾片段——不把逗号/空格形态钉进测试
        Assert.Contains("计价币 21", run.Stdout);
        Assert.Contains("00000000", run.Stdout);
        Assert.Contains("nonce 0", run.Stdout);
    }

    [Fact]
    public async Task transfer_国库转计价币_链上到账()
    {
        using var node = await WalletNode.StartAsync();
        var target = LedgerWallet.Create();

        var run = new CliRun();
        Assert.Equal(0, await run.RunAsync(
            "transfer", "--seed", node.TreasurySeed, "--to", target.Address.Hex,
            "--amount", "5", "--asset", "quote", "--node", node.NodeArg));
        Assert.Contains("已提交（计价币）：5.00000000", run.Stdout);
        Assert.Contains("交易哈希：", run.Stdout);

        // 出块是节点侧 500ms tick 的异步动作：等账实相符再断言 CLI 的读侧
        var state = await node.WaitForQuoteAsync(target.Address.Hex, 5 * LedgerSupply.AtomsPerCoin);
        Assert.Equal(5 * LedgerSupply.AtomsPerCoin, state.QuoteBalanceAtoms);

        var balance = new CliRun();
        Assert.Equal(0, await balance.RunAsync("balance", target.Address.Hex, "--node", node.NodeArg));
        Assert.Contains("计价币 5.00000000", balance.Stdout);
    }

    [Fact]
    public async Task transfer_余额不足_退出码1且带拒绝原因()
    {
        using var node = await WalletNode.StartAsync();
        var broke = LedgerWallet.Create();
        var target = LedgerWallet.Create();

        var run = new CliRun();
        Assert.Equal(1, await run.RunAsync(
            "transfer", "--seed", Convert.ToHexString(broke.Seed).ToLowerInvariant(), "--to", target.Address.Hex,
            "--amount", "1", "--asset", "quote", "--node", node.NodeArg));
        Assert.Contains("失败：", run.Stderr);
    }

    [Fact]
    public async Task deposit_币通道已退役_桥层拒绝()
    {
        using var node = await WalletNode.StartAsync();
        var treasury = new LedgerWallet(Convert.FromHexString(node.TreasurySeed));

        var run = new CliRun();
        Assert.Equal(1, await run.RunAsync(
            "deposit", "--seed", node.TreasurySeed, "--custody", treasury.Address.Hex,
            "--account", "bot01", "--amount", "1", "--asset", "coin", "--node", node.NodeArg));
        // 退役是资金面红线：误充 coin 不进交易所（① LedgerBridge 前置拒绝），CLI 必须原样把话说出来
        Assert.Contains("币通道已退役", run.Stderr);
    }

    [Fact]
    public async Task 参数校验_币数与资产名非法_明确报错()
    {
        using var node = await WalletNode.StartAsync();
        var treasury = new LedgerWallet(Convert.FromHexString(node.TreasurySeed));

        var badAmount = new CliRun();
        Assert.Equal(1, await badAmount.RunAsync(
            "transfer", "--seed", node.TreasurySeed, "--to", treasury.Address.Hex,
            "--amount", "-1", "--node", node.NodeArg));
        Assert.Contains("币数非法", badAmount.Stderr);

        var badAsset = new CliRun();
        Assert.Equal(1, await badAsset.RunAsync(
            "transfer", "--seed", node.TreasurySeed, "--to", treasury.Address.Hex,
            "--amount", "1", "--asset", "gold", "--node", node.NodeArg));
        Assert.Contains("--asset 非法", badAsset.Stderr);
    }

    [Fact]
    public async Task 节点不可连_退出码1且报失败()
    {
        // 没有任何节点监听这个端口（临时端口取不到就退到高位固定端口——断言只要求连不上）
        var run = new CliRun();
        Assert.Equal(1, await run.RunAsync("info", "--node", "127.0.0.1:1"));
        Assert.Contains("失败：", run.Stderr);
    }
}
