using System.Diagnostics;
using System.IO;
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
        => await RunCoreAsync(gitDir, arguments, null, null, cancellation).ConfigureAwait(false);

    /// <summary>
    /// 边跑边把 git 的进度行交出去。传输型命令（clone / fetch）唯一的进度来源是
    /// **stderr 上的回车刷新行**；缓冲到退出才交出等于没有进度——界面在那之前
    /// 一个字都拿不到，几百 MB 的克隆看上去就是卡死。
    /// 行很密，因此按 <see cref="ProgressIntervalMs"/> 节流，阶段末行不丢。
    /// </summary>
    public static async Task<GitResult> RunStreamingAsync(
        string gitDir,
        IReadOnlyList<string> arguments,
        IProgress<string>? progress,
        CancellationToken cancellation = default,
        IReadOnlyDictionary<string, string>? environment = null)
        => await RunCoreAsync(gitDir, arguments, null, progress, cancellation, environment)
            .ConfigureAwait(false);

    public static async Task<GitResult> RunWithInputAsync(
        string gitDir,
        IReadOnlyList<string> arguments,
        string standardInput,
        CancellationToken cancellation = default)
        => await RunCoreAsync(gitDir, arguments, standardInput, null, cancellation)
            .ConfigureAwait(false);

    /// <summary>两条相邻进度行之间至少隔这么久才再报一次。</summary>
    private const int ProgressIntervalMs = 400;

    private static async Task<GitResult> RunCoreAsync(
        string gitDir,
        IReadOnlyList<string> arguments,
        string? standardInput,
        IProgress<string>? progress,
        CancellationToken cancellation,
        IReadOnlyDictionary<string, string>? environment = null)
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

            ApplyNonInteractiveEnvironment(psi);
            foreach (var pair in environment ?? new Dictionary<string, string>())
                psi.Environment[pair.Key] = pair.Value;

            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(gitDir);
            // 路径按原样交出，不做八进制转义。git 默认 core.quotepath=true，凡是输出路径的命令
            // （status / diff / ls-files / for-each-ref）都会把非 ASCII 字节写成 \344\270\255
            // 这种三位八进制码。本库项目名和文件名大量是中文，于是提交前的差异弹窗里整排文件
            // 变成看不懂的数字串。关掉它只影响 git **打印**路径的方式，不影响匹配、暂存与提交。
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("core.quotepath=false");
            foreach (var argument in arguments)
                psi.ArgumentList.Add(argument);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("无法启动 git 进程");
            // Cancellation is enforced by WaitForExitAsync below, followed by an entire-tree kill.
            // Keep draining both pipes until the child exits so a full pipe cannot deadlock git.
            var outputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errorTask = progress == null
                ? process.StandardError.ReadToEndAsync(CancellationToken.None)
                : DrainWithProgressAsync(process.StandardError, progress);

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

    /// <summary>
    /// 一律不交互。窗口是隐藏的（<c>CreateNoWindow</c>），所以 git 一旦在终端上问问题
    /// ——问用户名、问密码、问要不要信任新主机——那个提示没人看得见，进程就停在那儿不动，
    /// 表现为「点了拉取以后什么都不发生」。关掉终端提问，这类情况改为立刻报错退出。
    ///
    /// 只关 git 自己的终端提问：凭据助手（GCM）有自己的界面，不受这里影响。
    /// <c>GIT_SSH_COMMAND</c> 只在环境里没有时才给，且 <c>core.sshCommand</c> 仍然优先，
    /// 因此自定义 ssh 配置不会被顶掉。
    /// </summary>
    private static void ApplyNonInteractiveEnvironment(ProcessStartInfo psi)
    {
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (!psi.Environment.ContainsKey("GIT_SSH_COMMAND"))
            psi.Environment["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes";
    }

    /// <summary>
    /// 一边读 stderr 一边把新出现的行交出去，同时照旧把全文攒下来给调用方。
    ///
    /// git 的传输进度是同一行反复用回车刷新的，<c>ReadLineAsync</c> 把每次刷新都当成一行，
    /// 于是一次克隆能刷出上千行。按 <see cref="ProgressIntervalMs"/> 节流，
    /// 但**最后一行必交**——阶段的收尾行（"done."、错误原文）恰好是最要紧的那一条。
    /// </summary>
    private static async Task<string> DrainWithProgressAsync(StreamReader reader, IProgress<string> progress)
    {
        var all = new StringBuilder();
        var lastReportedAt = Environment.TickCount64 - ProgressIntervalMs;
        string? pending = null;
        while (await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
        {
            all.Append(line).Append('\n');
            var text = line.Trim();
            if (text.Length == 0)
                continue;
            pending = text;
            var now = Environment.TickCount64;
            if (now - lastReportedAt < ProgressIntervalMs)
                continue;
            lastReportedAt = now;
            pending = null;
            progress.Report(text);
        }
        if (pending != null)
            progress.Report(pending);
        return all.ToString();
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
