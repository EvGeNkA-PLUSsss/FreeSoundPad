using System.Windows;
using System.Windows.Input;
using FreeSoundPad.App.ViewModels;

namespace FreeSoundPad.App;

public partial class OverlayWindow : Window
{
    private readonly MainViewModel _viewModel;

    public OverlayWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Hide();
        _viewModel.OnOverlayHidden();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_viewModel.IsShuttingDown) return;

        e.Cancel = true;
        Hide();
        _viewModel.OnOverlayHidden();
    }
}
