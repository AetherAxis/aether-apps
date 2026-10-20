using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.Notes.Domain.Catalog;

/// <summary>
/// 笔记单元来源端口：labs 文件（磁盘，<c>DirectoryNoteCatalogSource</c>）的统一出口。
/// 消费方（策略源/代码规则源/Lean 算法源）依赖此端口而非具体实现——DI 切注册即换来源，
/// 不触碰批量消费逻辑。SQLite note 版来源已删，目录版为唯一实现。
/// </summary>
public interface INoteCatalogSource
{
    /// <summary>数据源根目录绝对路径（目录版=labs 根；SQLite 源无文件根→null）。写入端据此落盘。</summary>
    string? Root { get; }

    /// <summary>枚举某类型的全部单元；<paramref name="nodeType"/> 为 null 时返回全部类型。</summary>
    Task<IReadOnlyList<CatalogNote>> EnumerateAsync(NoteNodeType? nodeType = null, CancellationToken ct = default);

    /// <summary>按 <see cref="CatalogNote.ExternalId"/> 取单个单元；不存在返回 null。目录版按相对路径。</summary>
    Task<CatalogNote?> FindByExternalIdAsync(string externalId, CancellationToken ct = default);

    /// <summary>源变更时触发（目录源由宿主 <c>LabsDirectoryWatcher</c> 驱动）。
    /// 参数为变更项相对源根的路径（目录源=labs 相对路径；可能为 null——旧信号源或路径不可得）。
    /// 消费方可据此判「是否涉及自己关心的目录」而跳过无关变更（如改 knowledge/ 不必重载聊天目标表）。</summary>
    event Action<string?>? Changed;
}
