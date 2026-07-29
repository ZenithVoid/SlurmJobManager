using System.Windows;
using System.Windows.Media.Animation;
using SlurmPilot.App.ViewModels;

namespace SlurmPilot.App.Views;

public partial class TrayMonitorFlyoutWindow : Window
{
    private const double EdgeMargin = 14;
    private readonly Action? _openMainAction;
    private bool _isPinned;
    private bool _isClosing;

    public TrayMonitorFlyoutWindow(MainViewModel viewModel, Action? openMainAction = null)
    {
        InitializeComponent();
        DataContext = viewModel;
        _openMainAction = openMainAction;

        Loaded += OnLoaded;
        Deactivated += OnDeactivated;
    }

    public bool IsPinned => _isPinned;
    public double ReservedNotificationHeight => Height + 10;
    public event EventHandler? PinStateChanged;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceBelowWorkArea();
        AnimateIn();
    }

    private void PlaceBelowWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - EdgeMargin;
        Top = workArea.Bottom + EdgeMargin;
    }

    private void AnimateIn()
    {
        var workArea = SystemParameters.WorkArea;
        var targetTop = workArea.Bottom - Height - EdgeMargin;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        BeginAnimation(TopProperty, new DoubleAnimation(Top, targetTop, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = ease,
        });

        RootCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = ease,
        });
    }

    public void CloseWithAnimation()
    {
        if (_isClosing)
            return;

        _isClosing = true;
        var workArea = SystemParameters.WorkArea;
        var targetTop = workArea.Bottom + EdgeMargin;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };

        var slide = new DoubleAnimation(Top, targetTop, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = ease,
        };
        slide.Completed += (_, _) => Close();

        BeginAnimation(TopProperty, slide);
        RootCard.BeginAnimation(OpacityProperty, new DoubleAnimation(RootCard.Opacity, 0, TimeSpan.FromMilliseconds(130)));
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
        => CloseWithAnimation();

    private void OpenMainButton_Click(object sender, RoutedEventArgs e)
        => _openMainAction?.Invoke();

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        PinButton.Opacity = _isPinned ? 1 : 0.58;
        PinButton.Background = _isPinned
            ? TryFindResource("BgSurface1Brush") as System.Windows.Media.Brush
            : System.Windows.Media.Brushes.Transparent;
        PinStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_isPinned)
            CloseWithAnimation();
    }
}
