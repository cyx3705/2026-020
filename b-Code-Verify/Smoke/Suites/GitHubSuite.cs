using HistoryVulcan.Core.Commands;
using HistoryJanus.GitHub;
using static HistoryJanus.Smoke.SmokeKit;

namespace HistoryJanus.Smoke.Suites;

/// <summary>并入 Janus 的 GitHub 连接治理：脱敏、连接检测、回滚与只读命令注册。</summary>
internal static class GitHubSuite
{
    public static async Task RunAsync(string[] args)
    {
        var temp = TemporaryDirectory("github");
        try
        {
            TestRedaction();
            await TestOverviewAndConnectionAsync(temp);
            await TestIdentityRollbackAsync(temp);
            await TestRemoteRollbackAsync(temp);
            await TestCommandsRegisteredAsync(temp);
        }
        finally
        {
            DeleteTree(temp);
        }
    }

    private static void TestRedaction()
    {
        var input = "https://alice:secret@github.com/acme/repo.git " +
                    "github_pat_ABC123 token=hidden Authorization: Bearer abc";
        var safe = GitHubRedactor.Redact(input);
        True(!safe.Contains("alice", StringComparison.Ordinal), "URL userinfo redacted");
        True(!safe.Contains("github_pat_", StringComparison.OrdinalIgnoreCase), "token redacted");
        True(!safe.Contains("token=hidden", StringComparison.OrdinalIgnoreCase), "assignment redacted");
        True(!safe.Contains("Bearer abc", StringComparison.OrdinalIgnoreCase), "bearer redacted");
    }

    private static async Task TestOverviewAndConnectionAsync(string temp)
    {
        var repository = Path.Combine(temp, "overview.git");
        var ssh = Path.Combine(temp, "ssh");
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(ssh);
        File.WriteAllText(Path.Combine(ssh, "id_ed25519.pub"), "ssh-ed25519 AAAA test");

        var runner = new FakeRunner((file, arguments) =>
        {
            var args = StripRepository(arguments);
            if (file == "gh") return Ok("gh version 2.74.0");
            if (file == "ssh-keygen") return Ok("256 SHA256:TEST comment (ED25519)");
            if (file == "ssh") return Fail("Hi! You've successfully authenticated");
            if (args.SequenceEqual(["--version"])) return Ok("git version 2.49.0.windows.1");
            if (args.SequenceEqual(["credential-manager", "--version"])) return Ok("2.6.1");
            if (args.SequenceEqual(["credential-manager", "github", "list"]))
                return Ok("github.com alice\ngithub.com alice\ngithub.com bob");
            if (args.SequenceEqual(["config", "--show-origin", "--get", "user.name"]))
                return Ok("file:.git/config Alice");
            if (args.SequenceEqual(["config", "--show-origin", "--get", "user.email"]))
                return Ok("file:.git/config alice@example.com");
            if (args.SequenceEqual(["remote", "get-url", "origin"]))
                return Ok("git@github.com:acme/studio.git");
            if (args.SequenceEqual(["remote", "get-url", "--push", "origin"]))
                return Ok("git@github.com:acme/studio.git");
            return Ok();
        });

        var service = new GitHubConnectionService(() => repository, runner, ssh);
        var overview = await service.GetOverviewAsync();
        Equal("git version 2.49.0.windows.1", overview.GitVersion, "git version");
        Equal("Alice", overview.EffectiveIdentity.Name, "identity name");
        Equal(GitRemoteTransport.Ssh, overview.Origin.Transport, "scp transport");
        Equal("acme", overview.Origin.Owner, "remote owner");
        Equal(2, overview.CredentialAccounts.Count, "deduplicated accounts");
        Equal(1, overview.Ssh.PublicKeys.Count, "public key count");

        var connection = await service.TestAsync("auto", 3);
        Equal("ok", connection.State, "SSH authenticated result");

        var timeout = new GitHubConnectionService(() => repository,
            new FakeRunner((file, arguments) => file == "ssh"
                ? new ToolProcessResult(-1, "", "", TimedOut: true)
                : runner.Handle(file, arguments)), ssh);
        Equal("timeout", (await timeout.TestAsync("ssh", 3)).State, "SSH timeout result");
    }

    private static async Task TestIdentityRollbackAsync(string temp)
    {
        var repository = Path.Combine(temp, "identity.git");
        Directory.CreateDirectory(repository);
        var calls = new List<string>();
        var runner = new FakeRunner((_, arguments) =>
        {
            var args = StripRepository(arguments);
            var text = string.Join(' ', args);
            calls.Add(text);
            if (args.SequenceEqual(["config", "--show-origin", "--get", "user.name"]))
                return Ok("file:.git/config Old Name");
            if (args.SequenceEqual(["config", "--show-origin", "--get", "user.email"]))
                return Ok("file:.git/config old@example.com");
            if (args.SequenceEqual(["config", "--local", "--get", "user.name"]))
                return Ok("Old Name");
            if (args.SequenceEqual(["config", "--local", "--get", "user.email"]))
                return Ok("old@example.com");
            if (args.SequenceEqual(["config", "--local", "user.email", "new@example.com"]))
                return Fail("email write failed github_pat_SECRET");
            return Ok();
        });

        var service = new GitHubConnectionService(() => repository, runner);
        await ThrowsAsync<InvalidOperationException>(() => service.SetIdentityAsync(
            "New Name", "new@example.com", "repository", apply: true));
        True(calls.Contains("config --local user.name Old Name"), "identity name rollback");
        True(calls.Contains("config --local user.email old@example.com"), "identity email rollback");
    }

    private static async Task TestRemoteRollbackAsync(string temp)
    {
        var repository = Path.Combine(temp, "remote.git");
        Directory.CreateDirectory(repository);
        var calls = new List<string>();
        var runner = new FakeRunner((_, arguments) =>
        {
            var args = StripRepository(arguments);
            var text = string.Join(' ', args);
            calls.Add(text);
            if (args.SequenceEqual(["remote", "get-url", "origin"]))
                return Ok("https://github.com/old/repo.git");
            if (args.SequenceEqual(["remote", "get-url", "--push", "origin"]))
                return Ok("git@github.com:old/repo.git");
            if (args.SequenceEqual(["remote", "set-url", "--push", "origin", "git@github.com:new/repo.git"]))
                return Fail("push URL failed");
            return Ok();
        });

        var service = new GitHubConnectionService(() => repository, runner);
        await ThrowsAsync<InvalidOperationException>(() => service.SetRemoteAsync(
            "https://github.com/new/repo.git", "git@github.com:new/repo.git", apply: true));
        True(calls.Contains("remote set-url origin https://github.com/old/repo.git"),
            "fetch URL rollback");
    }

    private static async Task TestCommandsRegisteredAsync(string temp)
    {
        var repository = Path.Combine(temp, "commands.git");
        Directory.CreateDirectory(repository);
        var runner = new FakeRunner((_, _) => Ok());
        var service = new GitHubConnectionService(() => repository, runner);

        var registry = new CommandRegistry();
        GitHubCommands.RegisterAll(registry, service, "module:HistoryJanus");
        foreach (var name in new[] { "janus.github.status", "janus.github.accounts", "janus.github.test" })
        {
            True(registry.TryGet(name, out var descriptor) && descriptor.Readonly,
                $"{name} stays a readonly module command");
            Equal("module:HistoryJanus", registry.GetSource(name),
                $"{name} registers under the Janus module source");
        }
        var test = registry.All().Single(d => d.Name == "janus.github.test");
        True(test.Parameters.Any(p => p.Name == "transport"
                && p.AllowedValues is ["auto", "ssh", "https"]),
            "janus.github.test keeps the transport enum schema");

        var bus = new CommandBus(registry, new MemoryLog());
        var accounts = await bus.ExecuteAsync("janus.github.accounts", "Smoke");
        True(accounts.Success, "janus.github.accounts executes through the bus");
        var invalid = await bus.ExecuteAsync("janus.github.test transport=bogus", "Smoke");
        True(!invalid.Success, "janus.github.test rejects an unknown transport");
        foreach (var name in new[] { "janus.github.login", "janus.github.logout", "janus.github.identity", "janus.github.remote" })
        {
            True(registry.TryGet(name, out var mutation) && mutation.ConfirmPrompt != null,
                $"{name} is registered with a confirmation gate");
        }
    }

    private static IReadOnlyList<string> StripRepository(IReadOnlyList<string> arguments)
        => arguments.Count >= 2 && arguments[0] == "-C" ? arguments.Skip(2).ToArray() : arguments;

    private static ToolProcessResult Ok(string output = "") => new(0, output, "");

    private static ToolProcessResult Fail(string error) => new(1, "", error);

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {typeof(T).Name}");
    }

    private sealed class FakeRunner(
        Func<string, IReadOnlyList<string>, ToolProcessResult> handler) : IToolProcessRunner
    {
        public ToolProcessResult Handle(string file, IReadOnlyList<string> arguments)
            => handler(file, arguments);

        public Task<ToolProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            int timeoutSeconds = 30,
            CancellationToken cancellation = default)
            => Task.FromResult(handler(fileName, arguments));
    }
}
