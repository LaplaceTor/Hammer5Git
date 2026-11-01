using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Datamodel;
using Datamodel.Codecs;

namespace Hammer5Git.Core.Services;

/// <summary>
/// VMAP 语义差异分析结果。
/// </summary>
public readonly record struct VmapSemanticDiffResult(
    bool HasMeaningfulChange,
    bool IsInitialImport,
    string Summary)
{
    public static implicit operator (bool HasMeaningfulChange, bool IsInitialImport, string Summary)(VmapSemanticDiffResult result)
        => (result.HasMeaningfulChange, result.IsInitialImport, result.Summary);
}

/// <summary>
/// 基于 KeyValues2(Datamodel.NET) 的 VMAP 只读语义分析器。
/// 采用语义边界截断 + 专属子树哈希算法，精确检测图元与实体变动并过滤视口/全局元数据噪声。
/// </summary>
public static class VmapSemanticAnalyzer
{
    private static readonly HashSet<string> SemanticClasses = new(StringComparer.Ordinal)
    {
        "CMapEntity",
        "CMapMesh",
        "CMapPrefab",
        "CMapSmartProp",
        "CMapGroup",
        "CMapPath"
    };

    private readonly record struct SemanticNode(Guid Id, string ClassName, string Label, ulong ContentHash);

    /// <summary>
    /// 分析两个 KV2 文本间的语义差异。旧内容为空时视为初始导入。
    /// </summary>
    public static VmapSemanticDiffResult AnalyzeDiff(string? oldKv2, string newKv2)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(oldKv2))
            {
                var initialNodes = ExtractSemanticNodes(newKv2);
                int meshCount = initialNodes.Values.Count(n => n.ClassName == "CMapMesh");
                int entityCount = initialNodes.Values.Count(n => n.ClassName == "CMapEntity");
                return new VmapSemanticDiffResult(true, true, $"初始导入 (包含 {meshCount} 个 Mesh, {entityCount} 个 Entity)");
            }

            var oldMap = ExtractSemanticNodes(oldKv2);
            var newMap = ExtractSemanticNodes(newKv2);

            var added = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var removed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var modified = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var (id, newNode) in newMap)
            {
                if (!oldMap.TryGetValue(id, out var oldNode))
                {
                    Increment(added, newNode.Label);
                }
                else if (oldNode.ContentHash != newNode.ContentHash)
                {
                    Increment(modified, newNode.Label);
                }
            }

            foreach (var (id, oldNode) in oldMap)
            {
                if (!newMap.ContainsKey(id))
                {
                    Increment(removed, oldNode.Label);
                }
            }

            var parts = new List<string>(3);
            if (added.Count > 0)
                parts.Add("新增 " + FormatCounts(added));
            if (modified.Count > 0)
                parts.Add("变更 " + FormatCounts(modified));
            if (removed.Count > 0)
                parts.Add("删除 " + FormatCounts(removed));

            if (parts.Count == 0)
            {
                return new VmapSemanticDiffResult(false, false, string.Empty);
            }

            return new VmapSemanticDiffResult(true, false, string.Join("; ", parts));
        }
        catch (Exception)
        {
            // 降级兜底：解析异常绝不阻塞 Git 提交
            return new VmapSemanticDiffResult(true, false, "地图结构变动");
        }
    }

    private static void Increment(Dictionary<string, int> dict, string key)
    {
        dict.TryGetValue(key, out int count);
        dict[key] = count + 1;
    }

    private static string FormatCounts(Dictionary<string, int> dict)
    {
        var top = dict.OrderByDescending(kv => kv.Value).Take(3)
            .Select(kv => $"{kv.Value} 个 {kv.Key}");
        string res = string.Join(", ", top);
        if (dict.Count > 3)
            res += $" 等({dict.Values.Sum()}项)";
        return res;
    }

    private static Dictionary<Guid, SemanticNode> ExtractSemanticNodes(string kv2Text)
    {
        var result = new Dictionary<Guid, SemanticNode>();
        int byteCount = Encoding.UTF8.GetByteCount(kv2Text);
        byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);

        try
        {
            int actualBytes = Encoding.UTF8.GetBytes(kv2Text.AsSpan(), rented.AsSpan());
            using var ms = new MemoryStream(rented, 0, actualBytes, writable: false);
            using var dm = Datamodel.Datamodel.Load(ms, DeferredMode.Disabled);

            var visited = new HashSet<Guid>();
            var hasher = new XxHash64();

            foreach (var el in dm.AllElements)
            {
                if (!SemanticClasses.Contains(el.ClassName))
                    continue;

                visited.Clear();
                hasher.Reset();
                HashElement(el, hasher, visited);
                ulong hash = hasher.GetCurrentHashAsUInt64();

                string label = ResolveLabel(el);
                result[el.ID] = new SemanticNode(el.ID, el.ClassName, label, hash);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return result;
    }

    private static void HashElement(Element el, XxHash64 hasher, HashSet<Guid> visited)
    {
        if (!visited.Add(el.ID))
            return;

        hasher.Append(MemoryMarshal.AsBytes(el.ClassName.AsSpan()));

        foreach (var key in el.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (key is "id" or "name")
                continue;

            var val = el[key];
            if (val == null)
                continue;

            hasher.Append(MemoryMarshal.AsBytes(key.AsSpan()));
            HashValue(val, hasher, visited);
        }
    }

    private static void HashValue(object val, XxHash64 hasher, HashSet<Guid> visited)
    {
        if (val is Element child)
        {
            // 遇到语义子元素边界：只喂入其 GUID，绝不下钻递归
            if (SemanticClasses.Contains(child.ClassName))
            {
                hasher.Append(child.ID.ToByteArray());
            }
            else
            {
                HashElement(child, hasher, visited);
            }
        }
        else if (val is string s)
        {
            hasher.Append(MemoryMarshal.AsBytes(s.AsSpan()));
        }
        else if (val is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item != null)
                {
                    HashValue(item, hasher, visited);
                }
            }
        }
        else
        {
            string formatted = string.Create(CultureInfo.InvariantCulture, $"{val}");
            hasher.Append(MemoryMarshal.AsBytes(formatted.AsSpan()));
        }
    }

    private static string ResolveLabel(Element el)
    {
        // 1. CMapEntity / CMapPath：解析 entity_properties 元素里的 classname 字符串
        if (el.ClassName == "CMapEntity" || el.ClassName == "CMapPath")
        {
            if (el.TryGetValue("entity_properties", out var ep) && ep is Element epEl &&
                epEl.TryGetValue("classname", out var cls) && cls is string s && !string.IsNullOrWhiteSpace(s))
            {
                return s;
            }

            return el.ClassName == "CMapEntity" ? "Entity" : "Path";
        }

        // 2. CMapPrefab：解析 targetMapPath 属性并提取文件名
        if (el.ClassName == "CMapPrefab")
        {
            if (el.TryGetValue("targetMapPath", out var p) && p is string path && !string.IsNullOrWhiteSpace(path))
            {
                string fileName = Path.GetFileNameWithoutExtension(path);
                return string.IsNullOrWhiteSpace(fileName) ? "Prefab" : $"Prefab({fileName})";
            }

            return "Prefab";
        }

        // 3. 其余语义元素
        return el.ClassName switch
        {
            "CMapMesh" => "Mesh",
            "CMapSmartProp" => "SmartProp",
            "CMapGroup" => "Group",
            _ => el.ClassName
        };
    }
}
