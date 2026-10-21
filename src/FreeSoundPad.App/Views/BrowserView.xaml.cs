using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FreeSoundPad.App.ViewModels;

namespace FreeSoundPad.App.Views;

public partial class BrowserView : UserControl
{
    private bool _attached;

    public BrowserView()
    {
        InitializeComponent();
        UrlBox.GotKeyboardFocus += (_, _) => UrlBox.SelectAll();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_attached) return;
        _attached = true;

        if (DataContext is MainViewModel viewModel)
        {
            viewModel.AttachBrowser(Web);
        }
    }

    private void OnUrlKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.NavigateAddressCommand.Execute(null);
        }

        e.Handled = true;
    }
}
