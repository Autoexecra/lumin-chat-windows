using System.Windows;

namespace LuminChatWin.App;

public partial class TextPromptWindow : Window
{
    public TextPromptWindow(string title, string prompt, string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptTitleTextBlock.Text = title;
        PromptTextBlock.Text = prompt;
        ResponseTextBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            ResponseTextBox.Focus();
            ResponseTextBox.SelectAll();
        };
    }

    public string ResponseText => ResponseTextBox.Text;

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}