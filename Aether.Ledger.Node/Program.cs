using Aether.Ledger;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// ── 虚拟币链·节点宿主（批 2，2026-10-05）────────────────────────────────────────────
// 单独起节点：dotnet run --project apps/LedgerNode
// （交易所/前置是另外的进程——链节点独立存活；agent 钱包/交易所桥是未来的批 3/4 消费方）
//
// 拓扑：链节点 = 资产层（账本+出块+链文件）；TCP JSON-RPC :17805（同机默认）。
// 配置（全部在本进程读）：
//   AETHER_LEDGER_ADDR              监听端点（默认 127.0.0.1:17805，host:port）
//   AETHER_LEDGER_CHAIN_FILE        链文件路径（默认 ~/.aether/ledger.chain——JSON lines：
//                                   首行创世 + 每行一块；重启 = 读取 + 全量重放恢复）
//   AETHER_LEDGER_CHAIN_ID          新链的 chainId（默认 77；已有链从链文件读——此值忽略）
//   AETHER_LEDGER_BLOCK_INTERVAL_SEC 心跳空块间隔（默认 60——锚定新鲜度）
//   AETHER_LEDGER_PRODUCER_SEED     重启已有链的出块人种子（hex——生成链时日志打印过；
//                                   链文件只存公钥，无种子无法出块）
//   AETHER_LEDGER_LOG               日志文件（默认 <链文件>.log）
// RPC op：getInfo / getBalance / submitTx / getBlock / getTx / exportChain——
//   请求 {op, arg} 一行 → 应答 {ok, result|err} 一行（每连接串行；无推送——查询型面）
// ──────────────────────────────────────────────────────────────────────────────────────

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService();   // 服务器部署形态：SCM 启动 → ServiceLifetime（服务停止 = 优雅关停停出块+停监听）；
                                        // 交互运行（dotnet run）仍是 console lifetime，本地行为零变。
                                        // 2026-10-09 服务起不来（Start-Service 1053 超时）的根因 = 本文件漏了此调用
                                        //（同交易所 host 2026-10-06 踩过的坑——纯 console host 不向 SCM 上报必超时）。

var opts = LedgerNodeOptions.FromEnvironment();
var logPath = Environment.GetEnvironmentVariable("AETHER_LEDGER_LOG") ?? opts.ChainFilePath + ".log";
var node = new LedgerNode(opts, logPath);
builder.Services.AddHostedService(_ => new LedgerNodeHostedService(node));
await builder.Build().RunAsync();

/// <summary>节点生命周期：启动 = 加载/生成链 + 监听 + 出块循环；停止 = 停监听（链文件逐块落盘，无需 flush）。</summary>
internal sealed class LedgerNodeHostedService(LedgerNode node) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        node.Start();
        Console.WriteLine("[LEDGER] Ctrl+C 退出。独立验证器：dotnet run --project apps/LedgerVerify -- <链文件>（不需要本节点活着）");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await node.StopAsync();
    }
}
