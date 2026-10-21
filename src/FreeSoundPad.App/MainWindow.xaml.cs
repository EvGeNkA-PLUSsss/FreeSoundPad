using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FreeSoundPad.App.ViewModels;

namespace FreeSoundPad.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        SourceInitialized += (_, _) => _viewModel.Initialize(this);
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => RestoreWindowBounds();

    private void RestoreWindowBounds()
    {
        var settings = _viewModel.Settings;
        if (settings.WindowLeft < 0 || settings.WindowTop < 0) return;

        var left = settings.WindowLeft;
        var top = settings.WindowTop;
        var width = Math.Max(MinWidth, settings.WindowWidth);
        var height = Math.Max(MinHeight, settings.WindowHeight);

        if (left + 200 > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth)
            left = Math.Max(0, SystemParameters.WorkArea.Right - width - 40);
        if (top + 150 > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
            top = Math.Max(0, SystemParameters.WorkArea.Bottom - height - 40);

        Left = left;
        Top = top;
        Width = width;
        Height = height;

        if (settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed) return;
        if (IsInteractive(e.OriginalSource)) return;
        DragMove();
    }

    private static bool IsInteractive(object source)
    {
        var current = source as DependencyObject;
        while (current != null)
        {
            if (current is ButtonBase or TextBox or ComboBox or Slider) return true;
            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) _viewModel.OnMinimized();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _viewModel.SaveWindowState(this);

        if (!_viewModel.IsShuttingDown && _viewModel.ShouldHideOnClose())
        {
            e.Cancel = true;
            return;
        }

        _viewModel.OnClosing(this);
    }

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;
        _ = _viewModel.ImportFilesAsync(files);
    }
}
