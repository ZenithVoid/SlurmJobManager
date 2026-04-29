using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows.Input;
using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Models;

namespace SlurmJobManager.App.ViewModels;

/// <summary>
/// Embedded remote-command console with ↑/↓ history (max 50 entries),
/// stderr/stdout visual distinction, execution time, exit code display,
/// and a cancel button for the currently running command.
/// </summary>
public sealed class ConsoleViewModel : ViewModelBase, IDisposable
{
    private readonly ISshClientService _ssh;
    private readonly IAppLogger?       _logger;
    private readonly ConnectionViewModel? _connection;
    private readonly AppSettings      _settings;
    private const int MaxHistory = 50;

    private string _commandInput = string.Empty;
    private bool _isBusy;
    private bool _isConnected;
    private int _historyIndex = -1;
    private CancellationTokenSource? _executeCts;

    public string CommandInput { get => _commandInput; set => SetField(ref _commandInput, value); }
    public bool IsBusy         { get => _isBusy;        private set => SetField(ref _isBusy, value); }

    /// <summary>True when the SSH connection is active — used to drive the connection-hint banner.</summary>
    public bool IsConnected    { get => _isConnected;   private set => SetField(ref _isConnected, value); }

    public ObservableCollection<ConsoleLine> OutputLines { get; } = new();
    public List<string> CommandHistory { get; } = new();

    public ICommand ExecuteCommand     { get; }
    public ICommand CancelCommand      { get; }
    public ICommand ClearCommand       { get; }
    public ICommand HistoryUpCommand   { get; }
    public ICommand HistoryDownCommand { get; }
    public ICommand CopyOutputCommand  { get; }

    public ConsoleViewModel(ISshClientService ssh, IAppLogger? logger = null, ConnectionViewModel? connection = null, AppSettings? settings = null)
    {
        _ssh        = ssh    ?? throw new ArgumentNullException(nameof(ssh));
        _logger     = logger;
        _connection = connection;
        _settings   = settings ?? new AppSettings();

        // Subscribe before reading the initial value to avoid a race between
        // subscribing and querying, then seed from the connection view-model.
        if (_connection != null)
        {
            _connection.PropertyChanged += OnConnectionPropertyChanged;
            _isConnected = _connection.IsConnected;
        }
        else
        {
            _isConnected = _ssh.IsConnected;
        }

        ExecuteCommand     = new AsyncRelayCommand(ExecuteAsync, () => !IsBusy);
        CancelCommand      = new RelayCommand(CancelExecution,  () => IsBusy);
        ClearCommand       = new RelayCommand(() => OutputLines.Clear());
        HistoryUpCommand   = new RelayCommand(HistoryUp);
        HistoryDownCommand = new RelayCommand(HistoryDown);
        CopyOutputCommand  = new RelayCommand(CopyOutput);
    }

    private async Task ExecuteAsync(CancellationToken ct)
    {
        var cmd = SanitizeCommand(CommandInput);
        if (string.IsNullOrEmpty(cmd))
        {
            if (!string.IsNullOrWhiteSpace(CommandInput))
                AppendLine(ConsoleLine.Error("[error] 命令包含非法字符，已拒绝执行。"));
            return;
        }

        if (!_ssh.IsConnected)
        {
            AppendLine(ConsoleLine.Error("未建立 SSH 连接，请先在连接页面建立连接。"));
            return;
        }

        AddToHistory(cmd);
        CommandInput  = string.Empty;
        _historyIndex = -1;

        AppendLine(ConsoleLine.Command($"$ {cmd}"));
        IsBusy = true;
        // Manage lifetimes explicitly: dispose linked source before the source it wraps
        var timeoutCts = new CancellationTokenSource(_settings.CommandTimeout);
        _executeCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var (stdout, stderr, exitCode) = await _ssh.ExecuteAsync(cmd, _executeCts.Token);
            sw.Stop();

            foreach (var line in stdout.Split('\n'))
                if (line.Length > 0) AppendLine(ConsoleLine.Stdout(line));

            if (!string.IsNullOrWhiteSpace(stderr))
                foreach (var line in stderr.Split('\n'))
                    if (line.Length > 0) AppendLine(ConsoleLine.Stderr(line));

            var meta = $"[exit {exitCode} | {sw.ElapsedMilliseconds} ms]";
            AppendLine(ConsoleLine.Meta(meta));
            _logger?.Debug($"Console cmd '{cmd}': exit {exitCode}, {sw.ElapsedMilliseconds} ms");
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            sw.Stop();
            AppendLine(ConsoleLine.Error($"[timeout] 命令超时（{_settings.CommandTimeout.TotalSeconds:0}s）已取消。"));
            _logger?.Warning($"Console cmd '{cmd}' timed out after {sw.ElapsedMilliseconds} ms");
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            AppendLine(ConsoleLine.Meta($"[cancelled after {sw.ElapsedMilliseconds} ms]"));
        }
        catch (Exception ex)
        {
            sw.Stop();
            AppendLine(ConsoleLine.Error($"[error] {ex.Message}"));
            _logger?.Error($"Console cmd '{cmd}' failed", ex);
        }
        finally
        {
            // Dispose linked source first, then the source it links to
            _executeCts.Dispose();
            _executeCts = null;
            timeoutCts.Dispose();
            SetBusy(false);
        }
    }

    /// <summary>
    /// Strips control characters (except tab) that could cause terminal injection or crashes.
    /// Returns null-equivalent empty string if the resulting command is blank.
    /// </summary>
    private static readonly Regex ControlCharRegex =
        new(@"[\x00-\x08\x0A-\x1F\x7F]", RegexOptions.Compiled);

    private static string SanitizeCommand(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        // Remove characters below 0x20 except horizontal tab (0x09), and remove DEL (0x7F)
        return ControlCharRegex.Replace(input, string.Empty).Trim();
    }

    private void CancelExecution()
    {
        _executeCts?.Cancel();
        AppendLine(ConsoleLine.Meta("[cancelling…]"));
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

    private void CopyOutput()
    {
        var text = string.Join(Environment.NewLine, OutputLines.Select(l => l.Text));
        System.Windows.Clipboard.SetText(text);
    }

    private void OnConnectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConnectionViewModel.IsConnected) && _connection != null)
            IsConnected = _connection.IsConnected;
    }

    public void Dispose()
    {
        if (_connection != null)
            _connection.PropertyChanged -= OnConnectionPropertyChanged;
        _executeCts?.Cancel();
        _executeCts?.Dispose();
    }

    // ── Thread-safe UI helpers ───────────────────────────────────────────────

    /// <summary>
    /// Appends a line to <see cref="OutputLines"/> ensuring execution on the UI thread.
    /// Protects against the rare case where an SSH library callback or continuation
    /// fires on a thread-pool thread.
    /// </summary>
    private void AppendLine(ConsoleLine line)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.InvokeAsync(() => OutputLines.Add(line));
        else
            OutputLines.Add(line);
    }

    /// <summary>
    /// Sets <see cref="IsBusy"/> on the UI thread to keep WPF bindings happy.
    /// </summary>
    private void SetBusy(bool value)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.InvokeAsync(() => IsBusy = value);
        else
            IsBusy = value;
    }
}
