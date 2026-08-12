using System.Windows;
using System.Windows.Controls;
using HistoryVulcan.Core.Commands;
using HistoryJanus.GitHub;

namespace HistoryJanus.Views;

public partial class GitHubConnectionView : UserControl
{
    private readonly Func<GitHubConnectionService?> _serviceAccessor;
    private readonly Func<CommandBus?> _busAccessor;
    private bool _busy;
    private IReadOnlyList<GitHubDiagnosticStep> _diagnosticSteps = [];

    public GitHubConnectionView(Func<GitHubConnectionService?> serviceAccessor, Func<CommandBus?> busAccessor)
    {
        InitializeComponent();
        _serviceAccessor = serviceAccessor;
        _busAccessor = busAccessor;
        ViewKit.RunOnceOnLoaded(this, RefreshAsync);
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        await RunAsync(async service =>
        {
            var overview = await service.GetOverviewAsync();
            ConnectionText.Text = $"{overview.Origin.Transport} · {overview.Connection.State} · Git {overview.GitVersion}";
            OriginText.Text = overview.Origin.FetchUrl;
            CheckedText.Text = overview.LastCheckedAt.ToString("yyyy-MM-dd HH:mm:ss");
            AccountsBox.ItemsSource = overview.CredentialAccounts;
            if (overview.CredentialAccounts.Count > 0)
                AccountsBox.SelectedIndex = 0;
            KeysBox.ItemsSource = overview.Ssh.PublicKeys;
            if (overview.Ssh.PublicKeys.Count > 0)
                KeysBox.SelectedIndex = 0;
            UpdateKeyFingerprint();
            _diagnosticSteps = overview.Connection.Steps;
            IdentityName.Text = overview.EffectiveIdentity.Name;
            IdentityEmail.Text = overview.EffectiveIdentity.Email;
            IdentitySourceText.Text = overview.EffectiveIdentity.Source;
            FetchUrl.Text = overview.Origin.FetchUrl;
            PushUrl.Text = overview.Origin.PushUrl;
            RepositoryText.Text = $"{overview.Origin.Owner}/{overview.Origin.Repository}";
        });
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
        => await RefreshAsync();

    private async void OnTestClick(object sender, RoutedEventArgs e)
    {
        await RunAsync(async service =>
        {
            var connection = await service.TestAsync("auto", 15);
            ConnectionText.Text = $"{connection.Transport} · {connection.State}";
            _diagnosticSteps = connection.Steps;
            Notify($"检测完成：{connection.Transport} · {connection.State}");
        });
    }

    private void OnDiagnosticsClick(object sender, RoutedEventArgs e)
    {
        if (_diagnosticSteps.Count == 0)
        {
            Notify("尚无诊断步骤，请先点击检测。");
            return;
        }

        var lines = _diagnosticSteps.Select(step =>
            $"{step.Step}\t{step.State}\t{step.DurationMs}ms\t{step.Detail}");
        Notify("诊断步骤\n\n" + string.Join("\n", lines));
    }

    private async void OnLoginClick(object sender, RoutedEventArgs e)
    {
        var account = AccountsBox.SelectedItem is GitCredentialAccount selected ? selected.Account : null;
        var command = "janus.github.login" +
                      (string.IsNullOrWhiteSpace(account) ? "" : $" account={CommandParser.QuoteArg(account)}");
        if (await ExecuteCommandAsync(command)) await RefreshAsync();
    }

    private async void OnLogoutClick(object sender, RoutedEventArgs e)
    {
        if (AccountsBox.SelectedItem is not GitCredentialAccount account)
        {
            Notify("请先选择要注销的 GCM 账号");
            return;
        }
        if (await ExecuteCommandAsync($"janus.github.logout account={CommandParser.QuoteArg(account.Account)}"))
            await RefreshAsync();
    }

    private void OnKeySelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateKeyFingerprint();

    private void UpdateKeyFingerprint()
    {
        KeyFingerprintText.Text = KeysBox.SelectedItem is SshPublicKeyInfo key
            ? key.Fingerprint
            : "-";
    }

    private string IdentityScopeValue()
        => (IdentityScope.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "repository";

    private async void OnIdentityPreviewClick(object sender, RoutedEventArgs e)
    {
        await ExecuteCommandAsync(BuildIdentityCommand(apply: false));
    }

    private async void OnIdentityApplyClick(object sender, RoutedEventArgs e)
    {
        if (await ExecuteCommandAsync(BuildIdentityCommand(apply: true))) await RefreshAsync();
    }

    private async void OnRemotePreviewClick(object sender, RoutedEventArgs e)
    {
        await ExecuteCommandAsync(BuildRemoteCommand(apply: false));
    }

    private async void OnRemoteApplyClick(object sender, RoutedEventArgs e)
    {
        if (await ExecuteCommandAsync(BuildRemoteCommand(apply: true))) await RefreshAsync();
    }

    private string BuildIdentityCommand(bool apply)
        => $"janus.github.identity name={CommandParser.QuoteArg(IdentityName.Text.Trim())} " +
           $"email={CommandParser.QuoteArg(IdentityEmail.Text.Trim())} " +
           $"scope={IdentityScopeValue()} apply={apply.ToString().ToLowerInvariant()}";

    private string BuildRemoteCommand(bool apply)
        => $"janus.github.remote fetch={CommandParser.QuoteArg(FetchUrl.Text.Trim())} " +
           $"push={CommandParser.QuoteArg(PushUrl.Text.Trim())} apply={apply.ToString().ToLowerInvariant()}";

    private async Task<bool> ExecuteCommandAsync(string command)
    {
        if (_busy || _busAccessor() is not { } bus)
            return false;
        SetBusy(true);
        try
        {
            var result = await bus.ExecuteAsync(command, "UI");
            return result.Success;
        }
        catch (Exception ex)
        {
            Notify(GitHubRedactor.Redact(ex.Message), MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RunAsync(Func<GitHubConnectionService, Task> operation)
    {
        if (_busy || _serviceAccessor() is not { } service)
            return;
        SetBusy(true);
        try
        {
            await operation(service);
        }
        catch (Exception ex)
        {
            Notify(GitHubRedactor.Redact(ex.Message), MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool value)
    {
        _busy = value;
        IsEnabled = !value;
    }

    private static void Notify(string message, MessageBoxImage image = MessageBoxImage.Information)
        => MessageBox.Show(message, "github", MessageBoxButton.OK, image);

}
