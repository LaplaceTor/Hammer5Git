using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hammer5Git.Core.Services;

internal static class ProcessRunner
{
    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDir = null,
        TimeSpan? timeout = null,
        IDictionary<string, string>? env = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDir ?? string.Empty,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        if (env != null)
        {
            foreach (var kv in env)
            {
                psi.Environment[kv.Key] = kv.Value;
            }
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动进程 '{fileName}'。");

        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(effectiveTimeout);

        try
        {
            var stdOutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stdErrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
            await proc.WaitForExitAsync(timeoutCts.Token);

            return (proc.ExitCode, await stdOutTask, await stdErrTask);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return (-99, string.Empty, $"进程执行超时 (>{effectiveTimeout.TotalSeconds:0}s): {fileName} {string.Join(" ", arguments)}");
        }
        catch
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }

    public static async Task<(bool Success, string StdErr)> RunToFileAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string outputFilePath,
        string? workingDir = null,
        TimeSpan? timeout = null,
        IDictionary<string, string>? env = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDir ?? string.Empty,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        if (env != null)
        {
            foreach (var kv in env)
            {
                psi.Environment[kv.Key] = kv.Value;
            }
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动进程 '{fileName}'。");

        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(effectiveTimeout);

        try
        {
            var stdErrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);

            string? outputDir = Path.GetDirectoryName(outputFilePath);
            if (!string.IsNullOrEmpty(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            await using (var fs = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await proc.StandardOutput.BaseStream.CopyToAsync(fs, timeoutCts.Token);
            }

            string err = await stdErrTask;
            await proc.WaitForExitAsync(timeoutCts.Token);
            return (proc.ExitCode == 0, err);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return (false, $"进程执行超时 (>{effectiveTimeout.TotalSeconds:0}s): {fileName} {string.Join(" ", arguments)}");
        }
        catch
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }
}
