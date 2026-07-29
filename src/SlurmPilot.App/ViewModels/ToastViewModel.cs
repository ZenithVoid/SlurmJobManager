using System.Windows.Input;
using System.Windows.Threading;

namespace SlurmPilot.App.ViewModels;

/// <summary>Toast notification type.</summary>
public enum ToastType { Info, Success, Warning, Error }

/// <summary>
/// View-model for a single toast notification.
/// Supports auto-dismiss after the configured duration,
/// with hover-to-pause behavior managed by the container view.
/// </summary>
public sealed class ToastViewModel : ViewModelBase, IDisposable
{
    private readonly DispatcherTimer _timer;
    private bool _disposed;
    private double _remainingMs;

    public string    Message  { get; }
    public ToastType Type     { get; }
    public bool IsPersistent { get; }
    public string    Icon     => Type switch
    {
        ToastType.Success => "✔",
        ToastType.Warning => "⚠",
        ToastType.Error   => "✖",
        _                 => "ℹ",
    };

    public ICommand CloseCommand { get; }

    /// <summary>Fired when this toast should be removed from the queue.</summary>
    public event EventHandler? DismissRequested;

    public ToastViewModel(string message, ToastType type, int durationSeconds = 4)
    {
        Message = message;
        Type    = type;
        IsPersistent = durationSeconds <= 0;

        CloseCommand = new RelayCommand(Dismiss);

        _remainingMs = durationSeconds * 1000.0;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += OnTick;
        if (!IsPersistent)
            _timer.Start();
    }

    /// <summary>Pauses the auto-dismiss countdown (called on mouse enter).</summary>
    public void PauseTimer()
    {
        if (!IsPersistent)
            _timer.Stop();
    }

    /// <summary>Resumes the auto-dismiss countdown (called on mouse leave).</summary>
    public void ResumeTimer()
    {
        if (!IsPersistent)
            _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _remainingMs -= 100;
        if (_remainingMs <= 0) Dismiss();
    }

    private void Dismiss()
    {
        _timer.Stop();
        DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
