using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Aether.Trading.Domain;
using Aether.Trading.Runtime.Domain;
using Aether.Trading.Runtime.Lean;
using Aether.Trading.Runtime.Lean.DependencyInjection;
using Ctp.Net;
using Ctp.Net.Front;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ═══════════════════════════════════════════════════════════════════════════
// Aether.DirectBot.Console —— **临时旁路客户端**：bot 绕过 CTP 前置，直连交易所（JSON-RPC :17800）。
//
// 为什么存在（2026-10-29）：前置交易腿的「F0 推送序号 240 → 主动断开 → SDK 自动重连 → 客户端
//   未重新认证 → 报单被当垃圾帧丢弃」缺陷会让 bot 在几十秒内静默失去下单能力（详见
//   ctp-net-client/doc/session-reauth-after-reconnect.md）。而行情是**交易所**的产物（订单簿变动
//   /成交即推 tick），前置只是协议转发——所以让 bot 直连交易所，市场照样波动，桌面端照旧通过
//   前置看行情、下单。困难点清单见 aether-ctp-hosts/doc/direct-bot-bypass.md。
//
// **这是旁路，不是替代**：前置重连认证修好后 bot 回归 Aether.CtpConsole（走前置），本客户端退役。
//   因此不做跨仓搬迁，直接复用 ctp-net-front 的 RemoteCtpSession（SimRpcClient → ICtpSession
//   适配器，前置自己每连接一份）。策略（algorithms/*.lean.cs）与撮合语义零改动。
//
// 不依赖：官方 CTP SDK（无原生库、无 frameworks 拷贝）、SQLite 配置库、REPL。行情腿 = 同一条
//   RPC 连接（RemoteCtpSession 的「行情随柜台」语义，CtpMarketDataConnect 参数被忽略）。
//
// 用法（凭据读 config.json 的档案，与 CtpConsole 同格式、可共用同一份）：
//   Aether.DirectBot.Console --profile botmm --exchange 127.0.0.1:17800 \
//       --run-strategy market-maker ax001 --real --max-volume 5
//   --user/--pass/--broker 覆盖档案；--data/--labs/--config 覆盖实例目录（默认当前工作目录）。
//   本客户端每 10s 写 <data>/directbot.heartbeat.json（tick/报单/成交计数 + 引擎状态）——
//   watchdog 用它在**没有前置日志可看**的情况下判定存活。
//
// 退出：Ctrl+C / SIGTERM 优雅停（停引擎→撤单腿→登出）；stdin 可输 quit/stop/status。
// ═══════════════════════════════════════════════════════════════════════════

AppLog.Init();

// —— 参数 + 配置档案（命令行 > 档案 > 内置默认；与 CtpConsole 同口径）——
var args2 = new Args(args);
var cwd = Directory.GetCurrentDirectory();
var configPath = args2.Get("config") ?? Path.Combine(cwd, "config.json");
var config = File.Exists(configPath) ? ConsoleConfig.Load(configPath) : new ConsoleConfig();

var profileName = args2.Get("profile") ?? config.DefaultProfile;
ConsoleProfile? profile = null;
if (!string.IsNullOrWhiteSpace(profileName))
{
    if (!config.Profiles.TryGetValue(profileName, out profile))
        throw new ArgumentException($"配置档案 {profileName} 不存在（{configPath} 的 profiles 段：{string.Join(", ", config.Profiles.Keys)}）");
}
else if (config.Profiles.Count == 1)
{
    profile = config.Profiles.Values.First();
}

// 全字段 Trim：账户名带尾空格 = 连到「同名带空格」的幽灵账户上 0 余额（2026-10-12 服务器实测踩中）
var user = (args2.Get("user") ?? profile?.User ?? throw new ArgumentException("缺少 --user <账户>（或 --profile 指向的档案没有 user）")).Trim();
var password = (args2.Get("pass") ?? profile?.Password ?? throw new ArgumentException("缺少 --pass <密码>（或档案没有 password）")).Trim();
var broker = (args2.Get("broker") ?? profile?.Broker ?? "9999").Trim();

// 交易所 JSON-RPC 地址。服务器上与交易所同机 → 默认回环（明文登录，绝不跨公网连）。
var exchange = (args2.Get("exchange") ?? "127.0.0.1:17800").Trim();
var (exHost, exPort) = ParseEndpoint(exchange);

var dataDir = args2.Get("data") ?? cwd;                       // lean-algo-cache / 心跳随实例目录走
var labsRoot = args2.Get("labs") ?? config.Labs ?? Path.Combine(dataDir, "labs");
Directory.CreateDirectory(dataDir);

// —— 直跑参数（--run-strategy <名> <合约>... [--real] [--max-volume N] [--param k=v]...）——
// 与 CtpConsole 的直跑模式同一套语义（位置参数是合约，其余是 -- 选项）。
var (strategyName, strategyOptions) = ParseRunStrategy(args);
if (strategyName is null)
{
    AppLog.Line("用法：Aether.DirectBot.Console --profile <名> [--exchange 127.0.0.1:17800] --run-strategy <算法名> <合约>... [--real] [--max-volume N] [--param k=v]...");
    return 2;
}

var instruments = strategyOptions.Instruments;
var real = strategyOptions.Real;
var maxVolume = strategyOptions.MaxVolume;
var verbose = strategyOptions.Verbose;
var resolution = strategyOptions.Resolution;
var parameters = strategyOptions.Parameters;
var cash = strategyOptions.Cash;

AppLog.Line($"[bot] 直连模式：交易所 {exHost}:{exPort}（旁路前置）账户 {user} broker {broker}");
AppLog.Line($"[bot] 实例目录 {dataDir}／labs {labsRoot}");
AppLog.Line($"[bot] 算法 {strategyName} 合约 {string.Join(",", instruments)} 分辨率 {resolution} " +
             $"真实下单={(real ? $"开（单笔开仓上限 {maxVolume} 手）" : "关（仅信号）")}");

// —— DI 组装：ICtpSession = 交易所 RPC 适配器（不走 CTP SDK）——
var services = new ServiceCollection();
services.AddSingleton<ICtpSession>(_ => new RemoteCtpSession(exHost, exPort));
services.AddTradingRuntimeLean();                              // ILiveTradingRuntime → LeanLiveTradingService
services.AddLeanAlgorithmServices(dataDir);                    // labs → Roslyn 编译 → LeanAlgorithmRegistry
services.AddSingleton<ILeanAlgorithmNoteSource>(_ => new LabsLeanAlgorithmNoteSource(labsRoot));
services.AddLogging(b => b.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.None));
await using var sp = services.BuildServiceProvider();

var session = sp.GetRequiredService<ICtpSession>();
var live = sp.GetRequiredService<ILiveTradingRuntime>();
var leanScripting = sp.GetRequiredService<ILeanAlgorithmScriptingService>();

// —— 登录 / 行情连接参数（提前构造：事件回调里的重连要用；地址字段被 RemoteCtpSession 忽略，
//    只取 broker/user/password。行情腿 = 同一条 RPC 连接，订阅即激活合约——交易所只对已激活合约推 tick）——
var loginRequest = new CtpLoginRequest
{
    FrontAddress = $"direct://{exHost}:{exPort}",
    MdFrontAddress = $"direct://{exHost}:{exPort}",
    BrokerId = broker,
    UserId = user,
    Password = password,
    AppId = "simnow_client_test",
    AuthCode = "0000000000000000",
};
var mdConnect = new CtpMarketDataConnect
{
    MdFrontAddress = $"direct://{exHost}:{exPort}",
    BrokerId = broker, UserId = user, Password = password,
};

// —— 事件 → 日志 + 心跳计数 ——
// [tick] 逐笔默认关（同 CtpConsole：活跃行情会把日志刷成每天数百 MB）；AETHER_CTP_TICK_TRACE=1 开。
var tickOn = Environment.GetEnvironmentVariable("AETHER_CTP_TICK_TRACE") is "1" or "true";
long ticks = 0, orders = 0, trades = 0;
string? lastTickAt = null;
var loggedIn = false;
var reconnecting = 0;

session.ConnectionStateChanged += (_, s) =>
{
    AppLog.Line($"[连接] {s} @{DateTime.Now:HH:mm:ss}");
    // 交易所重启/网络抖动：RPC 会话无自动重连（RemoteCtpSession 语义）——本客户端自己重登 + 重订阅，
    // 避免「进程活着但静默失明」。重登失败则让进程退出，由 watchdog 按心跳判死拉起。
    if (s == CtpConnectionStatus.Disconnected && loggedIn && Interlocked.Exchange(ref reconnecting, 1) == 0)
        _ = Task.Run(ReloginAsync);
};
session.OrderReceived += (_, o) => { Interlocked.Increment(ref orders); AppLog.Line($"[报单] {o}"); };
session.TradeReceived += (_, t) => { Interlocked.Increment(ref trades); AppLog.Line($"[成交] {t}"); };
session.TickReceived += (_, t) =>
{
    Interlocked.Increment(ref ticks);
    lastTickAt = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    if (tickOn)
        AppLog.Line($"[tick] {t.InstrumentId} last={Fmt(t.LastPrice)} bid={Fmt(t.BidPrice)}/{t.BidVolume} ask={Fmt(t.AskPrice)}/{t.AskVolume} vol={t.Volume} {t.UpdateTime}.{t.UpdateMillisec:000}");
};
live.LogReceived += (_, line) => AppLog.Line(line);
live.StatusChanged += (_, s) => AppLog.Line($"[实盘] 状态: {s}");

// —— 登录（RPC 明文域）——
AppLog.Line($"登录中：交易所 {exHost}:{exPort} 账户 {user} …");
var login = await session.LoginAsync(loginRequest);
loggedIn = true;
AppLog.Line($"已登录：交易日 {login.TradingDay} FrontId={login.FrontId} SessionId={login.SessionId} MaxOrderRef={login.MaxOrderRef}");

// —— 算法加载（labs → Roslyn 编译 → 注册表；加载一次，改算法重跑）——
await leanScripting.LoadAsync();
var names = LeanAlgorithmRegistry.GetRegisteredNames().OrderBy(n => n, StringComparer.Ordinal).ToList();
AppLog.Line(names.Count > 0
    ? $"[bot] 已加载算法：{string.Join(", ", names)}"
    : $"[bot] 未加载任何算法——检查 {Path.Combine(labsRoot, "leanalgorithms")}（*.lean.cs）");
foreach (var (algo, loadErr) in leanScripting.LoadErrors)
    AppLog.Line($"[bot] 算法编译失败 {algo}：{loadErr}");
if (!names.Contains(strategyName, StringComparer.Ordinal))
{
    AppLog.Line($"[bot] 算法 {strategyName} 不存在——退出（已加载：{string.Join(",", names)}）");
    session.Logout();
    return 3;
}

// —— 会话存活门禁（清场与启动都必须在一个**活着的**连接上做）——
// 2026-10-29 现场：botnoise 账户带着存量挂单/成交推送启动时，登录后到引擎启动前的窗口里连接被远端
// 关掉过一次（RemoteCtpSession 无自动重连语义 ⇒ IsConnected=false，清场与 StartAsync 双双失败）。
// 这里等它恢复（后台 ReloginAsync 负责重登 + 重订阅），超时才退出交给 watchdog。
if (!await WaitSessionAliveAsync(90_000))
{
    AppLog.Line("[bot] 会话 90s 未恢复——退出（交由 watchdog 拉起）。");
    session.Logout();
    return 6;
}

// —— 清场：撤掉本账户在交易所簿上的存量活单 ——
// bot 被硬杀后旧挂单留在簿上，而新实例的票根不认识它们（Lean 侧 GetOpenOrders 刻意返空）——
// 不清场会双份挂单。直连下 **ICtpSession 的查询就是 RPC 本身**，无需 CtpConsole 那条 raw RPC 旁路。
await SweepOpenOrdersAsync();
if (!await WaitSessionAliveAsync(90_000))
{
    AppLog.Line("[bot] 清场后会话 90s 未恢复——退出（交由 watchdog 拉起）。");
    session.Logout();
    return 6;
}

// —— 启动引擎 ——
var (ok, err) = await live.StartAsync(new LiveTradingRuntimeRequest
{
    Instruments = instruments,
    Engine = LiveStrategyEngine.LeanAlgorithm,
    StrategyName = strategyName,
    InitialCash = cash,
    StrategyParameters = parameters.Count > 0 ? parameters : null,
    Resolution = resolution,
    VerboseLog = verbose,
    EnableRealOrders = real,
    MaxOrderVolume = maxVolume,
    MarketDataConnect = new MarketDataConnectInput
    {
        MdFrontAddress = mdConnect.MdFrontAddress, BrokerId = broker, UserId = user, Password = password,
    },
});
if (!ok)
{
    AppLog.Line($"[bot] 引擎启动失败：{err}");
    session.Logout();
    return 4;
}
// StartAsync 返回时可能仍在 Starting——立即查 IsRunning 会误判
while (live.Status is LiveTradingRuntimeStatus.Starting or LiveTradingRuntimeStatus.Idle)
    await Task.Delay(200);
if (live.Status != LiveTradingRuntimeStatus.Running)
{
    AppLog.Line($"[bot] 引擎未进入运行态（{live.Status}）——退出。");
    session.Logout();
    return 5;
}
AppLog.Line("[bot] 引擎运行中。Ctrl+C / SIGTERM 优雅停止；stdin 可输 quit/stop/status。");

// —— 挂机：心跳 + 退出信号 ——
using var hbCts = new CancellationTokenSource();
var heartbeatTask = Task.Run(() => HeartbeatLoopAsync(hbCts.Token));
await WaitBotExitAsync();
hbCts.Cancel();
try { await heartbeatTask; } catch { }

if (live.IsRunning)
{
    AppLog.Line("[bot] 停止引擎（撤单腿）…");
    try { await live.StopAsync(); } catch (Exception ex) { AppLog.Line($"[bot] 停引擎异常：{ex.Message}"); }
}
WriteHeartbeat("stopped");
session.Logout();
AppLog.Line("[bot] 已退出。");
return 0;

// ═══════════════════════ 本地函数 ═══════════════════════

static (string Host, int Port) ParseEndpoint(string endpoint)
{
    var parts = endpoint.Split(':');
    if (parts.Length != 2 || !int.TryParse(parts[1], out var port))
        throw new ArgumentException($"交易所地址须 host:port（收到 {endpoint}）");
    return (parts[0], port);
}

/// <summary>扫描 <c>--run-strategy</c> 之后的 token：首位置 = 算法名，其余位置 = 合约，<c>--</c> 开头 = 选项。
/// 控制台级参数（--config/--labs/--data/--profile/--exchange/--user/--pass/--broker）不透传——
/// 旧版 CtpConsole 把「--exchange 117.72.186.128:17800」当成合约（2026-10-20 实测踩中）。</summary>
static (string? Name, StrategyOptions Options) ParseRunStrategy(string[] argv)
{
    var flag = argv.FirstOrDefault(a => a.Equals("--run-strategy", StringComparison.OrdinalIgnoreCase));
    if (flag is null) return (null, new StrategyOptions());
    var at = Array.IndexOf(argv, flag);
    if (at + 1 >= argv.Length) return (null, new StrategyOptions());

    var consoleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "config", "labs", "data", "profile", "exchange", "user", "pass", "broker" };

    var name = argv[at + 1];
    var options = new StrategyOptions();
    for (var i = at + 2; i < argv.Length; i++)
    {
        var a = argv[i];
        if (!a.StartsWith("--")) { options.Instruments.Add(a); continue; }
        var key = a[2..];
        var hasValue = i + 1 < argv.Length && !argv[i + 1].StartsWith("--");
        var value = hasValue ? argv[++i] : "";
        if (consoleKeys.Contains(key)) continue;   // 控制台级参数（Args 已消费）
        switch (key.ToLowerInvariant())
        {
            case "real": options.Real = true; break;
            case "verbose": options.Verbose = true; break;
            case "max-volume": options.MaxVolume = int.Parse(value, CultureInfo.InvariantCulture); break;
            case "cash": options.Cash = decimal.Parse(value, CultureInfo.InvariantCulture); break;
            case "resolution":
                options.Resolution = value switch
                {
                    "Tick" => BarResolution.Tick, "Second" => BarResolution.Second,
                    "Minute" => BarResolution.Minute, "Hour" => BarResolution.Hour, "Daily" => BarResolution.Daily,
                    _ => throw new ArgumentException($"分辨率非法：{value}"),
                };
                break;
            case "param":
            {
                var kv = value.Split('=', 2);
                if (kv.Length != 2) throw new ArgumentException($"--param 须 k=v：{value}");
                options.Parameters[kv[0]] = kv[1];
                break;
            }
            // 未知选项忽略（保持与 CtpConsole 直跑同容错）
        }
    }
    return (name, options);
}

/// <summary>等会话回到已登录（后台 <see cref="ReloginAsync"/> 负责重登 + 重订阅）。返回是否在期限内恢复。</summary>
async Task<bool> WaitSessionAliveAsync(int timeoutMs)
{
    if (session.IsConnected) return true;
    AppLog.Line($"[bot] 会话已断开——等待重连（最多 {timeoutMs / 1000}s）…");
    var deadline = Environment.TickCount64 + timeoutMs;
    while (!session.IsConnected && Environment.TickCount64 < deadline)
        await Task.Delay(500);
    if (session.IsConnected) AppLog.Line("[bot] 会话已恢复。");
    return session.IsConnected;
}

/// <summary>撤掉本账户全部活单（'3' 未成交在队列 / '1' 部分成交在队列）。失败不阻断启动。</summary>
async Task SweepOpenOrdersAsync()
{
    try
    {
        var orders0 = await session.QueryOrderAsync();
        var open = orders0.Where(o => o.OrderStatusRaw is (byte)'3' or (byte)'1').ToList();
        if (open.Count == 0) { AppLog.Line("[bot] 清场：无活单"); return; }
        var lr = session.LoginResult ?? throw new InvalidOperationException("未登录");
        AppLog.Line($"[bot] 清场：撤 {open.Count} 张接管前活单…");
        foreach (var o in open)
        {
            try
            {
                await session.OrderActionAsync(new CtpOrderRef(broker, user, lr.FrontId, lr.SessionId, o.OrderRef, o.ExchangeId));
            }
            catch (Exception ex) { AppLog.Line($"[bot] 清场撤单失败 ref={o.OrderRef}：{ex.Message}（继续）"); }
        }
    }
    catch (Exception ex) { AppLog.Line($"[bot] 清场不可用：{ex.Message}（继续启动）"); }
}

/// <summary>交易所重启/连接断开后的重登 + 重订阅（最多 10 次，5s 间隔）。失败则退出进程交给 watchdog。</summary>
async Task ReloginAsync()
{
    try
    {
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            await Task.Delay(attempt == 1 ? 500 : 5_000);
            try
            {
                await session.LoginAsync(loginRequest);
                await session.SubscribeMarketDataAsync(mdConnect, instruments);
                AppLog.Line($"[bot] 重连成功（第 {attempt} 次）——引擎继续运行");
                return;
            }
            catch (Exception ex)
            {
                AppLog.Line($"[bot] 重连失败（{attempt}/10）：{ex.Message}");
            }
        }
        AppLog.Line("[bot] 重连 10 次未成功——退出，交由 watchdog 拉起。");
        Environment.Exit(6);
    }
    finally { Interlocked.Exchange(ref reconnecting, 0); }
}

async Task HeartbeatLoopAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        WriteHeartbeat(live.Status.ToString());
        try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (TaskCanceledException) { return; }
    }
}

/// <summary>心跳面（watchdog 的存活判据——直连 bot 不再产生前置日志帧）：
/// 原子写（临时文件 + Move），读方永远看不到半个文件。</summary>
void WriteHeartbeat(string status)
{
    var path = Path.Combine(dataDir, "directbot.heartbeat.json");
    var payload = new
    {
        account = user,
        strategy = strategyName,
        instruments,
        engine = status,
        ticks = Interlocked.Read(ref ticks),
        orders = Interlocked.Read(ref orders),
        trades = Interlocked.Read(ref trades),
        lastTickAtUtc = lastTickAt,
        updatedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        pid = Environment.ProcessId,
        exchange = $"{exHost}:{exPort}",
    };
    try
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }
    catch { /* 心跳写失败不应影响交易 */ }
}

/// <summary>挂机等待：Ctrl+C / SIGTERM 优雅停；stdin 命令（无人值守时 EOF 不退出）。</summary>
async Task WaitBotExitAsync()
{
    var exit = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; AppLog.Line("[bot] 收到 Ctrl+C——优雅停止…"); exit.TrySetResult(); };
    // 停服 / ssh 断开送 SIGTERM，不经过 CancelKeyPress（同 CtpConsole 的反面教训：只挂后者会跳过撤单腿）。
    try
    {
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            AppLog.Line("[bot] 收到 SIGTERM——优雅停止…");
            exit.TrySetResult();
        });
        _ = Task.Run(async () =>
        {
            while (!exit.Task.IsCompleted)
            {
                var line = await Console.In.ReadLineAsync();
                if (line is null) { await Task.Delay(1000); continue; }   // EOF（无人值守）不退出
                switch (line.Trim().ToLowerInvariant())
                {
                    case "quit" or "exit": AppLog.Line("[bot] 收到退出命令。"); exit.TrySetResult(); return;
                    case "stop": AppLog.Line("[bot] 停止策略（输 quit 退出）…"); try { await live.StopAsync(); } catch { } break;
                    case "status": AppLog.Line($"[bot] 状态 {live.Status} ticks={ticks} orders={orders} trades={trades}"); break;
                }
            }
        });
        await exit.Task;
    }
    catch (PlatformNotSupportedException)
    {
        // 个别平台不支持 SIGTERM 注册——Ctrl+C 路径仍然可用
        await exit.Task;
    }
}

static string Fmt(double v) => v == 0 ? "-" : v.ToString("0.##", CultureInfo.InvariantCulture);

/// <summary>--run-strategy 的选项面（与 CtpConsole 直跑同默认值）。</summary>
file sealed class StrategyOptions
{
    public List<string> Instruments { get; } = new();
    public bool Real { get; set; }
    public bool Verbose { get; set; }
    public int MaxVolume { get; set; } = 1;
    public decimal Cash { get; set; } = 1_000_000m;
    public BarResolution Resolution { get; set; } = BarResolution.Tick;
    public Dictionary<string, string> Parameters { get; } = new();
}

/// <summary>
/// 客户端日志输出——一律写「真 stdout」（<see cref="Init"/> 启动期经 <see cref="Console.OpenStandardOutput"/> 捕获）。
/// <b>为什么不用 Console.WriteLine：</b>Lean 的 LiveTradingResultHandler.SetAlgorithm 会 Console.SetOut(
/// algorithm.Debug)——引擎期客户端的 Console 输出被回收成算法 Debug → Messaging Log.Trace → CtpLeanLogForwarder
/// → LogReceived → 客户端再写 Console → 再被回收 ⇒ 回声递归（2026-10-14 实测 7min 112MB）。写真 OS 流断链。
/// </summary>
internal static class AppLog
{
    private static TextWriter? _out;
    public static void Init() => _out = new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding) { AutoFlush = true };
    public static void Line(string s) => (_out ?? Console.Out).WriteLine(s);
}

/// <summary>极简 --key value 参数表（与 CtpConsole 的 Args 同款）。</summary>
internal sealed class Args
{
    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);
    public Args(string[] argv)
    {
        for (var i = 0; i < argv.Length; i++)
        {
            if (!argv[i].StartsWith("--")) continue;
            var key = argv[i][2..];
            var value = i + 1 < argv.Length && !argv[i + 1].StartsWith("--") ? argv[++i] : "true";
            _map[key] = value;
        }
    }
    public string? Get(string key) => _map.TryGetValue(key, out var v) ? v : null;
}
