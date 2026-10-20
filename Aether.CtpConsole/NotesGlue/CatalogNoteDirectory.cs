using System.Collections.Generic;

namespace Acme.Notes.Domain.Catalog;

/// <summary>
/// labs 目录名 ↔ <see cref="NoteNodeType"/> 的权威映射（「目录名即节点类型」）。
/// 单一来源：Infrastructure（<c>DirectoryNoteCatalogSource</c>）与 apps（labs 目录页树/图标/高亮）都查这里，
/// 不散落 switch。目录结构 = <c>&lt;labsRoot&gt;/&lt;DirectoryName&gt;/*.&lt;Extension&gt;</c>。
/// </summary>
public static class CatalogNoteDirectory
{
    /// <summary>
    /// 每类型的目录名 + 文件扩展名（含前导点）。<see cref="SecondaryExtension"/>：同目录的第二语言变体
    /// （当前仅 CodeRule 的 F# 变体 .coderule.fs——语言是文件属性，不另开 NodeType）。
    /// <see cref="Extension"/> 是主扩展名（新建文件默认、复合扩展名剥段）；glob 消费用 <see cref="AllExtensions"/>。
    /// </summary>
    public sealed record Layout(string DirectoryName, string Extension, string? SecondaryExtension = null)
    {
        /// <summary>该类型的全部文件扩展名（主 + 可选次），目录枚举/glob 消费用。</summary>
        public IReadOnlyList<string> AllExtensions =>
            SecondaryExtension is null
                ? new[] { Extension }
                : new[] { Extension, SecondaryExtension };
    }

    private static readonly IReadOnlyDictionary<NoteNodeType, Layout> Layouts =
        new Dictionary<NoteNodeType, Layout>
        {
            [NoteNodeType.Note] = new("note", ".md"),
            [NoteNodeType.Agent] = new("agents", ".agent.md"),
            [NoteNodeType.Workflow] = new("workflows", ".workflow.json"),
            [NoteNodeType.Rule] = new("rules", ".rule.yaml"),
            // C#（主）+ F#（language: fsharp 变体）同目录共存——同一 CodeRule 家族，语言是文件属性。
            [NoteNodeType.CodeRule] = new("coderules", ".coderule.cs", ".coderule.fs"),
            [NoteNodeType.Knowledge] = new("knowledge", ".md"),
            [NoteNodeType.CodeStrategy] = new("codestrategies", ".cs"),
            [NoteNodeType.Strategy] = new("strategies", ".strategy.yaml"),
            [NoteNodeType.LeanAlgorithm] = new("leanalgorithms", ".lean.cs"),
            // 技能是文件夹节点：skills/<name>/SKILL.md（+ 可选兄弟脚本 .py/.sh/.js）。
            // 扩展名 .md 仅 catalog source glob 用；labs 树对 Skill 子树列全部文件（见 KnowledgeObjectsViewModel.BuildNode）。
            [NoteNodeType.Skill] = new("skills", ".md"),
            // MCP server 配置：扁平 mcp-servers/<name>.json（mcpServers 映射格式）。
            [NoteNodeType.McpServer] = new("mcp-servers", ".json"),
        };

    /// <summary>取某类型的目录名与扩展名（未知类型回退 <see cref="NoteNodeType.Note"/>）。</summary>
    public static Layout For(NoteNodeType type) =>
        Layouts.TryGetValue(type, out var l) ? l : Layouts[NoteNodeType.Note];

    /// <summary>按目录名反查类型；不匹配返回 null（未知目录 → 忽略）。</summary>
    public static NoteNodeType? TypeForDirectory(string directoryName)
    {
        foreach (var (type, layout) in Layouts)
        {
            if (string.Equals(layout.DirectoryName, directoryName, System.StringComparison.OrdinalIgnoreCase))
                return type;
        }
        return null;
    }

    /// <summary>参与 labs 目录的全部类型（按声明序）。Knowledge 已在其中。</summary>
    public static IEnumerable<NoteNodeType> AllTypes => Layouts.Keys;
}
