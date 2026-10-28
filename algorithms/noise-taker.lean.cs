using System;
using System.Collections.Generic;
using Aether.Trading.Domain;
using Aether.Trading.Runtime.Lean;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Orders;

namespace LabsLeanAlgorithms;

/// <summary>
/// 噪声交易者机器人（Lean 版）：按节奏随机方向**深度穿越**吃流动性——让盘口有成交、价格会动。
/// 普通客户端策略（勾「真实下单」后经 wire 真实报单）。双向开仓（2026-10-10 现货开空解禁——随机买卖畅通）。
/// <para/>
/// 净持仓软钳（NetCap）：超界反转方向 ⇒ 持仓/OI 有界、方向自动回归；MaxLots 单笔上限。
/// 固定 Seed ⇒ 决策序列可复现。
/// <para/>
/// <b>为什么是「穿越单」而不是市价单/IOC</b>（2026-10-28 服务器双 bot 长跑定案）：
/// 早先走 <c>MarketOrder</c>，由 CtpOrderRequestMapper 换成「对手价 ± max(PriceTick,0.02)」的 IOC。
/// 余量只有 1~2 个 tick，而做市商是**事件驱动重报价**的：这 100ms 里它可能已经把内档撤走重挂。
/// 实测双 bot 在服务器上就是「噪声单挂 99.484375、做市卖单也挂 99.484375，价格相同却双双不成交」——
/// IOC 进场那一瞬对手缺席，几毫秒后才挂上，残量已被交易所作废（状态='4'）。一拍之差无法靠调参消除。
/// <para/>
/// 现在改为：以 <c>last</c> 为锚，向**穿价方向偏移 CrossTicks 个 tick** 下 GFD 限价单。
/// 深度穿越 ⇒ 只要做市商内档还在（哪怕价格已经飘了几个 tick）就必然成交，成交价仍按 maker 挂单价
/// （限价只是上限，无额外滑点）；GFD ⇒ 万一真的没成交，残单**留在簿上**等做市商下一次报价撞上它，
/// 而不是当场作废。每轮开头先撤上一轮残单，避免挂单堆积。
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
    private decimal _tickSize;
    private int _crossTicks;

    /// <summary>本策略挂出去的活单（每轮先撤再下，防残单堆积）。</summary>
    private readonly List<OrderTicket> _live = new();

    public static IReadOnlyList<StrategyParameterDef> ParameterDefinitions => new[]
    {
        new StrategyParameterDef("IntervalSec", "交易间隔（秒）", "3"),
        new StrategyParameterDef("MaxLots", "单笔手数上限", "3"),
        new StrategyParameterDef("NetCap", "净持仓软钳（手）", "3"),
        new StrategyParameterDef("Seed", "随机种子（可复现）", "42"),
        new StrategyParameterDef("TickSize", "价格网格（wire 量化 ulp，不是合约表的 PriceTick）", "0.015625"),
        new StrategyParameterDef("CrossTicks", "穿越档位数（越大越必成交，0=不穿越）", "10"),
    };

    public override void Initialize()
    {
        ConfigureFromConfig();
        _symbol = AddCtpData(GetParameter("symbol") ?? "ax001", Resolution.Second);

        var interval = GetIntParam("IntervalSec", 3);
        _maxLots = GetIntParam("MaxLots", 3);
        _netCap = GetIntParam("NetCap", 3);
        _rng = new Random(GetIntParam("Seed", 42));
        // 0.015625 = 1/64：CTP wire 的价格槽在 [64,128) 区间按 1/64 量化（见 CtpOrderRequestMapper 注释），
        // 合约表写的 0.01 只是品种名义精度，真正落到 wire 上的是 1/64——按 1/64 取整才能与回报价完全相等。
        _tickSize = GetDecimalParam("TickSize", 0.015625m);
        _crossTicks = GetIntParam("CrossTicks", 10);
        Schedule.On(DateRules.EveryDay(), TimeRules.Every(TimeSpan.FromSeconds(Math.Max(1, interval))), TakeTurn);
        BotLog($"[Noise] 初始化 {_symbol.Value}: 每{interval}s 随机穿越 ≤{_maxLots}手 净钳±{_netCap} 穿越{_crossTicks}×{_tickSize}");
    }

    // 数据订阅驱动 Security 行情价（穿越价的锚 + 引擎侧前置）；交易节拍由 Schedule 驱动，OnData 不用做事。

    private void TakeTurn()
    {
        var security = Portfolio[_symbol];
        if (security.Price <= 0)
        {
            BotLog("[Noise] 等待首帧行情（Price=0）——数据链未通则永远跳过");
            return;
        }

        CancelLive();   // 上一轮没成交的残单先撤：它们已占住旧价位，留着会与新单同价堆积

        var pos = security.Quantity;
        var dir = _rng.Next(2) == 0 ? 1 : -1;
        if (dir == 1 && pos >= _netCap) dir = -1;        // 净持仓软钳：超界反转
        else if (dir == -1 && pos <= -_netCap) dir = 1;
        var lots = _rng.Next(1, _maxLots + 1);

        // 锚在 last 上：买 = last + CrossTicks×tick（吃穿卖档），卖 = last − CrossTicks×tick（吃穿买档）。
        var anchor = security.Price;
        var price = RoundToTick(anchor + dir * _crossTicks * _tickSize);
        var ticket = LimitOrder(_symbol, dir * lots, price, tag: "噪声穿越");
        // 同款重入防护（照抄 MM 的教训）：回报可能同步到达并直接判终态，那时它还没进 _live——
        // 终态票不要收，否则下一轮会拿死票占位。
        if (ticket != null && !ticket.Status.IsClosed()) _live.Add(ticket);

        BotLog($"[Noise] 穿越 {dir * lots}手 @{price}（last={anchor} 净仓{pos} 偏移{dir * _crossTicks}tick）");
    }

    /// <summary>撤掉本策略仍在簿上的活单。先挑后撤——<c>Cancel()</c> 可能**同步**派发 OnOrderEvent
    /// 并对 <see cref="_live"/> 做 RemoveAll，边遍历边撤会撞「Collection was modified」（MM 2026-10-24 实测）。</summary>
    private void CancelLive()
    {
        var stale = new List<OrderTicket>();
        foreach (var t in _live)
        {
            if (t.Status == OrderStatus.CancelPending || t.Status.IsClosed()) continue;
            stale.Add(t);
        }
        foreach (var t in stale) t.Cancel();
        _live.RemoveAll(t => t.Status.IsClosed());
    }

    private decimal RoundToTick(decimal price)
        => _tickSize > 0 ? Math.Round(price / _tickSize) * _tickSize : price;

    private int GetIntParam(string key, int fallback)
        => GetParameter(key) is { Length: > 0 } s && int.TryParse(s, out var v) && v >= 0 ? v : fallback;

    private decimal GetDecimalParam(string key, decimal fallback)
        => GetParameter(key) is { Length: > 0 } s && decimal.TryParse(s, out var v) && v > 0 ? v : fallback;
}
