using Acme.Notes.Domain;
using Acme.Notes.Domain.Catalog;
using Aether.Trading.Runtime.Domain;

// —— labs 笔记源桥接（镜像 apps/Stockapp/Acme.Stockapp.Desktop/Hosting 的两个同款类——
//    host 层惯例「复用模式不复用代码」：console 是新 host，桥接类属宿主胶水，各自持有）。
//    刻意不写 namespace（global）：沿用 monorepo 惯例（旧 RootNamespace `Acme.Ctp.Console` 的 `Console`
//    段与 System.Console 在顶级语句里同名易撞——桥接类进 global namespace 零歧义；本仓 RootNamespace 为 `Aether.CtpConsole`）。 ——

/// <summary>
/// IStrategy 壳引擎的笔记源：按 <see cref="NoteNodeType.CodeStrategy"/>/<see cref="NoteNodeType.Strategy"/>
/// 过滤 labs 笔记并标记种类（Code→Roslyn 编译 IStrategy；Yaml→声明式配方）。
/// </summary>
public sealed class StrategyNoteSource : IStrategyNoteSource
{
    private readonly INoteCatalogSource _source;
    public StrategyNoteSource(INoteCatalogSource source) => _source = source;

    public async Task<IReadOnlyList<StrategyNote>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var code = await _source.EnumerateAsync(NoteNodeType.CodeStrategy, cancellationToken);
        var yaml = await _source.EnumerateAsync(NoteNodeType.Strategy, cancellationToken);
        return code
            .Select(n => new StrategyNote(n.ExternalId, n.Name, n.Content, StrategyKind.Code))
            .Concat(yaml.Select(n => new StrategyNote(n.ExternalId, n.Name, n.Content, StrategyKind.Yaml)))
            .ToList();
    }

    public Task<string> SaveAsync(string name, string content, StrategyKind kind, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("console 版只读（编辑笔记走桌面端资料库）");
}

/// <summary>
/// LeanAlgorithm 引擎的笔记源：按 <see cref="NoteNodeType.LeanAlgorithm"/> 过滤（.lean.cs 直写 QCAlgorithm 子类）。
/// </summary>
public sealed class LeanAlgorithmNoteSource : ILeanAlgorithmNoteSource
{
    private readonly INoteCatalogSource _source;
    public LeanAlgorithmNoteSource(INoteCatalogSource source) => _source = source;

    public async Task<IReadOnlyList<LeanAlgorithmNote>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var all = await _source.EnumerateAsync(NoteNodeType.LeanAlgorithm, cancellationToken);
        return all.Select(n => new LeanAlgorithmNote(n.ExternalId, n.Name, n.Content)).ToList();
    }

    public Task<string> SaveAsync(string name, string content, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("console 版只读（编辑笔记走桌面端资料库）");
}
