using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FreeSoundPad.App.ViewModels;

namespace FreeSoundPad.App.Views;

public partial class SoundboardView : UserControl
{
    public SoundboardView()
    {
        InitializeComponent();
        SearchBox.GotKeyboardFocus += (_, _) => SearchBox.SelectAll();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        e.Handled = true;
        _ = ViewModel?.ImportFilesAsync(files);
    }
}
