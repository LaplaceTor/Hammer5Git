using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
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

    // 匹配根节点 $prefix_element$ 中的 elementid，用于替换为固定 GUID 以消除 dmxconvert 输出的非确定性
    [GeneratedRegex(@"(""\$prefix_element\$""\s*\{\s*""id""\s+""elementid""\s+"")([^""]+)("")", RegexOptions.Compiled)]
    private static partial Regex PrefixElementIdRegex();

    [GeneratedRegex(@"^(.*?)(?:_autosave(?:_\d+)?|_bak(?:_\d+)?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex AutoSaveSuffixRegex();

    // 仅匹配 Hammer 自动保存标准的三位尾号 (例如 _001 .. _005)
    [GeneratedRegex(@"^(.*?)_(\d{3})$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ThreeDigitAutoSaveSuffixRegex();

    // 匹配 Hammer 实际使用的无下划线三位数字槽位 (例如 test001 .. test005)
    [GeneratedRegex(@"^(.*?)(\d{3})$", RegexOptions.Compiled)]
    private static partial Regex ThreeDigitNoUnderscoreAutoSaveSuffixRegex();

    /// <summary>
    /// 判断给定的文件扩展名是否为 VMAP 相关文件（.vmap 或 .vmap_autosave）。
    /// </summary>
    public static bool IsTrackableVmapExtension(string ext)
        => ext.Equals(".vmap", StringComparison.OrdinalIgnoreCase) ||
           ext.Equals(".vmap_autosave", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 判断给定文件路径是否为 VMAP 相关文件（.vmap 或 .vmap_autosave）。
    /// </summary>
    public static bool IsVmapRelatedFile(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        string ext = Path.GetExtension(path);
        return IsTrackableVmapExtension(ext);
    }

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
            if (c == '"')
            {
                // 计算引号前连续的反斜杠数量：奇数个表示引号被转义，偶数个（含0）表示反斜杠被转义而引号有效
                int backslashCount = 0;
                int j = i - 1;
                while (j >= 0 && kv2Text[j] == '\\')
                {
                    backslashCount++;
                    j--;
                }

                if ((backslashCount & 1) == 0)
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (!inQuotes)
            {
                if (c == '{') braceDepth++;
                else if (c == '}')
                {
                    braceDepth--;
                    if (braceDepth < 0)
                    {
                        error = "出现多余的闭合花括号 '}'，KV2 结构不匹配。";
                        return false;
                    }
                }
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
    /// Strips the asset_preview_thumbnail HEX payload, resets defaultcamera position & lookat to "0 0 0",
    /// standardizes the root $prefix_element$ elementid to an all-zero GUID, and normalizes all
    /// inline sub-element elementids to deterministic GUIDs based on their parent anchor to eliminate random GUID drift.
    /// </summary>
    public static string SanitizeKv2Text(string rawKv2)
    {
        // 1. Strip JPG hex thumbnail payload
        string step1 = ThumbnailRegex().Replace(rawKv2, m => $"{m.Groups[1].Value}{m.Groups[3].Value}");

        // 2. Zero out defaultcamera position and lookat
        string step2 = DefaultCameraRegex().Replace(step1, m =>
            $"{m.Groups[1].Value}0 0 0{m.Groups[3].Value}0 0 0{m.Groups[5].Value}");

        // 3. 将根节点 $prefix_element$ 的 elementid 归一化为全零固定 GUID，以消除 dmxconvert 每次调用重新生成随机 GUID 导致的非确定性。
        // 【关键安全边界】：严禁将此正则匹配范围扩大到全文的其他 elementid！
        // VmapSemanticAnalyzer 将每个实体的 GUID 用作主键，供 AnalyzeVmapSemanticDiff 准确识别实体的
        // 新增 (Added)、修改 (Modified) 与删除 (Deleted)。若将所有主图元 elementid 归一化，将破坏语义差异分析；
        // 而 $prefix_element$ 不属于语义类白名单，对其 GUID 进行固定既保障一致性，又完全不影响语义判定。
        string step3 = PrefixElementIdRegex().Replace(step2, m =>
            $"{m.Groups[1].Value}00000000-0000-0000-0000-000000000000{m.Groups[3].Value}");

        // 4. 将所有内联附属子元素（DmElement、Dme*、CDme*、EditGameClassProps 等）的随机 elementid 归一化为基于父级主节点锚点的确定性 GUID。
        // 主场景节点（CMap* / CVisibilityMgr / CStoredCamera*）原 elementid 保留充当唯一锚点；
        // 其下所有内联附属子元素（无持久身份、随每次保存随机生成 GUID 者）替换为基于“父级锚点 GUID + 相对路径/下标”经 XxHash128 派生的确定性 GUID。
        // 彻底根除未变动图元其附属子元素 GUID 随机漂移污染 Git diff 的缺陷。
        string step4 = NormalizeSubElementIds(step3);

        return step4;
    }

    private sealed class ScopeFrame
    {
        public bool IsArray;
        public string AnchorGuid = string.Empty;
        public string RelativePath = string.Empty;
        public string ArrayName = string.Empty;
        public int CurrentArrayIndex;
    }

    private static bool IsAnchorElementClass(ReadOnlySpan<char> className)
    {
        if (className.StartsWith("CMap", StringComparison.Ordinal))
            return true;
        return className.SequenceEqual("CVisibilityMgr") ||
               className.SequenceEqual("CStoredCamera") ||
               className.SequenceEqual("CStoredCameras");
    }

    private static void FormatDeterministicGuid(string anchorGuid, string relativePath, Span<char> destination)
    {
        int maxBytes = (anchorGuid.Length + 1 + relativePath.Length) * 3;
        byte[]? rented = null;
        Span<byte> utf8Bytes = maxBytes <= 256 ? stackalloc byte[256] : (rented = ArrayPool<byte>.Shared.Rent(maxBytes));

        try
        {
            int written1 = Encoding.UTF8.GetBytes(anchorGuid, utf8Bytes);
            utf8Bytes[written1] = (byte)'/';
            int written2 = Encoding.UTF8.GetBytes(relativePath, utf8Bytes[(written1 + 1)..]);
            int totalBytes = written1 + 1 + written2;

            Span<byte> hash = stackalloc byte[16];
            XxHash128.Hash(utf8Bytes[..totalBytes], hash);

            // RFC 4122 variant (bits 6..7 = 10) and version 5 (bits 12..15 = 0101)
            hash[7] = (byte)((hash[7] & 0x0F) | 0x50);
            hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

            var guid = new Guid(hash);
            guid.TryFormat(destination, out _);
        }
        finally
        {
            if (rented != null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// 扫描 KV2 文本并归一化所有内联附属子元素的 elementid。
    /// 主节点（CMap* / CVisibilityMgr / CStoredCamera*）作为根锚点保持原 GUID 不变；
    /// 附属子元素（DmElement、Dme*、CDme*、EditGameClassProps 等）的 GUID 替换为由
    /// "(父锚点 GUID + 相对路径/数组下标)" 经 XxHash128 计算得到的确定性唯一 GUID。
    /// 替换过程由于 GUID 长度恒为 36 字符，直接原地覆写字符缓冲区，保持格式、缩进与长度严格不变。
    /// </summary>
    public static string NormalizeSubElementIds(string kv2Text)
    {
        if (string.IsNullOrEmpty(kv2Text))
            return kv2Text;

        char[] chars = kv2Text.ToCharArray();
        var stack = new List<ScopeFrame>(32);

        ReadOnlySpan<char> textSpan = kv2Text.AsSpan();
        int lineStart = 0;

        string lastHeader1 = string.Empty;
        string lastHeader2 = string.Empty;
        bool hasPendingHeader = false;

        while (lineStart < textSpan.Length)
        {
            int lineEnd = textSpan[lineStart..].IndexOfAny('\r', '\n');
            int lineLength = lineEnd == -1 ? textSpan.Length - lineStart : lineEnd;
            ReadOnlySpan<char> line = textSpan.Slice(lineStart, lineLength);
            ReadOnlySpan<char> trimmed = line.Trim();

            if (trimmed.SequenceEqual("{"))
            {
                string className = string.Empty;
                string propName = string.Empty;

                if (hasPendingHeader)
                {
                    if (!string.IsNullOrEmpty(lastHeader2))
                    {
                        propName = lastHeader1;
                        className = lastHeader2;
                    }
                    else
                    {
                        className = lastHeader1;
                    }
                }

                hasPendingHeader = false;
                bool isAnchor = IsAnchorElementClass(className.AsSpan());

                int nextSearchPos = lineStart + lineLength;
                if (nextSearchPos < textSpan.Length && textSpan[nextSearchPos] == '\r') nextSearchPos++;
                if (nextSearchPos < textSpan.Length && textSpan[nextSearchPos] == '\n') nextSearchPos++;

                int idGuidIndexInSource = -1;
                string currentId = string.Empty;

                int searchLimit = Math.Min(textSpan.Length, nextSearchPos + 300);
                ReadOnlySpan<char> window = textSpan[nextSearchPos..searchLimit];
                int idMarker = window.IndexOf("\"id\" \"elementid\" \"", StringComparison.Ordinal);
                if (idMarker == -1)
                {
                    idMarker = window.IndexOf("\"id\"\t\"elementid\"\t\"", StringComparison.Ordinal);
                }

                if (idMarker != -1)
                {
                    int guidStart = nextSearchPos + idMarker + 18;
                    if (guidStart + 36 <= textSpan.Length && textSpan[guidStart + 36] == '"')
                    {
                        idGuidIndexInSource = guidStart;
                        currentId = new string(chars, guidStart, 36);
                    }
                }

                if (idGuidIndexInSource != -1)
                {
                    if (className == "$prefix_element$" || isAnchor)
                    {
                        stack.Add(new ScopeFrame { IsArray = false, AnchorGuid = currentId, RelativePath = string.Empty });
                    }
                    else
                    {
                        var parent = stack.Count > 0 ? stack[^1] : null;
                        string anchorGuid = parent?.AnchorGuid ?? "00000000-0000-0000-0000-000000000000";

                        string relPath;
                        if (parent != null && parent.IsArray)
                        {
                            string seg = $"{parent.ArrayName}[{parent.CurrentArrayIndex}]";
                            relPath = string.IsNullOrEmpty(parent.RelativePath) ? seg : $"{parent.RelativePath}/{seg}";
                            parent.CurrentArrayIndex++;
                        }
                        else
                        {
                            string seg = !string.IsNullOrEmpty(propName) ? propName : className;
                            string parentRel = parent?.RelativePath ?? string.Empty;
                            relPath = string.IsNullOrEmpty(parentRel) ? seg : $"{parentRel}/{seg}";
                        }

                        FormatDeterministicGuid(anchorGuid, relPath, chars.AsSpan(idGuidIndexInSource, 36));
                        stack.Add(new ScopeFrame { IsArray = false, AnchorGuid = anchorGuid, RelativePath = relPath });
                    }
                }
                else
                {
                    stack.Add(new ScopeFrame { IsArray = false });
                }
            }
            else if (trimmed.SequenceEqual("["))
            {
                string arrayName = hasPendingHeader ? lastHeader1 : string.Empty;
                hasPendingHeader = false;

                var parent = stack.Count > 0 ? stack[^1] : null;
                string parentRel = parent?.RelativePath ?? string.Empty;
                string anchor = parent?.AnchorGuid ?? string.Empty;

                stack.Add(new ScopeFrame
                {
                    IsArray = true,
                    AnchorGuid = anchor,
                    RelativePath = parentRel,
                    ArrayName = arrayName,
                    CurrentArrayIndex = 0
                });
            }
            else if (trimmed.SequenceEqual("}") || trimmed.SequenceEqual("},"))
            {
                hasPendingHeader = false;
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
            }
            else if (trimmed.SequenceEqual("]") || trimmed.SequenceEqual("],"))
            {
                hasPendingHeader = false;
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
            }
            else
            {
                if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
                {
                    int secondQuote = trimmed[1..].IndexOf('"');
                    if (secondQuote != -1)
                    {
                        ReadOnlySpan<char> tok1 = trimmed.Slice(1, secondQuote);
                        ReadOnlySpan<char> rest = trimmed[(1 + secondQuote + 1)..].Trim();
                        if (rest.Length >= 2 && rest[0] == '"' && rest[^1] == '"')
                        {
                            int fourthQuote = rest[1..].IndexOf('"');
                            if (fourthQuote == rest.Length - 2)
                            {
                                ReadOnlySpan<char> tok2 = rest.Slice(1, fourthQuote);
                                lastHeader1 = tok1.ToString();
                                lastHeader2 = tok2.ToString();
                                hasPendingHeader = true;
                            }
                            else
                            {
                                hasPendingHeader = false;
                            }
                        }
                        else if (rest.Length == 0)
                        {
                            lastHeader1 = tok1.ToString();
                            lastHeader2 = string.Empty;
                            hasPendingHeader = true;
                        }
                        else
                        {
                            hasPendingHeader = false;
                        }
                    }
                    else
                    {
                        hasPendingHeader = false;
                    }
                }
                else
                {
                    hasPendingHeader = false;
                }
            }

            lineStart += lineLength;
            if (lineStart < textSpan.Length && textSpan[lineStart] == '\r') lineStart++;
            if (lineStart < textSpan.Length && textSpan[lineStart] == '\n') lineStart++;
        }

        return new string(chars);
    }

    /// <summary>
    /// Maps an autosave VMAP path (e.g. maps/_autosave/test_001.vmap or maps/test_autosave_01.vmap or _toolsautosave maps/test001.vmap_autosave)
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

        // 仅处理 .vmap 与 .vmap_autosave，其余扩展名保持原样返回
        if (!IsTrackableVmapExtension(ext))
        {
            return ($"content/{normalized}", false);
        }

        // 扩展名为 .vmap_autosave 时，无论是否在 AutoSave 根目录下，均直接判定为自动保存草稿
        bool isAutoSave = isFromAutoSaveRoot || ext.Equals(".vmap_autosave", StringComparison.OrdinalIgnoreCase);

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
        // 分支 B: 仅当已确定为自动保存草稿时，才剥离末尾三位数字
        else if (isAutoSave)
        {
            // 优先匹配带下划线的形式 _\d{3} (如 test_001.vmap)
            var numMatch = ThreeDigitAutoSaveSuffixRegex().Match(fileNameNoExt);
            if (numMatch.Success && !string.IsNullOrWhiteSpace(numMatch.Groups[1].Value))
            {
                fileNameNoExt = numMatch.Groups[1].Value;
            }
            else
            {
                // 再匹配 Hammer 实际生成的无下划线三位数字槽位形式 \d{3} (如 test001.vmap_autosave)
                // 仅当剥离后仍至少保留 1 个字符时才剥离，防止形如 "001" 的地图名被剥离为空
                var noUnderscoreMatch = ThreeDigitNoUnderscoreAutoSaveSuffixRegex().Match(fileNameNoExt);
                if (noUnderscoreMatch.Success && !string.IsNullOrWhiteSpace(noUnderscoreMatch.Groups[1].Value))
                {
                    fileNameNoExt = noUnderscoreMatch.Groups[1].Value;
                }
            }
        }

        string finalRel = string.IsNullOrEmpty(dir)
            ? $"content/{fileNameNoExt}.vmap"
            : $"content/{dir}/{fileNameNoExt}.vmap";

        return (finalRel, isAutoSave);
    }

    /// <summary>
    /// 从文件名中提取自动保存槽位编号（支持 test_001 与 test001 两种三位数字槽位格式）。
    /// </summary>
    public static int ExtractAutoSaveSlotNumber(string filePath)
    {
        string fileNameNoExt = Path.GetFileNameWithoutExtension(filePath);
        var m = ThreeDigitAutoSaveSuffixRegex().Match(fileNameNoExt);
        if (m.Success && int.TryParse(m.Groups[2].Value, out int slot))
            return slot;

        var mNoUnder = ThreeDigitNoUnderscoreAutoSaveSuffixRegex().Match(fileNameNoExt);
        if (mNoUnder.Success && !string.IsNullOrWhiteSpace(mNoUnder.Groups[1].Value) &&
            int.TryParse(mNoUnder.Groups[2].Value, out int slotNoUnder))
        {
            return slotNoUnder;
        }

        return -1;
    }

    /// <summary>
    /// Analyzes the semantic difference between two KV2 texts of a VMAP.
    /// Returns whether there is any meaningful map change (excluding noisy camera/selection/visibility/prefix metadata).
    /// </summary>
    public static (bool HasMeaningfulChange, bool IsInitialImport, string Summary) AnalyzeVmapSemanticDiff(string? oldKv2, string newKv2)
        => VmapSemanticAnalyzer.AnalyzeDiff(oldKv2, newKv2);

    /// <summary>
    /// Generates an intelligent semantic summary by comparing old and new KV2 text of a VMAP.
    /// Identifies added/removed/modified entities, meshes, prefabs, etc.
    /// </summary>
    public static string GenerateVmapSemanticDiffSummary(string? oldKv2, string newKv2)
    {
        var (hasMeaningfulChange, _, summary) = AnalyzeVmapSemanticDiff(oldKv2, newKv2);
        return hasMeaningfulChange ? summary : "更新地图属性/全局配置";
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
