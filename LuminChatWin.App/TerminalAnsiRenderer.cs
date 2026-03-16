using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace LuminChatWin.App;

internal static class TerminalAnsiRenderer
{
    private static readonly Color DefaultForeground = Color.FromRgb(0xDD, 0xEF, 0xE7);
    private static readonly Color? DefaultBackground = null;
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x17, 0x20, 0x2A),
        Color.FromRgb(0xE0, 0x6C, 0x75),
        Color.FromRgb(0x98, 0xC3, 0x79),
        Color.FromRgb(0xE5, 0xC0, 0x7B),
        Color.FromRgb(0x61, 0xAF, 0xEF),
        Color.FromRgb(0xC6, 0x78, 0xDD),
        Color.FromRgb(0x56, 0xB6, 0xC2),
        Color.FromRgb(0xAB, 0xB2, 0xBF),
        Color.FromRgb(0x5C, 0x63, 0x70),
        Color.FromRgb(0xFF, 0x7B, 0x72),
        Color.FromRgb(0xB8, 0xE9, 0x86),
        Color.FromRgb(0xFF, 0xD8, 0x66),
        Color.FromRgb(0x82, 0xC4, 0xFF),
        Color.FromRgb(0xE3, 0x9D, 0xFF),
        Color.FromRgb(0x7F, 0xDB, 0xCA),
        Color.FromRgb(0xFF, 0xFF, 0xFF),
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
        var lines = new List<List<StyledCharacter>> { new() };
        var currentLine = lines[0];
        var cursorColumn = 0;

        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (current == '\u001b')
            {
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

            switch (current)
            {
                case '\a':
                    break;
                case '\r':
                    cursorColumn = 0;
                    break;
                case '\n':
                    currentLine = new List<StyledCharacter>();
                    lines.Add(currentLine);
                    cursorColumn = 0;
                    break;
                case '\b':
                    if (cursorColumn > 0)
                    {
                        cursorColumn--;
                        if (cursorColumn < currentLine.Count)
                        {
                            currentLine.RemoveAt(cursorColumn);
                        }
                    }
                    break;
                default:
                    var styledCharacter = new StyledCharacter(current, state.ToSpec());
                    if (cursorColumn < currentLine.Count)
                    {
                        currentLine[cursorColumn] = styledCharacter;
                    }
                    else
                    {
                        currentLine.Add(styledCharacter);
                    }

                    cursorColumn++;
                    break;
            }
        }

        WriteLines(paragraph, lines);
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

    private static void WriteLines(Paragraph paragraph, IReadOnlyList<List<StyledCharacter>> lines)
    {
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            if (line.Count > 0)
            {
                var buffer = new StringBuilder();
                var currentStyle = line[0].Style;
                foreach (var item in line)
                {
                    if (!item.Style.Equals(currentStyle))
                    {
                        paragraph.Inlines.Add(CreateRun(buffer.ToString(), currentStyle));
                        buffer.Clear();
                        currentStyle = item.Style;
                    }

                    buffer.Append(item.Character);
                }

                if (buffer.Length > 0)
                {
                    paragraph.Inlines.Add(CreateRun(buffer.ToString(), currentStyle));
                }
            }

            if (lineIndex < lines.Count - 1)
            {
                paragraph.Inlines.Add(new LineBreak());
            }
        }
    }

    private static Run CreateRun(string text, StyleSpec style)
    {
        return new Run(text)
        {
            Foreground = CreateBrush(style.Foreground),
            Background = style.Background is Color background ? CreateBrush(background) : Brushes.Transparent,
            FontWeight = style.Bold ? FontWeights.Bold : FontWeights.Normal,
        };
    }

    private static Color From256Color(int index)
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
            return Color.FromRgb((byte)(r == 0 ? 0 : r * 40 + 55), (byte)(g == 0 ? 0 : g * 40 + 55), (byte)(b == 0 ? 0 : b * 40 + 55));
        }

        var level = (byte)(8 + (index - 232) * 10);
        return Color.FromRgb(level, level, level);
    }

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private sealed class RenderState
    {
        public Color Foreground { get; set; } = DefaultForeground;
        public Color? Background { get; set; } = DefaultBackground;
        public bool Bold { get; set; }

        public void Reset()
        {
            Foreground = DefaultForeground;
            Background = DefaultBackground;
            Bold = false;
        }

        public StyleSpec ToSpec() => new(Foreground, Background, Bold);
    }

    private readonly record struct StyleSpec(Color Foreground, Color? Background, bool Bold);

    private readonly record struct StyledCharacter(char Character, StyleSpec Style);
}
