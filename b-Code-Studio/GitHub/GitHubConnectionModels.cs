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
    DateTimeOffset LastCheckedAt);

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
    /// 写进 origin 的 URL：优先 SSH。HTTPS 在大包（实测约 17MB 起）上会被稳定重置成
    /// <c>curl 55/56 Recv failure</c>，同一个包走 SSH 能推完；接口没给 ssh_url 时才退回 HTTPS。
    /// </summary>
    public string OriginUrl => string.IsNullOrWhiteSpace(SshUrl) ? CloneUrl : SshUrl;
}
