using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace SlurmJobManager.App.Behaviors;

/// <summary>
/// Attached behavior that intercepts mouse-wheel events on a <see cref="ScrollViewer"/>
/// and produces smooth, eased scrolling instead of discrete per-item jumps.
/// </summary>
public static class SmoothScrollBehavior
{
    // ── Attached property ─────────────────────────────────────────────────

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(SmoothScrollBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj)
        => (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value)
        => obj.SetValue(IsEnabledProperty, value);

    // ── Scroll step size (in device-independent pixels per wheel notch) ──

    public static readonly DependencyProperty ScrollAmountProperty =
        DependencyProperty.RegisterAttached(
            "ScrollAmount",
            typeof(double),
            typeof(SmoothScrollBehavior),
            new PropertyMetadata(60.0));

    public static double GetScrollAmount(DependencyObject obj)
        => (double)obj.GetValue(ScrollAmountProperty);

    public static void SetScrollAmount(DependencyObject obj, double value)
        => obj.SetValue(ScrollAmountProperty, value);

    // ── Animation duration ───────────────────────────────────────────────

    private static readonly Duration AnimationDuration =
        new(TimeSpan.FromMilliseconds(180));

    // ── Tracking per-ScrollViewer target offset (weak refs prevent memory leaks) ─

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScrollViewer, OffsetHolder>
        TargetOffsets = new();

    private sealed class OffsetHolder { public double Value; }

    // ── Attachment ────────────────────────────────────────────────────────

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;

        if ((bool)e.NewValue)
        {
            sv.PreviewMouseWheel += OnPreviewMouseWheel;
            sv.Unloaded          += OnUnloaded;
        }
        else
        {
            sv.PreviewMouseWheel -= OnPreviewMouseWheel;
            sv.Unloaded          -= OnUnloaded;
            TargetOffsets.Remove(sv);
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            sv.PreviewMouseWheel -= OnPreviewMouseWheel;
            sv.Unloaded          -= OnUnloaded;
            TargetOffsets.Remove(sv);
        }
    }

    // ── Wheel handler ─────────────────────────────────────────────────────

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        e.Handled = true;

        var step   = GetScrollAmount(sv);
        var delta  = e.Delta < 0 ? step : -step;

        var holder = TargetOffsets.GetOrCreateValue(sv);
        if (holder.Value == 0 && sv.VerticalOffset != 0)
            holder.Value = sv.VerticalOffset;

        holder.Value = Math.Max(0, Math.Min(sv.ScrollableHeight, holder.Value + delta));
        var target = holder.Value;

        var anim = new DoubleAnimation(
            sv.VerticalOffset,
            target,
            AnimationDuration,
            FillBehavior.Stop)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        anim.Completed += (_, _) =>
        {
            // Reset holder so the next wheel event starts from the current position
            if (TargetOffsets.TryGetValue(sv, out var h) &&
                Math.Abs(sv.VerticalOffset - h.Value) < 1.0)
            {
                h.Value = 0;
            }
        };

        sv.BeginAnimation(ScrollViewerHelper.VerticalOffsetProperty, anim);
    }
}

/// <summary>
/// Helper that exposes <see cref="ScrollViewer.VerticalOffset"/> as an animatable
/// dependency property (the built-in property is read-only).
/// </summary>
internal static class ScrollViewerHelper
{
    public static readonly DependencyProperty VerticalOffsetProperty =
        DependencyProperty.RegisterAttached(
            "VerticalOffset",
            typeof(double),
            typeof(ScrollViewerHelper),
            new PropertyMetadata(0.0, OnVerticalOffsetChanged));

    public static double GetVerticalOffset(DependencyObject obj)
        => (double)obj.GetValue(VerticalOffsetProperty);

    public static void SetVerticalOffset(DependencyObject obj, double value)
        => obj.SetValue(VerticalOffsetProperty, value);

    private static void OnVerticalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer sv)
            sv.ScrollToVerticalOffset((double)e.NewValue);
    }
}
