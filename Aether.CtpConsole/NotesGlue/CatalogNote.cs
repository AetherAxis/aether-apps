namespace Acme.Notes.Domain.Catalog;

/// <summary>
/// 中性「笔记单元」——去 Guid 化、去载体化。labs 目录文件（磁盘）来源
/// 统一压成此 record，消费方（ICustomAgentCatalog / 策略源 / 代码规则源等）只依赖它而不依赖
/// 文件 IO，从而消费方与存储解耦。
/// </summary>
/// <param name="ExternalId">中性引用：SQLite 源 = 节点 <see cref="Guid"/> 字符串；目录源 = labs 相对路径。</param>
/// <param name="Name">标题（note 版的 Title；文件版 = 文件名 stem）。</param>
/// <param name="Content">原始正文（front-matter + body，或纯源码），原样透传，不在此解析。</param>
/// <param name="NodeType">节点类型（复用 <see cref="NoteNodeType"/> 枚举作统一类型 key）。</param>
public sealed record CatalogNote(
    string ExternalId,
    string Name,
    string Content,
    NoteNodeType NodeType);
