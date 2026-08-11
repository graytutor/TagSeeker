using System.Windows;
using System.Windows.Input;

namespace CustomImageViewer;

public partial class TextInputWindow : Window
{
    public string Value => ValueBox.Text.Trim();

    public TextInputWindow(string title, string prompt, string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => Accept();

    private void ValueBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Accept();
        e.Handled = true;
    }

    private void Accept()
    {
        if (string.IsNullOrWhiteSpace(Value)) return;
        DialogResult = true;
    }
}
