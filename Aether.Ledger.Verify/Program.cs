using Aether.Ledger;

// ── 虚拟币链·独立验证器（批 1 交付的「不丢」性质载体）────────────────────────────────────
// 用法：Aether.Ledger.Verify <链文件路径>
// 读链文件（JSON lines：首行创世 + 每行一块）→ 全量重放 → 五条规则逐块校验 → 打印余额清单。
// 独立于节点/交易所存在——交易所死了、节点死了，链文件 + 本工具 = 不需要任何活进程的余额真相。
// 退出码：0 = 验证通过；2 = 验证失败（哪条规则、哪一块都会打印）。
// ──────────────────────────────────────────────────────────────────────────────────────────

if (args.Length != 1)
{
    Console.Error.WriteLine("用法：Aether.Ledger.Verify <链文件路径>");
    Console.Error.WriteLine("  链文件 = JSON lines（首行创世 + 每行一块——由节点 exportChain 产出）");
    return 2;
}

var path = args[0];
if (!File.Exists(path))
{
    Console.Error.WriteLine($"链文件不存在：{path}");
    return 2;
}

try
{
    var result = LedgerVerifier.VerifyFile(path);
    Console.WriteLine(LedgerVerifier.Summarize(result));
    return result.Ok ? 0 : 2;
}
catch (Exception ex)
{
    // 链文件结构性损坏（非法 JSON / 字段缺失 / hex/base64 非法）——同样是验证失败的一种
    Console.Error.WriteLine($"链文件不可读/结构非法：{ex.Message}");
    return 2;
}
