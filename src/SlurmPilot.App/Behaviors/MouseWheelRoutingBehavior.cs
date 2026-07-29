using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SlurmPilot.App.Behaviors;

/// <summary>
/// Attached behavior that ensures mouse-wheel events are always routed to the
/// host <see cref="ScrollViewer"/> when the control under the cursor cannot
/// scroll further in the requested direction.
/// </summary>
/// <remarks>
/// <para>
/// WPF child controls such as <see cref="TextBox"/>, <see cref="ComboBox"/>,
/// <see cref="ListBox"/>, and <see cref="DataGrid"/> consume
/// <see cref="UIElement.MouseWheelEvent"/> even when they have no remaining
/// scroll room, silently swallowing events that the user intended for the
/// outer page scroll.
/// </para>
/// <para>
/// This behavior hooks <see cref="UIElement.PreviewMouseWheelEvent"/> (tunneling)
/// on the host <see cref="ScrollViewer"/> and decides per-event:
/// <list type="bullet">
///   <item>If the element under the cursor is inside a scrollable child that still
///   has room to scroll in the given direction → do nothing (let the child handle
///   the event normally).</item>
///   <item>Otherwise → mark the original event as handled (preventing child
///   controls from consuming it) and re-raise a synthetic
///   <see cref="UIElement.PreviewMouseWheelEvent"/> directly on the host
///   <see cref="ScrollViewer"/> so that <see cref="SmoothScrollBehavior"/> can
///   animate the scroll.</item>
/// </list>
/// </para>
/// <para>
/// Detection covers both direct <see cref="ScrollViewer"/> nodes on the visual
/// path and internal template-part scroll viewers inside known container controls
/// (<see cref="TextBox"/>, <see cref="RichTextBox"/>, <see cref="ComboBox"/>,
/// <see cref="ListBox"/>, <see cref="ListView"/>, <see cref="DataGrid"/>).
/// </para>
/// </remarks>
public static class MouseWheelRoutingBehavior
{
    // ── IsEnabled attached property ───────────────────────────────────────

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(MouseWheelRoutingBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject obj) =>
        (bool)obj.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject obj, bool value) =>
        obj.SetValue(IsEnabledProperty, value);

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
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        sv.PreviewMouseWheel -= OnPreviewMouseWheel;
        sv.Unloaded          -= OnUnloaded;
    }

    // ── Re-entrancy guard (WPF is single-threaded, ThreadStatic is fine) ─

    [ThreadStatic]
    private static bool _isRerouting;

    // ── Wheel handler ─────────────────────────────────────────────────────

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Prevent the synthetic event we raise below from re-entering this handler.
        if (_isRerouting) return;
        if (sender is not ScrollViewer sv) return;

        // Check whether the child control under the cursor still has scroll room.
        var childSv = FindScrollableChild(e.OriginalSource as DependencyObject, sv);
        if (childSv != null && CanScrollInDirection(childSv, e.Delta))
            return; // Child can still scroll — let it handle the event.

        // The child (if any) is at its scroll boundary, or no scrollable child
        // exists between the event source and the host ScrollViewer.
        // Claim the original event and re-raise a synthetic PreviewMouseWheel
        // directly on the host so that SmoothScrollBehavior can pick it up.
        e.Handled = true;

        _isRerouting = true;
        try
        {
            var synthetic = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.PreviewMouseWheelEvent,
            };
            sv.RaiseEvent(synthetic);
        }
        finally
        {
            _isRerouting = false;
        }
    }

    // ── Scroll-ability helpers ────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="sv"/> has remaining
    /// scroll room in the direction indicated by <paramref name="delta"/>.
    /// </summary>
    private static bool CanScrollInDirection(ScrollViewer sv, int delta) =>
        delta < 0
            ? sv.VerticalOffset < sv.ScrollableHeight   // scrolling down
            : sv.VerticalOffset > 0;                    // scrolling up

    /// <summary>
    /// Walks the visual/logical tree from <paramref name="source"/> towards the
    /// host <paramref name="stopAt"/>, returning the first intermediate
    /// <see cref="ScrollViewer"/> found.
    /// </summary>
    /// <remarks>
    /// In addition to detecting explicit <see cref="ScrollViewer"/> nodes on the
    /// visual path, the method also inspects the internal visual subtree of known
    /// container types whose template-part scroll viewer may not appear on the
    /// direct path when the <see cref="RoutedEventArgs.OriginalSource"/> is the
    /// container element itself rather than a deeper renderer inside it.
    /// </remarks>
    private static ScrollViewer? FindScrollableChild(DependencyObject? source, DependencyObject stopAt)
    {
        var candidate = source;
        while (candidate != null && !ReferenceEquals(candidate, stopAt))
        {
            // The candidate IS a ScrollViewer (not the outer host).
            if (candidate is ScrollViewer childSv)
                return childSv;

            // The candidate is a known control whose ScrollViewer lives inside
            // its control template — search one level deeper.
            if (IsKnownScrollContainer(candidate))
            {
                var internalSv = FindFirstVisualDescendant<ScrollViewer>(candidate);
                if (internalSv != null)
                    return internalSv;
            }

            candidate = VisualTreeHelper.GetParent(candidate)
                     ?? LogicalTreeHelper.GetParent(candidate);
        }

        return null;
    }

    private static bool IsKnownScrollContainer(DependencyObject d) =>
        d is TextBox or RichTextBox or ComboBox or ListBox or ListView or DataGrid;

    // ── Visual-tree descent ───────────────────────────────────────────────

    private static T? FindFirstVisualDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;
            var nested = FindFirstVisualDescendant<T>(child);
            if (nested != null)
                return nested;
        }
        return null;
    }
}
