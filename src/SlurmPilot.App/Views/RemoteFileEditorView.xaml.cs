using System.Windows;
using System.ComponentModel;
using System.Windows.Input;
using SlurmPilot.App.ViewModels;
using SlurmPilot.App.ViewModels.Dialogs;
using SlurmPilot.App.Views.Dialogs;

namespace SlurmPilot.App.Views;

public partial class RemoteFileEditorView : Window
{
    private bool _allowClose;
    private bool _syncingFromViewModel;
    private bool _syncingFromEditor;
    private RemoteFileEditorViewModel? _boundViewModel;

    public RemoteFileEditorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Editor.TextChanged += Editor_TextChanged;
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

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_boundViewModel is not null)
            _boundViewModel.PropertyChanged -= ViewModel_PropertyChanged;

        _boundViewModel = e.NewValue as RemoteFileEditorViewModel;
        if (_boundViewModel is null)
            return;

        _boundViewModel.PropertyChanged += ViewModel_PropertyChanged;
        SyncEditorFromViewModel(_boundViewModel);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(RemoteFileEditorViewModel.Content), StringComparison.Ordinal))
            return;
        if (sender is not RemoteFileEditorViewModel vm)
            return;

        SyncEditorFromViewModel(vm);
    }

    private void SyncEditorFromViewModel(RemoteFileEditorViewModel vm)
    {
        if (_syncingFromEditor)
            return;

        var vmContent = vm.Content ?? string.Empty;
        if (string.Equals(Editor.Text, vmContent, StringComparison.Ordinal))
            return;

        _syncingFromViewModel = true;
        try
        {
            Editor.Text = vmContent;
        }
        finally
        {
            _syncingFromViewModel = false;
        }
    }

    private void Editor_TextChanged(object? sender, EventArgs e)
    {
        if (_syncingFromViewModel)
            return;
        if (DataContext is not RemoteFileEditorViewModel vm)
            return;

        var editorText = Editor.Text ?? string.Empty;
        if (string.Equals(vm.Content, editorText, StringComparison.Ordinal))
            return;

        _syncingFromEditor = true;
        try
        {
            vm.Content = editorText;
        }
        finally
        {
            _syncingFromEditor = false;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        if (WindowState == WindowState.Maximized)
            WindowState = WindowState.Normal;
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { }
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void BtnCloseChrome_Click(object sender, RoutedEventArgs e)
        => Close();

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not RemoteFileEditorViewModel vm)
            return;
        if (vm.IsBusy)
            return;

        var editorText = Editor.Text ?? string.Empty;
        await vm.SaveChangesAsync(editorText);
    }

    private async void BtnReload_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not RemoteFileEditorViewModel vm)
            return;
        if (vm.IsBusy)
            return;

        if (vm.IsTextMode && !string.Equals(Editor.Text, vm.Content, StringComparison.Ordinal))
            vm.Content = Editor.Text;

        var discardUnsavedChanges = true;
        if (vm.IsDirty)
        {
            var prompt = Application.Current?.TryFindResource("RemoteEditor.ReloadUnsavedPrompt") as string
                         ?? "当前存在未保存修改，重载将覆盖这些内容。是否继续？";
            var title = Application.Current?.TryFindResource("RemoteEditor.ReloadTitle") as string
                        ?? "重新加载文件";
            var confirmText = Application.Current?.TryFindResource("RemoteEditor.BtnReload") as string ?? "重新加载";
            var cancelText = Application.Current?.TryFindResource("Btn.Cancel") as string ?? "取消";
            var confirmVm = new ConfirmationDialogViewModel(
                title: title,
                message: prompt,
                confirmButtonText: confirmText,
                cancelButtonText: cancelText,
                isWarning: true);
            var dialog = new ConfirmationDialogView { DataContext = confirmVm, Owner = this };
            if (dialog.ShowDialog() != true)
                return;
        }

        await vm.ReloadAsync(Editor.Text, discardUnsavedChanges);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        if (DataContext is not RemoteFileEditorViewModel vm) return;
        if (vm.IsTextMode && !string.Equals(Editor.Text, vm.Content, StringComparison.Ordinal))
            vm.Content = Editor.Text;
        if (!vm.IsDirty) return;

        e.Cancel = true;
        _ = ConfirmCloseAsync(vm);
    }

    private async Task ConfirmCloseAsync(RemoteFileEditorViewModel vm)
    {
        var text = Application.Current?.TryFindResource("RemoteEditor.UnsavedPrompt") as string
                   ?? "检测到未保存修改。是否保存后关闭？";
        var title = Application.Current?.TryFindResource("RemoteEditor.UnsavedTitle") as string
                    ?? "未保存更改";
        var saveAndClose = Application.Current?.TryFindResource("RemoteEditor.BtnSaveAndClose") as string ?? "保存并关闭";
        var discardAndClose = Application.Current?.TryFindResource("RemoteEditor.BtnDiscardAndClose") as string ?? "不保存直接关闭";
        var cancel = Application.Current?.TryFindResource("Btn.Cancel") as string ?? "取消";

        var confirmVm = new ConfirmationDialogViewModel(
            title: title,
            message: text,
            confirmButtonText: saveAndClose,
            cancelButtonText: cancel,
            discardButtonText: discardAndClose,
            isWarning: true);
        var dialog = new ConfirmationDialogView { DataContext = confirmVm, Owner = this };
        var result = dialog.ShowDialog();

        // result != true means Cancel (Cancel button, X button, or Escape)
        if (result != true)
            return;

        if (!dialog.DiscardChosen)
        {
            // Confirm (Save & Close) was chosen
            var saved = await vm.SaveChangesAsync(Editor.Text);
            if (!saved)
                return;
        }
        // DiscardChosen == true means close without saving

        _allowClose = true;
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        DataContextChanged -= OnDataContextChanged;
        Editor.TextChanged -= Editor_TextChanged;
        if (_boundViewModel is not null)
        {
            _boundViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _boundViewModel = null;
        }
        Closing -= OnClosing;
        Closed  -= OnClosed;
    }
}
