using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace LuminChatWin.App;

internal static class TerminalAnsiRenderer
{
    private static readonly Brush DefaultForeground = CreateBrush(0xDD, 0xEF, 0xE7);
    private static readonly Brush DefaultBackground = Brushes.Transparent;
    private static readonly Brush[] Palette =
    [
        CreateBrush(0x17, 0x20, 0x2A),
        CreateBrush(0xE0, 0x6C, 0x75),
        CreateBrush(0x98, 0xC3, 0x79),
        CreateBrush(0xE5, 0xC0, 0x7B),
        CreateBrush(0x61, 0xAF, 0xEF),
        CreateBrush(0xC6, 0x78, 0xDD),
        CreateBrush(0x56, 0xB6, 0xC2),
        CreateBrush(0xAB, 0xB2, 0xBF),
        CreateBrush(0x5C, 0x63, 0x70),
        CreateBrush(0xFF, 0x7B, 0x72),
        CreateBrush(0xB8, 0xE9, 0x86),
        CreateBrush(0xFF, 0xD8, 0x66),
        CreateBrush(0x82, 0xC4, 0xFF),
        CreateBrush(0xE3, 0x9D, 0xFF),
        CreateBrush(0x7F, 0xDB, 0xCA),
        CreateBrush(0xFF, 0xFF, 0xFF),
    ];

    public static FlowDocument Render(string? text)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            Background = Brushes.Transparent,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            LineHeight = 16,
        };

        var paragraph = new Paragraph { Margin = new Thickness(0) };
        document.Blocks.Add(paragraph);

        if (string.IsNullOrEmpty(text))
        {
            return document;
        }

        var state = new RenderState();
        var buffer = new StringBuilder();
        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (current == '\u001b')
            {
                Flush(paragraph, buffer, state);
                if (TryReadCsi(text, ref index, out var sequence))
                {
                    ApplyCsi(sequence, state);
                    continue;
                }

                if (TrySkipOsc(text, ref index))
                {
                    continue;
                }
            }

            buffer.Append(current);
        }

        Flush(paragraph, buffer, state);
        return document;
    }

    private static bool TryReadCsi(string text, ref int index, out string sequence)
    {
        sequence = string.Empty;
        if (index + 1 >= text.Length || text[index + 1] != '[')
        {
            return false;
        }

        var end = index + 2;
        while (end < text.Length)
        {
            var current = text[end];
            if (current >= '@' && current <= '~')
            {
                sequence = text[(index + 2)..end];
                index = end;
                return current == 'm';
            }

            end++;
        }

        return false;
    }

    private static bool TrySkipOsc(string text, ref int index)
    {
        if (index + 1 >= text.Length || text[index + 1] != ']')
        {
            return false;
        }

        var end = index + 2;
        while (end < text.Length)
        {
            if (text[end] == '\a')
            {
                index = end;
                return true;
            }

            if (text[end] == '\u001b' && end + 1 < text.Length && text[end + 1] == '\\')
            {
                index = end + 1;
                return true;
            }

            end++;
        }

        return true;
    }

    private static void ApplyCsi(string sequence, RenderState state)
    {
        var parts = string.IsNullOrWhiteSpace(sequence)
            ? ["0"]
            : sequence.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], out var code))
            {
                continue;
            }

            switch (code)
            {
                case 0:
                    state.Reset();
                    break;
                case 1:
                    state.Bold = true;
                    break;
                case 22:
                    state.Bold = false;
                    break;
                case 30 when Palette.Length > 0:
                case 31:
                case 32:
                case 33:
                case 34:
                case 35:
                case 36:
                case 37:
                    state.Foreground = Palette[code - 30];
                    break;
                case 39:
                    state.Foreground = DefaultForeground;
                    break;
                case 40:
                case 41:
                case 42:
                case 43:
                case 44:
                case 45:
                case 46:
                case 47:
                    state.Background = Palette[code - 40];
                    break;
                case 49:
                    state.Background = DefaultBackground;
                    break;
                case 90:
                case 91:
                case 92:
                case 93:
                case 94:
                case 95:
                case 96:
                case 97:
                    state.Foreground = Palette[8 + (code - 90)];
                    break;
                case 100:
                case 101:
                case 102:
                case 103:
                case 104:
                case 105:
                case 106:
                case 107:
                    state.Background = Palette[8 + (code - 100)];
                    break;
                case 38 when index + 2 < parts.Length && parts[index + 1] == "5" && int.TryParse(parts[index + 2], out var fgIndex):
                    state.Foreground = From256Color(fgIndex);
                    index += 2;
                    break;
                case 48 when index + 2 < parts.Length && parts[index + 1] == "5" && int.TryParse(parts[index + 2], out var bgIndex):
                    state.Background = From256Color(bgIndex);
                    index += 2;
                    break;
            }
        }
    }

    private static void Flush(Paragraph paragraph, StringBuilder buffer, RenderState state)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        var run = new Run(buffer.ToString())
        {
            Foreground = state.Foreground,
            Background = state.Background,
            FontWeight = state.Bold ? FontWeights.Bold : FontWeights.Normal,
        };
        paragraph.Inlines.Add(run);
        buffer.Clear();
    }

    private static Brush From256Color(int index)
    {
        if (index < 0)
        {
            return DefaultForeground;
        }

        if (index < 16)
        {
            return Palette[Math.Min(index, Palette.Length - 1)];
        }

        if (index < 232)
        {
            var colorIndex = index - 16;
            var r = colorIndex / 36;
            var g = (colorIndex % 36) / 6;
            var b = colorIndex % 6;
            return CreateBrush((byte)(r == 0 ? 0 : r * 40 + 55), (byte)(g == 0 ? 0 : g * 40 + 55), (byte)(b == 0 ? 0 : b * 40 + 55));
        }

        var level = (byte)(8 + (index - 232) * 10);
        return CreateBrush(level, level, level);
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private sealed class RenderState
    {
        public Brush Foreground { get; set; } = DefaultForeground;
        public Brush Background { get; set; } = DefaultBackground;
        public bool Bold { get; set; }

        public void Reset()
        {
            Foreground = DefaultForeground;
            Background = DefaultBackground;
            Bold = false;
        }
    }
}
