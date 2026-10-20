using System;
using System.Collections.Generic;
using Aether.Trading.Domain;
using Aether.Trading.Runtime.Lean;
using QuantConnect;
using QuantConnect.Algorithm;

namespace LabsLeanAlgorithms;

/// <summary>
/// 噪声交易者机器人（Lean 版）：按节奏随机方向市价吃流动性——让盘口有成交、价格会动。
/// 普通客户端策略（勾「真实下单」后经 wire 真实报单）。双向开仓（2026-10-10 现货开空解禁——随机买卖畅通）。
/// <para/>
/// 净持仓软钳（NetCap）：超界反转方向 ⇒ 持仓/OI 有界、方向自动回归；MaxLots 单笔上限。
/// 对手侧无流动性（做市缺席/单边触限）时该轮报单被交易所拒 ⇒ 跳过，下一轮再试。
/// 固定 Seed ⇒ 决策序列可复现。
/// <para/>
/// 用法：实盘 tab → 策略引擎选 LeanAlgorithm → 算法选「noise-taker」→ 合约填 ax001 →
/// 勾「真实下单」（单笔开仓上限 ≥ MaxLots）→ 前置登录后启动。需与做市商同时运行（吃的就是它的挂单）。
/// </summary>
public class NoiseTakerAlgo : LeanAlgorithmBase
{
    private Symbol _symbol;
    private Random _rng = new(42);
    private int _maxLots;
    private int _netCap;

    public static IReadOnlyList<StrategyParameterDef> ParameterDefinitions => new[]
    {
        new StrategyParameterDef("IntervalSec", "交易间隔（秒）", "3"),
        new StrategyParameterDef("MaxLots", "单笔手数上限", "3"),
        new StrategyParameterDef("NetCap", "净持仓软钳（手）", "3"),
        new StrategyParameterDef("Seed", "随机种子（可复现）", "42"),
    };

    public override void Initialize()
    {
        ConfigureFromConfig();
        _symbol = AddCtpData(GetParameter("symbol") ?? "ax001", Resolution.Second);

        var interval = GetIntParam("IntervalSec", 3);
        _maxLots = GetIntParam("MaxLots", 3);
        _netCap = GetIntParam("NetCap", 3);
        _rng = new Random(GetIntParam("Seed", 42));
        Schedule.On(DateRules.EveryDay(), TimeRules.Every(TimeSpan.FromSeconds(Math.Max(1, interval))), TakeTurn);
        BotLog($"[Noise] 初始化 {_symbol.Value}: 每{interval}s 随机吃 ≤{_maxLots}手 净钳±{_netCap}");
    }

    // 数据订阅驱动 Security 行情价（市价单的引擎侧前置 + CtpOrderRequestMapper 的参考价）；
    // 交易节拍由 Schedule 驱动，OnData 不用做事。

    private void TakeTurn()
    {
        var security = Portfolio[_symbol];
        if (security.Price <= 0)
        {
            BotLog("[Noise] 等待首帧行情（Price=0）——数据链未通则永远跳过");
            return;
        }

        var pos = security.Quantity;
        var dir = _rng.Next(2) == 0 ? 1 : -1;
        if (dir == 1 && pos >= _netCap) dir = -1;        // 净持仓软钳：超界反转
        else if (dir == -1 && pos <= -_netCap) dir = 1;
        var lots = _rng.Next(1, _maxLots + 1);
        BotLog($"[Noise] 吃单 {dir * lots}手（last={security.Price} 净仓{pos}）");
        MarketOrder(_symbol, dir * lots, tag: "噪声吃单");
    }

    private int GetIntParam(string key, int fallback)
        => GetParameter(key) is { Length: > 0 } s && int.TryParse(s, out var v) && v > 0 ? v : fallback;
}
