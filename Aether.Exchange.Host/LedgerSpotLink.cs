using Ctp.Net;
using Aether.Ledger;
using Aether.Exchange;

// 全局命名空间（host 惯例——同 Program.cs 的 Log/ExchangeHostedService；tests 引 exe 程序集可直接用）

/// <summary>
/// 计价币桥接线（批 4/6 → **2026-10-10 币通道退役后只剩 AQ 一条线**）：把链侧 AQ IOU 与交易所核心的
/// 场内现金对齐。交易所单资产化——只收付计价币 AQ（像股票/期货账户充钱）；coin 留在链上钱包，
/// 充币铸造/提币烧毁通道已整体拆除（ax001 = AQ 计价的保证金品种，场内头寸全部纸面，与链上 coin 无关）。
/// 拓扑：LedgerBridge（链侦听）+ SimTradingCore（venue 账本）之间 <b>只有这一个类</b>，住在交易所宿主
/// （composition root——两侧领域零互引：Aether.Ledger 纯库不碰 venue，Simulator 纯库不碰链）。
///
/// <para><b>两个真相 + 一个对账循环</b>（任何崩溃窗口都收敛，无需额外持久化）：</para>
/// <list type="bullet">
/// <item>链侧真相：per-account AQ IOU（桥，从链全量重扫推导——充值 + / 提现 −，clamp 0）= 该账户的<b>净入账目标</b>（原子数）。</item>
/// <item>DB 侧真相：sim_quote_minted 台账（Credit/Withdraw 自动维护）= <b>已入账量</b>。</item>
/// <item>每秒对账：差正 → <see cref="SimTradingCore.CreditQuoteDeposit"/> 现金入账；差负 → WithdrawQuoteCash 扣减。
/// 交易（盈亏/手续费）只动现金<b>不改变两侧</b>——对账天然不感知交易。</item>
/// </list>
///
/// <para><b>提现闸门 = venue 可用现金</b>（经 <see cref="ILedgerWithdrawHook"/>，签名前扣减——静态可用：
/// 锁定保证金/挂单冻结/浮盈不可提，浮盈须平仓落袋）。asset=Coin 拒（币通道已退役）。</para>
///
/// <para><b>粉尘语义</b>：充值 atoms → 现金按 <see cref="SimTradingCore.QuoteAtomsPerToken"/> 换算（1 AQ = 1 现金），
/// 不足 1 AQ 的原子零头不入账、目标值稳定（不会反复触发对账）。</para>
/// </summary>
public sealed class LedgerSpotLink : ILedgerWithdrawHook, IDisposable
{
    private readonly SimTradingCore _core;
    private readonly LedgerBridge _bridge;
    private readonly TimeSpan _interval;
    private Timer? _timer;
    private int _reconciling;          // 对账重入守卫（timer tick 与 ReconcileNow 互斥）
    private readonly Dictionary<string, string> _lastErrors = new(StringComparer.Ordinal);   // 扣减失败日志去重

    public LedgerSpotLink(SimTradingCore core, LedgerBridge bridge, TimeSpan? reconcileInterval = null)
    {
        _core = core;
        _bridge = bridge;
        _interval = reconcileInterval ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>启动：首轮对账 + 周期对账（周期默认 1s——链出块间隔秒级，足够灵敏）。</summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        Reconcile("启动对账");
        _timer = new Timer(_ => Reconcile("对账"), null, _interval, _interval);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 提现钩子：计价币 = 扣场内现金（静态可用闸门——锁定保证金/挂单冻结/浮盈不可提）。
    /// 桥签名出金之前。asset=Coin 拒（币通道已退役）。
    /// </summary>
    public Task<(bool Ok, string? Error)> TryBurnForWithdrawalAsync(string accountKey, ulong amountAtoms,
        uint asset = LedgerSupply.Assets.Quote, CancellationToken ct = default)
    {
        if (asset != LedgerSupply.Assets.Quote)
            return Task.FromResult((false, "币通道已退役（2026-10-10 拆除——交易所只收付计价币 AQ；coin 留在链上钱包）"));
        try
        {
            _core.WithdrawQuoteCash(accountKey, amountAtoms);
            return Task.FromResult((true, (string?)null));
        }
        catch (ExchangeBusinessException ex)
        {
            return Task.FromResult((false, ex.ErrorMsg));
        }
    }

    /// <summary>同步对账一轮（tests 用——确定性驱动，不等 timer）。</summary>
    public void ReconcileNow() => Reconcile("手动对账");

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    // —— 内部 ——

    private void Reconcile(string tag)
    {
        if (Interlocked.CompareExchange(ref _reconciling, 1, 0) != 0) return;   // 重入守卫
        try
        {
            // AQ 侧（币通道退役后唯一对账线）：计价币铸造台账对齐链侧 AQ IOU——充值入现金 / 提现扣现金。
            // 现金不足扣不动时（亏损后链上还有 AQ IOU 差额——提现已扣过/运营者手动链上划转）受限重试。
            var quoteIou = _bridge.GetAllQuoteBalances();
            var quoteMinted = _core.GetQuoteMintedAtoms();
            foreach (var account in quoteIou.Keys.Concat(quoteMinted.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var targetAtoms = quoteIou.TryGetValue(account, out var qa) ? (long)qa : 0L;
                var currentAtoms = quoteMinted.TryGetValue(account, out var ca) ? ca : 0L;
                var quoteDelta = targetAtoms - currentAtoms;
                if (quoteDelta > 0)
                {
                    _core.CreditQuoteDeposit(account, (ulong)quoteDelta);
                    Log.Line($"[LEDGER-QUOTE] 现金入账 {(quoteDelta / (double)LedgerSupply.AtomsPerCoin):0.########} → {account}（AQ IOU {LedgerSupply.Format((ulong)quoteDelta)}）");
                }
                else if (quoteDelta < 0)
                {
                    try
                    {
                        _core.WithdrawQuoteCash(account, (ulong)(-quoteDelta));
                        Log.Line($"[LEDGER-QUOTE] 现金对账扣减 {(-quoteDelta / (double)LedgerSupply.AtomsPerCoin):0.########} ← {account}");
                    }
                    catch (ExchangeBusinessException ex)
                    {
                        if (!_lastErrors.TryGetValue(account, out var last) || last != ex.ErrorMsg)
                        {
                            _lastErrors[account] = ex.ErrorMsg;
                            Log.Line($"[LEDGER-QUOTE] ⚠ 对账扣现受阻（{account}）：{ex.ErrorMsg}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Line($"[LEDGER-QUOTE] {tag}异常（下一轮重试）：{ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _reconciling, 0);
        }
    }
}
