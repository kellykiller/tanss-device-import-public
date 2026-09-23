using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TanssSystemCapture.Client;

internal static class ThemeManager
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    public static bool IsDarkMode { get; private set; } = true;

    public static event Action<bool>? ThemeChanged;

    public static void ApplyTheme(bool darkMode)
    {
        var resources = Application.Current.Resources;
        var palette = darkMode ? DarkPalette : LightPalette;

        foreach (var (key, color) in palette)
        {
            SetBrush(resources, key, color);
        }

        IsDarkMode = darkMode;

        foreach (Window window in Application.Current.Windows)
        {
            ApplyWindowChrome(window);
        }

        ThemeChanged?.Invoke(darkMode);
    }

    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) => ApplyWindowChrome(window);
    }

    public static Brush GetBrush(string resourceKey)
    {
        if (Application.Current.TryFindResource(resourceKey) is Brush brush)
        {
            return brush;
        }

        return Brushes.Transparent;
    }

    private static void SetBrush(
        ResourceDictionary resources,
        string key,
        string colorValue)
    {
        var color = (Color)ColorConverter.ConvertFromString(colorValue);

        if (resources[key] is SolidColorBrush existingBrush &&
            !existingBrush.IsFrozen)
        {
            existingBrush.Color = color;
            return;
        }

        resources[key] = new SolidColorBrush(color);
    }

    private static void ApplyWindowChrome(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;

        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = IsDarkMode ? 1 : 0;
        var result = DwmSetWindowAttribute(
            handle,
            DwmwaUseImmersiveDarkMode,
            ref enabled,
            sizeof(int));

        if (result != 0)
        {
            _ = DwmSetWindowAttribute(
                handle,
                DwmwaUseImmersiveDarkModeBefore20H1,
                ref enabled,
                sizeof(int));
        }
    }

    private static readonly IReadOnlyDictionary<string, string> DarkPalette =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WindowBackgroundBrush"] = "#181A1F",
            ["SurfaceBrush"] = "#22252B",
            ["SurfaceAltBrush"] = "#2B2F36",
            ["ControlBackgroundBrush"] = "#202329",
            ["ControlHoverBrush"] = "#343A44",
            ["BorderBrush"] = "#4A505A",
            ["PrimaryTextBrush"] = "#F3F4F6",
            ["SecondaryTextBrush"] = "#B7BDC8",
            ["AccentBrush"] = "#4EA1FF",
            ["AccentHoverBrush"] = "#86C1FF",
            ["SelectionBrush"] = "#355A7F",
            ["InfoBackgroundBrush"] = "#202D3A",
            ["InfoBorderBrush"] = "#527DA8",
            ["WarningBackgroundBrush"] = "#3A2D1C",
            ["WarningBorderBrush"] = "#B48344",
            ["WarningTextBrush"] = "#F2C078",
            ["DangerBackgroundBrush"] = "#3A2225",
            ["DangerBorderBrush"] = "#A85A61",
            ["ErrorTextBrush"] = "#FF9BA3",
            ["SuccessBackgroundBrush"] = "#1F3327",
            ["SuccessBorderBrush"] = "#4F9865",
            ["SuccessTextBrush"] = "#79D99A",
            ["ToggleOffBrush"] = "#5B616C",
            ["ToggleThumbBrush"] = "#FFFFFF"
        };

    private static readonly IReadOnlyDictionary<string, string> LightPalette =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WindowBackgroundBrush"] = "#F5F7FA",
            ["SurfaceBrush"] = "#FFFFFF",
            ["SurfaceAltBrush"] = "#F1F3F5",
            ["ControlBackgroundBrush"] = "#FFFFFF",
            ["ControlHoverBrush"] = "#E7EDF5",
            ["BorderBrush"] = "#C4CAD3",
            ["PrimaryTextBrush"] = "#1B1F24",
            ["SecondaryTextBrush"] = "#56606B",
            ["AccentBrush"] = "#0969DA",
            ["AccentHoverBrush"] = "#0550AE",
            ["SelectionBrush"] = "#CFE8FF",
            ["InfoBackgroundBrush"] = "#EDF6FF",
            ["InfoBorderBrush"] = "#8BA9C7",
            ["WarningBackgroundBrush"] = "#FFF8F1",
            ["WarningBorderBrush"] = "#D7B58C",
            ["WarningTextBrush"] = "#8A4B08",
            ["DangerBackgroundBrush"] = "#FFF0F1",
            ["DangerBorderBrush"] = "#C56B6B",
            ["ErrorTextBrush"] = "#9A0000",
            ["SuccessBackgroundBrush"] = "#ECF8EE",
            ["SuccessBorderBrush"] = "#4D8C57",
            ["SuccessTextBrush"] = "#176B2C",
            ["ToggleOffBrush"] = "#AAB1BA",
            ["ToggleThumbBrush"] = "#FFFFFF"
        };

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}

