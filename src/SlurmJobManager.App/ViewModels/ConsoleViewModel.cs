using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using SlurmJobManager.App.Services;
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
    private static readonly Regex BuiltinCdRegex = new(@"^cd(?:\s+(.*))?$", RegexOptions.Compiled);

    private string _commandInput = string.Empty;
    private bool _isBusy;
    private bool _isConnected;
    private bool _isAutoScrollEnabled = true;
    private string _homeDirectory = string.Empty;
    private string _currentWorkingDirectory = string.Empty;
    private int _historyIndex = -1;
    private string _historyDraftInput = string.Empty;
    private CancellationTokenSource? _executeCts;

    public string CommandInput { get => _commandInput; set => SetField(ref _commandInput, value); }
    public bool IsBusy         { get => _isBusy;        private set => SetField(ref _isBusy, value); }
    public bool IsAutoScrollEnabled { get => _isAutoScrollEnabled; set => SetField(ref _isAutoScrollEnabled, value); }
    public string CurrentWorkingDirectoryDisplay => GetDisplayWorkingDirectory();
    public string PromptText => $"{GetPromptUser()}@{GetPromptHost()}:{CurrentWorkingDirectoryDisplay}$ ";
    public string ConsoleStatusSummary => string.Format(
        L("Console.StatusSummary"),
        IsConnected ? L("Console.StatusConnected") : L("Console.StatusDisconnected"),
        CurrentWorkingDirectoryDisplay,
        IsBusy ? L("Console.StatusBusy") : L("Console.StatusIdle"));

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
            NotifyPromptContextChanged();
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
                AppendLine(ConsoleLine.Error(L("Console.ErrInvalidCommand")));
            return;
        }

        if (!_ssh.IsConnected)
        {
            AppendLine(ConsoleLine.Error(L("Console.ErrNotConnected")));
            return;
        }

        AddToHistory(cmd);
        CommandInput  = string.Empty;
        _historyDraftInput = string.Empty;
        _historyIndex = -1;

        var promptAtSubmission = PromptText;
        AppendLine(ConsoleLine.Command($"{promptAtSubmission}{cmd}"));

        await EnsureShellContextAsync(ct);

        if (TryParseBuiltinCd(cmd, out var cdTarget))
        {
            var changed = await TryChangeDirectoryAsync(cdTarget, ct, appendFriendlyError: true);
            if (changed)
                AppendLine(ConsoleLine.Meta($"[cwd] {CurrentWorkingDirectoryDisplay}"));
            return;
        }

        IsBusy = true;
        // Manage lifetimes explicitly: dispose linked source before the source it wraps
        var timeoutCts = new CancellationTokenSource(_settings.CommandTimeout);
        _executeCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var effectiveCommand = BuildCommandInCurrentDirectory(cmd);
            var (stdout, stderr, exitCode) = await _ssh.ExecuteAsync(effectiveCommand, _executeCts.Token);
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
            AppendLine(ConsoleLine.Error(string.Format(L("Console.Timeout"), _settings.CommandTimeout.TotalSeconds)));
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
        AppendLine(ConsoleLine.Meta(L("Console.Cancelling")));
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
        CommandInput  = CommandHistory[_historyIndex];
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

    private void CopyOutput()
    {
        var text = string.Join(Environment.NewLine, OutputLines.Select(l => l.Text));
        System.Windows.Clipboard.SetText(text);
    }

    private void OnConnectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_connection == null) return;

        if (e.PropertyName is nameof(ConnectionViewModel.IsConnected))
        {
            IsConnected = _connection.IsConnected;
            if (IsConnected)
                _ = InitializeShellContextAsync();
            else
                ResetShellContext();
        }

        if (e.PropertyName is nameof(ConnectionViewModel.Username) or nameof(ConnectionViewModel.Host))
            NotifyPromptContextChanged();
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
            dispatcher.InvokeAsync(() =>
            {
                IsBusy = value;
                OnPropertyChanged(nameof(ConsoleStatusSummary));
            });
        else
        {
            IsBusy = value;
            OnPropertyChanged(nameof(ConsoleStatusSummary));
        }
    }

    public async Task<bool> OpenAtDirectoryAsync(string remoteDirectory, CancellationToken ct = default)
    {
        if (!_ssh.IsConnected)
        {
            AppendLine(ConsoleLine.Error(L("Console.ErrNotConnected")));
            return false;
        }

        if (string.IsNullOrWhiteSpace(remoteDirectory))
        {
            AppendLine(ConsoleLine.Error(L("Console.ErrNoTargetDirectory")));
            return false;
        }

        await EnsureShellContextAsync(ct);
        return await TryChangeDirectoryAsync(remoteDirectory, ct, appendFriendlyError: true);
    }

    private async Task InitializeShellContextAsync()
    {
        try
        {
            await EnsureShellContextAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger?.Warning($"Console shell context init failed: {ex.Message}");
        }
    }

    private async Task EnsureShellContextAsync(CancellationToken ct)
    {
        if (!_ssh.IsConnected) return;

        if (string.IsNullOrWhiteSpace(_homeDirectory))
        {
            var home = await _ssh.GetHomeDirectoryAsync(ct);
            _homeDirectory = NormalizeRemotePath(home);
        }

        if (string.IsNullOrWhiteSpace(_currentWorkingDirectory))
            _currentWorkingDirectory = !string.IsNullOrWhiteSpace(_homeDirectory) ? _homeDirectory : "/";

        NotifyPromptContextChanged();
    }

    private async Task<bool> TryChangeDirectoryAsync(string? requestedDirectory, CancellationToken ct, bool appendFriendlyError)
    {
        await EnsureShellContextAsync(ct);

        var target = string.IsNullOrWhiteSpace(requestedDirectory)
            ? "~"
            : StripWrappingQuotes(requestedDirectory.Trim());
        target = ExpandHomePath(target);

        var current = string.IsNullOrWhiteSpace(_currentWorkingDirectory)
            ? (!string.IsNullOrWhiteSpace(_homeDirectory) ? _homeDirectory : "/")
            : _currentWorkingDirectory;

        var command =
            $"cd -- {EscapeShellArg(current)} && " +
            $"cd -- {EscapeShellArg(target)} && " +
            "pwd";

        var (stdout, stderr, exitCode) = await _ssh.ExecuteAsync(command, ct);
        if (exitCode != 0)
        {
            if (appendFriendlyError)
                AppendCdError(stderr);
            return false;
        }

        var resolvedPath = NormalizeRemotePath(
            stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault());

        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            if (appendFriendlyError)
                AppendLine(ConsoleLine.Error(L("Console.CdFailedNoDetail")));
            return false;
        }

        _currentWorkingDirectory = resolvedPath;
        NotifyPromptContextChanged();
        return true;
    }

    private void AppendCdError(string stderr)
    {
        var detail = stderr.Trim();
        if (detail.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase))
        {
            AppendLine(ConsoleLine.Error(L("Console.CdPathMissing")));
            return;
        }

        if (detail.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
        {
            AppendLine(ConsoleLine.Error(L("Console.CdPermissionDenied")));
            return;
        }

        if (detail.Contains("Not a directory", StringComparison.OrdinalIgnoreCase))
        {
            AppendLine(ConsoleLine.Error(L("Console.CdNotDirectory")));
            return;
        }

        if (string.IsNullOrWhiteSpace(detail))
        {
            AppendLine(ConsoleLine.Error(L("Console.CdFailedNoDetail")));
            return;
        }

        AppendLine(ConsoleLine.Error(string.Format(L("Console.CdFailed"), detail)));
    }

    private static bool TryParseBuiltinCd(string command, out string targetDirectory)
    {
        targetDirectory = string.Empty;
        var match = BuiltinCdRegex.Match(command);
        if (!match.Success) return false;

        targetDirectory = match.Groups.Count > 1 && match.Groups[1].Success ? match.Groups[1].Value : string.Empty;
        return true;
    }

    private string BuildCommandInCurrentDirectory(string command)
    {
        var cwd = string.IsNullOrWhiteSpace(_currentWorkingDirectory)
            ? (!string.IsNullOrWhiteSpace(_homeDirectory) ? _homeDirectory : "/")
            : _currentWorkingDirectory;
        return $"cd -- {EscapeShellArg(cwd)} && {command}";
    }

    private void ResetShellContext()
    {
        _homeDirectory = string.Empty;
        _currentWorkingDirectory = string.Empty;
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

    private static string StripWrappingQuotes(string input)
    {
        if (input.Length < 2) return input;
        if ((input[0] == '\'' && input[^1] == '\'') || (input[0] == '"' && input[^1] == '"'))
            return input[1..^1];
        return input;
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
        => string.IsNullOrWhiteSpace(_connection?.Username) ? "user" : _connection!.Username.Trim();

    private string GetPromptHost()
        => string.IsNullOrWhiteSpace(_connection?.Host) ? "remote" : _connection!.Host.Trim();

    private static string L(string key)
        => Application.Current?.TryFindResource(key) as string ?? key;
}
