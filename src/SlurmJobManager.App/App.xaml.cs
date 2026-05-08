using System.Windows;
using System.Windows.Threading;
using SlurmJobManager.App.Services;
using SlurmJobManager.App.Services.CrashHandling;
using SlurmJobManager.App.ViewModels;
using SlurmJobManager.Core.Interfaces;
using SlurmJobManager.Core.Models;
using SlurmJobManager.Infrastructure.Logs;
using SlurmJobManager.Infrastructure.Security;
using SlurmJobManager.Infrastructure.Ssh;
using SlurmJobManager.Infrastructure.Storage;

namespace SlurmJobManager.App;

public partial class App : Application
{
    private MainViewModel?      _mainVm;
    private ISshClientService?  _sshService;
    private SerilogAppLogger?   _logger;
    private CrashHandler?       _crashHandler;
    private readonly SemaphoreSlim _shutdownGate = new(1, 1);
    private bool _shutdownCompleted;
    private bool _closeAfterCleanup;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Initialize toast service (must be first so VMs can use it)
        ToastService.Initialize();

        // Shared application settings (timeouts / retry)
        var settings = new AppSettings();

        // Logging (must be initialised early so the crash handler can write to it)
        _logger = new SerilogAppLogger();
        _logger.Info("SlurmJobManager starting up.");

        // Wire global unhandled-exception hooks after logging is ready
        RegisterCrashHandlers();

        // App-level user preferences (auto-connect on startup, etc.)
        var prefs = new AppPreferencesService();

        // Infrastructure services (one SSH client shared across all consumers)
        var ssh      = new SshClientService(settings);
        _sshService = ssh;
        var slurm    = new SlurmService(ssh, settings, _logger);
        var storage  = new TaskStorageService();
        var blueprints = new TaskBlueprintService();
        ILastTaskContextService lastTaskContextService = new LastTaskContextService(_logger);
        var logChunk = new SshLogChunkService(ssh, settings, _logger);
        INotificationService notificationService = new WindowsNotificationService(_logger);

        // Credential protection (DPAPI, Windows-only)
        IConnectionProfileStore? profileStore = null;
        if (DpapiCredentialProtector.IsSupported)
        {
            var protector = new DpapiCredentialProtector();
            profileStore  = new ConnectionProfileStore(protector, _logger);
        }
        IRecentConnectionService recentConnectionService = new RecentConnectionService(_logger);

        // ViewModels
        var connectionVm = new ConnectionViewModel(ssh, profileStore, recentConnectionService);
        var taskEditorVm = new TaskEditorViewModel(ssh, slurm, storage, blueprints, prefs, lastTaskContextService);
        var monitorVm    = new MonitorViewModel(slurm, settings, _logger, connectionVm, notificationService);
        var logViewerVm  = new LogViewerViewModel(logChunk, _logger);
        var consoleVm    = new ConsoleViewModel(ssh, _logger, connectionVm);

        // Wire SSH connection → TaskEditor auto-fill and optional task auto-restore
        connectionVm.ConnectionEstablished += username =>
        {
            _ = HandleConnectionEstablishedAsync(connectionVm, taskEditorVm, prefs, username);
        };

        _mainVm = new MainViewModel(connectionVm, taskEditorVm, monitorVm, logViewerVm, consoleVm, prefs);

        // Default locale: zh-CN (loaded regardless of system language)
        _mainVm.ApplyLocale("zh-CN");

        var mainWindow = new MainWindow { DataContext = _mainVm };
        mainWindow.Closing += OnMainWindowClosing;
        mainWindow.Show();

        // Auto-load saved profile on startup (async, does not block UI)
        if (profileStore != null)
            _ = AutoLoadProfileAsync(connectionVm, profileStore, prefs);
    }

    /// <summary>
    /// Loads the saved connection profile (including encrypted password) into the connection VM.
    /// If <see cref="AppPreferencesService.AutoConnectOnStartup"/> is enabled, also connects.
    /// </summary>
    private static async Task AutoLoadProfileAsync(
        ConnectionViewModel connectionVm,
        IConnectionProfileStore profileStore,
        AppPreferencesService prefs)
    {
        try
        {
            var profile = await profileStore.LoadAsync();
            if (profile is null) return;

            // Populate fields on the UI thread
            Current.Dispatcher.Invoke(() =>
            {
                connectionVm.Host                 = profile.Host;
                connectionVm.Port                 = profile.Port;
                connectionVm.Username             = profile.Username;
                connectionVm.Password             = profile.Password             ?? string.Empty;
                connectionVm.PrivateKeyPath       = profile.PrivateKeyPath       ?? string.Empty;
                connectionVm.PrivateKeyPassphrase = profile.PrivateKeyPassphrase ?? string.Empty;
                connectionVm.StatusMessage        = L("Conn.ProfileLoaded");

                if (prefs.AutoConnectOnStartup)
                    connectionVm.ConnectCommand.Execute(null);
            });
        }
        catch (Exception ex)
        {
            // Surface any failure (decryption errors, I/O errors, JSON parse errors, etc.) as a
            // friendly status message so the user can re-enter and save their credentials.
            Current.Dispatcher.Invoke(() =>
            {
                var template = L("Conn.ProfileLoadFailed");
                connectionVm.StatusMessage = string.Format(template, ex.GetType().Name);
            });
        }
    }

    private static string L(string key)
        => Current.TryFindResource(key) as string ?? key;

    private async Task HandleConnectionEstablishedAsync(
        ConnectionViewModel connectionVm,
        TaskEditorViewModel taskEditorVm,
        AppPreferencesService prefs,
        string username)
    {
        var restoredTaskId = await taskEditorVm.OnConnectionEstablishedAsync(
            connectionVm.Host,
            username,
            prefs.AutoRestoreLastTaskOnLogin);

        if (!restoredTaskId || !prefs.AutoRestoreLastTaskOnLogin || _mainVm == null)
            return;

        if (Current.Dispatcher.CheckAccess())
            _mainVm.ActiveTab = "Tasks";
        else
            await Current.Dispatcher.InvokeAsync(() => _mainVm.ActiveTab = "Tasks");
    }

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeAfterCleanup) return;
        e.Cancel = true;
        _ = ShutdownAndCloseMainWindowAsync(sender as Window);
    }

    // ── Global exception hooks ───────────────────────────────────────────────

    private void RegisterCrashHandlers()
    {
        // Build the crash-handler chain (logger may be null at very early startup)
        var dialogService = new CrashDialogService(GracefulShutdown);
        _crashHandler = new CrashHandler(_logger, dialogService);

        // 1. WPF UI-thread unhandled exceptions
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 2. CLR / thread-pool / background-thread unhandled exceptions
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

        // 3. Unobserved task exceptions (fire-and-forget async that threw)
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Mark as handled so WPF does not show its own generic crash box
        e.Handled = true;
        _crashHandler?.HandleException(e.Exception, "DispatcherUnhandledException");
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            _crashHandler?.HandleException(ex, "AppDomainUnhandledException");
        // If IsTerminating == true, the CLR will exit after this handler returns.
        // The dialog service blocks this thread until the user closes the dialog.
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved(); // prevent CLR from crashing the process for unobserved tasks
        _crashHandler?.HandleException(e.Exception, "UnobservedTaskException");
    }

    /// <summary>
    /// Disposes key resources and requests a graceful WPF shutdown.
    /// Called after the crash dialog is dismissed.
    /// </summary>
    private void GracefulShutdown()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ShutdownAsync();
                _logger?.Info("GracefulShutdown invoked after fatal error.");
            }
            catch
            {
                // best effort
            }
            finally
            {
                try
                {
                    var dispatcher = Current?.Dispatcher;
                    if (dispatcher != null && !dispatcher.HasShutdownStarted)
                    {
                        if (dispatcher.CheckAccess())
                            Current?.Shutdown(1);
                        else
                            _ = dispatcher.BeginInvoke(() => Current?.Shutdown(1));
                    }
                }
                catch
                {
                    // best effort
                }
            }
        });
    }

    private async Task ShutdownAndCloseMainWindowAsync(Window? window)
    {
        if (window != null)
        {
            try
            {
                window.IsEnabled = false;
                window.DataContext = null;
            }
            catch
            {
                // best effort
            }
        }

        await ShutdownAsync();
        if (window == null) return;

        _closeAfterCleanup = true;
        try
        {
            window.Closing -= OnMainWindowClosing;
            window.Close();
        }
        catch
        {
            // best effort
        }
    }

    private async Task ShutdownAsync()
    {
        await _shutdownGate.WaitAsync();
        try
        {
            if (_shutdownCompleted) return;
            _shutdownCompleted = true;

            if (_mainVm?.Monitor is MonitorViewModel monitor)
            {
                try
                {
                    if (monitor.IsPolling)
                    {
                        _logger?.Info("Application closing — stopping monitor polling.");
                        monitor.StopPollingCommand.Execute(null);
                    }
                }
                catch { /* best effort */ }

                await RunBoundedAsync(() => Task.Run(monitor.Dispose), TimeSpan.FromSeconds(2));
            }

            if (_mainVm?.LogViewer is LogViewerViewModel logViewer)
                await RunBoundedAsync(() => Task.Run(logViewer.Dispose), TimeSpan.FromSeconds(2));

            if (_mainVm?.Console is ConsoleViewModel console)
                await RunBoundedAsync(() => Task.Run(console.Dispose), TimeSpan.FromSeconds(3));

            if (_mainVm?.TaskEditor is TaskEditorViewModel taskEditor)
                await RunBoundedAsync(() => Task.Run(taskEditor.Dispose), TimeSpan.FromSeconds(1));

            if (_sshService != null)
                await RunBoundedAsync(() => Task.Run(_sshService.Dispose), TimeSpan.FromSeconds(3));

            _logger?.Info("SlurmJobManager shut down.");
            try { _logger?.Dispose(); } catch { /* best effort */ }
        }
        finally
        {
            _shutdownGate.Release();
        }
    }

    private static async Task RunBoundedAsync(Func<Task> action, TimeSpan timeout)
    {
        try
        {
            var task = action();
            using var timeoutCts = new CancellationTokenSource(timeout);
            var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token);
            var completed = await Task.WhenAny(task, timeoutTask);
            if (completed == task)
            {
                timeoutCts.Cancel();
                await task;
            }
        }
        catch
        {
            // best effort
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdownGate.Dispose();
        base.OnExit(e);
    }
}
