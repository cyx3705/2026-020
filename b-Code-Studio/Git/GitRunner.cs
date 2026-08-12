using System.Diagnostics;
using System.Text;

namespace HistoryJanus.Git;

public sealed record GitResult(int ExitCode, string Output)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// 统一 git 进程封装：固定 -C 工作目录、UTF-8 输出、外部取消与异常兜底。
/// 全项目禁止在此之外散落 Process.Start("git")。
/// </summary>
public static class GitRunner
{
    public static async Task<GitResult> RunAsync(
        string gitDir,
        IReadOnlyList<string> arguments,
        CancellationToken cancellation = default)
        => await RunCoreAsync(gitDir, arguments, null, cancellation).ConfigureAwait(false);

    public static async Task<GitResult> RunWithInputAsync(
        string gitDir,
        IReadOnlyList<string> arguments,
        string standardInput,
        CancellationToken cancellation = default)
        => await RunCoreAsync(gitDir, arguments, standardInput, cancellation)
            .ConfigureAwait(false);

    private static async Task<GitResult> RunCoreAsync(
        string gitDir,
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellation)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = standardInput != null,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(gitDir);
            foreach (var argument in arguments)
                psi.ArgumentList.Add(argument);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 git 进程");
            // Cancellation is enforced by WaitForExitAsync below, followed by an entire-tree kill.
            // Keep draining both pipes until the child exits so a full pipe cannot deadlock git.
            var outputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            try
            {
                if (standardInput != null)
                {
                    await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellation)
                        .ConfigureAwait(false);
                    process.StandardInput.Close();
                }
                await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 进程可能已自行退出
                }

                try
                {
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // 仅用于回收已取消的子进程
                }

                return new GitResult(-1,
                    $"git 命令已取消: git -C {FormatArgument(gitDir)} {FormatArguments(arguments)}");
            }

            var output = (await outputTask.ConfigureAwait(false) + "\n"
                          + await errorTask.ConfigureAwait(false)).TrimEnd();
            return new GitResult(process.ExitCode, output);
        }
        catch (Exception ex)
        {
            return new GitResult(-1, $"执行 git 命令异常: {ex.Message}");
        }
    }

    private static string FormatArguments(IEnumerable<string> arguments)
        => string.Join(' ', arguments.Select(FormatArgument));

    private static string FormatArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(char.IsWhiteSpace) && !argument.Contains('"'))
            return argument;

        return $"\"{argument.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }
}
