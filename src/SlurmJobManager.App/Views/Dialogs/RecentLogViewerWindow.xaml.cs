using System.Windows;
using SlurmJobManager.App.Services;

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
        catch (Exception ex)
        {
            var format = Application.Current?.TryFindResource("Settings.CopyDataDirFailedFormat") as string
                         ?? "Failed to copy log content: {0}";
            ToastService.Instance.Error(string.Format(format, ex.Message));
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();
}
