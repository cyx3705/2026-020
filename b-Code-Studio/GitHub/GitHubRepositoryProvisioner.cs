using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HistoryJanus.Git;

namespace HistoryJanus.GitHub;

/// <summary>
/// 首次推送时按需创建 GitHub 远端仓库。
/// 与 <see cref="GitHubConnectionService"/> 分开的原因：连接服务绑定单个仓库路径
/// （构造函数里的 Func&lt;string&gt;），而推送按各自的项目工作树发生，路径必须逐次传入。
/// </summary>
public interface IGitHubRepositoryProvisioner
{
    /// <summary>确保 <paramref name="repositoryName"/> 在 GitHub 上存在，返回接口给出的真实事实。</summary>
    /// <param name="repositoryPath">用于读取凭据的本地仓库路径。</param>
    /// <param name="repositoryName">期望的仓库名或 owner/name；指定 owner 时只复用或创建该地址。</param>
    /// <param name="visibility">public 或 private。</param>
    Task<GitHubRepositoryCreation> EnsureAsync(
        string repositoryPath,
        string repositoryName,
        string visibility,
        CancellationToken cancellation = default);
}

/// <summary>
/// 令牌来自 <c>git credential fill</c>：janus.github.login 已经把 HTTPS 凭据存进 GCM，
/// 所以建仓库不新增任何凭据面，也不把 token 落盘。全部对外文本经 GitHubRedactor 脱敏。
/// </summary>
public sealed partial class GitHubRepositoryProvisioner : IGitHubRepositoryProvisioner, IDisposable
{
    public const string DefaultVisibility = "public";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<string, CancellationToken, Task<string?>> _tokens;

    public GitHubRepositoryProvisioner(
        HttpMessageHandler? handler = null,
        Func<string, CancellationToken, Task<string?>>? tokenReader = null,
        string apiBaseUrl = "https://api.github.com/")
    {
        _ownsHttp = handler == null;
        _http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: _ownsHttp)
        {
            BaseAddress = new Uri(apiBaseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(60),
        };
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HistoryJanus");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        _tokens = tokenReader ?? ReadStoredTokenAsync;
    }

    public async Task<GitHubRepositoryCreation> EnsureAsync(
        string repositoryPath,
        string repositoryName,
        string visibility,
        CancellationToken cancellation = default)
    {
        var parts = (repositoryName ?? string.Empty).Trim().Split('/');
        if (parts.Length is < 1 or > 2 || parts.Any(part => part is "." or ".." || !RepositoryName().IsMatch(part))
            || parts.Length == 2 && !OwnerName().IsMatch(parts[0]))
            throw new ArgumentException($"仓库名不符合 GitHub 命名要求: {repositoryName}");
        var name = parts[^1];
        var scope = NormalizeVisibility(visibility);
        var token = await _tokens(repositoryPath, cancellation).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("未能从 Git 凭据存储读取 GitHub 令牌，请先执行 janus.github.login");

        var login = parts.Length == 1 ? await ReadLoginAsync(token, cancellation).ConfigureAwait(false) : null;
        var owner = parts.Length == 2 ? parts[0] : login!;
        var path = $"repos/{owner}/{name}";
        var existing = await SendAsync(HttpMethod.Get, path, null, token, cancellation).ConfigureAwait(false);
        if (existing.Status == HttpStatusCode.OK)
            return DescribeTarget(existing.Body, scope, false, owner, name);
        if (existing.Status != HttpStatusCode.NotFound)
            throw new InvalidOperationException($"检查 GitHub 仓库失败 (HTTP {(int)existing.Status}): {existing.Body}");

        login ??= await ReadLoginAsync(token, cancellation).ConfigureAwait(false);
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["name"] = name, ["private"] = scope == "private", ["auto_init"] = false,
        });
        var endpoint = owner.Equals(login, StringComparison.OrdinalIgnoreCase) ? "user/repos" : $"orgs/{owner}/repos";
        var created = await SendAsync(HttpMethod.Post, endpoint, body, token, cancellation).ConfigureAwait(false);
        if (created.Status == HttpStatusCode.Created)
            return DescribeTarget(created.Body, scope, true, owner, name);
        // 检查与创建之间可能被别人建好；只复查原地址，绝不另选账号或仓库名。
        if (created.Status == HttpStatusCode.UnprocessableEntity)
        {
            existing = await SendAsync(HttpMethod.Get, path, null, token, cancellation).ConfigureAwait(false);
            if (existing.Status == HttpStatusCode.OK)
                return DescribeTarget(existing.Body, scope, false, owner, name);
        }
        throw new InvalidOperationException($"创建 GitHub 仓库 {owner}/{name} 失败 (HTTP {(int)created.Status}): {created.Body}");
    }

    private static GitHubRepositoryCreation DescribeTarget(string body, string scope, bool created, string owner, string name)
    {
        var result = Describe(body, scope, created);
        if (!result.FullName.Equals($"{owner}/{name}", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("GitHub 返回的仓库与指定目标不一致");
        return result;
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    private async Task<string> ReadLoginAsync(string token, CancellationToken cancellation)
    {
        var response = await SendAsync(HttpMethod.Get, "user", null, token, cancellation)
            .ConfigureAwait(false);
        if (response.Status != HttpStatusCode.OK)
            throw new InvalidOperationException(
                $"GitHub 令牌校验失败 (HTTP {(int)response.Status}): {response.Body}");
        var login = ReadString(response.Body, "login");
        if (string.IsNullOrWhiteSpace(login))
            throw new InvalidOperationException("GitHub 未返回账号 login");
        return login;
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        HttpMethod method,
        string path,
        string? json,
        string token,
        CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (json != null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        try
        {
            using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            return (response.StatusCode, GitHubRedactor.Redact(body).Trim());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       && !cancellation.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"访问 GitHub API 失败: {GitHubRedactor.Redact(ex.Message)}", ex);
        }
    }

    /// <summary>
    /// 走 GCM 已存的 HTTPS 凭据。<c>credential.interactive=false</c> 是必需的：
    /// 缺凭据时 GCM 会弹窗，而推送链路跑在没有人值守的宿主进程里。
    /// </summary>
    private static async Task<string?> ReadStoredTokenAsync(
        string repositoryPath, CancellationToken cancellation)
    {
        var result = await GitRunner.RunWithInputAsync(
            repositoryPath,
            ["-c", "credential.interactive=false", "credential", "fill"],
            "protocol=https\nhost=github.com\n\n",
            cancellation).ConfigureAwait(false);
        if (!result.Success)
            return null;
        foreach (var line in result.Output.Split(
                     '\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("password=", StringComparison.Ordinal))
                return line["password=".Length..];
        }
        return null;
    }

    private static GitHubRepositoryCreation Describe(string body, string visibility, bool wasCreated)
    {
        var name = ReadString(body, "name");
        var fullName = ReadString(body, "full_name");
        var cloneUrl = ReadString(body, "clone_url");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(cloneUrl))
            throw new InvalidOperationException($"GitHub 返回的仓库描述不完整: {body}");
        return new GitHubRepositoryCreation(
            name,
            fullName,
            cloneUrl,
            ReadString(body, "ssh_url"),
            ReadString(body, "html_url"),
            wasCreated,
            // 已存在的仓库以接口事实为准，不用请求里的期望值冒充。
            ReadString(body, "visibility") is { Length: > 0 } actual ? actual : visibility);
    }

    private static string ReadString(string json, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty(property, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    public static string NormalizeVisibility(string? visibility)
    {
        var value = (visibility ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0)
            return DefaultVisibility;
        if (value is not ("public" or "private"))
            throw new ArgumentException($"仓库可见性只允许 public 或 private: {visibility}");
        return value;
    }

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$")]
    private static partial Regex OwnerName();

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepositoryName();
}
