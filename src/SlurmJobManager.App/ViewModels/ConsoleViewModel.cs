using System.Collections.ObjectModel;
using System.Windows.Input;
using SlurmJobManager.Core.Interfaces;

namespace SlurmJobManager.App.ViewModels;

/// <summary>
/// Embedded remote-command console with ↑/↓ history (max 20 entries).
/// </summary>
public sealed class ConsoleViewModel : ViewModelBase
{
    private readonly ISshClientService _ssh;
    private const int MaxHistory = 20;

    private string _commandInput = string.Empty;
    private bool _isBusy;
    private int _historyIndex = -1;

    public string CommandInput { get => _commandInput; set => SetField(ref _commandInput, value); }
    public bool IsBusy         { get => _isBusy;        private set => SetField(ref _isBusy, value); }

    public ObservableCollection<string> OutputLines { get; } = new();
    public List<string> CommandHistory { get; } = new();

    public ICommand ExecuteCommand     { get; }
    public ICommand ClearCommand       { get; }
    public ICommand HistoryUpCommand   { get; }
    public ICommand HistoryDownCommand { get; }

    public ConsoleViewModel(ISshClientService ssh)
    {
        _ssh = ssh ?? throw new ArgumentNullException(nameof(ssh));

        ExecuteCommand     = new AsyncRelayCommand(ExecuteAsync, () => !IsBusy);
        ClearCommand       = new RelayCommand(() => OutputLines.Clear());
        HistoryUpCommand   = new RelayCommand(HistoryUp);
        HistoryDownCommand = new RelayCommand(HistoryDown);
    }

    private async Task ExecuteAsync(CancellationToken ct)
    {
        var cmd = CommandInput.Trim();
        if (string.IsNullOrEmpty(cmd)) return;

        if (!_ssh.IsConnected)
        {
            OutputLines.Add("[error] Not connected. Please connect first.");
            return;
        }

        AddToHistory(cmd);
        CommandInput  = string.Empty;
        _historyIndex = -1;

        OutputLines.Add($"$ {cmd}");
        IsBusy = true;
        try
        {
            var (stdout, stderr, exitCode) = await _ssh.ExecuteAsync(cmd, ct);

            foreach (var line in stdout.Split('\n'))
                if (line.Length > 0) OutputLines.Add(line);

            if (!string.IsNullOrWhiteSpace(stderr))
                foreach (var line in stderr.Split('\n'))
                    if (line.Length > 0) OutputLines.Add($"[stderr] {line}");

            if (exitCode != 0)
                OutputLines.Add($"[exit {exitCode}]");
        }
        catch (Exception ex)
        {
            OutputLines.Add($"[error] {ex.Message}");
        }
        finally { IsBusy = false; }
    }

    private void AddToHistory(string cmd)
    {
        CommandHistory.Remove(cmd);
        CommandHistory.Insert(0, cmd);
        if (CommandHistory.Count > MaxHistory)
            CommandHistory.RemoveAt(CommandHistory.Count - 1);
    }

    private void HistoryUp()
    {
        if (CommandHistory.Count == 0) return;
        _historyIndex = Math.Min(_historyIndex + 1, CommandHistory.Count - 1);
        CommandInput  = CommandHistory[_historyIndex];
    }

    private void HistoryDown()
    {
        if (_historyIndex <= 0) { _historyIndex = -1; CommandInput = string.Empty; return; }
        _historyIndex--;
        CommandInput = CommandHistory[_historyIndex];
    }
}
