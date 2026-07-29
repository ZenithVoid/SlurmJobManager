using System.Collections.ObjectModel;

namespace SlurmPilot.App.Services;

/// <summary>
/// Application-wide singleton toast notification service.
/// Call <see cref="Show"/> from any thread; it marshals to the UI dispatcher.
/// </summary>
public sealed class ToastService
{
    private static ToastService? _instance;

    /// <summary>The single shared instance, set by <see cref="Initialize"/>.</summary>
    public static ToastService Instance => _instance
        ?? throw new InvalidOperationException("ToastService.Initialize() must be called first.");

    /// <summary>Creates and registers the singleton instance. Call once from App.OnStartup.</summary>
    public static void Initialize() => _instance = new ToastService();

    /// <summary>Live list of active toasts; bind an ItemsControl to this in the UI.</summary>
    public ObservableCollection<ViewModels.ToastViewModel> Toasts { get; } = new();

    private ToastService() { }

    // ── Public API ─────────────────────────────────────────────────────────

    public void Info   (string msg, int seconds = 4) => Show(msg, ViewModels.ToastType.Info,    seconds);
    public void Success(string msg, int seconds = 4) => Show(msg, ViewModels.ToastType.Success, seconds);
    public void Warning(string msg, int seconds = 4) => Show(msg, ViewModels.ToastType.Warning, seconds);
    public void Error  (string msg, int seconds = 5) => Show(msg, ViewModels.ToastType.Error,   seconds);

    public void Show(string message, ViewModels.ToastType type, int seconds = 4)
    {
        var dispatch = System.Windows.Application.Current?.Dispatcher;
        if (dispatch == null) return;

        dispatch.InvokeAsync(() =>
        {
            var vm = new ViewModels.ToastViewModel(message, type, seconds);
            vm.DismissRequested += (s, _) => Remove(s as ViewModels.ToastViewModel);
            Toasts.Add(vm);
        });
    }

    private void Remove(ViewModels.ToastViewModel? vm)
    {
        if (vm is null) return;
        Toasts.Remove(vm);
        vm.Dispose();
    }
}
