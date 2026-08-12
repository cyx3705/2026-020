using HistoryVulcan.Core.Commands;

namespace HistoryJanus.GitHub;

/// <summary>
/// janus.github.* 类注册（宿主域为模块名 HistoryJanus，类为 github）。
/// 读写均登记到宿主命令总线；写操作由 apply 参数和 ConfirmPrompt 统一保护。
/// </summary>
public static class GitHubCommands
{
    public static void RegisterAll(
        CommandRegistry registry,
        GitHubConnectionService service,
        string source = "app")
    {
        registry.Register(BuildStatus(service), source);
        registry.Register(BuildAccounts(service), source);
        registry.Register(BuildTest(service), source);
        registry.Register(BuildLogin(service), source);
        registry.Register(BuildLogout(service), source);
        registry.Register(BuildIdentity(service), source);
        registry.Register(BuildRemote(service), source);
    }

    private static CommandDescriptor BuildStatus(GitHubConnectionService service) => new()
    {
        Name = "janus.github.status",
        CommandClass = "github",
        Summary = "查看服务器 Git、GCM、提交身份、origin 和 SSH 状态",
        Readonly = true,
        Example = "janus.github.status",
        Handler = async _ =>
        {
            var overview = await service.GetOverviewAsync();
            return CommandResult.Ok(
                $"git={overview.GitVersion}; origin={overview.Origin.FetchUrl}; " +
                $"GCM 账号={overview.CredentialAccounts.Count} 个",
                overview);
        },
    };

    private static CommandDescriptor BuildAccounts(GitHubConnectionService service) => new()
    {
        Name = "janus.github.accounts",
        CommandClass = "github",
        Summary = "列出服务器 GCM 中已知的 GitHub HTTPS 凭据账号",
        Readonly = true,
        Example = "janus.github.accounts",
        Handler = async _ =>
        {
            var accounts = await service.GetAccountsAsync();
            return CommandResult.Ok($"GCM 账号 {accounts.Count} 个", accounts);
        },
    };

    private static CommandDescriptor BuildTest(GitHubConnectionService service) => new()
    {
        Name = "janus.github.test",
        CommandClass = "github",
        Summary = "只读检测服务器 GitHub SSH 或 HTTPS 连接，不执行 push",
        Readonly = true,
        Example = "janus.github.test transport=auto timeout=15",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "transport",
                Description = "检测通道",
                Default = "auto",
                AllowedValues = ["auto", "ssh", "https"],
            },
            new ParameterSpec
            {
                Name = "timeout",
                Description = "超时秒数(1~120)",
                Type = ParamType.Int,
                Default = "15",
            },
        ],
        Handler = async ctx =>
        {
            var transport = (ctx.GetString("transport") ?? "auto").ToLowerInvariant();
            if (transport is not ("auto" or "ssh" or "https"))
                return CommandResult.Fail("transport 只允许 auto、ssh 或 https");
            var timeout = Math.Clamp(ctx.GetInt("timeout", 15), 1, 120);
            var status = await service.TestAsync(transport, timeout);
            return CommandResult.Ok($"GitHub 连接检测: {status.State}", status);
        },
    };

    private static CommandDescriptor BuildLogin(GitHubConnectionService service) => new()
    {
        Name = "janus.github.login",
        CommandClass = "github",
        Summary = "在服务器本机启动 Git Credential Manager 登录",
        Example = "janus.github.login account=octocat",
        Parameters = [new ParameterSpec { Name = "account", Description = "可选 GitHub 账号" }],
        ConfirmPrompt = _ => "确认在服务器本机启动 Git Credential Manager 登录？",
        Handler = async ctx =>
        {
            var result = await service.LoginAsync(ctx.GetString("account"));
            return result.Success
                ? CommandResult.Ok("GitHub 登录流程已启动")
                : CommandResult.Fail(GitHubRedactor.Redact(result.CombinedOutput));
        },
    };

    private static CommandDescriptor BuildLogout(GitHubConnectionService service) => new()
    {
        Name = "janus.github.logout",
        CommandClass = "github",
        Summary = "注销服务器本机的 GitHub HTTPS 凭据",
        Example = "janus.github.logout account=octocat",
        Parameters = [new ParameterSpec { Name = "account", Description = "GitHub 账号", Required = true }],
        ConfirmPrompt = ctx => $"确认注销服务器 GCM 账号 {ctx.RequireString("account")}？",
        Handler = async ctx =>
        {
            var result = await service.LogoutAsync(ctx.RequireString("account"), apply: true);
            return CommandResult.Ok(result.Action, result.After);
        },
    };

    private static CommandDescriptor BuildIdentity(GitHubConnectionService service) => new()
    {
        Name = "janus.github.identity",
        CommandClass = "github",
        Summary = "预览或修改 Git 提交身份",
        Example = "janus.github.identity name=Name email=name@example.com scope=repository apply=false",
        Parameters =
        [
            new ParameterSpec { Name = "name", Description = "Git user.name", Required = true },
            new ParameterSpec { Name = "email", Description = "Git user.email", Required = true },
            new ParameterSpec { Name = "scope", Description = "repository 或 global", Default = "repository", AllowedValues = ["repository", "global"] },
            new ParameterSpec { Name = "apply", Description = "true 时写入", Type = ParamType.Bool, Default = "false" },
        ],
        ConfirmPrompt = ctx => ctx.GetBool("apply") ? "确认修改服务器 Git 提交身份？" : null,
        Handler = async ctx =>
        {
            var result = await service.SetIdentityAsync(
                ctx.RequireString("name"), ctx.RequireString("email"),
                ctx.GetString("scope") ?? "repository", ctx.GetBool("apply"));
            return CommandResult.Ok(result.Action, result);
        },
    };

    private static CommandDescriptor BuildRemote(GitHubConnectionService service) => new()
    {
        Name = "janus.github.remote",
        CommandClass = "github",
        Summary = "预览或修改 origin 远端地址",
        Example = "janus.github.remote fetch=https://github.com/o/r.git push=https://github.com/o/r.git apply=false",
        Parameters =
        [
            new ParameterSpec { Name = "fetch", Description = "fetch URL" },
            new ParameterSpec { Name = "push", Description = "push URL" },
            new ParameterSpec { Name = "apply", Description = "true 时写入", Type = ParamType.Bool, Default = "false" },
        ],
        ConfirmPrompt = ctx => ctx.GetBool("apply") ? "确认修改服务器 origin？" : null,
        Handler = async ctx =>
        {
            var result = await service.SetRemoteAsync(
                ctx.GetString("fetch"), ctx.GetString("push"), ctx.GetBool("apply"));
            return CommandResult.Ok(result.Action, result);
        },
    };
}
