using System.Windows;
using System.Windows.Input;

namespace FreeSoundPad.App.Dialogs;

public partial class TextInputWindow : Window
{
    public TextInputWindow(string header, string label, string value)
    {
        InitializeComponent();
        HeaderText.Text = header;
        LabelText.Text = label.ToUpperInvariant();
        ValueBox.Text = value;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public string Value => ValueBox.Text;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
