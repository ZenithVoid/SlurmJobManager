using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SlurmPilot.App.ViewModels;

namespace SlurmPilot.App.Views;

public partial class JobNotificationWindow : Window
{
    private const double NotificationHeight = 132;
    private const double EdgeMargin = 18;
    private const double StackGap = 10;

    private readonly DispatcherTimer _timer;
    private readonly TimeSpan? _duration;
    private readonly Action? _clickAction;
    private readonly Action? _markAllReadAction;
    private int _stackIndex;
    private double _bottomReservedHeight;
    private bool _isClosing;

    public JobNotificationWindow(
        string title,
        string message,
        ToastType type,
        TimeSpan? duration,
        Action? clickAction,
        Action? markAllReadAction,
        int stackIndex = 0,
        double bottomReservedHeight = 0)
    {
        InitializeComponent();

        _duration = duration;
        _clickAction = clickAction;
        _markAllReadAction = markAllReadAction;
        _stackIndex = Math.Max(0, stackIndex);
        _bottomReservedHeight = Math.Max(0, bottomReservedHeight);
        _timer = new DispatcherTimer { Interval = duration ?? TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => CloseWithAnimation();

        TitleText.Text = title;
        MessageText.Text = message;
        ApplyType(type);

        MouseEnter += (_, _) => _timer.Stop();
        MouseLeave += (_, _) =>
        {
            if (_duration.HasValue)
                _timer.Start();
        };
        Loaded += OnLoaded;
        Closed += (_, _) => _timer.Stop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceAtBottomRight();
        AnimateIn();
        if (_duration.HasValue)
            _timer.Start();
    }

    private void PlaceAtBottomRight()
    {
        Left = SystemParameters.WorkArea.Right;
        Top = CalculateTop(_stackIndex);
    }

    public void MoveToStackIndex(int stackIndex, bool animate, double bottomReservedHeight = 0)
    {
        if (_isClosing)
            return;

        _stackIndex = Math.Max(0, stackIndex);
        _bottomReservedHeight = Math.Max(0, bottomReservedHeight);
        var targetTop = CalculateTop(_stackIndex);
        if (!animate)
        {
            Top = targetTop;
            return;
        }

        BeginAnimation(TopProperty, CreateReflowAnimation(Top, targetTop));
    }

    private void ApplyType(ToastType type)
    {
        var resourceKey = type switch
        {
            ToastType.Success => "AccentGreenBrush",
            ToastType.Warning => "AccentYellowBrush",
            ToastType.Error => "AccentRedBrush",
            _ => "AccentBlueBrush",
        };

        IconText.Text = type switch
        {
            ToastType.Success => "✓",
            ToastType.Warning => "!",
            ToastType.Error => "×",
            _ => "i",
        };

        if (TryFindResource(resourceKey) is Brush brush)
        {
            IconText.Foreground = brush;
            IconHost.BorderBrush = brush;
        }
    }

    private void AnimateIn()
    {
        var targetLeft = SystemParameters.WorkArea.Right - Width - 18;
        var storyboard = new Storyboard();
        storyboard.Children.Add(CreateDoubleAnimation(this, Window.LeftProperty, Left, targetLeft, 260));
        storyboard.Children.Add(CreateDoubleAnimation(RootCard, OpacityProperty, 0, 1, 200));
        storyboard.Begin();
    }

    public void Dismiss()
        => CloseWithAnimation();

    public void CloseImmediately()
    {
        if (_isClosing)
            return;

        _isClosing = true;
        _timer.Stop();
        Close();
    }

    private void CloseWithAnimation()
    {
        if (_isClosing)
            return;

        _isClosing = true;
        _timer.Stop();
        var targetTop = SystemParameters.WorkArea.Bottom + 24;
        var storyboard = new Storyboard();
        storyboard.Children.Add(CreateDoubleAnimation(this, Window.TopProperty, Top, targetTop, 260, new QuarticEase { EasingMode = EasingMode.EaseIn }));
        storyboard.Children.Add(CreateDoubleAnimation(RootCard, OpacityProperty, RootCard.Opacity, 0, 190, new CubicEase { EasingMode = EasingMode.EaseIn }));
        storyboard.Completed += (_, _) => Close();
        storyboard.Begin();
    }

    private static DoubleAnimation CreateDoubleAnimation(
        DependencyObject target,
        DependencyProperty property,
        double from,
        double to,
        int milliseconds,
        IEasingFunction? easing = null)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            EasingFunction = easing ?? new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        return animation;
    }

    private static DoubleAnimation CreateReflowAnimation(double from, double to)
        => new()
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(240),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

    private double CalculateTop(int stackIndex)
    {
        var workArea = SystemParameters.WorkArea;
        return workArea.Bottom - _bottomReservedHeight - NotificationHeight - EdgeMargin - (Math.Max(0, stackIndex) * (NotificationHeight + StackGap));
    }

    private void RootCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isClosing)
            return;

        _clickAction?.Invoke();
        CloseWithAnimation();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CloseWithAnimation();
    }

    private void MarkAllReadButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _markAllReadAction?.Invoke();
    }
}
