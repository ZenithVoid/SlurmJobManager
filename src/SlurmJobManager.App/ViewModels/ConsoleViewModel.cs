using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using SlurmJobManager.App.Services;
using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Models;

namespace SlurmJobManager.App.ViewModels;

public sealed class ConsoleViewModel : ViewModelBase, IDisposable
{
    private readonly ISshClientService _ssh;
    private readonly IAppLogger? _logger;
    private readonly ConnectionViewModel? _connection;
    private readonly AppSettings _settings;
    private readonly object _markerSync = new();

    private const int MaxHistory = 50;
    private const string CwdMarker = "__SJM_CWD__";
    private static readonly Regex MarkerRegex = new($"{CwdMarker}(?<cwd>[^\\r\\n]*)\\r?\\n", RegexOptions.Compiled);
    private static readonly Regex ControlCharRegex = new(@"[\x00-\x08\x0A-\x1F\x7F]", RegexOptions.Compiled);

    private string _commandInput = string.Empty;
    private bool _isBusy;
    private bool _isConnected;
    private string _homeDirectory = string.Empty;
    private string _currentWorkingDirectory = string.Empty;
    private int _historyIndex = -1;
    private string _historyDraftInput = string.Empty;
    private string _markerCarry = string.Empty;
    private IInteractiveShellSession? _shellSession;
    private int _terminalCols = 120;
    private int _terminalRows = 36;
    private bool _isInitializingSession;

    public event EventHandler<string>? TerminalOutputReceived;
    public event EventHandler? FocusRequested;
    public event EventHandler? ClearRequested;

    public string CommandInput { get => _commandInput; set => SetField(ref _commandInput, value); }
    public bool IsBusy { get => _isBusy; private set => SetField(ref _isBusy, value); }
    public bool IsConnected { get => _isConnected; private set => SetField(ref _isConnected, value); }

    public string CurrentWorkingDirectoryDisplay => GetDisplayWorkingDirectory();
    public string PromptText => $"{GetPromptUser()}@{GetPromptHost()}:{CurrentWorkingDirectoryDisplay}$ ";
    public string ConsoleStatusSummary => string.Format(
        L("Console.StatusSummary"),
        IsConnected ? L("Console.StatusConnected") : L("Console.StatusDisconnected"),
        CurrentWorkingDirectoryDisplay,
        IsBusy ? L("Console.StatusBusy") : L("Console.StatusIdle"));

    public List<string> CommandHistory { get; } = new();

    public ICommand ExecuteCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand ResetSessionCommand { get; }
    public ICommand HistoryUpCommand { get; }
    public ICommand HistoryDownCommand { get; }

    public ConsoleViewModel(ISshClientService ssh, IAppLogger? logger = null, ConnectionViewModel? connection = null, AppSettings? settings = null)
    {
        _ssh = ssh ?? throw new ArgumentNullException(nameof(ssh));
        _logger = logger;
        _connection = connection;
        _settings = settings ?? new AppSettings();

        if (_connection != null)
        {
            _connection.PropertyChanged += OnConnectionPropertyChanged;
            _isConnected = _connection.IsConnected;
            NotifyPromptContextChanged();
        }
        else
        {
            _isConnected = _ssh.IsConnected;
        }

        ExecuteCommand = new AsyncRelayCommand(ExecuteAsync, () => IsConnected);
        CancelCommand = new RelayCommand(CancelExecution, () => IsConnected);
        ClearCommand = new RelayCommand(() => ClearRequested?.Invoke(this, EventArgs.Empty));
        ResetSessionCommand = new AsyncRelayCommand(ResetSessionAsync, () => IsConnected);
        HistoryUpCommand = new RelayCommand(HistoryUp);
        HistoryDownCommand = new RelayCommand(HistoryDown);
    }

    public async Task EnsureInteractiveShellReadyAsync(CancellationToken ct = default)
    {
        await EnsureShellSessionAsync(ct);
        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task ExecuteAsync(CancellationToken ct)
    {
        var cmd = SanitizeCommand(CommandInput);
        if (string.IsNullOrEmpty(cmd))
        {
            if (!string.IsNullOrWhiteSpace(CommandInput))
                PublishSystemMessage(L("Console.ErrInvalidCommand"));
            return;
        }

        if (!await EnsureShellSessionAsync(ct)) return;

        AddToHistory(cmd);
        CommandInput = string.Empty;
        _historyDraftInput = string.Empty;
        _historyIndex = -1;

        SetBusy(true);
        await SendRawInputAsync($"{cmd}\n", ct);
        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    public async Task ForwardTerminalInputAsync(string data, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(data)) return;
        if (!await EnsureShellSessionAsync(ct)) return;

        if (data.Contains('\r') || data.Contains('\n'))
            SetBusy(true);

        await SendRawInputAsync(data, ct);
    }

    public async Task ResizeTerminalAsync(int cols, int rows, CancellationToken ct = default)
    {
        _terminalCols = Math.Max(2, cols);
        _terminalRows = Math.Max(2, rows);

        var session = _shellSession;
        if (session?.IsOpen != true) return;

        try
        {
            await session.ResizeAsync(_terminalCols, _terminalRows, ct);
        }
        catch (Exception ex)
        {
            _logger?.Warning($"Console terminal resize failed: {ex.Message}");
        }
    }

    public async Task<bool> OpenAtDirectoryAsync(string remoteDirectory, CancellationToken ct = default)
    {
        if (!_ssh.IsConnected)
        {
            PublishSystemMessage(L("Console.ErrNotConnected"));
            return false;
        }

        if (string.IsNullOrWhiteSpace(remoteDirectory))
        {
            PublishSystemMessage(L("Console.ErrNoTargetDirectory"));
            return false;
        }

        if (!await EnsureShellSessionAsync(ct)) return false;

        await EnsureHomeDirectoryLoadedAsync(ct);
        var target = NormalizeRemotePath(ExpandHomePath(remoteDirectory));
        if (string.IsNullOrWhiteSpace(target))
            return false;

        if (IsBusy)
        {
            var askText = string.Format(L("Console.BusySwitchDirectoryPrompt"), CollapseHomePath(target));
            var askTitle = L("Console.BusySwitchDirectoryTitle");
            var decision = MessageBox.Show(askText, askTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (decision != MessageBoxResult.Yes)
                return false;

            await SendRawInputAsync("\u0003", ct);
            await Task.Delay(120, ct);
        }

        SetBusy(true);
        await SendRawInputAsync($"cd -- {EscapeShellArg(target)}\n", ct);
        FocusRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private async Task<bool> EnsureShellSessionAsync(CancellationToken ct)
    {
        if (_shellSession?.IsOpen == true) return true;
        if (!_ssh.IsConnected)
        {
            PublishSystemMessage(L("Console.ErrNotConnected"));
            return false;
        }

        if (_isInitializingSession) return false;
        _isInitializingSession = true;
        try
        {
            var session = await _ssh.StartInteractiveShellSessionAsync("xterm-256color", _terminalCols, _terminalRows, ct);
            session.OutputReceived += OnShellOutputReceived;
            session.Closed += OnShellClosed;
            _shellSession = session;
            lock (_markerSync)
                _markerCarry = string.Empty;

            await EnsureHomeDirectoryLoadedAsync(ct);
            await InitializeShellPromptAsync(ct);
            SetBusy(false);
            NotifyPromptContextChanged();
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Error("Failed to start interactive shell session.", ex);
            PublishSystemMessage($"[error] {ex.Message}");
            return false;
        }
        finally
        {
            _isInitializingSession = false;
        }
    }

    private async Task InitializeShellPromptAsync(CancellationToken ct)
    {
        var initScript = string.Join(
            "\n",
            "export TERM=xterm-256color",
            "export PS1='\\u@\\h:\\w$ '",
            $"PROMPT_COMMAND='printf \"{CwdMarker}%s\\\\n\" \"$PWD\"'",
            $"printf \"{CwdMarker}%s\\n\" \"$PWD\"") + "\n";

        await SendRawInputAsync(initScript, ct);
    }

    private async Task SendRawInputAsync(string data, CancellationToken ct)
    {
        var session = _shellSession;
        if (session?.IsOpen != true) return;

        try
        {
            await session.WriteAsync(data, ct);
        }
        catch (Exception ex)
        {
            _logger?.Warning($"Console send input failed: {ex.Message}");
        }
    }

    private async Task ResetSessionAsync(CancellationToken ct)
    {
        await CloseShellSessionAsync();
        await EnsureShellSessionAsync(ct);
        ClearRequested?.Invoke(this, EventArgs.Empty);
        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CancelExecution()
    {
        _ = ForwardTerminalInputAsync("\u0003");
        PublishSystemMessage(L("Console.Cancelling"));
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
        if (_historyIndex == -1)
            _historyDraftInput = CommandInput;

        _historyIndex = Math.Min(_historyIndex + 1, CommandHistory.Count - 1);
        CommandInput = CommandHistory[_historyIndex];
    }

    private void HistoryDown()
    {
        if (_historyIndex <= 0)
        {
            _historyIndex = -1;
            CommandInput = _historyDraftInput;
            return;
        }

        _historyIndex--;
        CommandInput = CommandHistory[_historyIndex];
    }

    private async void OnConnectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_connection == null) return;

        if (e.PropertyName is nameof(ConnectionViewModel.IsConnected))
        {
            IsConnected = _connection.IsConnected;
            if (IsConnected)
                await EnsureInteractiveShellReadyAsync();
            else
            {
                await CloseShellSessionAsync();
                ResetShellContext();
            }
        }

        if (e.PropertyName is nameof(ConnectionViewModel.Username) or nameof(ConnectionViewModel.Host))
            NotifyPromptContextChanged();
    }

    private void OnShellOutputReceived(object? sender, string output)
    {
        if (string.IsNullOrEmpty(output)) return;

        var filtered = FilterMarkers(output);
        if (filtered.Length > 0)
            TerminalOutputReceived?.Invoke(this, filtered);
    }

    private void OnShellClosed(object? sender, EventArgs e)
    {
        if (sender is not IInteractiveShellSession session) return;
        if (!ReferenceEquals(session, _shellSession)) return;
        _shellSession = null;
        SetBusy(false);
    }

    private string FilterMarkers(string chunk)
    {
        lock (_markerSync)
        {
            var combined = _markerCarry + chunk;
            var sb = new StringBuilder();
            var last = 0;

            foreach (Match m in MarkerRegex.Matches(combined))
            {
                sb.Append(combined, last, m.Index - last);
                last = m.Index + m.Length;

                var cwd = NormalizeRemotePath(m.Groups["cwd"].Value);
                if (!string.IsNullOrWhiteSpace(cwd))
                    SetCurrentWorkingDirectory(cwd);
                SetBusy(false);
            }

            var remainder = combined[last..];
            var markerStart = remainder.LastIndexOf(CwdMarker, StringComparison.Ordinal);
            if (markerStart >= 0)
            {
                sb.Append(remainder[..markerStart]);
                _markerCarry = remainder[markerStart..];
            }
            else
            {
                sb.Append(remainder);
                _markerCarry = string.Empty;
            }

            return sb.ToString();
        }
    }

    private void SetCurrentWorkingDirectory(string cwd)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(() =>
            {
                _currentWorkingDirectory = cwd;
                NotifyPromptContextChanged();
            });
        }
        else
        {
            _currentWorkingDirectory = cwd;
            NotifyPromptContextChanged();
        }
    }

    private async Task EnsureHomeDirectoryLoadedAsync(CancellationToken ct)
    {
        if (!_ssh.IsConnected || !string.IsNullOrWhiteSpace(_homeDirectory)) return;
        var home = await _ssh.GetHomeDirectoryAsync(ct);
        _homeDirectory = NormalizeRemotePath(home);
        if (string.IsNullOrWhiteSpace(_currentWorkingDirectory))
            _currentWorkingDirectory = _homeDirectory;
        NotifyPromptContextChanged();
    }

    private async Task CloseShellSessionAsync()
    {
        var session = _shellSession;
        _shellSession = null;
        if (session == null) return;

        try
        {
            session.OutputReceived -= OnShellOutputReceived;
            session.Closed -= OnShellClosed;
            await session.CloseAsync();
            session.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.Warning($"Console shell session close failed: {ex.Message}");
        }
    }

    private void PublishSystemMessage(string message)
    {
        TerminalOutputReceived?.Invoke(this, $"{message}{Environment.NewLine}");
    }

    private static string SanitizeCommand(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        return ControlCharRegex.Replace(input, string.Empty).Trim();
    }

    private void SetBusy(bool value)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            _ = dispatcher.InvokeAsync(() =>
            {
                IsBusy = value;
                OnPropertyChanged(nameof(ConsoleStatusSummary));
            });
        }
        else
        {
            IsBusy = value;
            OnPropertyChanged(nameof(ConsoleStatusSummary));
        }
    }

    private void ResetShellContext()
    {
        _homeDirectory = string.Empty;
        _currentWorkingDirectory = string.Empty;
        lock (_markerSync)
            _markerCarry = string.Empty;
        NotifyPromptContextChanged();
    }

    private void NotifyPromptContextChanged()
    {
        OnPropertyChanged(nameof(PromptText));
        OnPropertyChanged(nameof(CurrentWorkingDirectoryDisplay));
        OnPropertyChanged(nameof(ConsoleStatusSummary));
    }

    private string ExpandHomePath(string? path)
        => RemotePathDisplayHelper.ExpandHomePath(path, _homeDirectory);

    private string CollapseHomePath(string? path)
        => RemotePathDisplayHelper.CollapseHomePath(path, _homeDirectory);

    private static string NormalizeRemotePath(string? path)
        => RemotePathDisplayHelper.NormalizeRemotePath(path);

    private static string EscapeShellArg(string arg)
    {
        var sanitized = ControlCharRegex.Replace(arg, string.Empty);
        return "'" + sanitized.Replace("'", "'\\''") + "'";
    }

    private string GetDisplayWorkingDirectory()
    {
        var cwd = string.IsNullOrWhiteSpace(_currentWorkingDirectory)
            ? (!string.IsNullOrWhiteSpace(_homeDirectory) ? _homeDirectory : "~")
            : _currentWorkingDirectory;
        var display = CollapseHomePath(cwd);
        return string.IsNullOrWhiteSpace(display) ? "~" : display;
    }

    private string GetPromptUser()
        => _connection?.Username?.Trim() is { Length: > 0 } user ? user : "user";

    private string GetPromptHost()
        => _connection?.Host?.Trim() is { Length: > 0 } host ? host : "remote";

    private static string L(string key)
        => Application.Current?.TryFindResource(key) as string ?? key;

    public void Dispose()
    {
        if (_connection != null)
            _connection.PropertyChanged -= OnConnectionPropertyChanged;
        try { CloseShellSessionAsync().GetAwaiter().GetResult(); } catch { /* best effort */ }
    }
}
