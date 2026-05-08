using System.Windows;

namespace SlurmJobManager.App.Views.Dialogs;

public partial class RecentLogViewerWindow : Window
{
    public RecentLogViewerWindow(string viewerTitle, string filePath, string logContent)
    {
        InitializeComponent();
        ViewerTitle = viewerTitle;
        FilePath = filePath;
        LogContent = logContent;
        DataContext = this;
    }

    public string ViewerTitle { get; }
    public string FilePath { get; }
    public string LogContent { get; }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(LogContent ?? string.Empty);
        }
        catch
        {
            // best effort
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();
}
