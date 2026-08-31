using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using HistoryJanus.Git;

namespace HistoryJanus.GitHub;

public sealed partial class GitHubRemoteHistoryReader : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<string, CancellationToken, Task<string?>> _tokens;

    public GitHubRemoteHistoryReader(
        HttpMessageHandler? handler = null,
        Func<string, CancellationToken, Task<string?>>? tokenReader = null,
        string apiBaseUrl = "https://api.github.com/")
    {
        _ownsHttp = handler == null;
        _http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: _ownsHttp)
        {
            BaseAddress = new Uri(apiBaseUrl),
            Timeout = TimeSpan.FromSeconds(60),
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("HistoryJanus");
        _tokens = tokenReader ?? ReadStoredTokenAsync;
    }

    public async Task<(bool Success, string Message, BranchHistoryReport? Report)> ReadAsync(
        ProjectLifecycleRecord record,
        string localDirectory,
        int limit,
        int skip,
        CancellationToken cancellation)
    {
        if (!TryParseRepository(record.Remote, out var owner, out var repository))
            return (false, "归档项目的 origin 不是 GitHub，无法直接读取远端历史", null);
        var token = await _tokens(localDirectory, cancellation).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            return (false, "无法从现有 GitHub 凭据链读取认证信息", null);

        var needed = skip + limit + 1;
        var all = new List<BranchHistoryEntry>();
        for (var page = 1; all.Count < needed; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"repos/{owner}/{repository}/commits?sha={Uri.EscapeDataString(record.Branch)}&per_page=100&page={page}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       && !cancellation.IsCancellationRequested)
            {
                return (false, $"GitHub 不可用: {GitHubRedactor.Redact(ex.Message)}", null);
            }
            using (response)
            {
                var json = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return (false, $"GitHub 认证失败 (HTTP {(int)response.StatusCode})", null);
                if (!response.IsSuccessStatusCode)
                    return (false, $"读取 GitHub 历史失败 (HTTP {(int)response.StatusCode}): {GitHubRedactor.Redact(json)}", null);
                var rows = Parse(json);
                all.AddRange(rows);
                if (rows.Count < 100)
                    break;
            }
        }

        var hasMore = all.Count > skip + limit;
        var selected = all.Skip(skip).Take(limit).Reverse().ToList();
        if (selected.Count == 0)
            return (false, "GitHub 未返回该分支的提交历史", null);
        selected[^1] = selected[^1] with { IsHead = skip == 0 };
        var head = all[0].Sha;
        var fork = selected[0].Sha;
        var report = new BranchHistoryReport(record.ProjectName, null, fork, head, head,
            BranchRemoteState.InSync, 0, 0, skip + selected.Count + (hasMore ? 1 : 0),
            skip, limit, hasMore, false, null, selected);
        return (true, $"{record.ProjectName}：已从 GitHub 读取 {selected.Count} 条远端提交", report);
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    private static List<BranchHistoryEntry> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var rows = new List<BranchHistoryEntry>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var sha = item.GetProperty("sha").GetString() ?? "";
            var commit = item.GetProperty("commit");
            var author = commit.GetProperty("author");
            var dateText = author.GetProperty("date").GetString() ?? "";
            if (sha.Length == 0 || !DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var date))
                continue;
            var message = commit.GetProperty("message").GetString() ?? "";
            rows.Add(new BranchHistoryEntry(sha, sha[..Math.Min(7, sha.Length)],
                author.GetProperty("name").GetString() ?? "", date,
                message.Split('\n')[0], item.GetProperty("parents").GetArrayLength(),
                false, false, CommitRemoteState.Pushed));
        }
        return rows;
    }

    private static async Task<string?> ReadStoredTokenAsync(string directory, CancellationToken cancellation)
    {
        var result = await GitRunner.RunWithInputAsync(directory,
            ["-c", "credential.interactive=false", "credential", "fill"],
            "protocol=https\nhost=github.com\n\n", cancellation).ConfigureAwait(false);
        if (!result.Success) return null;
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(item => item.StartsWith("password=", StringComparison.Ordinal))?
            ["password=".Length..];
    }

    internal static bool TryParseRepository(string remote, out string owner, out string repository)
    {
        owner = "";
        repository = "";
        var match = GitHubRemote().Match(remote.Trim());
        if (!match.Success) return false;
        owner = match.Groups["owner"].Value;
        repository = match.Groups["repo"].Value;
        if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repository = repository[..^4];
        return owner.Length > 0 && repository.Length > 0;
    }

    [GeneratedRegex(@"^(?:https://github\.com/|git@github\.com:|ssh://git@github\.com/)(?<owner>[^/]+)/(?<repo>[^/?#]+?)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubRemote();
}
