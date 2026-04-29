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
    private SerilogAppLogger?   _logger;
    private CrashHandler?       _crashHandler;

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

        // Infrastructure services (one SSH client shared across all consumers)
        var ssh      = new SshClientService(settings);
        var slurm    = new SlurmService(ssh, settings, _logger);
        var storage  = new TaskStorageService();
        var logChunk = new SshLogChunkService(ssh, settings, _logger);

        // Credential protection (DPAPI, Windows-only)
        IConnectionProfileStore? profileStore = null;
        if (DpapiCredentialProtector.IsSupported)
        {
            var protector = new DpapiCredentialProtector();
            profileStore  = new ConnectionProfileStore(protector, _logger);
        }

        // ViewModels
        var connectionVm = new ConnectionViewModel(ssh, profileStore);
        var taskEditorVm = new TaskEditorViewModel(ssh, slurm, storage);
        var monitorVm    = new MonitorViewModel(slurm, settings, _logger, connectionVm);
        var logViewerVm  = new LogViewerViewModel(logChunk, _logger);
        var consoleVm    = new ConsoleViewModel(ssh, _logger, connectionVm);

        // Wire SSH connection → TaskEditor auto-fill
        connectionVm.ConnectionEstablished += username => taskEditorVm.OnConnectionEstablished(username);

        _mainVm = new MainViewModel(connectionVm, taskEditorVm, monitorVm, logViewerVm, consoleVm);

        // Default locale: zh-CN (loaded regardless of system language)
        _mainVm.ApplyLocale("zh-CN");

        var mainWindow = new MainWindow { DataContext = _mainVm };
        mainWindow.Closing += OnMainWindowClosing;
        mainWindow.Show();
    }

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Gracefully stop background tasks before the process exits
        if (_mainVm?.Monitor is MonitorViewModel monitor)
        {
            if (monitor.IsPolling)
            {
                _logger?.Info("Application closing — stopping monitor polling.");
                monitor.StopPollingCommand.Execute(null);
            }
            monitor.Dispose();
        }

        if (_mainVm?.LogViewer is LogViewerViewModel logViewer)
        {
            logViewer.Dispose();
        }

        if (_mainVm?.Console is ConsoleViewModel console)
        {
            console.Dispose();
        }

        _logger?.Info("SlurmJobManager shut down.");
        _logger?.Dispose();
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
        try
        {
            if (_mainVm?.Monitor is MonitorViewModel monitor)
            {
                try { if (monitor.IsPolling) monitor.StopPollingCommand.Execute(null); } catch { }
                try { monitor.Dispose(); } catch { }
            }
            try { _mainVm?.LogViewer?.Dispose(); } catch { }
            try { _mainVm?.Console?.Dispose();   } catch { }
            _logger?.Info("GracefulShutdown invoked after fatal error.");
            try { _logger?.Dispose(); } catch { }
        }
        catch
        {
            // Best-effort: we must always exit
        }
        finally
        {
            try
        {
            var dispatcher = Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.HasShutdownStarted)
            {
                if (dispatcher.CheckAccess())
                    Current?.Shutdown(1);           // Already on UI thread — call directly
                else
                    dispatcher.BeginInvoke(() => Current?.Shutdown(1));  // Schedule from background
            }
            else
            {
                Environment.Exit(1);
            }
        }
        catch
        {
            Environment.Exit(1);
        }
        }
    }
}
