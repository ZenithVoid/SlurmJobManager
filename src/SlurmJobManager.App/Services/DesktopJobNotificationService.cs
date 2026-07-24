using System.Windows;
using SlurmJobManager.App.ViewModels;
using SlurmJobManager.App.Views;

namespace SlurmJobManager.App.Services;

public sealed class DesktopJobNotificationService
{
    private static readonly Lazy<DesktopJobNotificationService> LazyInstance = new(() => new DesktopJobNotificationService());
    private readonly List<JobNotificationWindow> _activeWindows = new();
    private bool _isShuttingDown;

    public static DesktopJobNotificationService Instance => LazyInstance.Value;

    private DesktopJobNotificationService() { }

    public void Show(
        string title,
        string message,
        ToastType type,
        TimeSpan? duration,
        Action? clickAction)
    {
        if (_isShuttingDown)
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;

        dispatcher.InvokeAsync(() =>
        {
            if (_isShuttingDown)
                return;

            _activeWindows.RemoveAll(window => !window.IsVisible);
            var window = new JobNotificationWindow(title, message, type, duration, clickAction, CloseAll, _activeWindows.Count);
            window.Closed += (_, _) =>
            {
                _activeWindows.Remove(window);
                ReflowActiveWindows();
            };
            _activeWindows.Add(window);
            window.Show();
        });
    }

    public void CloseAll()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;

        dispatcher.InvokeAsync(() =>
        {
            var windows = _activeWindows
                .Where(window => window.IsVisible)
                .ToList();
            foreach (var window in windows)
                window.Dismiss();
        });
    }

    public void Shutdown()
    {
        _isShuttingDown = true;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;

        if (dispatcher.CheckAccess())
        {
            CloseAllImmediately();
            return;
        }

        dispatcher.Invoke(CloseAllImmediately);
    }

    private void CloseAllImmediately()
    {
        var windows = _activeWindows.ToList();
        _activeWindows.Clear();
        foreach (var window in windows)
        {
            try { window.CloseImmediately(); }
            catch { /* best effort */ }
        }
    }

    private void ReflowActiveWindows()
    {
        if (_isShuttingDown)
            return;

        _activeWindows.RemoveAll(window => !window.IsVisible);
        for (var index = 0; index < _activeWindows.Count; index++)
            _activeWindows[index].MoveToStackIndex(index, animate: true);
    }
}
