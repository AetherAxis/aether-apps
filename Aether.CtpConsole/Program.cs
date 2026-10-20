using System.Globalization;
using Ctp.Net;
using Ctp.Net.Client.DependencyInjection;
using Acme.Notes.Domain.Catalog;
using Acme.Notes.Infrastructure.Catalog;
using Aether.Trading.Domain;
using Aether.Trading.Runtime.Domain;
using Aether.Trading.Runtime.Lean;
using Aether.Trading.Runtime.Lean.DependencyInjection;
using Aether.Trading.Scripting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// ═══════════════════════════════════════════════════════════════════════════
// CTP console 客户端——登录 / 交易 / 行情 / 策略（Lean 实盘引擎，含 bot），
// 全部复用桌面端同款组件（ICtpSession 官方 SDK 链路 + ILiveTradingRuntime），
// 无 GUI 依赖：SSH 挂机跑 bot / 服务器无人值守 / 轻量手动交易。
//
// 用法（配置档案——推荐）：前置/凭据存 ~/.aether/ctp-console/config.json 的档案（首次启动生成模板），
//   之后只需 --profile <名>；命令行参数逐项覆盖档案。
//   dotnet run --project aether-apps/Aether.CtpConsole -- --profile botmm
//   （也可全命令行：--front tcp://x:17801 --md-front tcp://x:17802 --user xx --pass xx）
//
// **一个目录一个完整实例**（2026-10-11 用户定调）：config.json / labs/ / ctp.db / lean-algo-cache
// 全部默认落在**当前工作目录**——不碰 ~/.aether、不与桌面端共享任何东西。双 bot = 两个目录各跑一个
// console（各自 config.json + labs），完全隔离；发布目录拷到哪就在哪跑（self-contained 部署友好）。
//
// 直跑模式（挂机——2026-10-12）：
//   dotnet run -- --run-strategy market-maker ax001 --real --max-volume 5 [--data <实例目录>] [--exchange 127.0.0.1:17800]
//   登录 → 清场（JSON-RPC 旁路撤接管前活单）→ 启策略 → 阻塞挂机；Ctrl+C 或输 quit 优雅停（停时撤单腿），
//   stdin 可用命令：quit/exit（退出）、stop（停策略）、status（引擎状态）。服务器 RDP 两窗口各跑一条即双 bot 驻留。
//
// REPL 命令（help 可打印）：
//   sub <合约>...                          订阅行情（tick 实时打印）
//   unsub                                  退订全部行情
//   tick on|off                            tick 打印开关（bot 挂机时建议 off）
//   account / positions [合约] / orders [合约] / instruments
//   buy|sell <o|c|ct|cy> <合约> <价格|market> <数量>    下单（开/平/平今/平昨）
//   cancel <OrderRef>                      撤单
//   strategy list                           列出已编译策略（两种引擎）
//   strategy run <名> <合约>... [--engine lean|strategy] [--real] [--max-volume N]
//                                [--resolution Tick|Second|Minute|Hour|Daily]
//                                [--param k=v]... [--verbose] [--cash 1000000]
//   strategy stop / status
//   quit
// ═══════════════════════════════════════════════════════════════════════════

HostLog.Init();   // 真流捕获须在引擎 Console.SetOut 重定向之前（见 HostLog 注释）

// —— 配置文件 + 档案解析（命令行 > 配置档案 > 内置默认）——
// **一个目录一个完整实例**（用户定调 2026-10-11）：config.json / labs/ / ctp.db / lean-algo-cache
// 全部默认落在**当前工作目录**——不碰 ~/.aether、不碰桌面端任何东西。双 bot = 两个目录各跑一个
// console（各自 config.json + labs），完全隔离；发布目录拷到哪就在哪跑（self-contained 部署友好）。
// --config / --labs / --data 可逐项覆盖。
var args2 = new Args(args);
var cwd = Directory.GetCurrentDirectory();
var configPath = args2.Get("config") ?? Path.Combine(cwd, "config.json");

ConsoleConfig config;
if (File.Exists(configPath))
{
    config = ConsoleConfig.Load(configPath);
}
else
{
    // 首次启动：生成模板（占位凭据）——填好后重跑，或继续用命令行参数
    config = ConsoleConfig.Template();
    config.Save(configPath);
    HostLog.Line($"已生成配置模板：{configPath}（填好 front/user/password 后用 --profile 登录；命令行参数可直接覆盖）");
}

// 档案选择：--profile 显式 > defaultProfile > 唯一档案；命令行参数逐项覆盖档案
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

// 全字段 Trim：账户名/前置地址带尾空格 = 连到「同名带空格」的幽灵账户上 0 余额（2026-10-12 服务器实测踩中——
// config.json 手填密码时不小心带上的空格让 botmm 登成了「botmm 」）。账户名永远不会以空格开头/结尾，一律去。
var front = (args2.Get("front") ?? profile?.Front ?? throw new ArgumentException("缺少 --front <交易前置>（或配置档案的 front）")).Trim();
var mdFront = (args2.Get("md-front") ?? profile?.MdFront ?? "").Trim();
var user = (args2.Get("user") ?? profile?.User ?? throw new ArgumentException("缺少 --user <账户>（或配置档案的 user）")).Trim();
var password = (args2.Get("pass") ?? profile?.Password ?? throw new ArgumentException("缺少 --pass <密码>（或配置档案的 password）")).Trim();
var broker = (args2.Get("broker") ?? profile?.Broker ?? "9999").Trim();
var appId = (args2.Get("app") ?? profile?.AppId ?? "simnow_client_test").Trim();
var authCode = (args2.Get("auth") ?? profile?.AuthCode ?? "0000000000000000").Trim();
if (!string.IsNullOrWhiteSpace(profileName)) HostLog.Line($"档案：{profileName}");

// labs 同样落当前目录（命令行 > 配置 > ./labs）。算法/策略笔记放这里：
// leanalgorithms/*.lean.cs / coderules/*.coderule.cs / strategies/*.yaml。
var dataDir = args2.Get("data") ?? cwd;   // ctp.db + lean-algo-cache 也随实例目录走
var labsRoot = args2.Get("labs") ?? config.Labs ?? Path.Combine(dataDir, "labs");
Directory.CreateDirectory(dataDir);
if (!Directory.Exists(labsRoot))
    HostLog.Line($"⚠ labs 目录不存在：{labsRoot}（策略/算法加载为空）——把笔记放进来或 --labs 指定");

// —— DI 组装（全部现成组件；与 Stockapp App.axaml.cs 同款顺序）——
var services = new ServiceCollection();
services.AddCtpInfrastructure(sqliteDbPath: Path.Combine(dataDir, "ctp.db"));
services.AddTradingRuntimeLean();           // ILiveTradingRuntime → LeanLiveTradingService（consumes ICtpSession）
services.AddTradingScripting();           // IStrategy 壳引擎的脚本加载（labs → StrategyRegistry）
services.AddLeanAlgorithmServices(dataDir); // Lean 算法脚本编译（labs → LeanAlgorithmRegistry）
services.AddSingleton<INoteCatalogSource>(_ => new DirectoryNoteCatalogSource(() => labsRoot));
services.AddSingleton<IStrategyNoteSource, StrategyNoteSource>();
services.AddSingleton<ILeanAlgorithmNoteSource, LeanAlgorithmNoteSource>();
services.AddLogging(b => b.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.None)); // 引擎 ILogger → stdout
await using var sp = services.BuildServiceProvider();

var session = sp.GetRequiredService<ICtpSession>();
var live = sp.GetRequiredService<ILiveTradingRuntime>();
var scripting = sp.GetRequiredService<IStrategyScriptingService>();
var leanScripting = sp.GetRequiredService<ILeanAlgorithmScriptingService>();

// —— 事件 → console 打印 ——
var tickOn = true;
var subscribed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
session.ConnectionStateChanged += (_, s) => HostLog.Line($"[连接] {s}");
session.OrderReceived += (_, o) => HostLog.Line($"[报单] {o}");
session.TradeReceived += (_, t) => HostLog.Line($"[成交] {t}");
session.TickReceived += (_, t) =>
{
    if (tickOn)
        HostLog.Line($"[tick] {t.InstrumentId} last={Fmt(t.LastPrice)} bid={Fmt(t.BidPrice)}/{t.BidVolume} ask={Fmt(t.AskPrice)}/{t.AskVolume} vol={t.Volume} {t.UpdateTime}.{t.UpdateMillisec:000}");
};
live.LogReceived += (_, line) => HostLog.Line(line);
live.StatusChanged += (_, s) => HostLog.Line($"[实盘] 状态: {s}");

// —— 登录 ——
HostLog.Line($"登录中：{front} 账户 {user} …");
var login = await session.LoginAsync(new CtpLoginRequest
{
    FrontAddress = front,
    MdFrontAddress = mdFront,
    BrokerId = broker,
    UserId = user,
    Password = password,
    AppId = appId,
    AuthCode = authCode,
});
HostLog.Line($"已登录：交易日 {login.TradingDay} FrontId={login.FrontId} SessionId={login.SessionId} MaxOrderRef={login.MaxOrderRef}");

// —— 策略/算法笔记加载（labs → 编译 → 注册表；console 生命周期内加载一次，改笔记重跑）——
await scripting.LoadAsync();
await leanScripting.LoadAsync();
PrintStrategies();

// —— 直跑模式（--run-strategy <名> [合约...] [选项]——无 REPL 挂机：登录→清场→启策略→阻塞）——
// 位置参数（合约）不在 Args（--key value）里——从原始 args 扫描 --run-strategy 值后的非 -- token。
if (args.FirstOrDefault(a => a.Equals("--run-strategy", StringComparison.OrdinalIgnoreCase)) is { } rsFlag
    && Array.IndexOf(args, rsFlag) is var rsIdx && rsIdx >= 0 && rsIdx + 1 < args.Length)
{
    var strategyName = args[rsIdx + 1];
    var instruments = args.Skip(rsIdx + 2).TakeWhile(a => !a.StartsWith("--")).ToList();
    // StrategyRunAsync 的 parts[0] = 策略名（REPL 调它时已剥 "strategy run" 前缀）——这里同样不带前缀
    var passThrough = new List<string> { strategyName };
    passThrough.AddRange(instruments);
    // 其余 -- 选项（--real/--max-volume/--engine/--resolution/--param/--verbose/--cash）原样透传给 StrategyRunAsync。
    // 控制台级参数（--config/--labs/--data/--profile/--exchange）不透传——旧版把它们连值一起当合约
    // （合约=zx001,--exchange,117.72.186.128:17800——2026-10-20 实测踩中；用法注释尾部的 [--exchange] 照抄必踩）
    var consoleKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "config", "labs", "data", "profile", "exchange" };
    var skipValue = false;
    foreach (var a in args.Skip(rsIdx + 1).Skip(1 + instruments.Count))
    {
        if (skipValue) { skipValue = false; continue; }
        if (a.StartsWith("--") && consoleKeys.Contains(a[2..])) { skipValue = true; continue; }
        if (a != rsFlag) passThrough.Add(a);
    }

    await SweepOpenOrdersAsync();
    // 清场（旁路撤 30+ 张单触发前置推送流）期间 CTP 会话可能被断（4097——SDK 自动重连）——
    // 立即启策略会被「CTP 未登录」拒。等会话恢复再启动：CtpSessionService.ReconnectAsync 重建
    // trader 走完整登录（连接→认证→登录→结算确认各 15s 超时——最坏 ~60s），30s 窗口不够
    // （2026-10-13 服务器实测踩中：重连重登已到「登录响应」但结算确认未完 ⇒ 会话未 Connected
    // ⇒ 30s 到点误判退出）。等 90s，每 10s 报进度。
    var reconnectStart = Environment.TickCount64;
    CtpConnectionStatus lastStatus = CtpConnectionStatus.Reconnecting;
    session.ConnectionStateChanged += (_, s) => lastStatus = s;
    while (!session.IsConnected && Environment.TickCount64 - reconnectStart < 90_000)
    {
        await Task.Delay(500);
        var elapsed = Environment.TickCount64 - reconnectStart;
        if (elapsed % 10_000 < 500)
            HostLog.Line($"[bot] 等待 CTP 会话恢复…（{elapsed / 1000}s/90s，当前 {lastStatus}）");
    }
    if (!session.IsConnected)
    {
        HostLog.Line("[bot] 清场后 CTP 会话 90s 未恢复——退出（重跑即可）。");
        session.Logout();
        return;
    }
    HostLog.Line($"[bot] 启动策略 {strategyName}（合约 {string.Join(",", instruments)}）——Ctrl+C 或输 quit 停止");
    await StrategyRunAsync(passThrough.ToArray());
    // 等引擎脱离 Starting（StartAsync 返回时可能仍在 Starting——立即查 IsRunning 会误判启动失败）
    while (live.Status is LiveTradingRuntimeStatus.Starting or LiveTradingRuntimeStatus.Idle)
        await Task.Delay(500);
    if (live.Status == LiveTradingRuntimeStatus.Running)
        await WaitBotExitAsync();
    else
        HostLog.Line($"[bot] 引擎未进入运行态（{live.Status}）——退出。");
    if (live.IsRunning) { try { await live.StopAsync(); } catch { } }
    session.Logout();
    HostLog.Line("[bot] 已退出。");
    return;
}

// —— REPL ——
HostLog.Line("输入 help 查看命令。");
while (true)
{
    HostLog.Prompt("> ");
    var line = Console.ReadLine();
    if (line is null) break;   // EOF（Ctrl+D / 管道结束）
    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length == 0) continue;
    try
    {
        switch (parts[0].ToLowerInvariant())
        {
            case "quit" or "exit":
                await ShutdownAsync();
                return;
            case "help":
                PrintHelp();
                break;
            case "sub":
                await SubscribeAsync(parts[1..]);
                break;
            case "unsub":
                session.UnsubscribeMarketData();
                subscribed.Clear();
                HostLog.Line("已退订全部行情");
                break;
            case "tick":
                tickOn = parts.Length < 2 || !string.Equals(parts[1], "off", StringComparison.OrdinalIgnoreCase);
                HostLog.Line($"tick 打印 {(tickOn ? "开" : "关")}");
                break;
            case "account":
                HostLog.Line((await session.QueryAccountAsync()).ToString() ?? "");
                break;
            case "positions" or "pos":
                PrintList(await session.QueryPositionAsync(parts.Length > 1 ? parts[1] : null));
                break;
            case "orders":
                PrintList(await session.QueryOrderAsync(parts.Length > 1 ? parts[1] : null));
                break;
            case "instruments":
                PrintList(await session.QueryInstrumentsAsync());
                break;
            case "buy" or "sell":
                await OrderAsync(parts);
                break;
            case "cancel":
                await CancelAsync(parts);
                break;
            case "strategy":
                await StrategyAsync(parts);
                break;
            case "status":
                HostLog.Line($"引擎 {live.Status}（运行中={live.IsRunning}）tick 打印={(tickOn ? "开" : "关")}");
                break;
            default:
                HostLog.Line($"未知命令 {parts[0]}（help 查看命令）");
                break;
        }
    }
    catch (TimeoutException)
    {
        // 已知边界（CLAUDE.md CTP 前置节）：查报单空表时 SDK 不派发 17B tag 空应答 ⇒ 30s 超时后管线自动恢复
        //（真前置首查过滤时同样超时——录制实证）。查询超时不影响后续命令。
        HostLog.Line("[超时] 空表查询 SDK 不派发应答（已知边界——委托/持仓空表时可能发生），管线已恢复，可继续。");
    }
    catch (Exception ex)
    {
        HostLog.Line($"[错误] {ex.Message}");
    }
}

// —— 命令实现 ——

async Task SubscribeAsync(string[] symbols)
{
    if (symbols.Length == 0) { HostLog.Line("用法：sub <合约> <合约>…"); return; }
    if (mdFront.Length == 0) { HostLog.Line("缺少行情前置（启动参数 --md-front tcp://ip:port）"); return; }
    await session.SubscribeMarketDataAsync(new CtpMarketDataConnect
    {
        MdFrontAddress = mdFront, BrokerId = broker, UserId = user, Password = password,
    }, symbols);
    foreach (var s in symbols) subscribed.Add(s);
    HostLog.Line($"已订阅：{string.Join(' ', symbols)}");
}

async Task OrderAsync(string[] parts)
{
    // buy|sell <o|c|ct|cy> <合约> <价格|market> <数量> —— 5 个 token
    if (parts.Length != 5) { HostLog.Line("用法：buy|sell <o|c|ct|cy> <合约> <价格|market> <数量>"); return; }
    var direction = parts[0].Equals("buy", StringComparison.OrdinalIgnoreCase) ? CtpDirection.Buy : CtpDirection.Sell;
    var offset = parts[1].ToLowerInvariant() switch
    {
        "o" => CtpOffsetFlag.Open,
        "c" => CtpOffsetFlag.Close,
        "ct" => CtpOffsetFlag.CloseToday,
        "cy" => CtpOffsetFlag.CloseYesterday,
        _ => throw new ArgumentException($"开平非法：{parts[1]}（o=开 c=平 ct=平今 cy=平昨）"),
    };
    var isMarket = parts[3].Equals("market", StringComparison.OrdinalIgnoreCase);
    var price = isMarket ? 0 : ParseDouble(parts[3]);
    var volume = int.Parse(parts[4], CultureInfo.InvariantCulture);
    var result = await session.OrderInsertAsync(new CtpOrderRequest
    {
        BrokerId = broker, InvestorId = user, InstrumentId = parts[2],
        Direction = direction, OffsetFlag = offset,
        PriceType = isMarket ? CtpOrderPriceType.Market : CtpOrderPriceType.Limit,
        LimitPrice = price, Volume = volume,
    });
    HostLog.Line($"已报单 ref={result.OrderRef} reqId={result.RequestId}（回报异步推送——orders 查状态）");
}

async Task CancelAsync(string[] parts)
{
    if (parts.Length is < 2 or > 3) { HostLog.Line("用法：cancel <OrderRef> [exchangeId]"); return; }
    var loginResult = session.LoginResult ?? throw new InvalidOperationException("未登录");
    await session.OrderActionAsync(new CtpOrderRef(broker, user, loginResult.FrontId, loginResult.SessionId, parts[1], parts.Length == 3 ? parts[2] : ""));
    HostLog.Line("撤单请求已发（结果经报单回报推送）");
}

async Task StrategyAsync(string[] parts)
{
    if (parts.Length < 2) { HostLog.Line("用法：strategy list | run <名> <合约>... [选项] | stop"); return; }
    switch (parts[1].ToLowerInvariant())
    {
        case "list":
            PrintStrategies();
            break;
        case "stop":
            HostLog.Line("请求停止…");
            await live.StopAsync();
            break;
        case "run":
            await StrategyRunAsync(parts[2..]);
            break;
        default:
            HostLog.Line($"未知子命令 {parts[1]}（list / run / stop）");
            break;
    }
}

async Task StrategyRunAsync(string[] parts)
{
    if (parts.Length < 2) { HostLog.Line("用法：strategy run <名> <合约>... [--engine lean|strategy] [--real] [--max-volume N] [--param k=v]..."); return; }
    var name = parts[0];
    var engine = LiveStrategyEngine.LeanAlgorithm;   // console 主场景 = bot（Lean 算法）
    var instruments = new List<string>();
    var resolution = BarResolution.Tick;              // bot 默认 Tick（桌面实盘 tab 同款推荐）
    var real = false;
    var maxVolume = 1;
    var verbose = false;
    var cash = 1_000_000m;
    var parameters = new Dictionary<string, string>();
    for (var i = 1; i < parts.Length; i++)
    {
        switch (parts[i])
        {
            case "--engine":
                engine = parts[++i].Equals("strategy", StringComparison.OrdinalIgnoreCase) ? LiveStrategyEngine.IStrategy : LiveStrategyEngine.LeanAlgorithm;
                break;
            case "--real":
                real = true;
                break;
            case "--max-volume":
                maxVolume = int.Parse(parts[++i], CultureInfo.InvariantCulture);
                break;
            case "--resolution":
                resolution = parts[++i] switch
                {
                    "Tick" => BarResolution.Tick, "Second" => BarResolution.Second,
                    "Minute" => BarResolution.Minute, "Hour" => BarResolution.Hour,
                    "Daily" => BarResolution.Daily,
                    _ => throw new ArgumentException($"分辨率非法：{parts[i]}"),
                };
                break;
            case "--verbose":
                verbose = true;
                break;
            case "--cash":
                cash = ParseDecimal(parts[++i]);
                break;
            case "--param":
            {
                var kv = parts[++i].Split('=', 2);
                if (kv.Length != 2) throw new ArgumentException($"--param 须 k=v：{parts[i]}");
                parameters[kv[0]] = kv[1];
                break;
            }
            default:
                instruments.Add(parts[i]);
                break;
        }
    }
    if (instruments.Count == 0) { HostLog.Line("至少一个合约（strategy run <名> <合约>…）"); return; }
    if (mdFront.Length == 0) { HostLog.Line("缺少行情前置（启动参数 --md-front tcp://ip:port）"); return; }

    HostLog.Line($"[实盘] 启动：合约={string.Join(",", instruments)} 引擎={engine} 策略={name} 分辨率={resolution} " +
                      $"真实下单={(real ? $"开（单笔开仓上限 {maxVolume} 手）" : "关（仅信号）")}");
    var (ok, err) = await live.StartAsync(new LiveTradingRuntimeRequest
    {
        Instruments = instruments,
        Engine = engine,
        StrategyName = name,
        InitialCash = cash,
        StrategyParameters = parameters.Count > 0 ? parameters : null,
        Resolution = resolution,
        VerboseLog = verbose,
        EnableRealOrders = real,
        MaxOrderVolume = maxVolume,
        MarketDataConnect = new MarketDataConnectInput
        {
            MdFrontAddress = mdFront, BrokerId = broker, UserId = user, Password = password,
        },
    });
    if (!ok) HostLog.Line($"[实盘] 启动失败：{err}");
}

async Task ShutdownAsync()
{
    if (live.IsRunning)
    {
        HostLog.Line("停止策略引擎…");
        try { await live.StopAsync(); } catch { }
    }
    session.Logout();
    HostLog.Line("已退出。");
}

/// <summary>启动清场（直跑模式「接管」语义）：撤掉本账户全部活单。bot 意外死亡/被硬杀后旧挂单留在
/// 交易所簿上，而新实例的 OrderTicket 票根不认识它们（GetOpenOrders 刻意返空）——不清场会双份挂单。
/// **走 JSON-RPC 直连交易所旁路**（--exchange 覆盖，默认 127.0.0.1:17800——服务器上 bot 与交易所同机）：
/// CTP 线协议的查报单是已知边界（SDK 不派发 17B 空应答，恒 30s 超时）不可用。失败不阻断启动（提示后继续）。</summary>
async Task SweepOpenOrdersAsync()
{
    var exchangeAddr = args2.Get("exchange") ?? "127.0.0.1:17800";
    var parts = exchangeAddr.Split(':');
    var host = parts.Length == 2 ? parts[0] : "127.0.0.1";
    var port = parts.Length == 2 && int.TryParse(parts[1], out var p) ? p : 17800;
    try
    {
        using var rpc = new Aether.Exchange.Rpc.SimRpcClient(host, port);
        await rpc.ConnectAsync();
        // 明文域登录（密码与前置域独立——未设时首登补设，同密码则两域同通）
        await rpc.LoginAsync(new Aether.Exchange.Rpc.LoginArg(broker, user, password));
        var orders = await rpc.QueryOrdersAsync(null);
        var openOrders = orders.Where(o => o.OrderStatusRaw is (byte)'3' or (byte)'1').ToList();   // '3' 未成交在队列 / '1' 部分成交在队列
        if (openOrders.Count == 0) { HostLog.Line("[bot] 清场：无活单"); return; }
        HostLog.Line($"[bot] 清场：撤 {openOrders.Count} 张接管前活单…");
        foreach (var o in openOrders)
        {
            try
            {
                await rpc.CancelOrderAsync(new Aether.Exchange.CancelKey(broker, user, 0, 0, o.OrderRef, ""));
                HostLog.Line($"[bot] 清场撤单 {o.InstrumentId} ref={o.OrderRef}");
            }
            catch (Exception ex) { HostLog.Line($"[bot] 清场撤单失败 ref={o.OrderRef}：{ex.Message}（继续）"); }
        }
    }
    catch (Exception ex) { HostLog.Line($"[bot] 清场旁路不可用（{exchangeAddr}：{ex.Message}）——继续启动"); }
}

/// <summary>直跑模式的挂机等待：Ctrl+C（CancelKeyPress，e.Cancel 防立即死）或 stdin 输 quit/exit 退出；
/// stop = 只停策略不退出（可再次 strategy run？直跑模式无 REPL——stop 后输 quit 退出）。
/// stdin EOF（管道结束）不退出——挂机场景无输入者。</summary>
async Task WaitBotExitAsync()
{
    var exit = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;   // 拦截默认终止——走优雅路径（StopAsync 撤单腿）
        HostLog.Line("[bot] 收到 Ctrl+C——优雅停止…");
        exit.TrySetResult();
    };
    _ = Task.Run(async () =>
    {
        while (!exit.Task.IsCompleted)
        {
            var line = await Console.In.ReadLineAsync();
            if (line is null) { await Task.Delay(1000); continue; }   // EOF（无人值守）不退出
            switch (line.Trim().ToLowerInvariant())
            {
                case "quit" or "exit":
                    HostLog.Line("[bot] 收到退出命令。");
                    exit.TrySetResult();
                    return;
                case "stop":
                    HostLog.Line("[bot] 停止策略（输 quit 退出）…");
                    try { await live.StopAsync(); } catch { }
                    break;
                case "status":
                    HostLog.Line($"[bot] 状态 {live.Status}（运行中={live.IsRunning}）");
                    break;
            }
        }
    });
    await exit.Task;
}

void PrintStrategies()
{
    var lean = LeanAlgorithmRegistry.GetRegisteredNames();
    HostLog.Line(lean.Count > 0
        ? $"Lean 算法（--engine lean）：{string.Join(", ", lean.OrderBy(n => n, StringComparer.Ordinal))}"
        : "Lean 算法：无（labs leanalgorithms 目录为空或编译失败）");
    var strats = StrategyRegistry.GetRegisteredNames();
    HostLog.Line(strats.Count > 0
        ? $"IStrategy（--engine strategy）：{string.Join(", ", strats.OrderBy(n => n, StringComparer.Ordinal))}"
        : "IStrategy：无（labs coderules/strategies 目录为空或编译失败）");
}

static void PrintList<T>(IReadOnlyList<T> list)
{
    if (list.Count == 0) { HostLog.Line("（空）"); return; }
    foreach (var item in list) HostLog.Line(item?.ToString() ?? "");
}

static void PrintHelp()
{
    HostLog.Line("""
        sub <合约>...            订阅行情（tick 实时打印）
        unsub                    退订全部
        tick on|off              tick 打印开关（bot 挂机建议 off）
        account                  查资金
        positions [合约]          查持仓
        orders [合约]            查委托
        instruments              查合约表（sim 上市表）
        buy|sell <o|c|ct|cy> <合约> <价格|market> <数量>   下单（开/平/平今/平昨）
        cancel <OrderRef>        撤单
        strategy list            列出已编译策略
        strategy run <名> <合约>... [--engine lean|strategy] [--real] [--max-volume N]
                                  [--resolution Tick|Second|Minute|Hour|Daily] [--param k=v]... [--verbose] [--cash N]
        strategy stop            停止引擎
        status                   引擎状态
        quit                     退出
        """);
}

static double ParseDouble(string s) => double.Parse(s, CultureInfo.InvariantCulture);
static decimal ParseDecimal(string s) => decimal.Parse(s, CultureInfo.InvariantCulture);

static string Fmt(double v) => v == 0 ? "-" : v.ToString("0.##", CultureInfo.InvariantCulture);

/// <summary>
/// 宿主日志输出——一律写「真 stdout」（<see cref="Init"/> 启动期经 <see cref="Console.OpenStandardOutput"/> 捕获，
/// 不受引擎期重定向影响）。<b>为什么不用 Console.WriteLine：</b>Lean 的 LiveTradingResultHandler.SetAlgorithm 会
/// Console.SetOut(algorithm.Debug)——实盘引擎运行期宿主的 Console 输出被回收成「算法 Debug 消息」→ Messaging
/// 又 Log.Trace("Debug: …") → CtpLeanLogForwarder → LogReceived → 宿主再 Console.WriteLine → 再被回收 ⇒
/// 回声递归（每圈内容嵌套翻倍——2026-10-14 实测 7min 112MB、输出滞后分钟级、CPU 被字符串拼装吃掉，
/// console「好像不行」的主因；桌面端日志走 UI 不碰 Console 故无此症）。写真 OS 流 = 回声链断于第一跳。
/// </summary>
internal static class HostLog
{
    private static TextWriter? _out;
    public static void Init() => _out = new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding) { AutoFlush = true };
    public static void Line(string s) => (_out ?? Console.Out).WriteLine(s);
    public static void Prompt(string s) => (_out ?? Console.Out).Write(s);
}

/// <summary>极简 --key value 参数表（与 apps/LedgerWallet 的 Args 同款模式）。</summary>
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
