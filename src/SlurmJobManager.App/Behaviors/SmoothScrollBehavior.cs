using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SlurmJobManager.App.Behaviors;

/// <summary>
/// Attached behavior that intercepts mouse-wheel events on a <see cref="ScrollViewer"/>
/// and produces smooth, continuous scrolling instead of discrete per-item jumps.
/// <para>
/// Child scrollable controls (DataGrid, TextBox with scrollbar, etc.) can still
/// scroll independently when they have remaining scroll room; the wheel event is
/// only claimed by the parent ScrollViewer when the child is at its boundary.
/// </para>
/// </summary>
public static class SmoothScrollBehavior
{
    // ── IsEnabled attached property ───────────────────────────────────────

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

    // ── ScrollAmount (pixels per wheel notch) ────────────────────────────

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

    // ── Per-ScrollViewer scroll state (DispatcherTimer-based lerp) ───────

    private sealed class ScrollState
    {
        /// <summary>Accumulated target vertical offset.</summary>
        public double TargetOffset;
        /// <summary>Timer that drives the animation tick (~60 fps).</summary>
        public DispatcherTimer? Timer;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScrollViewer, ScrollState>
        States = new();

    // ── Lerp factor per 16 ms frame (≈ 60 fps). Larger = faster. ─────────
    private const double LerpFactor = 0.25;
    // ── Stop animating when closer than this many pixels to the target. ──
    private const double SnapDistance = 0.5;

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
            StopAndRemoveState(sv);
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        sv.PreviewMouseWheel -= OnPreviewMouseWheel;
        sv.Unloaded          -= OnUnloaded;
        StopAndRemoveState(sv);
    }

    private static void StopAndRemoveState(ScrollViewer sv)
    {
        if (States.TryGetValue(sv, out var state))
        {
            state.Timer?.Stop();
            States.Remove(sv);
        }
    }

    // ── Wheel handler ─────────────────────────────────────────────────────

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;

        // Let child scrollable controls handle the event when they have scroll room.
        if (ChildCanScrollInDirection(e.OriginalSource as DependencyObject, e.Delta, sv))
            return;

        e.Handled = true;

        var step  = GetScrollAmount(sv);
        var delta = e.Delta < 0 ? step : -step;

        var state = States.GetOrCreateValue(sv);

        // Bootstrap: if no animation is running, start from the current visual offset.
        if (state.Timer is not { IsEnabled: true })
            state.TargetOffset = sv.VerticalOffset;

        state.TargetOffset = Math.Max(0, Math.Min(sv.ScrollableHeight, state.TargetOffset + delta));

        // Lazily create the timer.
        if (state.Timer == null)
        {
            var capturedSv = sv;
            state.Timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16),
            };
            state.Timer.Tick += (_, _) => AnimationTick(capturedSv, state);
        }

        if (!state.Timer.IsEnabled)
            state.Timer.Start();
    }

    // ── Per-frame lerp towards target ─────────────────────────────────────

    private static void AnimationTick(ScrollViewer sv, ScrollState state)
    {
        var current = sv.VerticalOffset;
        var target  = state.TargetOffset;
        var diff    = target - current;

        if (Math.Abs(diff) <= SnapDistance)
        {
            sv.ScrollToVerticalOffset(target);
            state.Timer?.Stop();
            return;
        }

        sv.ScrollToVerticalOffset(current + diff * LerpFactor);
    }

    // ── Child-scroll awareness ────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when the element under the cursor is
    /// inside a nested <see cref="ScrollViewer"/> (e.g., DataGrid, TextBox)
    /// that still has room to scroll in the requested direction.
    /// </summary>
    private static bool ChildCanScrollInDirection(
        DependencyObject? source,
        int wheelDelta,
        ScrollViewer parentSv)
    {
        if (source == null) return false;

        var candidate = source;
        while (candidate != null && candidate != parentSv)
        {
            if (candidate is ScrollViewer childSv && !ReferenceEquals(childSv, parentSv))
            {
                // Check whether the child has scroll room in the requested direction.
                if (wheelDelta < 0) // scrolling down
                    return childSv.VerticalOffset < childSv.ScrollableHeight;
                else                // scrolling up
                    return childSv.VerticalOffset > 0;
            }

            // Walk up the visual tree.
            candidate = VisualTreeHelper.GetParent(candidate)
                        ?? LogicalTreeHelper.GetParent(candidate);
        }

        return false;
    }
}
