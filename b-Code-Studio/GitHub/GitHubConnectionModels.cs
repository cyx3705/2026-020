using HistoryJanus.Git;

namespace HistoryJanus.GitHub;

public enum GitRemoteTransport
{
    None,
    Ssh,
    Https,
    Other,
}

public sealed record GitIdentityInfo(string Name, string Email, string Source);

public sealed record GitRemoteInfo(
    string FetchUrl,
    string PushUrl,
    GitRemoteTransport Transport,
    string Host,
    string Owner,
    string Repository);

public sealed record GitCredentialAccount(string Account);

public sealed record SshPublicKeyInfo(string FileName, string Algorithm, string Fingerprint);

public sealed record SshAccountStatus(
    string Host,
    string HostAlias,
    IReadOnlyList<SshPublicKeyInfo> PublicKeys,
    string ProbeState,
    string ProbeMessage);

public sealed record GitHubDiagnosticStep(
    string Step,
    string State,
    long DurationMs,
    string Detail);

public sealed record GitHubConnectionStatus(
    string State,
    string Transport,
    IReadOnlyList<GitHubDiagnosticStep> Steps);

public sealed record GitHubAccountOverview(
    string GitVersion,
    string CredentialManagerVersion,
    bool GhCliAvailable,
    GitIdentityInfo EffectiveIdentity,
    GitRemoteInfo Origin,
    IReadOnlyList<GitCredentialAccount> CredentialAccounts,
    SshAccountStatus Ssh,
    GitHubConnectionStatus Connection,
    DateTimeOffset LastCheckedAt,
    string PlannedRemoteUrl = "");

public sealed record GitHubMutationResult<T>(T Before, T After, bool Applied, string Action);

/// <summary>首次推送时按需创建（或复用）的远端仓库事实，字段全部来自 GitHub 接口回执。</summary>
public sealed record GitHubRepositoryCreation(
    string Name,
    string FullName,
    string CloneUrl,
    string SshUrl,
    string HtmlUrl,
    bool Created,
    string Visibility)
{
    /// <summary>
    /// 调用方指定的地址。可以是用户预填的 https；写进 origin 时由 <see cref="OriginUrl"/>
    /// 换成 SSH。HTTPS 传输会在连接重置后拖住整轮刷新。
    /// </summary>
    public string ConfiguredUrl { get; init; } = "";

    public string OriginUrl
    {
        get
        {
            if (ArchiveCloner.TryRewriteHttpToSsh(ConfiguredUrl, out var configured))
                return configured;
            if (ConfiguredUrl.Length > 0)
                return ConfiguredUrl;
            if (!string.IsNullOrWhiteSpace(SshUrl))
                return SshUrl;
            if (ArchiveCloner.TryRewriteHttpToSsh(CloneUrl, out var clone))
                return clone;
            return CloneUrl;
        }
    }
}
