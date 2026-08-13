using HistoryJanus;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using Xunit;

namespace HistoryJanus.Contracts;

/// <summary>
/// 命令目录合同：组合根是 Janus 业务命令的唯一权威注册入口，Help/命令集/MCP/手册
/// 全部从运行时注册表投影（QA-001；代码管道化 §4.1）。这里的断言替代人工核对，
/// 使命令数量、来源、只读与危险确认的漂移在 Contracts 阶段即被阻断。
/// </summary>
public sealed class CommandCatalogContractTests
{
    // 组合根注册的 31 条业务命令；第 32 条 janus.status 由模块宿主从
    // [ModuleCommand] 投影，不在组合根内（QA-001 的 32 = 31 + Status）。
    // 3.5.0 全部指令改为 janus.<类>.<方法> 三段式全小写命名。
    // 3.8.0 新增 GitHub 登录、注销、身份和 origin 的总线命令，31 → 35。
    // 3.9.0 新增 graph 只读 DAG 四条，35 → 39。
    private const int ExpectedCommandCount = 39;

    // DEC-012：janus 域内五类；新增类需同级决策。
    private static readonly string[] ExpectedClasses =
        ["github", "gitrule", "graph", "history", "proj"];

    private static readonly string[] ReadOnlyCommands =
    [
        "janus.proj.list", "janus.proj.tree", "janus.proj.scan", "janus.proj.config", "janus.proj.metas",
        "janus.history.list", "janus.history.show", "janus.history.diff",
        "janus.gitrule.scan", "janus.gitrule.review", "janus.gitrule.list",
        "janus.github.status", "janus.github.accounts", "janus.github.test",
        "janus.graph.summary", "janus.graph.branches", "janus.graph.commits", "janus.graph.node",
    ];

    private static readonly string[] ConfirmedCommands =
    [
        "janus.github.login", "janus.github.logout", "janus.github.identity", "janus.github.remote",
        "janus.proj.delete", "janus.proj.commitall", "janus.proj.pushall", "janus.proj.repair",
        "janus.history.rollback", "janus.history.reset", "janus.history.forcepush",
        "janus.gitrule.sync", "janus.gitrule.set", "janus.gitrule.batchset", "janus.gitrule.remove",
    ];

    [Fact]
    public void CompositionRegistersTheFullCommandCatalog()
    {
        using var fixture = new CompositionFixture();

        Assert.Equal(ExpectedCommandCount, fixture.Registry.All().Count);
    }

    // DEC-008：类清单是合同的一部分。业务模块不注册诊断/自动化辅助类（如 debug），
    // 也不为两条命令单开一个类（meta 已并入 proj）。
    [Fact]
    public void CommandsUseOnlyTheAgreedClasses()
    {
        using var fixture = new CompositionFixture();

        var actual = fixture.Registry.All()
            .Select(descriptor => descriptor.Name.Split('.')[1])
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedClasses, actual);
    }

    // 三段式无例外：域恒为 janus，类与方法都不留空段。
    [Fact]
    public void CommandNamesAlwaysCarryThreeSegments()
    {
        using var fixture = new CompositionFixture();

        foreach (var descriptor in fixture.Registry.All())
        {
            var segments = descriptor.Name.Split('.');
            Assert.Equal(3, segments.Length);
            Assert.Equal("janus", segments[0]);
            Assert.All(segments, segment => Assert.NotEmpty(segment));
        }
    }

    [Fact]
    public void EveryCommandCarriesTheRequestedSource()
    {
        using var fixture = new CompositionFixture();

        foreach (var descriptor in fixture.Registry.All())
        {
            Assert.Equal(
                CompositionFixture.Source,
                fixture.Registry.GetSource(descriptor.Name));
        }
    }

    [Fact]
    public void CommandNamesAreUniqueAndLowercase()
    {
        using var fixture = new CompositionFixture();
        var names = fixture.Registry.All().Select(descriptor => descriptor.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, name => Assert.Equal(name, name.ToLowerInvariant()));
    }

    [Fact]
    public void EveryCommandHasHelpProjectionText()
    {
        using var fixture = new CompositionFixture();

        foreach (var descriptor in fixture.Registry.All())
        {
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Summary), descriptor.Name);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Example), descriptor.Name);
        }
    }

    [Fact]
    public void ReadOnlyProjectionMatchesTheContract()
    {
        using var fixture = new CompositionFixture();
        var actual = fixture.Registry.All()
            .Where(descriptor => descriptor.Readonly)
            .Select(descriptor => descriptor.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ReadOnlyCommands.OrderBy(name => name, StringComparer.Ordinal),
            actual);
    }

    [Fact]
    public void DangerousCommandsCarryConfirmationGates()
    {
        using var fixture = new CompositionFixture();

        foreach (var name in ConfirmedCommands)
        {
            Assert.True(
                fixture.Registry.TryGet(name, out var descriptor), $"missing: {name}");
            Assert.True(
                descriptor.IsDangerous,
                $"{name} must expose a confirmation gate for catalog/MCP projection");
        }
    }

    [Fact]
    public void ReadOnlyCommandsNeverRequireConfirmation()
    {
        using var fixture = new CompositionFixture();

        foreach (var name in ReadOnlyCommands)
        {
            Assert.True(
                fixture.Registry.TryGet(name, out var descriptor), $"missing: {name}");
            Assert.False(descriptor.IsDangerous, $"{name} is read-only and must stay confirmation-free");
        }
    }

    [Fact]
    public void CommandClassMatchesTheMiddleNameSegment()
    {
        using var fixture = new CompositionFixture();

        foreach (var descriptor in fixture.Registry.All())
        {
            var parts = descriptor.Name.Split('.');
            Assert.True(parts.Length >= 3, $"{descriptor.Name} must be janus.<class>.<method>");
            Assert.Equal("janus", parts[0]);
            Assert.True(
                string.Equals(parts[1], descriptor.CommandClass, StringComparison.Ordinal),
                $"{descriptor.Name}: CommandClass must equal '{parts[1]}', was '{descriptor.CommandClass}'");
        }
    }

    private sealed class CompositionFixture : IDisposable
    {
        public const string Source = "test:contracts";

        private readonly string _dataDirectory;

        public CompositionFixture()
        {
            _dataDirectory = Path.Combine(
                Path.GetTempPath(), "HistoryJanus-Contracts", Guid.NewGuid().ToString("N"));
            Registry = new CommandRegistry();
            var bus = new CommandBus(Registry, new NullLog());
            StudioBusinessCompositionFactory.Register(
                Registry, bus, new MemorySettings(), new NullLog(), _dataDirectory, Source);
        }

        public CommandRegistry Registry { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_dataDirectory))
                    Directory.Delete(_dataDirectory, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响合同结论；操作系统会回收 %TEMP%。
            }
        }
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public int GetInt(string key, int fallback)
            => int.TryParse(Get(key), out var value) ? value : fallback;

        public void Set(string key, string value) => _values[key] = value;

        public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
    }

    private sealed class NullLog : IShellLog
    {
        public event EventHandler<ShellLogEntry>? EntryAdded;

        public IReadOnlyList<ShellLogEntry> Snapshot() => [];

        public void Log(ShellLogLevel level, string category, string message)
            => EntryAdded?.Invoke(this, new ShellLogEntry(DateTime.Now, level, category, message));
    }
}
