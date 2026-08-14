using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace HistoryJanus.GitHub;

/// <summary>服务器本机 Git 作者、GitHub 凭据、远端和 SSH 事实的读取与修改入口。</summary>
public sealed partial class GitHubConnectionService
{
    private readonly Func<string> _repositoryPath;
    private readonly IToolProcessRunner _processes;
    private readonly string _sshDirectory;

    public GitHubConnectionService(
        Func<string> repositoryPath,
        IToolProcessRunner? processes = null,
        string? sshDirectory = null)
    {
        _repositoryPath = repositoryPath;
        _processes = processes ?? new ToolProcessRunner();
        _sshDirectory = sshDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
    }

    public async Task<GitHubAccountOverview> GetOverviewAsync(
        CancellationToken cancellation = default)
    {
        var gitVersionTask = RunAsync("git", ["--version"], 10, cancellation);
        var gcmVersionTask = RunAsync("git", ["credential-manager", "--version"], 10, cancellation);
        var ghTask = RunAsync("gh", ["--version"], 5, cancellation);
        var identityTask = ReadIdentityAsync(cancellation);
        var remoteTask = ReadRemoteAsync(cancellation);
        var accountsTask = GetAccountsAsync(cancellation);
        var keysTask = ReadPublicKeysAsync(cancellation);
        await Task.WhenAll(gitVersionTask, gcmVersionTask, ghTask, identityTask,
            remoteTask, accountsTask, keysTask).ConfigureAwait(false);

        var gitVersion = await gitVersionTask.ConfigureAwait(false);
        var gcmVersion = await gcmVersionTask.ConfigureAwait(false);
        var gh = await ghTask.ConfigureAwait(false);
        var remote = await remoteTask.ConfigureAwait(false);
        var keys = await keysTask.ConfigureAwait(false);
        return new GitHubAccountOverview(
            FirstLine(gitVersion.CombinedOutput),
            gcmVersion.Success ? FirstLine(gcmVersion.CombinedOutput) : "不可用",
            gh.Success,
            await identityTask.ConfigureAwait(false),
            remote,
            await accountsTask.ConfigureAwait(false),
            new SshAccountStatus(remote.Host, remote.Host, keys, "unknown", "尚未检测"),
            new GitHubConnectionStatus("unknown", remote.Transport.ToString().ToLowerInvariant(), []),
            DateTimeOffset.Now);
    }

    public async Task<IReadOnlyList<GitCredentialAccount>> GetAccountsAsync(
        CancellationToken cancellation = default)
    {
        var result = await RunAsync("git", ["credential-manager", "github", "list"], 20, cancellation)
            .ConfigureAwait(false);
        if (!result.Success)
            return [];
        var accounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in result.CombinedOutput.Split(
                     '\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Contains(' ')
                ? line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Last()
                : line;
            candidate = candidate.Trim('"', '\'', '[', ']', ',', ':');
            if (AccountName().IsMatch(candidate))
                accounts.Add(candidate);
        }
        return accounts.Order(StringComparer.OrdinalIgnoreCase)
            .Select(account => new GitCredentialAccount(account)).ToList();
    }

    public async Task<GitHubConnectionStatus> TestAsync(
        string transport,
        int timeoutSeconds,
        CancellationToken cancellation = default)
    {
        var steps = new List<GitHubDiagnosticStep>();
        var remote = await ReadRemoteAsync(cancellation).ConfigureAwait(false);
        var selected = transport.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? remote.Transport.ToString().ToLowerInvariant()
            : transport.ToLowerInvariant();
        if (remote.Transport == GitRemoteTransport.None)
            return new GitHubConnectionStatus("no-origin", selected,
                [new GitHubDiagnosticStep("origin", "failed", 0, "未配置 origin")]);

        var watch = Stopwatch.StartNew();
        ToolProcessResult result;
        if (selected == "ssh")
        {
            var host = string.IsNullOrWhiteSpace(remote.Host) ? "github.com" : remote.Host;
            result = await RunAsync("ssh",
                ["-T", "-o", "BatchMode=yes", "-o", $"ConnectTimeout={Math.Clamp(timeoutSeconds, 1, 120)}", $"git@{host}"],
                timeoutSeconds, cancellation).ConfigureAwait(false);
            var output = GitHubRedactor.Redact(result.CombinedOutput);
            var authenticated = output.Contains(
                "successfully authenticated", StringComparison.OrdinalIgnoreCase);
            var state = authenticated ? "ok" : Classify(result, output);
            steps.Add(new GitHubDiagnosticStep("ssh", state, watch.ElapsedMilliseconds, output));
            return new GitHubConnectionStatus(state, "ssh", steps);
        }

        result = await GitAsync(["ls-remote", "--heads", "origin"], timeoutSeconds, cancellation)
            .ConfigureAwait(false);
        var detail = GitHubRedactor.Redact(result.CombinedOutput);
        var httpsState = result.Success ? "ok" : Classify(result, detail);
        steps.Add(new GitHubDiagnosticStep("https", httpsState, watch.ElapsedMilliseconds, detail));
        return new GitHubConnectionStatus(httpsState, selected, steps);
    }

    public Task<ToolProcessResult> LoginAsync(
        string? account,
        CancellationToken cancellation = default)
    {
        var arguments = new List<string> { "credential-manager", "github", "login" };
        if (!string.IsNullOrWhiteSpace(account))
        {
            arguments.Add("--username");
            arguments.Add(account.Trim());
        }
        return RunAsync("git", arguments, 300, cancellation);
    }

    public async Task<GitHubMutationResult<IReadOnlyList<GitCredentialAccount>>> LogoutAsync(
        string account,
        bool apply,
        CancellationToken cancellation = default)
    {
        var before = await GetAccountsAsync(cancellation).ConfigureAwait(false);
        if (!apply)
            return new GitHubMutationResult<IReadOnlyList<GitCredentialAccount>>(
                before, before, false, $"将注销 HTTPS 凭据账号 {account}");
        var result = await RunAsync(
            "git", ["credential-manager", "github", "logout", account], 60, cancellation)
            .ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException(GitHubRedactor.Redact(result.CombinedOutput));
        var after = await GetAccountsAsync(cancellation).ConfigureAwait(false);
        return new GitHubMutationResult<IReadOnlyList<GitCredentialAccount>>(
            before, after, true, $"已注销 HTTPS 凭据账号 {account}");
    }

    public async Task<GitHubMutationResult<GitIdentityInfo>> SetIdentityAsync(
        string name,
        string email,
        string scope,
        bool apply,
        CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Git user.name 和 user.email 不能为空");
        if (!scope.Equals("repository", StringComparison.OrdinalIgnoreCase)
            && !scope.Equals("global", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("身份作用域只允许 repository 或 global");

        var before = await ReadIdentityAsync(cancellation).ConfigureAwait(false);
        var desired = new GitIdentityInfo(name.Trim(), email.Trim(), scope.ToLowerInvariant());
        if (!apply)
            return new GitHubMutationResult<GitIdentityInfo>(
                before, desired, false, "将修改 Git 提交身份");

        var prefix = scope.Equals("global", StringComparison.OrdinalIgnoreCase)
            ? new[] { "config", "--global" }
            : new[] { "config", "--local" };
        var scopedBefore = await ReadScopedIdentityAsync(prefix, desired.Source, cancellation)
            .ConfigureAwait(false);
        var nameResult = await GitAsync(
            [.. prefix, "user.name", desired.Name], 20, cancellation).ConfigureAwait(false);
        if (!nameResult.Success)
            throw new InvalidOperationException(GitHubRedactor.Redact(nameResult.CombinedOutput));
        var emailResult = await GitAsync(
            [.. prefix, "user.email", desired.Email], 20, cancellation).ConfigureAwait(false);
        if (!emailResult.Success)
        {
            await RestoreIdentityAsync(prefix, scopedBefore, cancellation).ConfigureAwait(false);
            throw new InvalidOperationException(GitHubRedactor.Redact(emailResult.CombinedOutput));
        }
        return new GitHubMutationResult<GitIdentityInfo>(
            before, await ReadIdentityAsync(cancellation).ConfigureAwait(false), true,
            "Git 提交身份已修改");
    }

    public async Task<GitHubMutationResult<GitRemoteInfo>> SetRemoteAsync(
        string? fetch,
        string? push,
        bool apply,
        CancellationToken cancellation = default)
    {
        var before = await ReadRemoteAsync(cancellation).ConfigureAwait(false);
        var targetFetch = string.IsNullOrWhiteSpace(fetch) ? before.FetchUrl : fetch.Trim();
        var targetPush = string.IsNullOrWhiteSpace(push) ? before.PushUrl : push.Trim();
        ValidateRemote(targetFetch);
        ValidateRemote(targetPush);
        var desired = ParseRemote(targetFetch, targetPush);
        if (!apply)
            return new GitHubMutationResult<GitRemoteInfo>(before, desired, false, "将修改 origin");

        if (!targetFetch.Equals(before.FetchUrl, StringComparison.Ordinal))
        {
            var result = await GitAsync(
                ["remote", "set-url", "origin", targetFetch], 20, cancellation).ConfigureAwait(false);
            if (!result.Success)
                throw new InvalidOperationException(GitHubRedactor.Redact(result.CombinedOutput));
        }
        if (!targetPush.Equals(before.PushUrl, StringComparison.Ordinal))
        {
            var result = await GitAsync(
                ["remote", "set-url", "--push", "origin", targetPush], 20, cancellation)
                .ConfigureAwait(false);
            if (!result.Success)
            {
                if (!string.IsNullOrWhiteSpace(before.FetchUrl))
                    await GitAsync(
                        ["remote", "set-url", "origin", before.FetchUrl], 20, cancellation)
                        .ConfigureAwait(false);
                throw new InvalidOperationException(GitHubRedactor.Redact(result.CombinedOutput));
            }
        }
        return new GitHubMutationResult<GitRemoteInfo>(
            before, await ReadRemoteAsync(cancellation).ConfigureAwait(false), true, "origin 已修改");
    }

    public async Task<GitIdentityInfo> ReadIdentityAsync(
        CancellationToken cancellation = default)
    {
        var name = await GitAsync(
            ["config", "--show-origin", "--get", "user.name"], 10, cancellation)
            .ConfigureAwait(false);
        var email = await GitAsync(
            ["config", "--show-origin", "--get", "user.email"], 10, cancellation)
            .ConfigureAwait(false);
        var (nameValue, source) = ParseConfig(name);
        var (emailValue, emailSource) = ParseConfig(email);
        return new GitIdentityInfo(nameValue, emailValue,
            source == emailSource ? source : $"name:{source}; email:{emailSource}");
    }

    public async Task<GitRemoteInfo> ReadRemoteAsync(
        CancellationToken cancellation = default)
    {
        var fetch = await GitAsync(
            ["remote", "get-url", "origin"], 10, cancellation).ConfigureAwait(false);
        var push = await GitAsync(
            ["remote", "get-url", "--push", "origin"], 10, cancellation).ConfigureAwait(false);
        return ParseRemote(
            fetch.Success ? fetch.StandardOutput.Trim() : "",
            push.Success ? push.StandardOutput.Trim() : "");
    }

    private async Task<IReadOnlyList<SshPublicKeyInfo>> ReadPublicKeysAsync(
        CancellationToken cancellation)
    {
        if (!Directory.Exists(_sshDirectory))
            return [];
        var keys = new List<SshPublicKeyInfo>();
        foreach (var file in Directory.EnumerateFiles(
                     _sshDirectory, "*.pub", SearchOption.TopDirectoryOnly))
        {
            var result = await RunAsync(
                "ssh-keygen", ["-lf", file, "-E", "sha256"], 10, cancellation)
                .ConfigureAwait(false);
            var output = GitHubRedactor.Redact(result.CombinedOutput);
            var parts = output.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            keys.Add(new SshPublicKeyInfo(
                Path.GetFileName(file),
                parts.Length > 3 ? parts[^1].Trim('(', ')') : "unknown",
                parts.FirstOrDefault(part => part.StartsWith(
                    "SHA256:", StringComparison.OrdinalIgnoreCase)) ?? "不可用"));
        }
        return keys;
    }

    private Task<ToolProcessResult> GitAsync(
        IReadOnlyList<string> arguments,
        int timeoutSeconds,
        CancellationToken cancellation)
    {
        var repository = _repositoryPath();
        return !Directory.Exists(repository)
            ? Task.FromResult(new ToolProcessResult(
                -1, "", $"仓库路径不存在: {repository}"))
            : RunAsync("git", ["-C", repository, .. arguments], timeoutSeconds, cancellation);
    }

    private async Task<ToolProcessResult> RunAsync(
        string file,
        IReadOnlyList<string> arguments,
        int timeoutSeconds,
        CancellationToken cancellation)
    {
        var result = await _processes.RunAsync(
            file, arguments, timeoutSeconds, cancellation).ConfigureAwait(false);
        return result with
        {
            StandardOutput = GitHubRedactor.Redact(result.StandardOutput).Trim(),
            StandardError = GitHubRedactor.Redact(result.StandardError).Trim(),
        };
    }

    private async Task RestoreIdentityAsync(
        IReadOnlyList<string> prefix,
        GitIdentityInfo before,
        CancellationToken cancellation)
    {
        await RestoreConfigValueAsync(
            prefix, "user.name", before.Name, cancellation).ConfigureAwait(false);
        await RestoreConfigValueAsync(
            prefix, "user.email", before.Email, cancellation).ConfigureAwait(false);
    }

    private async Task<GitIdentityInfo> ReadScopedIdentityAsync(
        IReadOnlyList<string> prefix,
        string scope,
        CancellationToken cancellation)
    {
        var name = await GitAsync(
            [.. prefix, "--get", "user.name"], 10, cancellation).ConfigureAwait(false);
        var email = await GitAsync(
            [.. prefix, "--get", "user.email"], 10, cancellation).ConfigureAwait(false);
        return new GitIdentityInfo(
            name.Success ? FirstLine(name.StandardOutput) : "",
            email.Success ? FirstLine(email.StandardOutput) : "",
            scope);
    }

    private async Task RestoreConfigValueAsync(
        IReadOnlyList<string> prefix,
        string key,
        string value,
        CancellationToken cancellation)
    {
        string[] arguments = string.IsNullOrWhiteSpace(value)
            ? [.. prefix, "--unset", key]
            : [.. prefix, key, value];
        await GitAsync(arguments, 20, cancellation).ConfigureAwait(false);
    }

    private static (string Value, string Source) ParseConfig(ToolProcessResult result)
    {
        if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
            return ("", "unset");
        var line = FirstLine(result.StandardOutput);
        var split = line.Split(['\t', ' '], 2, StringSplitOptions.RemoveEmptyEntries);
        if (split.Length == 1)
            return (split[0], "effective");
        var origin = split[0];
        var source = origin.Contains(".gitconfig", StringComparison.OrdinalIgnoreCase)
            ? "global"
            : origin.Contains("config", StringComparison.OrdinalIgnoreCase)
                ? "repository"
                : "system";
        return (split[1].Trim(), source);
    }

    private static GitRemoteInfo ParseRemote(string fetch, string push)
    {
        var safeFetch = GitHubRedactor.SanitizeRemoteUrl(fetch);
        var safePush = GitHubRedactor.SanitizeRemoteUrl(push);
        var raw = string.IsNullOrWhiteSpace(fetch) ? push : fetch;
        if (string.IsNullOrWhiteSpace(raw))
            return new GitRemoteInfo("", "", GitRemoteTransport.None, "", "", "");

        string host;
        string path;
        GitRemoteTransport transport;
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            && !string.IsNullOrWhiteSpace(uri.Host))
        {
            host = uri.Host;
            path = uri.AbsolutePath.Trim('/');
            transport = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
                ? GitRemoteTransport.Https
                : uri.Scheme.Equals("ssh", StringComparison.OrdinalIgnoreCase)
                    ? GitRemoteTransport.Ssh
                    : GitRemoteTransport.Other;
        }
        else
        {
            var match = ScpRemote().Match(raw);
            host = match.Success ? match.Groups["host"].Value : "";
            path = match.Success ? match.Groups["path"].Value : raw;
            transport = match.Success ? GitRemoteTransport.Ssh : GitRemoteTransport.Other;
        }
        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return new GitRemoteInfo(
            safeFetch,
            safePush,
            transport,
            host,
            segments.Length >= 2 ? segments[^2] : "",
            segments.Length >= 1 ? segments[^1] : "");
    }

    private static void ValidateRemote(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("远端 URL 不能为空");
        if (value.Contains("github_pat_", StringComparison.OrdinalIgnoreCase)
            || TokenLike().IsMatch(value))
            throw new ArgumentException("远端 URL 不得包含 token");
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)))
            throw new ArgumentException("远端 URL 不得包含 userinfo 或 query secret");
    }

    private static string Classify(ToolProcessResult result, string output)
    {
        if (result.Cancelled) return "cancelled";
        if (result.TimedOut) return "timeout";
        if (output.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase))
            return "auth-failed";
        if (output.Contains("Could not resolve", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase)
            || output.Contains("Temporary failure in name resolution", StringComparison.OrdinalIgnoreCase))
            return "network-failed";
        return "failed";
    }

    private static string FirstLine(string value)
        => value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$")]
    private static partial Regex AccountName();

    [GeneratedRegex("^(?:[^@/:]+@)?(?<host>[^:]+):(?<path>.+)$")]
    private static partial Regex ScpRemote();

    [GeneratedRegex("(?:github_pat_|gh[pousr]_)", RegexOptions.IgnoreCase)]
    private static partial Regex TokenLike();
}
