using System.Windows;

namespace HistoryJanus.Views;

public partial class HistoryPreviewDialog : Window
{
    public HistoryPreviewDialog(string title, string summary, string content)
    {
        InitializeComponent();
        Title = title;
        SummaryText.Text = summary;
        ContentBox.Text = string.IsNullOrWhiteSpace(content) ? "(无差异内容)" : content;
    }
}
