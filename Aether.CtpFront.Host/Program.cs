using System.Diagnostics;
using Ctp.Net.Front;
using Aether.Exchange.Rpc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// ── CTP 仿真前置服务器（独立服务端进程——2026-10-05 起与交易所分离，真实世界对齐）───────────
// 单独起前置：dotnet run --project aether-ctp-hosts/Aether.CtpFront.Host（交易所须先起/随后起均可——
// 本进程监听即起，交易所不在时每个客户端会话在登录时诚实失败关连接；启动 banner 后台探活直到连上）。
// 客户端（Aether 桌面 app）一律官方 SDK 链路：期货页登录窗口前置地址填 tcp://127.0.0.1:17801/17802——
// 与连 SimNow 完全同形；第三方 CTP 软件亦可直连（协议适配层）。
//
// 拓扑（真实世界镜像：一个交易所 + N 券商前置，前置从不直连撮合）：
//   客户端 → 本进程 :17801（交易腿）/ :17802（行情腿）→ SimRpcClient → 交易所进程 :17800
//   每个客户端会话一条交易所 RPC 连接（登录绑定账户、推送按连接路由——真实券商柜台镜像）。
//
// 配置（全部在本进程读——交易所自身的 DB/BOARD 等在交易所进程）：
//   AETHER_CTP_EXCHANGE_ADDR  交易所端点（默认 127.0.0.1:17800，格式 [host:]port——同机部署即默认；
//                             交易所进程在远端则填其地址；`SimAddress.Parse` 同款格式）
//   AETHER_CTP_FRONT_ADDR     交易腿监听端点（默认 0.0.0.0:17801——对客户端；[host:]port 格式）
//   AETHER_CTP_MD_FRONT_PORT  行情腿监听端口（默认 17802；与交易腿同 host 绑定）
//   AETHER_CTP_FRONT_LOG      日志文件路径（默认空=仅控制台——Windows 服务形态由 setup 脚本设置）
// ──────────────────────────────────────────────────────────────────────────────

var builder = Host.CreateApplicationBuilder(args);

// 与交易所 host 同款：Aether.Exchange.Rpc 的诊断走 Trace.WriteLine，服务形态没有控制台，
// 不接监听器就只剩「会话关闭：交易所连接断开」这种结论行，看不到断开的原因（超时？写失败？坏帧？）。
Trace.Listeners.Add(new LogTraceListener());
Trace.AutoFlush = true;

builder.Services.AddWindowsService();   // 服务器部署形态：SCM 启动 → ServiceLifetime（服务停止 = 停监听/优雅关停）；
                                        // 交互运行（dotnet run）仍是 console lifetime，本地行为零变。
                                        // 与 Sim.Host 同款——2026-10-05 分离时两 host 都漏了此调用，2026-10-06 补齐。

var opts = FrontHostOptions.FromEnvironment();
var ctpFront = new CtpFrontServer(opts.ExchangeHost, opts.ExchangePort, opts.Host, opts.TradePort, msg => Log.Line($"[CTP-Front] {msg}"));
var ctpMdFront = new CtpFrontMdServer(opts.ExchangeHost, opts.ExchangePort, opts.Host, opts.MdPort, msg => Log.Line($"[CTP-Front-MD] {msg}"));
builder.Services.AddHostedService(_ => new FrontHostedService(ctpFront, ctpMdFront, opts));
await builder.Build().RunAsync();

/// <summary>前置生命周期：启动 = 双腿监听（监听即起——交易所不在时每会话诚实失败）+ 后台探活 banner；停止 = 停监听。</summary>
internal sealed class FrontHostedService(CtpFrontServer ctpFront, CtpFrontMdServer ctpMdFront, FrontHostOptions opts)
    : IHostedService
{
    private Task? _probe;

    public Task StartAsync(CancellationToken ct)
    {
        ctpFront.Start();   // CTP 协议前置·交易腿（客户端接入——官方 SDK/第三方 CTP 软件）
        ctpMdFront.Start(); // CTP 协议前置·行情腿（官方 SDK 行情订阅 + tick 推送）
        var trade = ctpFront.LocalEndpoint?.Port ?? opts.TradePort;
        var md = ctpMdFront.LocalEndpoint?.Port ?? opts.MdPort;
        Log.Line($"[CTP-FRONT] 前置已就绪：{opts.Host}:{trade}（交易腿）/ :{md}（行情腿） → 交易所 {opts.ExchangeHost}:{opts.ExchangePort}");
        Log.Line($"[CTP-FRONT]   客户端前置地址：tcp://<本机IP>:{trade} / tcp://<本机IP>:{md}（与连 SimNow 同形）");
        _probe = Task.Run(() => ProbeExchangeAsync(ct), ct);
        return Task.CompletedTask;
    }

    /// <summary>后台探活：交易所未起时周期重试（每 5s）直到 Hello 成功——启动序无硬依赖（前后无所谓）。</summary>
    private async Task ProbeExchangeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var probe = new SimRpcClient(opts.ExchangeHost, opts.ExchangePort);
                await probe.ConnectAsync(ct);
                var hello = probe.Hello;
                probe.Dispose();
                Log.Line($"[CTP-FRONT] 交易所连通（协议版本 {hello?.ProtocolVersion.ToString() ?? "?"}）——就绪");
                return;
            }
            catch
            {
                Log.Line($"[CTP-FRONT] 交易所 {opts.ExchangeHost}:{opts.ExchangePort} 未就绪，5s 后重试…（客户端会话此刻登录会失败）");
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_probe is not null) { try { await _probe; } catch { } }
        await ctpFront.StopAsync();
        await ctpMdFront.StopAsync();
        Log.Line("[CTP-FRONT] 前置已退出（交易所进程不受影响——账本在前置重启中不丢）。");
    }
}

/// <summary>前置宿主参数（env 见文件头注释；放本进程非 lib——与 SimHostOptions 同款「可单测」惯例留给后续需要时）。</summary>
internal sealed record FrontHostOptions(string ExchangeHost, int ExchangePort, string Host, int TradePort, int MdPort)
{
    public const string ExchangeEnvVar = "AETHER_CTP_EXCHANGE_ADDR";
    public const string FrontEnvVar = "AETHER_CTP_FRONT_ADDR";

    public static FrontHostOptions FromEnvironment()
    {
        var (exHost, exPort) = SimAddress.Parse(Environment.GetEnvironmentVariable(ExchangeEnvVar));
        // 监听端点：默认 0.0.0.0:17801（对客户端——服务器部署绑全部网卡；本地同值可用）
        var front = Environment.GetEnvironmentVariable(FrontEnvVar);
        var (host, tradePort) = ParseListen(front);
        var mdPort = int.TryParse(Environment.GetEnvironmentVariable("AETHER_CTP_MD_FRONT_PORT"), out var mdp) ? mdp : 17802;
        return new FrontHostOptions(exHost, exPort, host, tradePort, mdPort);
    }

    private static (string, int) ParseListen(string? addr)
    {
        if (string.IsNullOrWhiteSpace(addr)) return ("0.0.0.0", 17801);
        var (h, p) = SimAddress.Parse(addr);
        return (h, p);
    }
}

/// <summary>最小可观测性：恒写控制台；AETHER_CTP_FRONT_LOG 设了路径时（Windows 服务形态）追加到文件（镜像 Sim.Host 的 Log）。</summary>
internal sealed class LogTraceListener : TraceListener
{
    public override void Write(string? message) { }

    public override void WriteLine(string? message)
    {
        if (!string.IsNullOrEmpty(message)) Log.Line(message);
    }
}

internal static class Log
{
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("AETHER_CTP_FRONT_LOG");

    // 单写者锁：AppendAllText 每次都是 open/write/close，用的是独占写句柄；两个线程同时写，
    // 输的一方抛 IOException，被下面的 catch 吞掉 → **日志静默丢行**。
    // 2026-10-30 实测：查报单回 200 行时，出帧线程在连写 200 条「→ [446B] …」，
    // 派发线程那行「查报单 reqId=6 → 逐帧写出 200 条，89200B，流水 4..203」被挤掉，
    // 而同一轮的持仓（2 条）/查合约（1 条）都在——丢的恰好是行数最多、最需要的那条。
    private static readonly object Gate = new();

    public static void Line(string msg)
    {
        Console.WriteLine(msg);
        if (LogPath is null) return;
        try
        {
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                // 新文件先种 UTF-8 BOM（同 Sim.Host 的 Log——Windows PowerShell 5.1 的 Get-Content
                // 对无 BOM 的 UTF-8 按 ANSI/GBK 读会乱码；带 BOM 即自动识别）
                if (!File.Exists(LogPath) || new FileInfo(LogPath).Length == 0)
                    File.WriteAllBytes(LogPath, new byte[] { 0xEF, 0xBB, 0xBF });
                // 时间戳取在锁内：日志顺序 == 落盘顺序，便于按流水对账
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
            }
        }
        catch { /* 磁盘/权限故障不打断前置 */ }
    }
}
