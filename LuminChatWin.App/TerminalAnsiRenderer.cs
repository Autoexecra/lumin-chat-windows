using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace LuminChatWin.App;

internal static class TerminalAnsiRenderer
{
    private static readonly Dictionary<int, string> ForegroundPalette = new()
    {
        [30] = "#FF1E1E1E",
        [31] = "#FFE06C75",
        [32] = "#FF98C379",
        [33] = "#FFE5C07B",
        [34] = "#FF61AFEF",
        [35] = "#FFC678DD",
        [36] = "#FF56B6C2",
        [37] = "#FFABB2BF",
        [90] = "#FF5C6370",
        [91] = "#FFFB6C6C",
        [92] = "#FF7EE787",
        [93] = "#FFF2CC60",
        [94] = "#FF79C0FF",
        [95] = "#FFD2A8FF",
        [96] = "#FF7FE9FF",
        [97] = "#FFF5FBFF",
    };

    public static FlowDocument BuildDocument(string rawText)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            PageWidth = 100000,
            TextAlignment = TextAlignment.Left,
            Background = Brushes.Transparent,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
        };
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        document.Blocks.Add(paragraph);

        var style = new TerminalTextStyle();
        var buffer = new StringBuilder();
        var lineStartIndex = 0;

        for (var index = 0; index < rawText.Length; index++)
        {
            var ch = rawText[index];
            if (ch == '\u001b')
            {
                FlushBuffer(paragraph, buffer, style);
                index = HandleEscapeSequence(rawText, index, style);
                continue;
            }

            switch (ch)
            {
                case '\a':
                    continue;
                case '\b':
                    if (buffer.Length > 0)
                    {
                        buffer.Length--;
                    }
                    else if (paragraph.Inlines.LastInline is Run run && run.Text.Length > 0)
                    {
                        run.Text = run.Text[..^1];
                    }
                    continue;
                case '\r':
                    FlushBuffer(paragraph, buffer, style);
                    RemoveCurrentLine(paragraph, ref lineStartIndex);
                    continue;
                case '\n':
                    buffer.Append(ch);
                    FlushBuffer(paragraph, buffer, style);
                    lineStartIndex = CountVisibleCharacters(paragraph);
                    continue;
                default:
                    buffer.Append(ch);
                    break;
            }
        }

        FlushBuffer(paragraph, buffer, style);
        return document;
    }

    private static int HandleEscapeSequence(string text, int index, TerminalTextStyle style)
    {
        if (index + 1 >= text.Length)
        {
            return index;
        }

        if (text[index + 1] == '[')
        {
            var end = index + 2;
            while (end < text.Length && text[end] is not ('m' or 'K' or 'J'))
            {
                end++;
            }

            if (end >= text.Length)
            {
                return text.Length - 1;
            }

            if (text[end] == 'm')
            {
                ApplySgr(text[(index + 2)..end], style);
            }

            return end;
        }

        if (text[index + 1] == ']')
        {
            var end = index + 2;
            while (end < text.Length && text[end] != '\a')
            {
                end++;
            }
            return Math.Min(end, text.Length - 1);
        }

        return index;
    }

    private static void ApplySgr(string sequence, TerminalTextStyle style)
    {
        var parts = string.IsNullOrWhiteSpace(sequence)
            ? ["0"]
            : sequence.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var code))
            {
                continue;
            }

            switch (code)
            {
                case 0:
                    style.Reset();
                    break;
                case 1:
                    style.Bold = true;
                    break;
                case 22:
                    style.Bold = false;
                    break;
                case 39:
                    style.ForegroundHex = null;
                    break;
                default:
                    if (ForegroundPalette.TryGetValue(code, out var color))
                    {
                        style.ForegroundHex = color;
                    }
                    break;
            }
        }
    }

    private static void FlushBuffer(Paragraph paragraph, StringBuilder buffer, TerminalTextStyle style)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        var run = new Run(buffer.ToString());
        run.Foreground = style.ForegroundHex is null
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFDDEFE7"))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString(style.ForegroundHex));
        run.FontWeight = style.Bold ? FontWeights.SemiBold : FontWeights.Normal;
        paragraph.Inlines.Add(run);
        buffer.Clear();
    }

    private static void RemoveCurrentLine(Paragraph paragraph, ref int lineStartIndex)
    {
        var currentLength = CountVisibleCharacters(paragraph);
        var removeCount = Math.Max(0, currentLength - lineStartIndex);
        if (removeCount == 0)
        {
            return;
        }

        while (removeCount > 0 && paragraph.Inlines.LastInline is Run run)
        {
            if (run.Text.Length <= removeCount)
            {
                removeCount -= run.Text.Length;
                paragraph.Inlines.Remove(run);
                continue;
            }

            run.Text = run.Text[..(run.Text.Length - removeCount)];
            removeCount = 0;
        }
    }

    private static int CountVisibleCharacters(Paragraph paragraph)
    {
        return paragraph.Inlines.OfType<Run>().Sum(run => run.Text.Length);
    }

    private sealed class TerminalTextStyle
    {
        public string? ForegroundHex { get; set; }
        public bool Bold { get; set; }

        public void Reset()
        {
            ForegroundHex = null;
            Bold = false;
        }
    }
}