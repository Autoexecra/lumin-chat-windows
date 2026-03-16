using System.Windows;
using System.Windows.Controls;

namespace LuminChatWin.App;

public sealed class AnsiTerminalBox : RichTextBox
{
    public static readonly DependencyProperty AnsiTextProperty = DependencyProperty.Register(
        nameof(AnsiText),
        typeof(string),
        typeof(AnsiTerminalBox),
        new PropertyMetadata(string.Empty, OnAnsiTextChanged));

    public string AnsiText
    {
        get => (string)GetValue(AnsiTextProperty);
        set => SetValue(AnsiTextProperty, value);
    }

    public AnsiTerminalBox()
    {
        IsReadOnly = true;
        IsUndoEnabled = false;
        BorderThickness = new Thickness(0);
        Background = null;
    }

    private static void OnAnsiTextChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not AnsiTerminalBox terminalBox)
        {
            return;
        }

        terminalBox.Document = TerminalAnsiRenderer.BuildDocument(e.NewValue as string ?? string.Empty);
        terminalBox.CaretPosition = terminalBox.Document.ContentEnd;
        terminalBox.ScrollToEnd();
    }
}