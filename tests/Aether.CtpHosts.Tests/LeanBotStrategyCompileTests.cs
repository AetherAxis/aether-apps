using System.IO;
using Aether.Trading.Runtime.Lean;
using QuantConnect.Algorithm;
using Xunit;

namespace Aether.CtpHosts.Tests;

/// <summary>
/// 交易所机器人策略（做市商 / 噪声交易者，Lean 版）的编译锁定——源文件 = **仓库分发副本**
/// <c>algorithms/{market-maker,noise-taker}.lean.cs</c>（2026-10-05 起入仓：克隆项目即可见；
/// 运行时读各机器 tenant labs 的 <c>leanalgorithms/</c>，改完 labs 想记录进仓库 = 拷回 algorithms/，
/// 本测试随之锁新版本——不再维护仓内字符串镜像）。
/// 两个策略 = 「bots 即策略」路线（用户拍板）：机器人不是服务端内嵌代码，而是普通客户端账户上跑的
/// Lean 算法笔记，经 P2 真实下单（实盘 tab 勾「真实下单」）对着交易所交易。
/// <para>（2026-10-23 自 monorepo Acme.Trading.Runtime.Tests 迁入 aether-apps，今 aether-ctp-hosts：algorithms/ 存档
/// 在本仓根目录——策略源与其编译锁定同仓；compiler 引 aether-trading 的 Runtime.Lean。）</para>
/// </summary>
public class LeanBotStrategyCompileTests
{
    [Fact]
    public void MarketMaker_strategy_compiles()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "lean-bot-mm-test");
        Directory.CreateDirectory(cacheDir);
        var ok = LeanAlgorithmScriptCompiler.TryCompile(
            ReadRepoAlgorithm("market-maker.lean.cs"), "market-maker", cacheDir, out var type, out _, out var err);
        Assert.True(ok, err ?? "(无错误信息)");
        Assert.NotNull(type);
        Assert.True(typeof(QCAlgorithm).IsAssignableFrom(type));
    }

    [Fact]
    public void NoiseTaker_strategy_compiles()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "lean-bot-noise-test");
        Directory.CreateDirectory(cacheDir);
        var ok = LeanAlgorithmScriptCompiler.TryCompile(
            ReadRepoAlgorithm("noise-taker.lean.cs"), "noise-taker", cacheDir, out var type, out _, out var err);
        Assert.True(ok, err ?? "(无错误信息)");
        Assert.NotNull(type);
        Assert.True(typeof(QCAlgorithm).IsAssignableFrom(type));
    }

    /// <summary>读 algorithms/ 的仓库分发副本（算法随仓库提交——缺文件 = 提交遗漏，测试直接失败）。</summary>
    private static string ReadRepoAlgorithm(string file)
    {
        var path = Path.Combine(FindRepoRoot(), "algorithms", file);
        Assert.True(File.Exists(path), $"缺少 {path}——算法分发副本应随仓库提交");
        return File.ReadAllText(path);
    }

    /// <summary>从测试 bin 输出向上找仓库根（slnx 标记——本仓根为 Aether.CtpHosts.slnx）。</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Aether.CtpHosts.slnx")))
                return dir.FullName;
            dir = dir.Parent!;
        }
        throw new InvalidOperationException($"未找到仓库根（{AppContext.BaseDirectory} 起无 Aether.CtpHosts.slnx 祖先目录）");
    }
}
