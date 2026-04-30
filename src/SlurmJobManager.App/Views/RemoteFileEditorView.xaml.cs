using System.Windows;
using System.ComponentModel;
using SlurmJobManager.App.ViewModels;

namespace SlurmJobManager.App.Views;

public partial class RemoteFileEditorView : Window
{
    private bool _allowClose;

    public RemoteFileEditorView()
    {
        InitializeComponent();
        Closing += OnClosing;
        Closed  += OnClosed;
        Loaded += (_, _) =>
        {
            Editor.TextArea.TextView.CurrentLineBackground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(80, 67, 80, 108));
            Editor.TextArea.TextView.CurrentLineBorder = null;
            Editor.LineNumbersForeground = (System.Windows.Media.Brush?)FindResource("TextMutedBrush");
            Editor.Options.HighlightCurrentLine = true;
        };
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        if (DataContext is not RemoteFileEditorViewModel vm || !vm.IsDirty) return;

        e.Cancel = true;
        _ = ConfirmCloseAsync(vm);
    }

    private async Task ConfirmCloseAsync(RemoteFileEditorViewModel vm)
    {
        var text = Application.Current?.TryFindResource("RemoteEditor.UnsavedPrompt") as string
                   ?? "检测到未保存修改。是否保存后关闭？";
        var title = Application.Current?.TryFindResource("RemoteEditor.UnsavedTitle") as string
                    ?? "未保存更改";

        var result = MessageBox.Show(
            text,
            title,
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Cancel)
            return;

        if (result == MessageBoxResult.Yes)
        {
            var saved = await vm.SaveChangesAsync();
            if (!saved)
                return;
        }

        _allowClose = true;
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closing -= OnClosing;
        Closed  -= OnClosed;
    }
}
