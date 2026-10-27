using Aether.Exchange;
using Aether.Ledger;

/// <summary>
/// 结算轨道工厂（组合根职责）：按配置选一条外部结算轨道。
/// <para>配置：<c>AETHER_SETTLEMENT_RAIL</c> = <c>ledger-aq</c>（虚拟币线，默认）| <c>none</c>（不接任何外部账本）。</para>
/// <para>未设置时的默认保持拆出前的手感：**设了 <c>AETHER_LEDGER_CUSTODY_SEED</c> 就是 ledger-aq，否则 none**
/// （不设 seed = 交易所照常跑，只是没有外部入金）。</para>
/// <para>接第二种外部币 = 在这里加一个分支 + 一个 <see cref="IExternalCashRail"/> 实现；
/// ④ 交易所核心与各引擎仓一行都不用改（端口在 ④，适配器住本仓组合根——它同时需要两侧，只有这里能持有两边）。</para>
/// </summary>
internal static class SettlementRailFactory
{
    public const string None = "none";

    public static IExternalCashRail? Create(SimTradingCore core)
    {
        var configured = Environment.GetEnvironmentVariable("AETHER_SETTLEMENT_RAIL");
        var railId = string.IsNullOrWhiteSpace(configured)
            ? (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AETHER_LEDGER_CUSTODY_SEED"))
                ? None
                : LedgerCashRail.RailIdValue)
            : configured.Trim();

        return railId switch
        {
            None => null,
            LedgerCashRail.RailIdValue => CreateLedger(core),
            // 配错不该静默降级成「不结算」——那会让交易所看起来正常、钱却进不来
            _ => throw new InvalidOperationException(
                $"未知 AETHER_SETTLEMENT_RAIL=\"{railId}\"（可用：{LedgerCashRail.RailIdValue} / {None}）"),
        };
    }

    private static LedgerCashRail CreateLedger(SimTradingCore core)
    {
        var seed = Environment.GetEnvironmentVariable("AETHER_LEDGER_CUSTODY_SEED");
        if (string.IsNullOrWhiteSpace(seed))
            throw new InvalidOperationException(
                $"AETHER_SETTLEMENT_RAIL={LedgerCashRail.RailIdValue} 需要 AETHER_LEDGER_CUSTODY_SEED（custody 钱包种子，hex 64）");

        var nodeAddr = Environment.GetEnvironmentVariable("AETHER_LEDGER_NODE_ADDR") ?? "127.0.0.1:17805";
        var (nodeHost, nodePort) = nodeAddr.LastIndexOf(':') > 0
            ? (nodeAddr[..nodeAddr.LastIndexOf(':')], int.Parse(nodeAddr[(nodeAddr.LastIndexOf(':') + 1)..]))
            : ("127.0.0.1", 17805);

        var custody = new LedgerWallet(Convert.FromHexString(seed));
        var nodeClient = new LedgerClient(nodeHost, nodePort);
        var bridge = new LedgerBridge(nodeClient, custody, msg => Log.Line($"[LEDGER-BRIDGE] {msg}"));
        return new LedgerCashRail(core, bridge);
    }
}
