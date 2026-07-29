using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SlurmPilot.App.Services;
using SlurmPilot.App.Services.CrashHandling;
using SlurmPilot.App.Services.ExternalTargets;
using SlurmPilot.App.Services.Logging;
using SlurmPilot.App.Services.Packaging;
using SlurmPilot.App.Services.Updates;
using SlurmPilot.App.Services.Validation;
using SlurmPilot.App.ViewModels;
using SlurmPilot.App.ViewModels.Dialogs;
using SlurmPilot.App.Views;
using SlurmPilot.App.Views.Dialogs;
using SlurmPilot.Core.Interfaces;
using SlurmPilot.Core.Models;
using SlurmPilot.Infrastructure.Logs;
using SlurmPilot.Infrastructure.Security;
using SlurmPilot.Infrastructure.Ssh;
using SlurmPilot.Infrastructure.Storage;

namespace SlurmPilot.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\SlurmPilot.SingleInstance.Mutex";
    private const string SingleInstanceActivationEventName = @"Local\SlurmPilot.SingleInstance.Activate";

    private MainViewModel?      _mainVm;
    private ISshClientService?  _sshService;
    private INotificationService? _notificationService;
    private AppPreferencesService? _prefs;
    private SerilogAppLogger?   _logger;
    private CrashHandler?       _crashHandler;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _singleInstanceActivationEvent;
    private RegisteredWaitHandle? _singleInstanceActivationRegistration;
    private readonly SemaphoreSlim _shutdownGate = new(1, 1);
    private bool _shutdownCompleted;
    private bool _closeAfterCleanup;
    private bool _closeConfirmationInProgress;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!TryAcquireSingleInstance())
        {
            SignalExistingInstance();
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Initialize toast service (must be first so VMs can use it)
        ToastService.Initialize();

        // Shared application settings (timeouts / retry)
        var settings = new AppSettings();

        // Logging (must be initialised early so the crash handler can write to it)
        var logger = new SerilogAppLogger();
        _logger = logger;
        logger.Info("SlurmPilot starting up.");

        // Wire global unhandled-exception hooks after logging is ready
        RegisterCrashHandlers();

        // App-level user preferences (auto-connect on startup, etc.)
        var prefs = new AppPreferencesService();
        _prefs = prefs;
        ILogFileService logFileService = new LogFileService();
        IExternalTargetOpener externalTargetOpener = new ShellExternalTargetOpener();
        IApplicationVersionService versionService = new ApplicationVersionService();
        logger.Info($"SlurmPilot version: {versionService.CurrentVersionDisplay} (Comparable={versionService.CurrentVersion})");
        IUpdateCheckService updateCheckService = new UpdateCheckService(versionService, logger);
        IUpdateLaunchService updateLaunchService = new UpdateLaunchService(versionService);
        IPackagingFeatureAuthorizationService packagingFeatureAuthorizationService = new PackagingFeatureAuthorizationService(logger);

        // Infrastructure services (one SSH client shared across all consumers)
        var ssh      = new SshClientService(settings, logger);
        _sshService = ssh;
        var slurm    = new SlurmService(ssh, settings, logger);
        var storage  = new TaskStorageService();
        var blueprints = new TaskBlueprintService();
        var taskValidationService = new TaskValidationService(ssh);
        ILastTaskContextService lastTaskContextService = new LastTaskContextService(logger);
        var logChunk = new SshLogChunkService(ssh, settings, logger);
        INotificationService notificationService = new WindowsNotificationService(logger);
        _notificationService = notificationService;

        // Credential protection (DPAPI, Windows-only)
        IConnectionProfileStore? profileStore = null;
        if (DpapiCredentialProtector.IsSupported)
        {
            var protector = new DpapiCredentialProtector();
            profileStore  = new ConnectionProfileStore(protector, logger);
        }
        IRecentConnectionService recentConnectionService = new RecentConnectionService(logger);

        // ViewModels
        var connectionVm = new ConnectionViewModel(ssh, profileStore, recentConnectionService, logger);
        var taskEditorVm = new TaskEditorViewModel(ssh, slurm, storage, blueprints, taskValidationService, prefs, lastTaskContextService, logger: logger);
        var monitorVm    = new MonitorViewModel(slurm, settings, prefs, logger, connectionVm, notificationService);
        var logViewerVm  = new LogViewerViewModel(logChunk, logger);
        var consoleVm    = new ConsoleViewModel(ssh, logger, connectionVm);

        // Wire SSH connection → TaskEditor auto-fill and optional task auto-restore
        connectionVm.ConnectionEstablished += username =>
        {
            _ = HandleConnectionEstablishedAsync(connectionVm, taskEditorVm, prefs, username);
        };

        _mainVm = new MainViewModel(
            connectionVm,
            taskEditorVm,
            monitorVm,
            logViewerVm,
            consoleVm,
            prefs,
            updateCheckService,
            versionService,
            updateLaunchService,
            packagingFeatureAuthorizationService,
            logFileService,
            externalTargetOpener,
            logger);

        // Default locale: zh-CN (loaded regardless of system language)
        _mainVm.ApplyLocale("zh-CN");

        var mainWindow = new MainWindow { DataContext = _mainVm };
        mainWindow.Closing += OnMainWindowClosing;
        mainWindow.Show();
        StartSingleInstanceActivationListener();

        _ = _mainVm.Settings.TryAutoCheckUpdatesOnStartupAsync();

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

    private bool TryAcquireSingleInstance()
    {
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
            if (!createdNew)
            {
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                return false;
            }

            _singleInstanceActivationEvent = new EventWaitHandle(
                initialState: false,
                mode: EventResetMode.AutoReset,
                name: SingleInstanceActivationEventName);

            return true;
        }
        catch
        {
            return true;
        }
    }

    private static void SignalExistingInstance()
    {
        TryAllowExistingInstanceToForeground();

        try
        {
            using var activationEvent = EventWaitHandle.OpenExisting(SingleInstanceActivationEventName);
            activationEvent.Set();
        }
        catch
        {
            // If the first instance is still starting, failing to signal is non-fatal.
        }
    }

    private void StartSingleInstanceActivationListener()
    {
        if (_singleInstanceActivationEvent == null)
            return;

        _singleInstanceActivationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _singleInstanceActivationEvent,
            (_, _) => Dispatcher.BeginInvoke(ActivateExistingMainWindow),
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);
    }

    private void ActivateExistingMainWindow()
    {
        if (MainWindow is MainWindow mainWindow)
        {
            mainWindow.RestoreFromTray();
            BringWindowToForeground(mainWindow);
            return;
        }

        if (MainWindow is { } window)
        {
            window.ShowInTaskbar = true;
            window.Show();
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Activate();
            BringWindowToForeground(window);
        }
    }

    private static void TryAllowExistingInstanceToForeground()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            var existing = Process
                .GetProcessesByName(current.ProcessName)
                .FirstOrDefault(p => p.Id != current.Id);

            if (existing != null)
            {
                AllowSetForegroundWindow(existing.Id);
                existing.Dispose();
            }
        }
        catch
        {
            // Windows may deny this in some launch contexts; normal activation still runs.
        }
    }

    private static void BringWindowToForeground(Window window)
    {
        try
        {
            window.Activate();
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero)
                SetForegroundWindow(handle);
        }
        catch
        {
            // Best effort: Windows foreground rules can reject activation.
        }
    }

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
        if (_closeConfirmationInProgress)
        {
            e.Cancel = true;
            return;
        }
        e.Cancel = true;
        _ = HandleMainWindowCloseRequestAsync(sender as Window);
    }

    public void RequestApplicationExit(Window? window = null)
    {
        var targetWindow = window ?? Current.MainWindow;
        if (targetWindow is MainWindow mainWindow && !mainWindow.IsVisible)
            mainWindow.RestoreFromTray();

        if (_closeConfirmationInProgress)
            return;

        _ = ConfirmUnsavedAndCloseMainWindowAsync(targetWindow);
    }

    private async Task HandleMainWindowCloseRequestAsync(Window? window)
    {
        var behavior = _prefs?.CloseButtonBehavior ?? CloseButtonBehavior.Ask;
        if (behavior == CloseButtonBehavior.Ask)
        {
            behavior = PromptForCloseButtonBehavior();
            if (behavior == CloseButtonBehavior.Ask)
                return;

            if (_prefs != null && !_prefs.TrySetCloseButtonBehavior(behavior, out var saveError))
                _logger?.Warning($"Failed to save close button behavior. Error={saveError}");
            _mainVm?.Settings.NotifyCloseButtonBehaviorChanged();
        }

        if (behavior == CloseButtonBehavior.MinimizeToTray)
        {
            if (window is MainWindow mainWindow)
                mainWindow.MinimizeToTray();
            else
                window?.Hide();
            return;
        }

        await ConfirmUnsavedAndCloseMainWindowAsync(window);
    }

    private CloseButtonBehavior PromptForCloseButtonBehavior()
    {
        if (_closeConfirmationInProgress)
            return CloseButtonBehavior.Ask;

        _closeConfirmationInProgress = true;
        try
        {
            var vm = new ConfirmationDialogViewModel(
                title: L("App.CloseBehaviorTitle"),
                message: L("App.CloseBehaviorMessage"),
                details: L("App.CloseBehaviorDetails"),
                confirmButtonText: L("App.CloseBehaviorCloseApplication"),
                cancelButtonText: L("Btn.Cancel"),
                isWarning: false,
                discardButtonText: L("App.CloseBehaviorMinimizeToTray"));

            var dialog = new ConfirmationDialogView { DataContext = vm };
            if (Current.MainWindow is { IsVisible: true } mainWindow)
                dialog.Owner = mainWindow;

            if (dialog.ShowDialog() != true)
                return CloseButtonBehavior.Ask;

            return dialog.DiscardChosen
                ? CloseButtonBehavior.MinimizeToTray
                : CloseButtonBehavior.CloseApplication;
        }
        finally
        {
            _closeConfirmationInProgress = false;
        }
    }

    private async Task ConfirmUnsavedAndCloseMainWindowAsync(Window? window)
    {
        if (_closeConfirmationInProgress)
            return;

        _closeConfirmationInProgress = true;
        try
        {
            if (!ConfirmCloseWithUnsavedChanges())
                return;

            await ShutdownAndCloseMainWindowAsync(window);
        }
        finally
        {
            _closeConfirmationInProgress = false;
        }
    }

    private bool ConfirmCloseWithUnsavedChanges()
    {
        var taskEditorHasUnsavedChanges = _mainVm?.TaskEditor.HasUnsavedChanges == true;
        var dirtyRemoteEditors = 0;
        foreach (var window in Current.Windows.OfType<RemoteFileEditorView>())
        {
            if (window.DataContext is RemoteFileEditorViewModel { IsDirty: true })
                dirtyRemoteEditors++;
        }

        if (!taskEditorHasUnsavedChanges && dirtyRemoteEditors == 0)
            return true;

        var unsavedItems = new List<string>();
        if (taskEditorHasUnsavedChanges)
            unsavedItems.Add($"- {L("Task.UnsavedSourceTaskConfig")}");
        if (dirtyRemoteEditors > 0)
            unsavedItems.Add($"- {string.Format(L("App.UnsavedSourceRemoteEditors"), dirtyRemoteEditors)}");

        var prompt = string.Format(L("App.UnsavedClosePrompt"), string.Join("\n", unsavedItems));
        var vm = new ConfirmationDialogViewModel(
            title: L("Task.UnsavedTitle"),
            message: prompt,
            confirmButtonText: L("Btn.Confirm"),
            cancelButtonText: L("Btn.Cancel"),
            isWarning: true);
        return ShowConfirmationDialog(vm);
    }

    private static bool ShowConfirmationDialog(ConfirmationDialogViewModel vm)
    {
        var dialog = new ConfirmationDialogView { DataContext = vm };
        if (Current.MainWindow is { } mainWindow)
            dialog.Owner = mainWindow;
        return dialog.ShowDialog() == true;
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
        if (IsExpectedBackgroundCancellation(e.Exception))
        {
            _logger?.Warning($"Suppressed expected background cancellation: {e.Exception.GetBaseException().Message}");
            return;
        }

        _crashHandler?.HandleException(e.Exception, "UnobservedTaskException");
    }

    private static bool IsExpectedBackgroundCancellation(Exception exception)
    {
        IEnumerable<Exception> exceptions = exception is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions
            : new[] { exception };

        return exceptions.Any() && exceptions.All(IsExpectedBackgroundCancellationItem);
    }

    private static bool IsExpectedBackgroundCancellationItem(Exception exception)
    {
        if (exception is OperationCanceledException)
            return true;

        if (exception is ObjectDisposedException disposed)
        {
            return string.Equals(disposed.ObjectName, "Renci.SshNet.SshCommand", StringComparison.Ordinal) ||
                   disposed.Message.Contains("SshCommand", StringComparison.OrdinalIgnoreCase);
        }

        return exception.InnerException is not null && IsExpectedBackgroundCancellationItem(exception.InnerException);
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
            CloseOwnedApplicationWindows(window);
            window.Closing -= OnMainWindowClosing;
            window.Close();
            if (!Current.Dispatcher.HasShutdownStarted)
                Current.Shutdown(0);
        }
        catch
        {
            // best effort
        }
    }

    private static void CloseOwnedApplicationWindows(Window? mainWindow)
    {
        DesktopJobNotificationService.Instance.Shutdown();

        var windows = Current.Windows
            .OfType<Window>()
            .Where(w => !ReferenceEquals(w, mainWindow))
            .ToList();

        foreach (var ownedWindow in windows)
        {
            try
            {
                ownedWindow.Owner = null;
                ownedWindow.DataContext = null;
                ownedWindow.Close();
            }
            catch
            {
                // best effort
            }
        }
    }

    private async Task ShutdownAsync()
    {
        await _shutdownGate.WaitAsync();
        try
        {
            if (_shutdownCompleted) return;
            _shutdownCompleted = true;
            DesktopJobNotificationService.Instance.Shutdown();

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

            if (_notificationService is IDisposable notificationDisposable)
            {
                try { notificationDisposable.Dispose(); } catch { /* best effort */ }
                _notificationService = null;
            }

            _logger?.Info("SlurmPilot shut down.");
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
        try { DesktopJobNotificationService.Instance.Shutdown(); } catch { /* best effort */ }
        try { _singleInstanceActivationRegistration?.Unregister(null); } catch { /* best effort */ }
        try { _singleInstanceActivationEvent?.Dispose(); } catch { /* best effort */ }
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* best effort */ }
        try { _singleInstanceMutex?.Dispose(); } catch { /* best effort */ }
        _shutdownGate.Dispose();
        base.OnExit(e);
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
