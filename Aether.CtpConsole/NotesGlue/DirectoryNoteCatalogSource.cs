using Acme.Notes.Domain;
using Acme.Notes.Domain.Catalog;

namespace Acme.Notes.Infrastructure.Catalog;

/// <summary>
/// <see cref="INoteCatalogSource"/> 的目录实现：扫描 labs 根目录（<c>&lt;root&gt;/&lt;目录名&gt;/*.&lt;扩展&gt;</c>），
/// 按 <see cref="CatalogNoteDirectory"/> 把子目录名映射成 <see cref="NoteNodeType"/>，读文件原文产出
/// <see cref="CatalogNote"/>（<c>ExternalId</c> = labs 相对路径）。这是权威来源（SQLite note 版已删）。
/// <para>层化：根目录经 <c>Func&lt;string?&gt;</c> 惰性获取（宿主注入 <c>IKnowledgeDirectoryProvider.CurrentDirectory</c>），
/// Infrastructure 不引用 apps/Mvvm。<c>Changed</c> 由宿主的目录 watcher 经 <see cref="NotifyChanged"/> 驱动
///（本实现不自行挂 watcher——目录变化信号源在宿主层）。</para>
/// </summary>
public sealed class DirectoryNoteCatalogSource : INoteCatalogSource
{
    private readonly Func<string?>? _rootProvider;
    private readonly Func<string, string> _readFile;

    public DirectoryNoteCatalogSource(Func<string?>? rootProvider, Func<string, string>? readFile = null)
    {
        _rootProvider = rootProvider;
        _readFile = readFile ?? (path => System.IO.File.ReadAllText(path));
    }

    /// <summary>当前 labs 根目录（Provider 未给或未打开时 null）。</summary>
    public string? Root => _rootProvider?.Invoke();

    public Task<IReadOnlyList<CatalogNote>> EnumerateAsync(NoteNodeType? nodeType = null, System.Threading.CancellationToken ct = default)
    {
        var root = Root;
        if (string.IsNullOrWhiteSpace(root) || !System.IO.Directory.Exists(root))
            return System.Threading.Tasks.Task.FromResult<IReadOnlyList<CatalogNote>>(Array.Empty<CatalogNote>());

        var result = new List<CatalogNote>();
        var types = nodeType is null
            ? CatalogNoteDirectory.AllTypes
            : new[] { nodeType.Value };

        foreach (var type in types)
        {
            ct.ThrowIfCancellationRequested();
            var layout = CatalogNoteDirectory.For(type);
            var dir = System.IO.Path.Combine(root, layout.DirectoryName);
            if (!System.IO.Directory.Exists(dir)) continue;

            foreach (var ext in layout.AllExtensions) // CodeRule 双扩展名（.coderule.cs + .coderule.fs）
            {
                string[] files;
                try
                {
                    files = System.IO.Directory.GetFiles(dir, "*" + ext, System.IO.SearchOption.TopDirectoryOnly);
                }
                catch { continue; } // 目录无权限/不可访问 → 跳过

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    string content;
                    try { content = _readFile(file); }
                    catch { continue; } // 单个文件读取失败 → 跳过（不阻断整个枚举）

                    var rel = System.IO.Path.GetRelativePath(root, file);
                    var name = System.IO.Path.GetFileNameWithoutExtension(file);
                    // 复合扩展名（.agent.md / .workflow.json / .rule.yaml / .coderule.cs / .coderule.fs / .lean.cs）
                    // 去掉首段（两种 CodeRule 扩展名首段同为 coderule，剥段一致），回退 stem
                    name = StripCompoundExtension(name, ext);
                    result.Add(new CatalogNote(rel, name, content, type));
                }
            }
        }

        return System.Threading.Tasks.Task.FromResult<IReadOnlyList<CatalogNote>>(result);
    }

    public Task<CatalogNote?> FindByExternalIdAsync(string externalId, System.Threading.CancellationToken ct = default)
    {
        var root = Root;
        if (string.IsNullOrWhiteSpace(root)) return System.Threading.Tasks.Task.FromResult<CatalogNote?>(null);

        // ExternalId = labs 相对路径；直接拼到根下读（防路径越界：resolve 后仍须在 root 下）
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, externalId));
        var rootFull = System.IO.Path.GetFullPath(root);
        if (!full.StartsWith(rootFull, System.StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(full))
            return System.Threading.Tasks.Task.FromResult<CatalogNote?>(null);

        // 反查类型：取相对路径首段目录名
        var rel = System.IO.Path.GetRelativePath(root, full);
        var seg = rel.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)[0];
        var type = CatalogNoteDirectory.TypeForDirectory(seg);
        if (type is null) return System.Threading.Tasks.Task.FromResult<CatalogNote?>(null);

        string content;
        try { content = _readFile(full); }
        catch { return System.Threading.Tasks.Task.FromResult<CatalogNote?>(null); }

        var layout = CatalogNoteDirectory.For(type.Value);
        return System.Threading.Tasks.Task.FromResult<CatalogNote?>(
            new CatalogNote(rel, StripCompoundExtension(System.IO.Path.GetFileNameWithoutExtension(full), layout.Extension), content, type.Value));
    }

    public event System.Action<string?>? Changed;

    /// <summary>宿主在目录 watcher 触发时调用，广播源变更（消费方据此重载）。
    /// <paramref name="changedRelativePath"/> 为变更项相对根目录的相对路径（labs 相对路径）；null 表示未知。
    /// 消费方可据此按「首段目录名」判是否涉及自己关心的类型（见 <see cref="CatalogNoteDirectory.TypeForDirectory"/>）。</summary>
    public void NotifyChanged(string? changedRelativePath = null) => Changed?.Invoke(changedRelativePath);

    /// <summary>复合扩展名去尾段（如 <c>foo.agent</c> 去掉 <c>.agent</c> 得 <c>foo</c>）；无复合段则原样。</summary>
    private static string StripCompoundExtension(string stem, string extension)
    {
        // extension 形如 ".agent.md"；文件名 stem（GetFileNameWithoutExtension 去的是最后一节）
        // 复合 ext 的第一段（如 "agent"）作为 stem 尾缀被 GetFileNameWithoutExtension 保留，需再剥。
        var first = extension.TrimStart('.');
        var dot = first.IndexOf('.');
        if (dot > 0)
        {
            var firstPart = first[..dot]; // "agent"
            if (stem.EndsWith("." + firstPart, System.StringComparison.OrdinalIgnoreCase))
                return stem[..^(firstPart.Length + 1)];
        }
        return stem;
    }
}
