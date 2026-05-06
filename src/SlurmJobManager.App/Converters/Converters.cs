using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SlurmJobManager.App.ViewModels;

namespace SlurmJobManager.App.Converters;

/// <summary>Converts a Slurm job state string to the matching themed brush.</summary>
[ValueConversion(typeof(string), typeof(Brush))]
public sealed class JobStateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = (value as string) switch
        {
            "RUNNING"    => "StateRunningBrush",
            "COMPLETING" => "StateRunningBrush",
            "PENDING"    => "StatePendingBrush",
            "FAILED"     => "StateFailedBrush",
            "CANCELLED"  => "StateCancelledBrush",
            "TIMEOUT"    => "StateCancelledBrush",
            "NODE_FAIL"  => "StateFailedBrush",
            "COMPLETED"  => "StateCompletedBrush",
            _            => "StateDefaultBrush",
        };
        return Application.Current?.TryFindResource(key) as Brush
               ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}

/// <summary>Converts bool to Visibility (true → Visible, false → Collapsed).</summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>Inverted bool to Visibility (true → Collapsed, false → Visible).</summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}

/// <summary>Converts a ConsoleLineKind enum to a themed brush for console output.</summary>
[ValueConversion(typeof(ConsoleLineKind), typeof(Brush))]
public sealed class ConsoleLineKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ConsoleLineKind kind) return DependencyProperty.UnsetValue;
        var key = kind switch
        {
            ConsoleLineKind.Command => "AccentGreenBrush",
            ConsoleLineKind.Stderr  => "AccentRedBrush",
            ConsoleLineKind.Error   => "AccentRedBrush",
            ConsoleLineKind.Meta    => "AccentYellowBrush",
            _                      => "TextPrimaryBrush",
        };
        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}

/// <summary>Looks up a resource dictionary string by key.</summary>
[ValueConversion(typeof(string), typeof(string))]
public sealed class LocalizationKeyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string key)
        {
            var res = Application.Current?.TryFindResource(key);
            if (res is string s) return s;
        }
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}

/// <summary>Converts a style key string to a Style resource.</summary>
[ValueConversion(typeof(string), typeof(Style))]
public sealed class StyleKeyToStyleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string key || string.IsNullOrWhiteSpace(key))
            return DependencyProperty.UnsetValue;

        var style = Application.Current?.TryFindResource(key) as Style;
        return style ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}

/// <summary>Converts connection status enum values to semantic themed brushes.</summary>
[ValueConversion(typeof(ConnectionStatus), typeof(Brush))]
public sealed class ConnectionStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is ConnectionStatus status
            ? status switch
            {
                ConnectionStatus.Connected                  => "AccentGreenBrush",
                ConnectionStatus.Disconnected               => "TextMutedBrush",
                ConnectionStatus.TestSucceeded              => "AccentGreenBrush",
                ConnectionStatus.Error or ConnectionStatus.ConnectFailed or ConnectionStatus.TestFailed => "AccentRedBrush",
                ConnectionStatus.Connecting or ConnectionStatus.Reconnecting or ConnectionStatus.Testing => "AccentYellowBrush",
                _                                           => "TextMutedBrush",
            }
            : "TextMutedBrush";

        return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}


/// <summary>Inverts a boolean value (true → false, false → true).</summary>
[ValueConversion(typeof(bool), typeof(bool))]
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;
}

/// <summary>Converts an integer count to Visibility: 0 → Visible (empty-state shown), >0 → Collapsed.</summary>
[ValueConversion(typeof(int), typeof(Visibility))]
public sealed class ZeroCountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int n && n == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => DependencyProperty.UnsetValue;
}


public enum ConsoleLineKind
{
    Command,
    Stdout,
    Stderr,
    Error,
    Meta,
}
