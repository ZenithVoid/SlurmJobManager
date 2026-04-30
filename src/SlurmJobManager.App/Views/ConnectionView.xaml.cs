using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using SlurmJobManager.App.ViewModels;

namespace SlurmJobManager.App.Views;

public partial class ConnectionView : UserControl
{
    private bool _isSyncingFromViewModel;
    private ConnectionViewModel? _subscribedVm;

    public ConnectionView()
    {
        InitializeComponent();
        PwdBox.PasswordChanged        += OnPasswordChanged;
        PassphraseBox.PasswordChanged += OnPassphraseChanged;
        DataContextChanged            += OnDataContextChanged;
        Loaded                        += (_, _) => SyncPasswordBoxesFromViewModel();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isSyncingFromViewModel) return;
        if (DataContext is ConnectionViewModel vm)
            vm.Password = PwdBox.Password;
    }

    private void OnPassphraseChanged(object sender, RoutedEventArgs e)
    {
        if (_isSyncingFromViewModel) return;
        if (DataContext is ConnectionViewModel vm)
            vm.PrivateKeyPassphrase = PassphraseBox.Password;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_subscribedVm != null)
            _subscribedVm.PropertyChanged -= OnViewModelPropertyChanged;

        _subscribedVm = DataContext as ConnectionViewModel;
        if (_subscribedVm != null)
            _subscribedVm.PropertyChanged += OnViewModelPropertyChanged;

        SyncPasswordBoxesFromViewModel();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConnectionViewModel.Password) or nameof(ConnectionViewModel.PrivateKeyPassphrase))
            SyncPasswordBoxesFromViewModel();
    }

    private void SyncPasswordBoxesFromViewModel()
    {
        if (DataContext is not ConnectionViewModel vm) return;

        _isSyncingFromViewModel = true;
        try
        {
            if (PwdBox.Password != vm.Password)
                PwdBox.Password = vm.Password ?? string.Empty;
            if (PassphraseBox.Password != vm.PrivateKeyPassphrase)
                PassphraseBox.Password = vm.PrivateKeyPassphrase ?? string.Empty;
        }
        finally
        {
            _isSyncingFromViewModel = false;
        }
    }
}
