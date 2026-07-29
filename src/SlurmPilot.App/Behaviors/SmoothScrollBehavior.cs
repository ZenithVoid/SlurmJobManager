using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace SlurmPilot.App.Behaviors;

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

    public static readonly DependencyProperty TrackDropDownProperty =
        DependencyProperty.RegisterAttached(
            "TrackDropDown",
            typeof(bool),
            typeof(SmoothScrollBehavior),
            new PropertyMetadata(false, OnTrackDropDownChanged));

    public static bool GetTrackDropDown(DependencyObject obj)
        => (bool)obj.GetValue(TrackDropDownProperty);

    public static void SetTrackDropDown(DependencyObject obj, bool value)
        => obj.SetValue(TrackDropDownProperty, value);

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
        public bool IsRendering;
        public TimeSpan LastRenderingTime;
        public EventHandler? RenderingHandler;
        public ComboBox? OpenComboBox;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ScrollViewer, ScrollState>
        States = new();

    private const double SnapDistance = 0.35;

    // ── Attachment ────────────────────────────────────────────────────────

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;

        if ((bool)e.NewValue)
        {
            sv.PreviewMouseWheel += OnPreviewMouseWheel;
            sv.ScrollChanged      += OnScrollChanged;
            sv.Unloaded          += OnUnloaded;
        }
        else
        {
            sv.PreviewMouseWheel -= OnPreviewMouseWheel;
            sv.ScrollChanged      -= OnScrollChanged;
            sv.Unloaded          -= OnUnloaded;
            StopAndRemoveState(sv);
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        sv.PreviewMouseWheel -= OnPreviewMouseWheel;
        sv.ScrollChanged      -= OnScrollChanged;
        sv.Unloaded          -= OnUnloaded;
        StopAndRemoveState(sv);
    }

    private static void OnTrackDropDownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox comboBox) return;

        if ((bool)e.NewValue)
        {
            comboBox.DropDownOpened += OnComboBoxDropDownOpened;
            comboBox.DropDownClosed += OnComboBoxDropDownClosed;
            comboBox.Unloaded       += OnTrackedComboBoxUnloaded;
        }
        else
        {
            comboBox.DropDownOpened -= OnComboBoxDropDownOpened;
            comboBox.DropDownClosed -= OnComboBoxDropDownClosed;
            comboBox.Unloaded       -= OnTrackedComboBoxUnloaded;
        }
    }

    private static void OnTrackedComboBoxUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox comboBox) return;
        comboBox.DropDownOpened -= OnComboBoxDropDownOpened;
        comboBox.DropDownClosed -= OnComboBoxDropDownClosed;
        comboBox.Unloaded       -= OnTrackedComboBoxUnloaded;
    }

    private static void StopAndRemoveState(ScrollViewer sv)
    {
        if (States.TryGetValue(sv, out var state))
        {
            StopRendering(state);
            States.Remove(sv);
        }
    }

    // ── Wheel handler ─────────────────────────────────────────────────────

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;

        CloseTrackedComboBox(sv);

        // Let child scrollable controls handle the event when they have scroll room.
        if (ChildCanScrollInDirection(e.OriginalSource as DependencyObject, e.Delta, sv))
            return;

        e.Handled = true;

        var step  = GetScrollAmount(sv);
        var delta = e.Delta < 0 ? step : -step;

        var state = States.GetOrCreateValue(sv);

        // Bootstrap: if no animation is running, start from the current visual offset.
        if (!state.IsRendering)
            state.TargetOffset = sv.VerticalOffset;

        state.TargetOffset = Math.Max(0, Math.Min(sv.ScrollableHeight, state.TargetOffset + delta));
        StartRendering(sv, state);
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (Math.Abs(e.VerticalChange) <= 0 && Math.Abs(e.HorizontalChange) <= 0) return;

        CloseTrackedComboBox(sv);
    }

    private static void OnComboBoxDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is not ComboBox comboBox) return;

        var scroller = FindAncestorScrollViewer(comboBox);
        if (scroller == null || !GetIsEnabled(scroller)) return;

        States.GetOrCreateValue(scroller).OpenComboBox = comboBox;
    }

    private static void OnComboBoxDropDownClosed(object? sender, EventArgs e)
    {
        if (sender is not ComboBox comboBox) return;

        var scroller = FindAncestorScrollViewer(comboBox);
        if (scroller == null) return;
        if (!States.TryGetValue(scroller, out var state)) return;
        if (ReferenceEquals(state.OpenComboBox, comboBox))
            state.OpenComboBox = null;
    }

    private static void StartRendering(ScrollViewer sv, ScrollState state)
    {
        if (state.IsRendering)
            return;

        state.IsRendering = true;
        state.LastRenderingTime = TimeSpan.Zero;
        state.RenderingHandler ??= (_, args) =>
        {
            if (args is RenderingEventArgs renderingArgs)
                OnRenderingFrame(sv, state, renderingArgs.RenderingTime);
        };

        CompositionTarget.Rendering += state.RenderingHandler;
    }

    private static void OnRenderingFrame(ScrollViewer sv, ScrollState state, TimeSpan renderingTime)
    {
        if (!state.IsRendering)
            return;

        var current = sv.VerticalOffset;
        var diff = state.TargetOffset - current;
        if (Math.Abs(diff) <= SnapDistance)
        {
            sv.ScrollToVerticalOffset(state.TargetOffset);
            StopRendering(state);
            return;
        }

        var elapsedSeconds = 1d / 60d;
        if (state.LastRenderingTime != TimeSpan.Zero)
            elapsedSeconds = Math.Clamp((renderingTime - state.LastRenderingTime).TotalSeconds, 1d / 240d, 1d / 20d);

        state.LastRenderingTime = renderingTime;
        var frameFactor = 1 - Math.Pow(0.0008, elapsedSeconds);
        sv.ScrollToVerticalOffset(current + diff * frameFactor);
    }

    private static void StopRendering(ScrollState state)
    {
        if (!state.IsRendering)
            return;

        if (state.RenderingHandler != null)
            CompositionTarget.Rendering -= state.RenderingHandler;

        state.IsRendering = false;
        state.LastRenderingTime = TimeSpan.Zero;
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

            candidate = GetParentSafely(candidate);
        }

        return false;
    }

    private static DependencyObject? GetParentSafely(DependencyObject node)
    {
        if (node is Visual or Visual3D)
        {
            var visualParent = VisualTreeHelper.GetParent(node);
            if (visualParent != null)
                return visualParent;
        }

        if (node is FrameworkContentElement fce)
            return fce.Parent ?? LogicalTreeHelper.GetParent(fce);

        if (node is ContentElement ce)
            return ContentOperations.GetParent(ce) ?? LogicalTreeHelper.GetParent(ce);

        return LogicalTreeHelper.GetParent(node);
    }

    private static void CloseTrackedComboBox(ScrollViewer sv)
    {
        if (!States.TryGetValue(sv, out var state)) return;
        var comboBox = state.OpenComboBox;
        if (comboBox is not { IsDropDownOpen: true }) return;

        comboBox.IsDropDownOpen = false;
        state.OpenComboBox = null;
    }

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject source)
    {
        var candidate = GetParentSafely(source);
        while (candidate != null)
        {
            if (candidate is ScrollViewer scroller)
                return scroller;

            candidate = GetParentSafely(candidate);
        }

        return null;
    }
}
