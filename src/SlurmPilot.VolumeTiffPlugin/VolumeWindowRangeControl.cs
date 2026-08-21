using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SlurmPilot.VolumeTiffPlugin;

internal sealed class VolumeWindowRangeControl : Grid
{
    private readonly TextBox _lowerText = new() { Width = 68, TextAlignment = TextAlignment.Right };
    private readonly TextBox _upperText = new() { Width = 68, TextAlignment = TextAlignment.Right };
    private readonly Grid _trackHost = new() { Height = 28, Margin = new Thickness(8, 0, 8, 0), ClipToBounds = false };
    private readonly Border _selection = new() { Height = 8, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.28 };
    private readonly Border _lowerMarker = CreateMarker();
    private readonly Border _upperMarker = CreateMarker();
    private bool _draggingLower;
    private bool _draggingUpper;
    private bool _updatingText;

    public VolumeWindowRangeControl()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        MinWidth = 360;

        _lowerText.VerticalContentAlignment = VerticalAlignment.Center;
        _upperText.VerticalContentAlignment = VerticalAlignment.Center;
        _lowerText.LostKeyboardFocus += (_, _) => CommitText(_lowerText, lower: true);
        _upperText.LostKeyboardFocus += (_, _) => CommitText(_upperText, lower: false);
        _lowerText.KeyDown += TextKeyDown;
        _upperText.KeyDown += TextKeyDown;
        Children.Add(_lowerText);

        var track = new Border
        {
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(2),
            BorderThickness = new Thickness(1),
            Background = new LinearGradientBrush(
                [new GradientStop(Colors.Black, 0), new GradientStop(Color.FromRgb(112, 112, 112), 0.5), new GradientStop(Colors.White, 1)],
                new Point(0, 0.5), new Point(1, 0.5))
        };
        track.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        _selection.SetResourceReference(Border.BackgroundProperty, "AccentBlueBrush");
        _lowerMarker.SetResourceReference(Border.BackgroundProperty, "AccentBlueBrush");
        _upperMarker.SetResourceReference(Border.BackgroundProperty, "AccentBlueBrush");
        _lowerMarker.SetResourceReference(Border.BorderBrushProperty, "TextPrimaryBrush");
        _upperMarker.SetResourceReference(Border.BorderBrushProperty, "TextPrimaryBrush");
        _trackHost.Children.Add(track);
        _trackHost.Children.Add(_selection);
        _trackHost.Children.Add(_lowerMarker);
        _trackHost.Children.Add(_upperMarker);
        _trackHost.SizeChanged += (_, _) => UpdateMarkers();
        _trackHost.MouseLeftButtonDown += TrackMouseDown;
        _trackHost.MouseMove += TrackMouseMove;
        _trackHost.MouseLeftButtonUp += TrackMouseUp;
        _trackHost.LostMouseCapture += (_, _) => EndDrag();
        _trackHost.MouseWheel += TrackMouseWheel;
        _trackHost.ToolTip = "拖动两端调整显示范围；滚轮缩放调节精度";
        SetColumn(_trackHost, 1);
        Children.Add(_trackHost);

        SetColumn(_upperText, 2);
        Children.Add(_upperText);
        SetValues(0, 1, raiseEvent: false);
    }

    public event EventHandler? RangeChanged;
    public double DomainMinimum { get; private set; } = -1;
    public double DomainMaximum { get; private set; } = 2;
    public double LowerValue { get; private set; }
    public double UpperValue { get; private set; } = 1;

    public void Configure(double imageMinimum, double imageMaximum, double lower, double upper)
    {
        (DomainMinimum, DomainMaximum) = CreateDomain(imageMinimum, imageMaximum);
        SetValues(lower, upper, raiseEvent: true);
    }

    public void SetValues(double lower, double upper, bool raiseEvent = true)
    {
        if (upper <= lower) upper = lower + 1;
        ExpandDomainToInclude(lower, upper);
        LowerValue = lower;
        UpperValue = upper;
        UpdateText();
        UpdateMarkers();
        if (raiseEvent) RangeChanged?.Invoke(this, EventArgs.Empty);
    }

    internal static (double Minimum, double Maximum) CreateDomain(double imageMinimum, double imageMaximum)
    {
        var span = Math.Max(1, imageMaximum - imageMinimum);
        return (imageMinimum - span, imageMaximum + span);
    }

    private static Border CreateMarker()
        => new()
        {
            Width = 4,
            Height = 20,
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1)
        };

    private void TextKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitText((TextBox)sender, ReferenceEquals(sender, _lowerText));
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void CommitText(TextBox textBox, bool lower)
    {
        if (_updatingText) return;
        if (!double.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value))
        {
            UpdateText();
            return;
        }
        value = Math.Round(value, 3);
        if (lower) SetValues(Math.Min(value, UpperValue - 0.001), UpperValue);
        else SetValues(LowerValue, Math.Max(value, LowerValue + 0.001));
    }

    private void TrackMouseDown(object sender, MouseButtonEventArgs e)
    {
        var value = PositionToValue(e.GetPosition(_trackHost).X);
        _draggingLower = Math.Abs(value - LowerValue) <= Math.Abs(value - UpperValue);
        _draggingUpper = !_draggingLower;
        _trackHost.CaptureMouse();
        UpdateDraggedValue(value);
        e.Handled = true;
    }

    private void TrackMouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingLower && !_draggingUpper) return;
        UpdateDraggedValue(PositionToValue(e.GetPosition(_trackHost).X));
        e.Handled = true;
    }

    private void TrackMouseUp(object sender, MouseButtonEventArgs e)
    {
        EndDrag();
        e.Handled = true;
    }

    private void EndDrag()
    {
        _draggingLower = false;
        _draggingUpper = false;
        if (_trackHost.IsMouseCaptured) _trackHost.ReleaseMouseCapture();
    }

    private void UpdateDraggedValue(double value)
    {
        var minimumGap = Math.Max(0.001, (DomainMaximum - DomainMinimum) / Math.Max(1000, _trackHost.ActualWidth * 4));
        if (_draggingLower) SetValues(Math.Min(value, UpperValue - minimumGap), UpperValue);
        else SetValues(LowerValue, Math.Max(value, LowerValue + minimumGap));
    }

    private void TrackMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var factor = e.Delta > 0 ? 0.72 : 1.38;
        var center = (LowerValue + UpperValue) * 0.5;
        var halfSpan = Math.Max(0.5, (DomainMaximum - DomainMinimum) * factor * 0.5);
        DomainMinimum = center - halfSpan;
        DomainMaximum = center + halfSpan;
        ExpandDomainToInclude(LowerValue, UpperValue);
        UpdateMarkers();
        e.Handled = true;
    }

    private double PositionToValue(double x)
    {
        var width = Math.Max(1, _trackHost.ActualWidth);
        var ratio = Math.Clamp(x / width, 0, 1);
        return DomainMinimum + ratio * (DomainMaximum - DomainMinimum);
    }

    private double ValueToPosition(double value)
    {
        var span = Math.Max(0.000001, DomainMaximum - DomainMinimum);
        return Math.Clamp((value - DomainMinimum) / span, 0, 1) * Math.Max(1, _trackHost.ActualWidth);
    }

    private void ExpandDomainToInclude(double lower, double upper)
    {
        var span = Math.Max(1, DomainMaximum - DomainMinimum);
        if (lower < DomainMinimum) DomainMinimum = lower - span * 0.15;
        if (upper > DomainMaximum) DomainMaximum = upper + span * 0.15;
    }

    private void UpdateText()
    {
        _updatingText = true;
        _lowerText.Text = FormatValue(LowerValue);
        _upperText.Text = FormatValue(UpperValue);
        _updatingText = false;
    }

    private void UpdateMarkers()
    {
        if (_trackHost.ActualWidth <= 0) return;
        var lowerX = ValueToPosition(LowerValue);
        var upperX = ValueToPosition(UpperValue);
        _lowerMarker.Margin = new Thickness(lowerX - _lowerMarker.Width / 2, 0, 0, 0);
        _lowerMarker.HorizontalAlignment = HorizontalAlignment.Left;
        _upperMarker.Margin = new Thickness(upperX - _upperMarker.Width / 2, 0, 0, 0);
        _upperMarker.HorizontalAlignment = HorizontalAlignment.Left;
        _selection.Width = Math.Max(0, upperX - lowerX);
        _selection.Margin = new Thickness(lowerX, 0, 0, 0);
        _selection.HorizontalAlignment = HorizontalAlignment.Left;
    }

    private static string FormatValue(double value)
        => Math.Abs(value - Math.Round(value)) < 0.0005
            ? Math.Round(value).ToString(CultureInfo.CurrentCulture)
            : value.ToString("0.###", CultureInfo.CurrentCulture);
}
