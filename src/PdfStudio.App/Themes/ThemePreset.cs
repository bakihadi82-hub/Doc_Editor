using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using SkiaSharp;

namespace PdfStudio.App.Themes;

public sealed record ThemePreset(
    string Id,
    string Name,
    string CategoryLabel,
    bool IsDark,
    string DeskHex,
    string SidebarHex,
    string SubtleHex,
    string ToolbarHex,
    string HairlineBorderHex,
    string FloatingBorderHex,
    string PageBorderHex,
    string PageShadowHex,
    string TextPrimaryHex,
    string TextSecondaryHex,
    string TextTertiaryHex,
    string AccentHex,
    string AccentHoverHex,
    string AccentSubtleHex,
    string AccentTextHex)
{
    public SKColor PageBorderSkColor => ParseSkColor(PageBorderHex);
    public SKColor PageShadowSkColor => ParseSkColor(PageShadowHex);

    private static SKColor ParseSkColor(string hex)
    {
        var c = Color.Parse(hex);
        return new SKColor(c.R, c.G, c.B, c.A);
    }
}

public sealed partial class ThemeOptionViewModel : ObservableObject
{
    public ThemeOptionViewModel(ThemePreset preset)
    {
        Preset = preset;
    }

    public ThemePreset Preset { get; }
    public string Id => Preset.Id;
    public string Name => Preset.Name;
    public string CategoryLabel => Preset.CategoryLabel;
    public bool IsDark => Preset.IsDark;
    public IBrush DeskBrush => new SolidColorBrush(Color.Parse(Preset.DeskHex));
    public IBrush SidebarBrush => new SolidColorBrush(Color.Parse(Preset.SidebarHex));
    public IBrush ToolbarBrush => new SolidColorBrush(Color.Parse(Preset.ToolbarHex));
    public IBrush AccentBrush => new SolidColorBrush(Color.Parse(Preset.AccentHex));
    public IBrush BorderBrush => new SolidColorBrush(Color.Parse(Preset.HairlineBorderHex));

    [ObservableProperty]
    private bool _isSelected;
}

public sealed record RecentFileEntry(
    string FilePath,
    string FileName,
    string ParentFolder,
    string LastOpenedDisplay,
    int PageCount)
{
    public string PageCountLabel => PageCount == 1 ? "1 page" : $"{PageCount} pages";
}

public sealed class UserAppSettings
{
    public string ThemeId { get; set; } = "obsidian";
    public double WindowWidth { get; set; } = 1360;
    public double WindowHeight { get; set; } = 860;
    public bool IsMaximized { get; set; }
    public double LeftSidebarWidth { get; set; } = 220;
    public bool IsLeftPanelOpen { get; set; } = true;
    public bool IsRightPanelOpen { get; set; } = true;
    public List<RecentFileEntry> RecentFiles { get; set; } = [];
}

public static class ThemeManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static ThemePreset StudioLight { get; } = new(
        Id: "studio-light",
        Name: "Studio Light",
        CategoryLabel: "Cool stone desk",
        IsDark: false,
        DeskHex: "#E5E7EB",
        SidebarHex: "#F5F6F8",
        SubtleHex: "#ECEEF2",
        ToolbarHex: "#FFFFFF",
        HairlineBorderHex: "#D1D5DB",
        FloatingBorderHex: "#C4C9D4",
        PageBorderHex: "#CBD5E1",
        PageShadowHex: "#26000000",
        TextPrimaryHex: "#111827",
        TextSecondaryHex: "#4B5563",
        TextTertiaryHex: "#6B7280",
        AccentHex: "#2563EB",
        AccentHoverHex: "#1D4ED8",
        AccentSubtleHex: "#242563EB",
        AccentTextHex: "#FFFFFF");

    public static ThemePreset WarmPaper { get; } = new(
        Id: "warm-paper",
        Name: "Warm Paper",
        CategoryLabel: "Warm parchment",
        IsDark: false,
        DeskHex: "#E8E3D9",
        SidebarHex: "#F4F0E8",
        SubtleHex: "#EDE8DE",
        ToolbarHex: "#FAF8F5",
        HairlineBorderHex: "#D6CFC2",
        FloatingBorderHex: "#C8BFA8",
        PageBorderHex: "#D0C8B8",
        PageShadowHex: "#26000000",
        TextPrimaryHex: "#292524",
        TextSecondaryHex: "#57534E",
        TextTertiaryHex: "#78716C",
        AccentHex: "#B45309",
        AccentHoverHex: "#92400E",
        AccentSubtleHex: "#24B45309",
        AccentTextHex: "#FFFFFF");

    public static ThemePreset Obsidian { get; } = new(
        Id: "obsidian",
        Name: "Obsidian",
        CategoryLabel: "Neutral charcoal",
        IsDark: true,
        DeskHex: "#0D0F12",
        SidebarHex: "#14171D",
        SubtleHex: "#101318",
        ToolbarHex: "#1C2028",
        HairlineBorderHex: "#2A303C",
        FloatingBorderHex: "#363E4D",
        PageBorderHex: "#2A303C",
        PageShadowHex: "#26000000",
        TextPrimaryHex: "#F3F4F6",
        TextSecondaryHex: "#9CA3AF",
        TextTertiaryHex: "#6B7280",
        AccentHex: "#3B82F6",
        AccentHoverHex: "#60A5FA",
        AccentSubtleHex: "#2E3B82F6",
        AccentTextHex: "#FFFFFF");

    public static ThemePreset MidnightNord { get; } = new(
        Id: "midnight-nord",
        Name: "Midnight Nord",
        CategoryLabel: "Arctic slate",
        IsDark: true,
        DeskHex: "#1E222A",
        SidebarHex: "#252A34",
        SubtleHex: "#212630",
        ToolbarHex: "#2E3440",
        HairlineBorderHex: "#3B4252",
        FloatingBorderHex: "#434C5E",
        PageBorderHex: "#3B4252",
        PageShadowHex: "#26000000",
        TextPrimaryHex: "#ECEFF4",
        TextSecondaryHex: "#D8DEE9",
        TextTertiaryHex: "#9099AB",
        AccentHex: "#88C0D0",
        AccentHoverHex: "#8FBCBB",
        AccentSubtleHex: "#2E88C0D0",
        AccentTextHex: "#1E222A");

    public static ThemePreset TokyoNight { get; } = new(
        Id: "tokyo-night",
        Name: "Tokyo Night",
        CategoryLabel: "Deep indigo",
        IsDark: true,
        DeskHex: "#16161E",
        SidebarHex: "#1A1B26",
        SubtleHex: "#181922",
        ToolbarHex: "#24283B",
        HairlineBorderHex: "#2F3549",
        FloatingBorderHex: "#3B4261",
        PageBorderHex: "#2F3549",
        PageShadowHex: "#26000000",
        TextPrimaryHex: "#C0CAF5",
        TextSecondaryHex: "#A9B1D6",
        TextTertiaryHex: "#565F89",
        AccentHex: "#7AA2F7",
        AccentHoverHex: "#89DDFF",
        AccentSubtleHex: "#2E7AA2F7",
        AccentTextHex: "#16161E");

    public static ThemePreset MonokaiDimmed { get; } = new(
        Id: "monokai-dimmed",
        Name: "Monokai Dimmed",
        CategoryLabel: "Warm carbon",
        IsDark: true,
        DeskHex: "#19181A",
        SidebarHex: "#222124",
        SubtleHex: "#1E1D20",
        ToolbarHex: "#2D2A2E",
        HairlineBorderHex: "#3A373C",
        FloatingBorderHex: "#49454B",
        PageBorderHex: "#3A373C",
        PageShadowHex: "#26000000",
        TextPrimaryHex: "#FCFCFA",
        TextSecondaryHex: "#C1C0C0",
        TextTertiaryHex: "#727072",
        AccentHex: "#FFD866",
        AccentHoverHex: "#FFE28A",
        AccentSubtleHex: "#2EFFD866",
        AccentTextHex: "#19181A");

    public static ThemePreset DefaultDark => Obsidian;
    public static ThemePreset DefaultLight => StudioLight;

    public static IReadOnlyList<ThemePreset> LightPresets { get; } =
    [
        StudioLight,
        WarmPaper
    ];

    public static IReadOnlyList<ThemePreset> DarkPresets { get; } =
    [
        Obsidian,
        MidnightNord,
        TokyoNight,
        MonokaiDimmed
    ];

    public static IReadOnlyList<ThemePreset> AllPresets { get; } =
    [
        StudioLight,
        WarmPaper,
        Obsidian,
        MidnightNord,
        TokyoNight,
        MonokaiDimmed
    ];

    public static ThemePreset GetByIdOrName(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return DefaultDark;
        }

        string trimmed = key.Trim();
        foreach (var preset in AllPresets)
        {
            if (string.Equals(preset.Id, trimmed, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(preset.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return preset;
            }
        }

        return DefaultDark;
    }

    public static void ApplyTheme(ThemePreset preset, Application? app = null)
    {
        app ??= Application.Current;
        if (app is null || !app.CheckAccess()) return;

        var targetVariant = preset.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        app.RequestedThemeVariant = targetVariant;

        var updates = BuildBrushMap(preset);

        foreach (var (key, brush) in updates)
        {
            app.Resources[key] = brush;
        }

        ApplyToResourceDictionary(app.Resources, targetVariant, updates);
    }

    private static Dictionary<string, SolidColorBrush> BuildBrushMap(ThemePreset preset)
    {
        return new Dictionary<string, SolidColorBrush>(StringComparer.Ordinal)
        {
            ["CanvasBackgroundBrush"] = new SolidColorBrush(Color.Parse(preset.DeskHex)),
            ["PanelBackgroundBrush"] = new SolidColorBrush(Color.Parse(preset.SidebarHex)),
            ["PanelSubtleBackgroundBrush"] = new SolidColorBrush(Color.Parse(preset.SubtleHex)),
            ["FloatingToolbarBrush"] = new SolidColorBrush(Color.Parse(preset.ToolbarHex)),
            ["HairlineBorderBrush"] = new SolidColorBrush(Color.Parse(preset.HairlineBorderHex)),
            ["FloatingBorderBrush"] = new SolidColorBrush(Color.Parse(preset.FloatingBorderHex)),
            ["PageBorderBrush"] = new SolidColorBrush(Color.Parse(preset.PageBorderHex)),
            ["TextPrimaryBrush"] = new SolidColorBrush(Color.Parse(preset.TextPrimaryHex)),
            ["TextSecondaryBrush"] = new SolidColorBrush(Color.Parse(preset.TextSecondaryHex)),
            ["TextTertiaryBrush"] = new SolidColorBrush(Color.Parse(preset.TextTertiaryHex)),
            ["AccentBrush"] = new SolidColorBrush(Color.Parse(preset.AccentHex)),
            ["AccentHoverBrush"] = new SolidColorBrush(Color.Parse(preset.AccentHoverHex)),
            ["AccentSubtleBrush"] = new SolidColorBrush(Color.Parse(preset.AccentSubtleHex)),
            ["AccentTextBrush"] = new SolidColorBrush(Color.Parse(preset.AccentTextHex)),
            ["ControlHoverBrush"] = new SolidColorBrush(Color.Parse(preset.IsDark ? "#14FFFFFF" : "#0F111827")),
            ["ControlPressedBrush"] = new SolidColorBrush(Color.Parse(preset.IsDark ? "#21FFFFFF" : "#1A111827")),
            ["ScrollTrackBrush"] = new SolidColorBrush(Color.Parse(preset.IsDark ? "#14FFFFFF" : "#12111827")),
            ["ScrollThumbBrush"] = new SolidColorBrush(Color.Parse(preset.IsDark ? "#599CA3AF" : "#596B7280")),
            ["ScrollThumbHoverBrush"] = new SolidColorBrush(Color.Parse(preset.IsDark ? "#99D1D5DB" : "#994B5563")),
            ["ScrollThumbPressedBrush"] = new SolidColorBrush(Color.Parse(preset.IsDark ? "#CCF3F4F6" : "#CC1F2937")),
            ["ModalScrimBrush"] = new SolidColorBrush(Color.Parse(preset.IsDark ? "#99090B0E" : "#59111827"))
        };
    }

    private static void ApplyToResourceDictionary(
        IResourceDictionary dictionary,
        ThemeVariant variant,
        IReadOnlyDictionary<string, SolidColorBrush> updates)
    {
        if (dictionary.ThemeDictionaries.TryGetValue(variant, out var themeDictProvider) &&
            themeDictProvider is IResourceDictionary themeDict)
        {
            foreach (var (key, brush) in updates)
            {
                themeDict[key] = brush;
            }
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (merged is IResourceDictionary childDict)
            {
                ApplyToResourceDictionary(childDict, variant, updates);
            }
        }
    }

    public static string GetDefaultSettingsPath()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "PdfStudio", "settings.json");
    }

    public static UserAppSettings LoadAppSettings(string? customSettingsPath = null)
    {
        string target = !string.IsNullOrWhiteSpace(customSettingsPath)
            ? customSettingsPath
            : GetDefaultSettingsPath();

        try
        {
            if (File.Exists(target))
            {
                string json = File.ReadAllText(target);
                var settings = JsonSerializer.Deserialize<UserAppSettings>(json, JsonOptions);
                if (settings is not null)
                {
                    settings.RecentFiles ??= [];
                    if (settings.WindowWidth < 640) settings.WindowWidth = 1360;
                    if (settings.WindowHeight < 480) settings.WindowHeight = 860;
                    if (settings.LeftSidebarWidth < 160 || settings.LeftSidebarWidth > 420) settings.LeftSidebarWidth = 220;
                    return settings;
                }
            }
        }
        catch
        {
            // Fall back cleanly when settings.json is absent or malformed
        }

        return new UserAppSettings();
    }

    public static void SaveAppSettings(UserAppSettings settings, string? customSettingsPath = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string target = !string.IsNullOrWhiteSpace(customSettingsPath)
            ? customSettingsPath
            : GetDefaultSettingsPath();

        try
        {
            string? dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(settings, JsonOptions);
            string tempPath = $"{target}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, target, overwrite: true);
        }
        catch
        {
            // Non-fatal if settings directory cannot be written
        }
    }

    public static ThemePreset LoadSavedTheme(string? settingsPath = null)
    {
        var settings = LoadAppSettings(settingsPath);
        return GetByIdOrName(settings.ThemeId);
    }

    public static void SaveTheme(ThemePreset preset, string? settingsPath = null)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var settings = LoadAppSettings(settingsPath);
        settings.ThemeId = preset.Id;
        SaveAppSettings(settings, settingsPath);
    }
}

