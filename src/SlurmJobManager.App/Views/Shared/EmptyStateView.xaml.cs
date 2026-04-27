using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SlurmJobManager.App.Views.Shared;

public partial class EmptyStateView : UserControl
{
    // ── DependencyProperties ────────────────────────────────────────────────

    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(string), typeof(EmptyStateView),
            new PropertyMetadata("⚙"));

    public static readonly DependencyProperty EmptyTitleProperty =
        DependencyProperty.Register(nameof(EmptyTitle), typeof(string), typeof(EmptyStateView),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty EmptyHintProperty =
        DependencyProperty.Register(nameof(EmptyHint), typeof(string), typeof(EmptyStateView),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty EmptyActionLabelProperty =
        DependencyProperty.Register(nameof(EmptyActionLabel), typeof(string), typeof(EmptyStateView),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty EmptyActionCommandProperty =
        DependencyProperty.Register(nameof(EmptyActionCommand), typeof(ICommand), typeof(EmptyStateView),
            new PropertyMetadata(null));

    public static readonly DependencyProperty HasEmptyActionProperty =
        DependencyProperty.Register(nameof(HasEmptyAction), typeof(bool), typeof(EmptyStateView),
            new PropertyMetadata(false));

    // ── CLR wrappers ────────────────────────────────────────────────────────

    public string Icon
    {
        get => (string)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string EmptyTitle
    {
        get => (string)GetValue(EmptyTitleProperty);
        set => SetValue(EmptyTitleProperty, value);
    }

    public string EmptyHint
    {
        get => (string)GetValue(EmptyHintProperty);
        set => SetValue(EmptyHintProperty, value);
    }

    public string EmptyActionLabel
    {
        get => (string)GetValue(EmptyActionLabelProperty);
        set => SetValue(EmptyActionLabelProperty, value);
    }

    public ICommand? EmptyActionCommand
    {
        get => (ICommand?)GetValue(EmptyActionCommandProperty);
        set => SetValue(EmptyActionCommandProperty, value);
    }

    public bool HasEmptyAction
    {
        get => (bool)GetValue(HasEmptyActionProperty);
        set => SetValue(HasEmptyActionProperty, value);
    }

    public EmptyStateView()
    {
        InitializeComponent();
    }
}

