using System.Windows;
using System.Windows.Controls;
using SlurmJobManager.App.ViewModels;

namespace SlurmJobManager.App.Views;

public partial class ConnectionView : UserControl
{
    public ConnectionView()
    {
        InitializeComponent();
        PwdBox.PasswordChanged        += OnPasswordChanged;
        PassphraseBox.PasswordChanged += OnPassphraseChanged;
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionViewModel vm)
            vm.Password = PwdBox.Password;
    }

    private void OnPassphraseChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionViewModel vm)
            vm.PrivateKeyPassphrase = PassphraseBox.Password;
    }
}
