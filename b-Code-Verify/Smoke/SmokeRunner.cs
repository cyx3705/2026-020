using System.Diagnostics;

namespace HistoryJanus.Smoke;

/// <summary>
/// 冒烟套件的执行器：并行调度、具名超时与耗时汇报都归这里，Program.cs 只保留套件清单。
/// </summary>
/// <remarks>
/// 三条设计取舍：
/// 1. <b>并行优先</b>。每个套件都用 <see cref="SmokeKit"/> 的 GUID 临时目录建自己的 git 仓库，
///    彼此不共享可变状态，因此整轮墙钟时间取决于最慢的套件而不是所有套件之和。
/// 2. <b>输出按登记顺序</b>。并行完成顺序是不确定的，直接边跑边打印会让两次运行的输出无法比对，
///    所以结果先收集再按登记顺序打印。
/// 3. <b>挂死必须指名</b>。托管任务无法安全中止，超时只能报告而不能回收；
///    因此超时后打印指名失败并让调用方强制结束进程，绝不整轮静默停住。
/// </remarks>
internal static class SmokeRunner
{
    /// <summary>单个套件的墙钟上限。最慢的真实套件约 10 秒，20 秒足以覆盖冷启动抖动。</summary>
    private static readonly TimeSpan SuiteTimeout = TimeSpan.FromSeconds(20);

    internal readonly record struct SuiteOutcome(
        string Name,
        bool Passed,
        bool TimedOut,
        TimeSpan Elapsed,
        Exception? Failure);

    /// <summary>并行执行全部套件，并按登记顺序返回结果。</summary>
    internal static async Task<IReadOnlyList<SuiteOutcome>> RunAllAsync(
        IReadOnlyList<(string Name, Func<string[], Task> Run)> suites,
        string[] args)
    {
        // 登记顺序即输出顺序：Task 数组与 suites 同序，WhenAll 保留下标对应关系。
        var running = suites.Select(suite => RunOneAsync(suite.Name, suite.Run, args)).ToArray();
        return await Task.WhenAll(running).ConfigureAwait(false);
    }

    private static async Task<SuiteOutcome> RunOneAsync(
        string name,
        Func<string[], Task> run,
        string[] args)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var suite = Task.Run(() => run(args));
            var finished = await Task.WhenAny(suite, Task.Delay(SuiteTimeout)).ConfigureAwait(false);
            if (!ReferenceEquals(finished, suite))
                return new SuiteOutcome(name, Passed: false, TimedOut: true, watch.Elapsed, Failure: null);

            await suite.ConfigureAwait(false);
            return new SuiteOutcome(name, Passed: true, TimedOut: false, watch.Elapsed, Failure: null);
        }
        catch (Exception ex)
        {
            return new SuiteOutcome(name, Passed: false, TimedOut: false, watch.Elapsed, ex);
        }
    }

    /// <summary>按登记顺序打印结果，返回失败套件数。</summary>
    internal static int Report(IReadOnlyList<SuiteOutcome> outcomes)
    {
        var failed = 0;
        foreach (var outcome in outcomes)
        {
            var seconds = outcome.Elapsed.TotalSeconds;
            if (outcome.Passed)
            {
                Console.WriteLine($"{outcome.Name}Smoke: PASS ({seconds:0.0}s)");
                continue;
            }

            failed++;
            if (outcome.TimedOut)
            {
                // 指名超时：整轮不会静默停住，日志直接点出是哪一套挂住了。
                Console.Error.WriteLine(
                    $"{outcome.Name}Smoke: TIMEOUT after {SuiteTimeout.TotalSeconds:0}s " +
                    "(suite did not finish; the run is aborted so it cannot hang silently)");
                continue;
            }

            Console.Error.WriteLine(outcome.Failure);
            Console.Error.WriteLine($"{outcome.Name}Smoke: FAIL ({seconds:0.0}s)");
        }

        return failed;
    }

    /// <summary>是否存在挂死套件；调用方据此强制结束进程，避免被未回收的任务拖住。</summary>
    internal static bool AnyTimedOut(IReadOnlyList<SuiteOutcome> outcomes)
        => outcomes.Any(outcome => outcome.TimedOut);
}
