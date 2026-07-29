using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SlurmPilot.App.Controls;
using SlurmPilot.App.ViewModels;

namespace SlurmPilot.App.Views;

public partial class ConsoleView : UserControl
{
    private ConsoleViewModel? _subscribedVm;
    private readonly object _outputSync = new();
    private readonly System.Text.StringBuilder _outputBuffer = new();
    private bool _outputFlushQueued;

    public ConsoleView()
    {
        InitializeComponent();
        Loaded += ConsoleView_Loaded;
        Unloaded += ConsoleView_Unloaded;
        DataContextChanged += (_, _) => ResubscribeViewModel();
    }

    private async void ConsoleView_Loaded(object sender, RoutedEventArgs e)
    {
        ResubscribeViewModel();
        FocusTerminal();
        if (DataContext is ConsoleViewModel vm)
            await vm.EnsureInteractiveShellReadyAsync();
    }

    private void ConsoleView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribedVm == null) return;
        _subscribedVm.TerminalOutputReceived -= OnTerminalOutputReceived;
        _subscribedVm.FocusRequested -= OnFocusRequested;
        _subscribedVm.ClearRequested -= OnClearRequested;
        _subscribedVm = null;
    }

    private void ResubscribeViewModel()
    {
        if (_subscribedVm != null)
        {
            _subscribedVm.TerminalOutputReceived -= OnTerminalOutputReceived;
            _subscribedVm.FocusRequested -= OnFocusRequested;
            _subscribedVm.ClearRequested -= OnClearRequested;
        }

        _subscribedVm = DataContext as ConsoleViewModel;
        if (_subscribedVm == null) return;

        _subscribedVm.TerminalOutputReceived += OnTerminalOutputReceived;
        _subscribedVm.FocusRequested += OnFocusRequested;
        _subscribedVm.ClearRequested += OnClearRequested;
    }

    private void OnTerminalOutputReceived(object? sender, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        lock (_outputSync)
        {
            _outputBuffer.Append(text);
            if (_outputFlushQueued)
                return;
            _outputFlushQueued = true;
        }

        _ = Dispatcher.InvokeAsync(FlushBufferedOutput, System.Windows.Threading.DispatcherPriority.Render);
    }

    private void OnFocusRequested(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
            FocusTerminal();
        else
            Dispatcher.BeginInvoke(() =>
            {
                try { FocusTerminal(); }
                catch { /* best effort */ }
            });
    }

    private void OnClearRequested(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
            TerminalSurface.Clear();
        else
            Dispatcher.BeginInvoke(() =>
            {
                try { TerminalSurface.Clear(); }
                catch { /* best effort */ }
            });
    }

    private void FocusTerminal()
    {
        TerminalSurface.Focus();
        Keyboard.Focus(TerminalSurface);
    }

    public void RequestTerminalFocus() => FocusTerminal();

    private void TerminalSurface_InputGenerated(object sender, string input)
    {
        if (DataContext is not ConsoleViewModel vm) return;
        if (vm.TryForwardTerminalInput(input))
            return;
        _ = ForwardTerminalInputSafelyAsync(vm, input);
    }

    private async void TerminalSurface_TerminalResized(object sender, TerminalResizedEventArgs e)
    {
        if (DataContext is not ConsoleViewModel vm) return;
        await vm.ResizeTerminalAsync(e.Cols, e.Rows);
    }

    private void CopyTerminal_Click(object sender, RoutedEventArgs e)
    {
        TerminalSurface.CopySelectionOrVisibleTextToClipboard();
    }

    private void PasteTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (TerminalSurface.PasteFromClipboard())
            FocusTerminal();
    }

    private void CmdInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ConsoleViewModel vm) return;

        switch (e.Key)
        {
            case Key.Enter:
                vm.ExecuteCommand.Execute(null);
                e.Handled = true;
                FocusTerminal();
                break;
            case Key.Up:
                vm.HistoryUpCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Down:
                vm.HistoryDownCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void FlushBufferedOutput()
    {
        string payload;
        lock (_outputSync)
        {
            payload = _outputBuffer.ToString();
            _outputBuffer.Clear();
            _outputFlushQueued = false;
        }

        if (payload.Length == 0)
            return;

        try { TerminalSurface.Write(payload); }
        catch { /* best effort */ }
    }

    private static async Task ForwardTerminalInputSafelyAsync(ConsoleViewModel vm, string input)
    {
        try
        {
            await vm.ForwardTerminalInputAsync(input);
        }
        catch
        {
            // best effort: keep terminal input path resilient without surfacing UI exceptions
        }
    }
}
