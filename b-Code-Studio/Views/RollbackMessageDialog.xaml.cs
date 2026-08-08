using System.Windows;
using System.Windows.Controls;

namespace HistoryJanus.Views;

public partial class RollbackMessageDialog : Window
{
    public string CommitMessage => MessageBox.Text.Trim();

    public RollbackMessageDialog(string shortSha, string subject)
    {
        InitializeComponent();
        TargetText.Text = $"目标节点：{shortSha}  {subject}";
        MessageBox.Text = $"恢复到 {shortSha}：{subject}";
        MessageBox.SelectAll();
        MessageBox.Focus();
    }

    private void OnMessageChanged(object sender, TextChangedEventArgs e)
    {
        if (ConfirmButton != null)
            ConfirmButton.IsEnabled = CommitMessage.Length > 0;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (CommitMessage.Length == 0)
            return;
        DialogResult = true;
    }
}
