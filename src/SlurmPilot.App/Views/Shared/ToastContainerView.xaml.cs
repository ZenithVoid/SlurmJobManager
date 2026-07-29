using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SlurmPilot.App.ViewModels;

namespace SlurmPilot.App.Views.Shared;

public partial class ToastContainerView : UserControl
{
    public ToastContainerView()
    {
        InitializeComponent();
    }

    private void Toast_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is ContentPresenter cp && cp.Content is ToastViewModel vm)
            vm.PauseTimer();
    }

    private void Toast_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is ContentPresenter cp && cp.Content is ToastViewModel vm)
            vm.ResumeTimer();
    }
}
