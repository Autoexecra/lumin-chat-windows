using System.Windows;
using System.Windows.Media;

namespace LuminChatWin.App;

public sealed record ThemePalette(
    string Id,
    string Name,
    string Background,
    string Surface,
    string Panel,
    string Accent,
    string AccentSoft,
    string Text,
    string Muted,
    string Border,
    string Success,
    string Warning,
    string Error,
    string FontFamily);

public static class ThemeManager
{
    public static IReadOnlyList<ThemePalette> Themes { get; } =
    [
        new("sandstone-study", "Sandstone Study", "#FFF6F1E8", "#FFFFFBF5", "#FFF2E6D7", "#FF184E77", "#FFD4E2ED", "#FF1F2A30", "#FF6C777F", "#FFD8C7B2", "#FF2F6A4F", "#FFAA6A12", "#FF9E3030", "Segoe UI"),
        new("noir-editor", "Noir Editor", "#FF0E1014", "#FF171A21", "#FF202530", "#FF6EE7F2", "#FF213F47", "#FFF2F6FB", "#FFA7B1C2", "#FF344255", "#FF54C28C", "#FFE0B454", "#FFEC6A88", "Consolas"),
        new("mint-glass", "Mint Glass", "#FFEFFAF6", "#FFFFFFFF", "#FFDFF4ED", "#FF0F8B6D", "#FFCDEBE2", "#FF16312A", "#FF5B766D", "#FFB7D7CB", "#FF299A74", "#FFCA8A04", "#FFD14343", "Segoe UI"),
        new("sunset-drive", "Sunset Drive", "#FFFFF1E8", "#FFFFFAF6", "#FFFFDFC8", "#FFCB5A2E", "#FFF6C3A7", "#FF3D241A", "#FF8F6F5F", "#FFE9B89F", "#FF3B8B6A", "#FFD88A1D", "#FFC94A55", "Trebuchet MS"),
        new("sakura-paper", "Sakura Paper", "#FFFFF4F8", "#FFFFFBFD", "#FFF6DCE8", "#FFB54D7A", "#FFF2C4D8", "#FF3E2731", "#FF8A6877", "#FFE9B9CF", "#FF3F9B7A", "#FFCC8B27", "#FFC84C63", "Yu Gothic UI"),
        new("iceberg-lab", "Iceberg Lab", "#FFF1F7FB", "#FFFCFEFF", "#FFD9EAF5", "#FF2F6FA3", "#FFC7DFF0", "#FF1A2C38", "#FF607889", "#FFB7D0E0", "#FF318770", "#FFB8841D", "#FFC74B4B", "Bahnschrift"),
        new("forest-fog", "Forest Fog", "#FFF1F5EF", "#FFFBFDFC", "#FFDCE8DE", "#FF3F6B4F", "#FFC9DCCF", "#FF243128", "#FF6F7C73", "#FFB9C9BB", "#FF4D8A5F", "#FFB8822F", "#FFB94A4A", "Segoe UI"),
        new("retro-terminal", "Retro Terminal", "#FF151A14", "#FF1F261D", "#FF2D3828", "#FF7CE38B", "#FF334B35", "#FFEAF7EA", "#FFA1B5A2", "#FF4C6250", "#FF58B168", "#FFD0B04A", "#FFE46A6A", "Cascadia Mono"),
        new("royal-velvet", "Royal Velvet", "#FFF6F1FA", "#FFFEFBFF", "#FFE7D9F1", "#FF6C3C91", "#FFDCC6EE", "#FF2E2038", "#FF776582", "#FFC9B1DA", "#FF44856D", "#FFC58A1F", "#FFD25368", "Georgia"),
        new("ocean-board", "Ocean Board", "#FFEFF8FA", "#FFFCFEFF", "#FFD8ECF0", "#FF147A8A", "#FFBFE4EB", "#FF1B3135", "#FF63797C", "#FFB1D3D7", "#FF2D8B6E", "#FFBF861C", "#FFCE4E58", "Segoe UI"),
        new("amber-ledger", "Amber Ledger", "#FFFFF8ED", "#FFFFFDF8", "#FFF7E6C9", "#FF9B6B17", "#FFF0D39A", "#FF3C2D18", "#FF88755A", "#FFE0C392", "#FF387E60", "#FFC5871A", "#FFC95252", "Cambria"),
        new("midnight-neon", "Midnight Neon", "#FF0A0F1D", "#FF121A2F", "#FF1A2542", "#FF5B8CFF", "#FF243768", "#FFF5F8FF", "#FF9CA9CB", "#FF33446F", "#FF3ECF8E", "#FFF4B740", "#FFFF6B8B", "Segoe UI"),
        new("terracotta-studio", "Terracotta Studio", "#FFFBF1ED", "#FFFFFBFA", "#FFF2D8CF", "#FFB85C3F", "#FFECC2B5", "#FF382620", "#FF866A63", "#FFD8B5A8", "#FF3F8A6A", "#FFCF8922", "#FFC85050", "Segoe UI"),
        new("lilac-circuit", "Lilac Circuit", "#FFF6F4FF", "#FFFEFDFF", "#FFE6E1F8", "#FF6B62C5", "#FFD6D0F5", "#FF29263E", "#FF77728F", "#FFC7C0E9", "#FF4A9274", "#FFC5962D", "#FFD25A72", "Segoe UI"),
        new("meadow-postcard", "Meadow Postcard", "#FFF5F7EA", "#FFFFFEFA", "#FFE8ECCB", "#FF748334", "#FFD5DDA8", "#FF31341D", "#FF7C7F61", "#FFC5CB9D", "#FF4B8A58", "#FFC58E1F", "#FFC24F56", "Calibri"),
        new("coral-wave", "Coral Wave", "#FFFFF2EF", "#FFFFFCFB", "#FFFBDDD6", "#FFDA6B5A", "#FFF5C2B9", "#FF412724", "#FF926A64", "#FFE6B6AF", "#FF3A8C72", "#FFD08823", "#FFD45061", "Segoe UI"),
        new("steel-blueprint", "Steel Blueprint", "#FFF0F4F7", "#FFFBFCFD", "#FFD9E2EA", "#FF3A607F", "#FFC7D6E5", "#FF1F2C35", "#FF687985", "#FFB3C5D3", "#FF3D8B73", "#FFB88724", "#FFC44E55", "Bahnschrift"),
        new("grape-soda", "Grape Soda", "#FFF9F1FB", "#FFFFFBFF", "#FFF0D7F4", "#FF9A49B3", "#FFE4BAEF", "#FF372239", "#FF86698A", "#FFD9A9E1", "#FF40906C", "#FFC58B1B", "#FFD6546D", "Segoe UI"),
        new("desert-night", "Desert Night", "#FF171310", "#FF231D18", "#FF342A22", "#FFD08E42", "#FF4E3827", "#FFF8F0E7", "#FFC0AA97", "#FF5E4A39", "#FF56B184", "#FFE0AD48", "#FFEF7A6E", "Segoe UI"),
        new("aqua-notebook", "Aqua Notebook", "#FFEFFBFB", "#FFFCFFFF", "#FFD7F1F0", "#FF149AA4", "#FFB8E7E4", "#FF1A3132", "#FF607D7E", "#FFAED5D3", "#FF2A8F72", "#FFC48F21", "#FFD24F57", "Segoe UI")
    ];

    public static ThemePalette GetTheme(string? themeId)
    {
        return Themes.FirstOrDefault(theme => string.Equals(theme.Id, themeId, StringComparison.OrdinalIgnoreCase)) ?? Themes[0];
    }

    public static void ApplyTheme(ResourceDictionary resources, string? themeId)
    {
        var theme = GetTheme(themeId);
        ApplyColor(resources, "ColorBackground", theme.Background);
        ApplyColor(resources, "ColorSurface", theme.Surface);
        ApplyColor(resources, "ColorPanel", theme.Panel);
        ApplyColor(resources, "ColorAccent", theme.Accent);
        ApplyColor(resources, "ColorAccentSoft", theme.AccentSoft);
        ApplyColor(resources, "ColorText", theme.Text);
        ApplyColor(resources, "ColorMuted", theme.Muted);
        ApplyColor(resources, "ColorBorder", theme.Border);
        ApplyColor(resources, "ColorSuccess", theme.Success);
        ApplyColor(resources, "ColorWarning", theme.Warning);
        ApplyColor(resources, "ColorError", theme.Error);
        resources["BackgroundBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.Background));
        resources["SurfaceBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.Surface));
        resources["PanelBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.Panel));
        resources["AccentBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.Accent));
        resources["AccentSoftBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.AccentSoft));
        resources["TextBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.Text));
        resources["MutedBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.Muted));
        resources["BorderBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.Border));
        resources["SuccessBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.Success));
        resources["WarningBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.Warning));
        resources["ErrorBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.Error));
        resources["AppFontFamily"] = new FontFamily(theme.FontFamily);
    }

    private static void ApplyColor(ResourceDictionary resources, string key, string value)
    {
        resources[key] = (Color)ColorConverter.ConvertFromString(value);
    }
}