using System;
using System.Collections.Generic;
using System.Linq;
using Aether.Trading.Domain;
using Aether.Trading.Runtime.Lean;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Data;
using QuantConnect.Orders;

namespace LabsLeanAlgorithms;

/// <summary>
/// 做市商机器人（Lean 版）：围绕最新成交价上下各挂多档阶梯限价单（默认每侧 5 档），给交易所供给连续深度。
/// 普通客户端策略（勾「真实下单」后经 wire 真实报单）。双向开仓做市（买开多 / 卖开空——2026-10-10
/// 现货开空解禁后无需先充币攒库存即可双边挂单）。
/// <para/>
/// <b>防档位消失（带宽保留式维护，2026-10-04 单档改阶梯）</b>：期望档位 = last ∓ (SpreadTicks + i×LevelStepTicks) tick，
/// 内到外。在挂腿价位仍在期望集合内 ⇒ 不动——价格滚动时只有边缘腿换防（撤最外档、补最内档），内侧腿持续在场；
/// 单腿被吃掉其余档位仍在簿上，买1/卖1 不会突然空白，缺档下一数据事件补回。旧版每侧单档：腿被吃/移心撤单后
/// 要等终态事件+下一数据事件才补挂，这个窗口里该侧就是空的。
/// <para/>
/// 义务自律（库存偏斜的连续版）：补挂预算 = PositionCap − 该向敞口（当前持仓 + 在挂剩余量）——全部成交后极限
/// 持仓恰触仓限。仓位越满该侧深度越薄（外档先缺），触限停挂该侧全部腿，超限市价减仓（对手侧只有自己挂单时会被
/// 交易所自成交跳过 ⇒ 该轮失败，下一数据事件自动重试——真实做市语义）。
/// <para/>
/// <b>挂单跟踪用 OrderTicket 自持</b>——brokerage 的 GetOpenOrders 刻意返空（引擎外订单不注册），
/// 不能用它查自己的活单（会每帧重复挂单）；撤单是异步的：本轮撤、下一数据事件补（天然一跳延迟防双单——
/// 撤单 pending 的腿仍占着价位，不会同价重复挂）。
/// <para/>
/// 用法：实盘 tab → 策略引擎选 LeanAlgorithm → 算法选「market-maker」→ 合约填 ax001 →
/// 勾「真实下单」→ 前置登录后启动。PriceTick 默认 0.01（ax001 最小变动价位）。
/// </summary>
public class MarketMakerAlgo : LeanAlgorithmBase
{
    private Symbol _symbol;
    private decimal _last;
    private decimal _priceTick;
    private int _spreadTicks;      // 内侧点差：最内档距最新价的 tick 数
    private int _levelStepTicks;   // 相邻档位间距（tick 数）
    private int _levels;           // 每侧档数
    private decimal _quoteVolume;  // 单档挂单手数
    private decimal _positionCap;
    /// <summary>两次重新报价之间的最小墙钟间隔（防「tick → 撤改 → 簿变更 → tick」自激风暴）。</summary>
    private int _minQuoteIntervalMs;
    private DateTime _lastQuoteUtc = DateTime.MinValue;
    private readonly List<OrderTicket> _bidLegs = new();
    private readonly List<OrderTicket> _askLegs = new();

    public static IReadOnlyList<StrategyParameterDef> ParameterDefinitions => new[]
    {
        new StrategyParameterDef("SpreadTicks", "内侧点差（最内档距最新价 tick 数）", "2"),
        new StrategyParameterDef("Levels", "每侧档位数", "5"),
        new StrategyParameterDef("LevelStepTicks", "相邻档位间距（tick 数）", "1"),
        new StrategyParameterDef("PriceTick", "最小变动价位", "0.01"),
        new StrategyParameterDef("QuoteVolume", "单档挂单手数", "5"),
        new StrategyParameterDef("PositionCap", "单边持仓上限（手）", "200"),
        new StrategyParameterDef("MinQuoteIntervalMs", "最小重新报价间隔（毫秒，防自激风暴）", "300"),
        new StrategyParameterDef("RepriceThresholdTicks", "重定档容忍（last 偏离锚点多少 tick 才整体改挂）", "2"),
    };

    public override void Initialize()
    {
        ConfigureFromConfig();
        // 关掉 Lean 自身的 Trace/Debug 输出（2026-10-29 定案）：默认全量 Trace，每笔单都打
        // 「Total margin information / New Order Event / OrderEvent()」等几十行，做市商每分钟数百次
        // 撤挂 ⇒ 日志每小时涨 GB 级，实测把服务器磁盘写满导致整机 I/O 卡死（SSH 握手超时、
        // 前置端口能连但无响应）。只保留 Error，策略自己的 BotLog 照常输出。
        // 注意：本版 Lean（2.5.18193）**没有** LoggingLevel 类型、也没有 SetLoggingLevel API——
        // 2026-10-29 曾误用 SetLoggingLevel 导致整个脚本编译失败（引擎报「未注册的 Lean 算法」），
        // 只能自己包一层 handler 过滤。BotLog 走 CtpLeanGateway.Log，不经过 LogHandler，不受影响。
        QuantConnect.Logging.Log.LogHandler = new ErrorOnlyLogHandler(QuantConnect.Logging.Log.LogHandler);
        _symbol = AddCtpData(GetParameter("symbol") ?? "ax001", Resolution.Second);

        _priceTick = GetDecimalParam("PriceTick", 0.01m);
        _spreadTicks = GetIntParam("SpreadTicks", 2);
        _levelStepTicks = GetIntParam("LevelStepTicks", 1);
        _levels = GetIntParam("Levels", 5);
        _quoteVolume = GetDecimalParam("QuoteVolume", 5m);
        _positionCap = GetDecimalParam("PositionCap", 200m);
        _minQuoteIntervalMs = GetIntParam("MinQuoteIntervalMs", 300);
        _repriceThresholdTicks = GetIntParam("RepriceThresholdTicks", 2);
        // 兜底重挂（2026-10-18 36h 长跑定案）：算法纯事件驱动（tick→维护）——撤光/吃光挂单后若市场
        // 完全静止（无挂/撤/成事件 ⇒ 无 tick），「下一数据事件补挂」永不发生 ⇒ 簿空自锁、市场假死
        // （实测 MM 撤光后 14h 不补挂、NT 吃陈旧价全作废）。定时器独立于行情，保证静止市场也会恢复深度
        // （force=true 绕过节流）。
        Schedule.On(DateRules.EveryDay(), TimeRules.Every(TimeSpan.FromSeconds(30)), () => RefreshQuotes(force: true));
        BotLog($"[MM] 初始化 {_symbol.Value}: 上下各{_levels}档 点差{_spreadTicks}tick 档距{_levelStepTicks}tick 单档{_quoteVolume}手 仓限±{_positionCap} 最小报价间隔{_minQuoteIntervalMs}ms");
        // 仓位来源自检（2026-10-29）：两侧挂腿由 Portfolio 净持仓算预算——若持仓视图与实际账户不符，
        // 会表现为「一侧永不挂单」（例：以为空头触限 ⇒ 预算 ≤0 ⇒ 卖侧静默消失 ⇒ 簿上无卖盘 ⇒ 永无成交）。
        // 这行把 Lean 的持仓视图摊到日志里，与「账户查询」的真值直接对照。
        BotLog($"[MM] 初始持仓视图 Portfolio={Portfolio[_symbol].Quantity}（与账户查询对照）");
    }

    /// <summary>最近锚定的中心价：只有真实 last 偏离它超过 RepriceThresholdTicks 才重定档。</summary>
    private decimal _anchoredLast;
    /// <summary>重定锚阈值（tick 数）：真实 last 偏离当前锚点达到这个幅度，才整条阶梯平移。</summary>
    private int _repriceThresholdTicks;
    /// <summary>上次打印仓位视图的墙钟时刻（诊断节流）。</summary>
    private DateTime _lastPosLogUtc = DateTime.MinValue;

    public override void OnData(Slice slice)
    {
        if (IsWarmingUp || !slice.TryGet<CtpBar>(_symbol, out var bar)) return;
        if (_last == 0) BotLog($"[MM] 首帧行情 last={bar.Close}（自举成功——数据链已通）");
        _last = bar.Close;   // 最新成交价（交易所模式：tick 即成交/簿事件驱动）
        RefreshQuotes();
    }

    public override void OnOrderEvent(OrderEvent e)
    {
        // 终态（撤/成/拒）清票根——腿位留缺，下一数据事件补（天然一跳延迟防双单）
        if (e.Status.IsClosed())
        {
            _bidLegs.RemoveAll(t => t.OrderId == e.OrderId);
            _askLegs.RemoveAll(t => t.OrderId == e.OrderId);
        }
        if (e.Status is OrderStatus.Filled or OrderStatus.PartiallyFilled)
            BotLog($"[MM] 成交 {e.Symbol.Value} {e.FillQuantity}@{e.FillPrice}（净持仓 {Portfolio[_symbol].Quantity}）");
    }

    /// <summary>阶梯维护：超限减仓 → 撤移出带宽/触限腿 → 补缺档（预算感知）。撤单异步——本轮不补挂，下一数据事件补。</summary>
    private void RefreshQuotes(bool force = false)
    {
        var security = Portfolio[_symbol];
        var pos = security.Quantity;
        if (security.Price <= 0) return;   // 尚无行情价——市价减仓腿会被引擎拒价，等首个行情

        // 节流（2026-10-25）：做市商挂在每次 tick 上，而撤改挂单本身又会制造簿变更 → 新 tick。
        // 不节流时这个回路会在同一毫秒内自我放大（实测：前端每秒被灌几百帧同值快照，客户端
        // native 侧积压后表现成「同一笔 tick 反复重投」、bot 日志 30 秒涨到 160MB）。
        // 节流只丢「中间态」：下一次允许的报价始终用最新 `_last`，深度不会停在旧价位。
        var now = DateTime.UtcNow;
        // 仓位视图节流打印（15s 一次，见 Initialize 注释）：盯「一侧预算是否被持仓视图吃掉」。
        if ((now - _lastPosLogUtc).TotalSeconds >= 15)
        {
            _lastPosLogUtc = now;
            BotLog($"[MM] 仓位视图 Portfolio={pos} 在挂 买{_bidLegs.Count}/卖{_askLegs.Count} 预算 买{_positionCap - pos}/卖{_positionCap + pos}");
        }
        var throttled = false;
        if (!force)
        {
            if ((now - _lastQuoteUtc).TotalMilliseconds < _minQuoteIntervalMs) { throttled = true; }
            else { _lastQuoteUtc = now; }
        }
        else
        {
            _lastQuoteUtc = now;
        }

        // 超限减仓（自办强平）：市价吃非自有的对手流动性
        if (pos > _positionCap) MarketOrder(_symbol, _positionCap - pos, tag: "超限减仓");
        else if (pos < -_positionCap) MarketOrder(_symbol, -_positionCap - pos, tag: "超限减仓");

        // —— 重定档锚点（2026-10-29 定案）：不为每个 tick 重算阶梯——
        // 真实 last 偏离锚点不足 RepriceThresholdTicks 就沿用旧锚点，已挂腿一律不动；
        // 偏离超阈值才换锚点，一次性平移阶梯。实测：未加锚点前 60s 撤挂 ~855 次、成交 22 笔。
        var anchor = RoundToTick(_last);
        if (_anchoredLast == 0) _anchoredLast = anchor;
        else if (!throttled && Math.Abs(anchor - _anchoredLast) >= _repriceThresholdTicks * _priceTick)
            _anchoredLast = anchor;

        var desiredBids = DesiredLevels(bid: true);
        var desiredAsks = DesiredLevels(bid: false);

        RollStaleLegs(_bidLegs, desiredBids, pos >= _positionCap, "买", isBid: true);
        RollStaleLegs(_askLegs, desiredAsks, pos <= -_positionCap, "卖", isBid: false);

        // 补挂预算 = 仓限 − 该向敞口：多头越满买侧越薄，空头越满卖侧越薄
        FillMissingLegs(_bidLegs, desiredBids, _positionCap - pos, isBid: true);
        FillMissingLegs(_askLegs, desiredAsks, _positionCap + pos, isBid: false);
    }

    /// <summary>期望档位价集合（内到外）：锚点 ∓ (SpreadTicks + i×档距)×PriceTick。</summary>
    private List<decimal> DesiredLevels(bool bid)
    {
        var levels = new List<decimal>(_levels);
        for (var i = 0; i < _levels; i++)
        {
            var offset = (_spreadTicks + i * _levelStepTicks) * _priceTick;
            levels.Add(RoundToTick(bid ? _anchoredLast - offset : _anchoredLast + offset));
        }
        return levels;
    }

    /// <summary>
    /// 容忍带（2026-10-29 定案）：腿满足下列任一条件才撤——
    /// ① 价格站到了锚点的**错误一侧**（买单 ≥ 锚、卖单 ≤ 锚，说明已不是做市档位）；
    /// ② 价格偏离锚点 ≥ (SpreadTicks + Levels + 2)×tick（彻底脱离做市区）。
    /// 小幅漂动（last 在锚点 ± RepriceThresholdTicks 内）已挂腿一律保留。
    /// </summary>
    private bool ShouldRoll(decimal price, bool isBid)
    {
        if (isBid && price >= _anchoredLast) return true;
        if (!isBid && price <= _anchoredLast) return true;
        var deadBand = (_spreadTicks + _levels + 2) * _priceTick;
        return Math.Abs(price - _anchoredLast) >= deadBand;
    }

    /// <summary>撤移出带宽或触限的腿。撤单 pending 的腿不重复撤（等终态事件清根）。</summary>
    private void RollStaleLegs(List<OrderTicket> legs, List<decimal> desired, bool capped, string side, bool isBid)
    {
        // 先挑、后撤（不要边遍历边撤）：leg.Cancel() 可能**同步**派发 OnOrderEvent，终态分支会对
        // 同一个 legs 做 RemoveAll —— 直接 foreach 就撞「Collection was modified」把引擎打死
        // （2026-10-24 botmm 实测：RuntimeError → 引擎退出）。
        var stale = new List<OrderTicket>();
        foreach (var leg in legs)
        {
            if (leg.Status == OrderStatus.CancelPending) continue;
            var price = leg.Get(OrderField.LimitPrice);
            if (capped)
            {
                BotLog($"[MM] 撤{side}腿 @{price}（触限）");
                stale.Add(leg);
            }
            else if (!desired.Contains(price) && ShouldRoll(price, isBid))
            {
                BotLog($"[MM] 撤{side}腿 @{price}（移出带宽）");
                stale.Add(leg);
            }
        }
        foreach (var leg in stale) leg.Cancel();
    }

    /// <summary>
    /// 补缺档（内档优先）：该价位无活腿（撤单 pending 的腿也占位——防同价双单）且预算有余才挂。
    /// 预算先扣在挂剩余量，耗尽即停——外档先缺，仓位越满深度越薄。
    /// </summary>
    private void FillMissingLegs(List<OrderTicket> legs, List<decimal> desired, decimal budget, bool isBid)
    {
        if (budget <= 0) return;
        var resting = legs.Sum(t => Math.Abs(t.QuantityRemaining));
        foreach (var price in desired)
        {
            if (legs.Exists(t => t.Get(OrderField.LimitPrice) == price)) continue;
            var volume = Math.Min(_quoteVolume, budget - resting);
            if (volume < 1m) break;
            var ticket = LimitOrder(_symbol, isBid ? volume : -volume, price, tag: isBid ? "MM买" : "MM卖");
            // 同款重入防护：下单回报可能同步到达并判终态（那时它还没进 legs，OnOrderEvent 的
            // RemoveAll 扑空）——终态票不要进 legs，否则下一轮会拿死票占位/撤单。
            if (!ticket.Status.IsClosed()) legs.Add(ticket);
            resting += volume;
            BotLog($"[MM] 挂{(isBid ? "买" : "卖")} {price} × {volume}手（last={_last}）");
        }
    }

    private decimal RoundToTick(decimal price)
        => _priceTick > 0 ? Math.Round(price / _priceTick) * _priceTick : price;

    private decimal GetDecimalParam(string key, decimal fallback)
        => GetParameter(key) is { Length: > 0 } s && decimal.TryParse(s, out var v) && v > 0 ? v : fallback;

    private int GetIntParam(string key, int fallback)
        => GetParameter(key) is { Length: > 0 } s && int.TryParse(s, out var v) && v > 0 ? v : fallback;

    /// <summary>
    /// 只放行 Error 的 Lean 日志过滤器（2026-10-29）：本版 Lean 无日志级别 API，用包装 handler 把
    /// Trace/Debug（每笔单几十行 margin/OrderEvent）全部丢弃，只把 Error 透给原 handler。
    /// 不 Dispose 内层（那是引擎自己的 handler，进程级共享，不能由策略关掉）。
    /// </summary>
    private sealed class ErrorOnlyLogHandler : QuantConnect.Logging.ILogHandler
    {
        private readonly QuantConnect.Logging.ILogHandler _inner;

        public ErrorOnlyLogHandler(QuantConnect.Logging.ILogHandler inner) => _inner = inner;

        public void Error(string text)
        {
            if (_inner is not null) _inner.Error(text);
            else Console.Error.WriteLine(text);
        }

        public void Debug(string text) { }

        public void Trace(string text) { }

        public void Report(string text) => _inner?.Report(text);

        public void Dispose() { }
    }
}
