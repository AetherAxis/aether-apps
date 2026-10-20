using Ctp.Net;
using Aether.Exchange.Rpc;
using Aether.Exchange;
using Aether.Exchange.Storage;
using Aether.Ledger;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// ── CTP 本地仿真交易所（独立服务端进程——2026-10-05 起与前置分离，真实世界对齐）────────────
// 单独起交易所：dotnet run --project aether-apps/Aether.Exchange.Host
// 前置（券商侧接入层）是独立进程：dotnet run --project aether-apps/Aether.CtpFront.Host
// （连接本交易所 127.0.0.1:17800，对客户端监听 17801/17802——详见该 host 头注释）
// 客户端（Aether 桌面 app）一律官方 SDK 链路：期货页登录窗口前置地址填 tcp://127.0.0.1:17801/17802。
//
// 配置（全部在本进程读）：
//   AETHER_CTP_SIM_ADDR    监听端点（默认 127.0.0.1:17800，格式 [host:]port）——进程分离后交易所
//                          只服务前置进程（同机 127.0.0.1 即可；服务器部署脚本即此默认，17800 无需对外）
//   AETHER_CTP_SIM_DB      账本库路径（默认 ~/.aether/ctp-sim.db；与真实 ctp.db 分文件）
//   AETHER_CTP_SIM_BALANCE 新账户初始资金
//   AETHER_CTP_SIM_BOARD   上市合约表（逗号分隔合约号；默认 zx001——「我就是交易所」的自有品种（2 字母官方命名合规），
//                          自定义 id 经 解析链：精确→品种派生→合成 兜底；坏值容忍回默认）
//   AETHER_CTP_SIM_LOGIN_MAX_FAILURES / AETHER_CTP_SIM_LOGIN_LOCKOUT_SEC  防爆破（登录锁定）
//   AETHER_CTP_SIM_LOG     日志文件路径（默认空=仅控制台；Windows 服务形态部署时由 setup 脚本设置）
//
// 批 3 交易所桥（2026-10-06；2026-10-10 币通道退役——只收付计价币 AQ）：托管充提现——须 AETHER_LEDGER_* env：
//   AETHER_LEDGER_NODE_ADDR     链节点端点（默认 127.0.0.1:17805；未设且链节点未运行 = 桥不启动——交易所照常跑）
//   AETHER_LEDGER_CUSTODY_SEED  custody 钱包种子（hex 64 字符——丢失 = 托管 AQ 不可提；生成时日志打印）
//
// 价格源恒为订单簿价格发现（2026-10-03 起 walk/回放已删除）：行情 = 簿顶 + 成交流，tick 事件驱动；
// 流动性由外部客户端策略（labs leanalgorithms 做市/噪声 bot）供给——不起 bot = 空闲市场无对手价（真实交易所本义）。
// 行情随柜台：sim 登录的行情恒为本地产生（按订阅推送）——无环境变量开关。
// ──────────────────────────────────────────────────────────────────────────────

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService();   // 服务器部署形态：SCM 启动 → ServiceLifetime（服务停止 = 优雅关停 flush 账本）；
                                        // 交互运行（dotnet run）仍是 console lifetime，本地行为零变。
                                        // 2026-10-06 服务起不来（Start-Service 1053 超时）的根因 = 2026-10-05 进程分离
                                        // 重写本文件时把此调用弄丢——纯 console host 不向 SCM 上报，SCM 必超时。

var opts = SimHostOptions.FromEnvironment();

// Storage 注入：Exchange 核心不感知具体实现——宿主按 DatabasePath 构造。
// 有路径 = SimStore（SQLite 落盘），无路径 = NullSimStore（纯内存）。
ISimStore store;
if (!string.IsNullOrWhiteSpace(opts.Core.DatabasePath))
{
    store = SimStore.ForFile(opts.Core.DatabasePath);
    await store.InitializeAsync();
    Log.Line($"[CTP-SIM] SQLite 账本已就绪：{opts.Core.DatabasePath}");
}
else
{
    store = NullSimStore.Instance;
}

var core = new SimTradingCore(opts.Core, store: store);

// 批 3 交易所桥（2026-10-10 币通道退役——AQ 单资产）：custody 充提现（opt-in——未设 env = 不启动，交易所照常跑）。
// LedgerSpotLink = AQ 对账（链 IOU ↔ 现金台账：充值入现金 / 提现扣现金）+ 提现闸门（venue 可用现金）。
LedgerBridge? bridge = null;
LedgerSpotLink? spotLink = null;
SimWithdrawHandler? withdrawHandler = null;
var custodySeed = Environment.GetEnvironmentVariable("AETHER_LEDGER_CUSTODY_SEED");
if (!string.IsNullOrWhiteSpace(custodySeed))
{
    var nodeAddr = Environment.GetEnvironmentVariable("AETHER_LEDGER_NODE_ADDR") ?? "127.0.0.1:17805";
    var (nodeHost, nodePort) = nodeAddr.LastIndexOf(':') > 0
        ? (nodeAddr[..nodeAddr.LastIndexOf(':')], int.Parse(nodeAddr[(nodeAddr.LastIndexOf(':') + 1)..]))
        : ("127.0.0.1", 17805);
    var custody = new LedgerWallet(Convert.FromHexString(custodySeed));
    var nodeClient = new LedgerClient(nodeHost, nodePort);
    bridge = new LedgerBridge(nodeClient, custody, msg => Log.Line($"[LEDGER-BRIDGE] {msg}"));
    spotLink = new LedgerSpotLink(core, bridge);
    bridge.WithdrawHook = spotLink;   // 提现闸门 = venue 可用现金（签名前扣减）
    // 提币操作面（wire op `withdraw`）：桥 → SimWithdrawHandler 契约（LedgerWithdrawGlue 共享映射；
    // 未接桥 = handler null → op 侧诚实报 90019）
    withdrawHandler = LedgerWithdrawGlue.Wrap(bridge);
}

var server = new SimRpcServer(core, opts.Host, opts.Port, withdrawHandler);   // wire op `withdraw`（提币操作面）
builder.Services.AddHostedService(_ => new ExchangeHostedService(core, server, opts, bridge, spotLink));
await builder.Build().RunAsync();

/// <summary>交易所生命周期：启动 = 恢复账本 + RPC 监听 + 桥初始化（opt-in）+ 现货对账；停止 = 关连接 + 落盘 + 桥停。</summary>
internal sealed class ExchangeHostedService(SimTradingCore core, SimRpcServer server, SimHostOptions opts,
    LedgerBridge? bridge, LedgerSpotLink? spotLink)
    : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await core.InitializeAsync(ct);   // 重启恢复：挂单作废、报单号/成交号序列续排（host 启动时一次）
        server.Start();
        if (bridge is not null)
        {
            await bridge.StartAsync(ct);   // 全量重扫链恢复 IOU 状态（链是唯一真相）
            Log.Line($"[LEDGER-BRIDGE] 交易所桥就绪：custody {bridge.CustodyAddress.Hex[..12]}…");
        }
        if (spotLink is not null)
        {
            await spotLink.StartAsync(ct);   // 首轮对账：补铸历史充币 + custody 供给同步；此后每秒一轮
        }
        var port = server.LocalEndpoint?.Port ?? opts.Port;
        var listed = core.QueryListedInstruments();
        var accounts = core.QueryAccounts();
        Log.Line($"[CTP-SIM] 本地仿真交易所已就绪：{opts.Host}:{port}（JSON-RPC——前置进程/E2E 客户端连接口）");
        Log.Line($"[CTP-SIM]   账本：{opts.Core.DatabasePath ?? "纯内存（不落盘）"}");
        if (accounts.Count > 0)
            Log.Line($"[CTP-SIM]   账户 {accounts.Count} 个（{accounts.Count(a => a.HasPassword)} 个已设密码）：{string.Join(",", accounts.Select(a => a.AccountId))}");
        Log.Line($"[CTP-SIM]   行情 交易所模式：订单簿 + 成交驱动（价格发现归簿）| 做市/噪声 = 外部客户端策略（labs leanalgorithms）| 新账户初始资金 {opts.Core.InitialBalance}");
        Log.Line($"[CTP-SIM]   上市合约 {listed.Count} 个：{string.Join(",", listed.Select(i => i.InstrumentId))}");
        Log.Line("[CTP-SIM] Ctrl+C 退出（账本自动落盘）。前置进程：dotnet run --project aether-apps/Aether.CtpFront.Host（连接 127.0.0.1:17800）。");
    }

    public async Task StopAsync(CancellationToken ct)
    {
        spotLink?.Dispose();   // 先停对账循环（不再铸造/烧毁），桥再停
        bridge?.Dispose();
        await server.StopAsync();
        core.Dispose();   // 同步 flush（重启续用靠这里落盘）
        Log.Line("[CTP-SIM] 交易所已退出，账本已落盘。");
    }
}

/// <summary>
/// 最小可观测性：恒写控制台；<c>AETHER_CTP_SIM_LOG</c> 设了路径时（Windows 服务形态——headless 无控制台可见）
/// 追加到文件。日志失败不致命（try/catch 吞）——遥测自身故障绝不打断交易所。
/// </summary>
internal static class Log
{
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("AETHER_CTP_SIM_LOG");

    public static void Line(string msg)
    {
        Console.WriteLine(msg);
        if (LogPath is null) return;
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // 新文件先种 UTF-8 BOM（显式字节——不赌 AppendAllText 的前导行为）：Windows PowerShell 5.1 的
            // Get-Content 对无 BOM 的 UTF-8 按默认 ANSI/GBK 读会乱码（2026-10-05 服务器实测）；带 BOM 即自动识别。
            if (!File.Exists(LogPath) || new FileInfo(LogPath).Length == 0)
                File.WriteAllBytes(LogPath, new byte[] { 0xEF, 0xBB, 0xBF });
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
        }
        catch { /* 磁盘/权限故障不打断交易所 */ }
    }
}
