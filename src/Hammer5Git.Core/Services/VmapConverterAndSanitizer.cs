using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Hammer5Git.Core.Models;

namespace Hammer5Git.Core.Services;

public partial class VmapConverterAndSanitizer
{
    private static readonly TimeSpan DefaultConversionTimeout = TimeSpan.FromSeconds(45);

    public string DmxConvertPath { get; set; }

    // Matches the asset_preview_thumbnail binary block inside $prefix_element$
    [GeneratedRegex(@"(""asset_preview_thumbnail""\s+""binary""\s*[\r\n]+\s*"")([\s\S]*?)("")", RegexOptions.Compiled)]
    private static partial Regex ThumbnailRegex();

    // Matches the defaultcamera CStoredCamera block to zero out position and lookat
    [GeneratedRegex(@"(""defaultcamera""\s+""CStoredCamera""\s*\{[\s\S]*?""position""\s+""vector3""\s+"")([^""]+)(""[\s\S]*?""lookat""\s+""vector3""\s+"")([^""]+)("")", RegexOptions.Compiled)]
    private static partial Regex DefaultCameraRegex();

    // Matches root or nested DMX blocks with id/classname/targetMapPath for smart summary
    [GeneratedRegex(@"""([A-Za-z0-9_]+)""\s*\{\s*""id""\s+""elementid""\s+""([0-9a-fA-F\-]+)""([\s\S]*?)(?=\r?\n\t*""[A-Za-z0-9_]+""\s*\{\s*""id""|\z)", RegexOptions.Compiled)]
    private static partial Regex ElementBlockRegex();

    [GeneratedRegex(@"""classname""\s+""string""\s+""([^""]+)""", RegexOptions.Compiled)]
    private static partial Regex ClassnameRegex();

    [GeneratedRegex(@"""targetMapPath""\s+""string""\s+""([^""]+)""", RegexOptions.Compiled)]
    private static partial Regex TargetMapPathRegex();

    [GeneratedRegex(@"^(.*?)(?:_autosave(?:_\d+)?|_bak(?:_\d+)?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex AutoSaveSuffixRegex();

    // 仅匹配 Hammer 自动保存标准的三位尾号 (例如 _001 .. _005)
    [GeneratedRegex(@"^(.*?)_(\d{3})$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ThreeDigitAutoSaveSuffixRegex();

    public VmapConverterAndSanitizer(string dmxConvertPath)
    {
        DmxConvertPath = dmxConvertPath;
    }

    /// <summary>
    /// Converts a binary (or text) .vmap file to sanitized keyvalues2 text format.
    /// Validates exit code and DMX structure so corrupted/truncated files are never committed.
    /// </summary>
    public async Task<VmapConversionResult> ConvertAndSanitizeToKv2Async(string sourceVmapPath, CancellationToken cancellationToken = default)
    {
        string dmxConvertPath = DmxConvertPath;
        if (!File.Exists(dmxConvertPath))
        {
            return new VmapConversionResult
            {
                Success = false,
                ExitCode = -1,
                ErrorMessage = $"找不到 dmxconvert.exe: {dmxConvertPath}"
            };
        }

        if (!File.Exists(sourceVmapPath))
        {
            return new VmapConversionResult
            {
                Success = false,
                ExitCode = -1,
                ErrorMessage = $"源 VMAP 文件不存在: {sourceVmapPath}"
            };
        }

        var fileInfo = new FileInfo(sourceVmapPath);
        if (fileInfo.Length < 64)
        {
            return new VmapConversionResult
            {
                Success = false,
                ExitCode = -2,
                ErrorMessage = $"VMAP 文件体积异常 ({fileInfo.Length} 字节)，疑似截断或空文件。"
            };
        }

        // Verify header starts with "<!-- dmx encoding"
        try
        {
            byte[] headerBytes = new byte[32];
            await using (var fs = new FileStream(sourceVmapPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int read = await fs.ReadAsync(headerBytes.AsMemory(0, headerBytes.Length), cancellationToken);
                string headerStr = Encoding.ASCII.GetString(headerBytes, 0, read);
                if (!headerStr.StartsWith("<!-- dmx encoding", StringComparison.OrdinalIgnoreCase))
                {
                    return new VmapConversionResult
                    {
                        Success = false,
                        ExitCode = -3,
                        ErrorMessage = "VMAP 文件头签名无效，不是合法的 DMX 格式。"
                    };
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new VmapConversionResult
            {
                Success = false,
                ExitCode = -4,
                ErrorMessage = $"读取 VMAP 文件头失败: {ex.Message}"
            };
        }

        string tempOutPath = Path.Combine(Path.GetTempPath(), $"hammer_git_{Guid.NewGuid():N}.vmap");
        try
        {
            var runRes = await RunDmxConvertProcessAsync(
                dmxConvertPath,
                ["-i", sourceVmapPath, "-o", tempOutPath, "-oe", "keyvalues2", "-of", "vmap"],
                cancellationToken);

            if (!runRes.Started)
            {
                return new VmapConversionResult
                {
                    Success = false,
                    ExitCode = -5,
                    ErrorMessage = "无法启动 dmxconvert.exe 进程。"
                };
            }

            if (runRes.ExitCode != 0 || !File.Exists(tempOutPath))
            {
                return new VmapConversionResult
                {
                    Success = false,
                    ExitCode = runRes.ExitCode,
                    ErrorMessage = $"dmxconvert 转换失败 (ExitCode={runRes.ExitCode}): {runRes.StdErr} {runRes.StdOut}".Trim()
                };
            }

            string rawKv2 = await File.ReadAllTextAsync(tempOutPath, new UTF8Encoding(false), cancellationToken);
            if (!ValidateKv2Structure(rawKv2, out string validationError))
            {
                return new VmapConversionResult
                {
                    Success = false,
                    ExitCode = -6,
                    ErrorMessage = $"DMX 完整性校验未通过: {validationError}"
                };
            }

            string sanitized = SanitizeKv2Text(rawKv2);
            return new VmapConversionResult
            {
                Success = true,
                ExitCode = 0,
                SanitizedKv2Content = sanitized
            };
        }
        finally
        {
            if (File.Exists(tempOutPath))
            {
                try { File.Delete(tempOutPath); } catch { }
            }
        }
    }

    /// <summary>
    /// Converts a keyvalues2 text .vmap back to Hammer binary .vmap format.
    /// </summary>
    public async Task<(bool Success, string ErrorMessage)> ConvertKv2ToBinaryAsync(
        string kv2FilePath,
        string outputBinaryVmapPath,
        CancellationToken cancellationToken = default)
    {
        string dmxConvertPath = DmxConvertPath;
        if (!File.Exists(dmxConvertPath))
            return (false, $"找不到 dmxconvert.exe: {dmxConvertPath}");

        if (!File.Exists(kv2FilePath))
            return (false, $"找不到源 KV2 文本文件: {kv2FilePath}");

        string tempBinPath = Path.Combine(Path.GetTempPath(), $"hammer_restore_{Guid.NewGuid():N}.vmap");
        try
        {
            var runRes = await RunDmxConvertProcessAsync(
                dmxConvertPath,
                ["-i", kv2FilePath, "-o", tempBinPath, "-oe", "binary", "-of", "vmap"],
                cancellationToken);

            if (!runRes.Started)
                return (false, "无法启动 dmxconvert.exe 进程。");

            if (runRes.ExitCode != 0 || !File.Exists(tempBinPath))
            {
                return (false, $"转回二进制 VMAP 失败 (ExitCode={runRes.ExitCode}): {runRes.StdErr} {runRes.StdOut}".Trim());
            }

            var outInfo = new FileInfo(tempBinPath);
            if (outInfo.Length < 64)
            {
                return (false, "生成的二进制 VMAP 长度异常。");
            }

            var outDir = Path.GetDirectoryName(outputBinaryVmapPath);
            if (!string.IsNullOrEmpty(outDir))
                Directory.CreateDirectory(outDir);

            File.Copy(tempBinPath, outputBinaryVmapPath, overwrite: true);
            return (true, string.Empty);
        }
        finally
        {
            if (File.Exists(tempBinPath))
            {
                try { File.Delete(tempBinPath); } catch { }
            }
        }
    }

    private static async Task<(bool Started, int ExitCode, string StdOut, string StdErr)> RunDmxConvertProcessAsync(
        string exePath,
        IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        try
        {
            var (exitCode, stdout, stderr) = await ProcessRunner.RunAsync(
                exePath,
                arguments,
                workingDir: null,
                timeout: DefaultConversionTimeout,
                env: null,
                ct: ct);

            return (true, exitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, -1, string.Empty, ex.Message);
        }
    }

    /// <summary>
    /// Validates that the converted keyvalues2 text is complete and balanced.
    /// </summary>
    public static bool ValidateKv2Structure(string kv2Text, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(kv2Text))
        {
            error = "转换后的 KV2 内容为空。";
            return false;
        }

        if (!kv2Text.StartsWith("<!-- dmx encoding keyvalues2", StringComparison.OrdinalIgnoreCase))
        {
            error = "缺少 keyvalues2 头部声明。";
            return false;
        }

        if (!kv2Text.Contains("\"CMapRootElement\"", StringComparison.Ordinal))
        {
            error = "缺少 CMapRootElement 根节点，文件可能损坏或截断。";
            return false;
        }

        if (!kv2Text.Contains("\"world\" \"CMapWorld\"", StringComparison.Ordinal))
        {
            error = "缺少 CMapWorld 世界节点，文件可能损坏或截断。";
            return false;
        }

        // Check curly brace balance outside quotes
        int braceDepth = 0;
        bool inQuotes = false;
        for (int i = 0; i < kv2Text.Length; i++)
        {
            char c = kv2Text[i];
            if (c == '"' && (i == 0 || kv2Text[i - 1] != '\\'))
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes)
            {
                if (c == '{') braceDepth++;
                else if (c == '}') braceDepth--;
            }
        }

        if (inQuotes || braceDepth != 0)
        {
            error = $"括号或引号未闭合 (BraceDepth={braceDepth}, InQuotes={inQuotes})，文件在保存时被截断。";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Strips the asset_preview_thumbnail HEX payload and resets defaultcamera position & lookat to "0 0 0".
    /// </summary>
    public static string SanitizeKv2Text(string rawKv2)
    {
        // 1. Strip JPG hex thumbnail payload
        string step1 = ThumbnailRegex().Replace(rawKv2, m => $"{m.Groups[1].Value}{m.Groups[3].Value}");

        // 2. Zero out defaultcamera position and lookat
        string step2 = DefaultCameraRegex().Replace(step1, m =>
            $"{m.Groups[1].Value}0 0 0{m.Groups[3].Value}0 0 0{m.Groups[5].Value}");

        return step2;
    }

    /// <summary>
    /// Maps an autosave VMAP path (e.g. maps/_autosave/test_001.vmap or maps/test_autosave_01.vmap or _toolsautosave maps/test_001.vmap)
    /// back to its canonical relative path inside the addon (e.g. content/maps/test.vmap).
    /// Also indicates whether the file was an autosave.
    /// </summary>
    public static (string CanonicalRelativePath, bool IsAutoSave) NormalizeVmapRelativePath(
        string relativeToSourceRoot,
        bool isFromAutoSaveRoot = false)
    {
        string normalized = relativeToSourceRoot.Replace('\\', '/').TrimStart('/');
        string dir = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? string.Empty;
        string fileNameNoExt = Path.GetFileNameWithoutExtension(normalized);
        string ext = Path.GetExtension(normalized);

        if (!ext.Equals(".vmap", StringComparison.OrdinalIgnoreCase))
        {
            return ($"content/{normalized}", false);
        }

        bool isAutoSave = isFromAutoSaveRoot;

        // 兼容旧式 maps/_autosave/ 子目录结构
        var dirParts = dir.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (dirParts.Any(p => p.Equals("_autosave", StringComparison.OrdinalIgnoreCase) ||
                              p.Equals("autosave", StringComparison.OrdinalIgnoreCase)))
        {
            isAutoSave = true;
            dirParts.RemoveAll(p => p.Equals("_autosave", StringComparison.OrdinalIgnoreCase) ||
                                    p.Equals("autosave", StringComparison.OrdinalIgnoreCase));
            dir = string.Join("/", dirParts);
        }

        // 分支 A: 文件名显式包含 _autosave(_N) 或 _bak(_N)
        var explicitMatch = AutoSaveSuffixRegex().Match(fileNameNoExt);
        if (explicitMatch.Success && !string.IsNullOrWhiteSpace(explicitMatch.Groups[1].Value))
        {
            isAutoSave = true;
            fileNameNoExt = explicitMatch.Groups[1].Value;
        }
        // 分支 B: 仅当来自 _toolsautosave 根或 _autosave 子目录时，才剥离末尾三位数字 _\d{3}
        else if (isAutoSave)
        {
            var numMatch = ThreeDigitAutoSaveSuffixRegex().Match(fileNameNoExt);
            if (numMatch.Success && !string.IsNullOrWhiteSpace(numMatch.Groups[1].Value))
            {
                fileNameNoExt = numMatch.Groups[1].Value;
            }
        }

        string finalRel = string.IsNullOrEmpty(dir)
            ? $"content/{fileNameNoExt}.vmap"
            : $"content/{dir}/{fileNameNoExt}.vmap";

        return (finalRel, isAutoSave);
    }

    public static int ExtractAutoSaveSlotNumber(string filePath)
    {
        string fileNameNoExt = Path.GetFileNameWithoutExtension(filePath);
        var m = ThreeDigitAutoSaveSuffixRegex().Match(fileNameNoExt);
        if (m.Success && int.TryParse(m.Groups[2].Value, out int slot))
            return slot;
        return -1;
    }

    /// <summary>
    /// Analyzes the semantic difference between two KV2 texts of a VMAP.
    /// Returns whether there is any meaningful map change (excluding noisy camera/selection/visibility/prefix metadata).
    /// </summary>
    public static (bool HasMeaningfulChange, bool IsInitialImport, string Summary) AnalyzeVmapSemanticDiff(string? oldKv2, string newKv2)
    {
        if (string.IsNullOrWhiteSpace(oldKv2))
        {
            var initialElements = ExtractElementSnapshots(newKv2);
            int meshCount = initialElements.Values.Count(e => e.Type == "CMapMesh");
            int entityCount = initialElements.Values.Count(e => e.Type == "CMapEntity");
            return (true, true, $"初始导入 (包含 {meshCount} 个 Mesh, {entityCount} 个 Entity)");
        }

        var oldMap = ExtractElementSnapshots(oldKv2);
        var newMap = ExtractElementSnapshots(newKv2);

        var added = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var removed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var modified = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        HashSet<string> metadataTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "$prefix_element$", "DmePlugList", "EditGameClassProps", "DmElement",
            "CStoredCamera", "CStoredCameras", "CVisibilityMgr", "CMapSelectionSet",
            "CObjectSelectionSetDataElement"
        };

        foreach (var (id, newEl) in newMap)
        {
            if (metadataTypes.Contains(newEl.Type))
                continue;

            if (!oldMap.TryGetValue(id, out var oldEl))
            {
                Increment(added, newEl.Label);
            }
            else if (oldEl.ContentHash != newEl.ContentHash)
            {
                Increment(modified, newEl.Label);
            }
        }

        foreach (var (id, oldEl) in oldMap)
        {
            if (metadataTypes.Contains(oldEl.Type))
                continue;

            if (!newMap.ContainsKey(id))
            {
                Increment(removed, oldEl.Label);
            }
        }

        var parts = new List<string>();
        if (added.Count > 0)
            parts.Add("新增 " + FormatCounts(added));
        if (modified.Count > 0)
            parts.Add("变更 " + FormatCounts(modified));
        if (removed.Count > 0)
            parts.Add("删除 " + FormatCounts(removed));

        if (parts.Count == 0)
        {
            return (false, false, string.Empty);
        }

        return (true, false, string.Join("; ", parts));
    }

    /// <summary>
    /// Generates an intelligent semantic summary by comparing old and new KV2 text of a VMAP.
    /// Identifies added/removed/modified entities, meshes, prefabs, etc.
    /// </summary>
    public static string GenerateVmapSemanticDiffSummary(string? oldKv2, string newKv2)
    {
        var (hasMeaningfulChange, _, summary) = AnalyzeVmapSemanticDiff(oldKv2, newKv2);
        return hasMeaningfulChange ? summary : "更新地图属性/全局配置";
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

    private record ElementSnapshot(string Id, string Type, string Label, int ContentHash);

    private static Dictionary<string, ElementSnapshot> ExtractElementSnapshots(string kv2)
    {
        var result = new Dictionary<string, ElementSnapshot>(StringComparer.OrdinalIgnoreCase);
        var matches = ElementBlockRegex().Matches(kv2);
        foreach (Match m in matches)
        {
            string type = m.Groups[1].Value;
            string id = m.Groups[2].Value;
            string body = m.Groups[3].Value;

            string label = type;
            if (type == "CMapEntity" || type == "EditGameClassProps")
            {
                var clsMatch = ClassnameRegex().Match(body);
                if (clsMatch.Success)
                    label = clsMatch.Groups[1].Value;
            }
            else if (type == "CMapPrefab")
            {
                var prefabMatch = TargetMapPathRegex().Match(body);
                if (prefabMatch.Success)
                    label = $"Prefab({Path.GetFileNameWithoutExtension(prefabMatch.Groups[1].Value)})";
            }

            int hash = body.GetHashCode(StringComparison.Ordinal);
            result[id] = new ElementSnapshot(id, type, label, hash);
        }
        return result;
    }

    public readonly record struct VmapSourceCandidate(
        string FullSourcePath,
        string CanonicalRelativePath,
        bool IsAutoSave,
        DateTime LastWriteTimeUtc,
        int SlotNumber);

    /// <summary>
    /// 纯函数仲裁：在映射到同一 CanonicalRelativePath 的候选集合中选出唯一最新胜出者。
    /// </summary>
    public static VmapSourceCandidate SelectNewestVmapCandidate(IEnumerable<VmapSourceCandidate> candidates)
    {
        if (candidates == null)
            throw new ArgumentNullException(nameof(candidates));

        return candidates
            .OrderByDescending(c => c.LastWriteTimeUtc)
            .ThenBy(c => c.IsAutoSave ? 1 : 0) // false(正式文件=0) 排在 true(autosave=1) 前面
            .ThenByDescending(c => c.SlotNumber)
            .ThenBy(c => c.FullSourcePath, StringComparer.OrdinalIgnoreCase)
            .First();
    }
}
