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
        IsReadOnlyCaretVisible = true;
        IsUndoEnabled = false;
        Focusable = true;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        Document = TerminalAnsiRenderer.Render(string.Empty);
        PreviewMouseDown += OnPreviewMouseDown;
        GotKeyboardFocus += OnGotKeyboardFocus;
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
            PlaceCaretAtEnd();
            ScrollToEnd();
        }
        finally
        {
            _updatingDocument = false;
        }
    }

    public void FocusTerminalInput()
    {
        Focus();
        PlaceCaretAtEnd();
        ScrollToEnd();
    }

    private void OnPreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        FocusTerminalInput();
    }

    private void OnGotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        PlaceCaretAtEnd();
    }

    private void PlaceCaretAtEnd()
    {
        var end = Document?.ContentEnd;
        if (end is null)
        {
            return;
        }

        Selection.Select(end, end);
        CaretPosition = end;
    }
}