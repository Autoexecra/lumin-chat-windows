using System.Windows;
using System.Windows.Controls;

namespace LuminChatWin.App;

public sealed class AnsiTerminalBox : RichTextBox
{
    public static readonly DependencyProperty AnsiTextProperty = DependencyProperty.Register(
        nameof(AnsiText),
        typeof(string),
        typeof(AnsiTerminalBox),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender, OnAnsiTextChanged));

    private bool _updatingDocument;

    public AnsiTerminalBox()
    {
        IsReadOnly = true;
        IsUndoEnabled = false;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        Document = TerminalAnsiRenderer.Render(string.Empty);
    }

    public string AnsiText
    {
        get => (string)GetValue(AnsiTextProperty);
        set => SetValue(AnsiTextProperty, value);
    }

    private static void OnAnsiTextChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is AnsiTerminalBox box)
        {
            box.UpdateDocument(e.NewValue as string ?? string.Empty);
        }
    }

    private void UpdateDocument(string text)
    {
        if (_updatingDocument)
        {
            return;
        }

        try
        {
            _updatingDocument = true;
            Document = TerminalAnsiRenderer.Render(text);
            ScrollToEnd();
        }
        finally
        {
            _updatingDocument = false;
        }
    }
}