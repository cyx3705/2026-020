using System.Diagnostics;
using System.Text;

namespace HistoryJanus.GitHub;

public sealed record ToolProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut = false,
    bool Cancelled = false)
{
    public bool Success => ExitCode == 0 && !TimedOut && !Cancelled;
    public string CombinedOutput => string.Join('\n',
        new[] { StandardOutput, StandardError }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

public interface IToolProcessRunner
{
    Task<ToolProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        int timeoutSeconds = 30,
        CancellationToken cancellation = default);
}

/// <summary>Git、GCM 和 SSH 共用的无 Shell 进程门面。</summary>
public sealed class ToolProcessRunner : IToolProcessRunner
{
    public async Task<ToolProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        int timeoutSeconds = 30,
        CancellationToken cancellation = default)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"无法启动 {fileName}");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
            var stderr = process.StandardError.ReadToEndAsync(cancellation);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(
                Math.Clamp(timeoutSeconds, 1, 600)));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellation, timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                return new ToolProcessResult(
                    -1,
                    await SafeReadAsync(stdout).ConfigureAwait(false),
                    await SafeReadAsync(stderr).ConfigureAwait(false),
                    TimedOut: timeout.IsCancellationRequested && !cancellation.IsCancellationRequested,
                    Cancelled: cancellation.IsCancellationRequested);
            }

            return new ToolProcessResult(
                process.ExitCode,
                await stdout.ConfigureAwait(false),
                await stderr.ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return new ToolProcessResult(-1, "", ex.Message);
        }
    }

    private static async Task<string> SafeReadAsync(Task<string> task)
    {
        try { return await task.ConfigureAwait(false); }
        catch { return ""; }
    }
}
