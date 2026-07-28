using MudBlazor;

namespace VegaBridgeApp.Themes;

/// <summary>
/// Dark, high-contrast theme for motorcycle outdoor use.
/// </summary>
public static class VegaTheme
{
    public static MudTheme Create() => new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#E65100",
            PrimaryContrastText = "#FFFFFF",
            Secondary = "#00838F",
            SecondaryContrastText = "#FFFFFF",
            Tertiary = "#B71C1C",
            TertiaryContrastText = "#FFFFFF",
            Success = "#2E7D32",
            Warning = "#E65100",
            Error = "#C62828",
            Info = "#1565C0",
        },

        PaletteDark = new PaletteDark
        {
            Primary = "#FF8F00",
            PrimaryContrastText = "#1A1A1A",
            PrimaryDarken = "#E65100",
            PrimaryLighten = "#FFB300",
            Secondary = "#00BCD4",
            SecondaryContrastText = "#1A1A1A",
            Tertiary = "#D32F2F",
            TertiaryContrastText = "#FFFFFF",
            Background = "#121212",
            Surface = "#1E1E1E",
            DrawerBackground = "#181818",
            AppbarBackground = "#181818",
            TextPrimary = "rgba(255,255,255,0.90)",
            TextSecondary = "rgba(255,255,255,0.60)",
            TextDisabled = "rgba(255,255,255,0.30)",
            Success = "#4CAF50",
            Warning = "#FF9800",
            Error = "#F44336",
            Info = "#2196F3",
            Divider = "rgba(255,255,255,0.08)",
            DividerLight = "rgba(255,255,255,0.05)",
            LinesDefault = "rgba(255,255,255,0.10)",
            LinesInputs = "rgba(255,255,255,0.15)",
            ActionDefault = "rgba(255,255,255,0.40)",
            HoverOpacity = 0.06,
            GrayDark = "rgba(255,255,255,0.12)",
            GrayLight = "rgba(255,255,255,0.08)",
        },

        Typography = new Typography
        {
            Default = new DefaultTypography { FontFamily = ["Segoe UI", "system-ui", "sans-serif"] },
            H1 = new H1Typography { FontSize = "2.5rem" },
            H2 = new H2Typography { FontSize = "2rem" },
            H3 = new H3Typography { FontSize = "1.5rem" },
            H4 = new H4Typography { FontSize = "1.25rem" },
            H5 = new H5Typography { FontSize = "1.1rem" },
            H6 = new H6Typography { FontSize = "1rem" },
        },

        LayoutProperties = new LayoutProperties { DefaultBorderRadius = "8px" },
    };
}
