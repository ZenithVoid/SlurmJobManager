using System.Windows;
using SlurmJobManager.App.ViewModels;

namespace SlurmJobManager.App.Views;

public partial class RemoteFileEditorView : Window
{
    public RemoteFileEditorView()
    {
        InitializeComponent();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
