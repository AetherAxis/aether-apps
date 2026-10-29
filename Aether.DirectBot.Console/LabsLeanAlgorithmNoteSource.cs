using Aether.Trading.Runtime.Domain;

/// <summary>
/// labs → Lean 算法笔记（只读）：扫描 <c>&lt;labs&gt;/leanalgorithms/*.lean.cs</c>。
/// <para>
/// CtpConsole 走 <c>INoteCatalogSource</c> + <c>LeanAlgorithmNoteSource</c> 两跳（那张表要覆盖
/// codestrategies/strategies 等全部节点类型，因为 console 还跑 IStrategy 壳引擎）。直连客户端只跑
/// Lean 算法，直接扫一个目录即可——不为一种节点类型搬全套 Notes 目录映射胶水。
/// </para>
/// <para>
/// <b>名称 = 去复合扩展名的文件名</b>（<c>market-maker.lean.cs</c> → <c>market-maker</c>），
/// 与 <c>--run-strategy</c> 的取值同一口径（<c>LeanAlgorithmScriptingService</c> 用 <c>Title</c> 注册）。
/// </para>
/// </summary>
public sealed class LabsLeanAlgorithmNoteSource : ILeanAlgorithmNoteSource
{
    private const string CompoundExtension = ".lean";

    private readonly string _dir;

    public LabsLeanAlgorithmNoteSource(string labsRoot)
        => _dir = Path.Combine(labsRoot, "leanalgorithms");

    public Task<IReadOnlyList<LeanAlgorithmNote>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_dir))
            return Task.FromResult<IReadOnlyList<LeanAlgorithmNote>>(Array.Empty<LeanAlgorithmNote>());

        var notes = new List<LeanAlgorithmNote>();
        foreach (var file in Directory.GetFiles(_dir, "*.lean.cs", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string content;
            try { content = File.ReadAllText(file); }
            catch { continue; }   // 单文件读失败不阻断整轮加载（同 DirectoryNoteCatalogSource 的取舍）

            var rel = Path.GetRelativePath(Path.GetDirectoryName(_dir)!, file);
            notes.Add(new LeanAlgorithmNote(rel, NameOf(file), content));
        }

        return Task.FromResult<IReadOnlyList<LeanAlgorithmNote>>(notes);
    }

    public Task<string> SaveAsync(string name, string content, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("直连 bot 只读——编辑算法走桌面端资料库（labs）");

    /// <summary><c>market-maker.lean.cs</c> → <c>market-maker</c>。</summary>
    internal static string NameOf(string filePath)
    {
        var stem = Path.GetFileNameWithoutExtension(filePath);   // market-maker.lean
        return stem.EndsWith(CompoundExtension, StringComparison.OrdinalIgnoreCase)
            ? stem[..^CompoundExtension.Length]
            : stem;
    }
}
