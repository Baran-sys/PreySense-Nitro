using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using PreySense.Core.Configuration;

namespace PreySense.Frontend.Services;

public static class ThemeManager
{
    public static bool IsLightTheme { get; private set; }
    public static bool IsGlassEffectEnabled { get; private set; }

    private static readonly IReadOnlyDictionary<string, (Color Dark, Color Light)> Palette =
        new Dictionary<string, (Color, Color)>
        {
            ["WindowBgColor"] = (Parse("#1C1C1C"), Parse("#F5F6F8")),
            ["CardBgColor"] = (Parse("#1C1C1C"), Parse("#FFFFFF")),
            ["ButtonMainColor"] = (Parse("#2E2E2E"), Parse("#FFFFFF")),
            ["ButtonSecondColor"] = (Parse("#242424"), Parse("#EEF0F3")),
            ["ButtonHoverColor"] = (Parse("#383838"), Parse("#E2E6EB")),
            ["BorderMainColor"] = (Parse("#373737"), Parse("#B9C0C9")),
            ["BorderSecondColor"] = (Parse("#2A2A2A"), Parse("#D8DDE3")),
            ["TextPrimaryColor"] = (Parse("#F0F0F0"), Parse("#17191C")),
            ["IconForegroundColor"] = (Parse("#FFFFFF"), Parse("#000000")),
            ["TextSecondaryColor"] = (Parse("#A8A8A8"), Parse("#535B66")),
            ["TextMutedColor"] = (Parse("#787878"), Parse("#747E8A")),
            ["TextOnAccentColor"] = (Parse("#FFFFFF"), Parse("#17191C")),
            ["ControlTrackColor"] = (Parse("#282B32"), Parse("#DDE1E6")),
            ["DisabledControlColor"] = (Parse("#3A3A3A"), Parse("#C5CAD1")),
            ["ControlTrackFillColor"] = (Parse("#4A5260"), Parse("#98A3B0")),
            ["ControlThumbColor"] = (Parse("#5C6576"), Parse("#FFFFFF")),
            ["ControlThumbHoverColor"] = (Parse("#758197"), Parse("#F7F9FB")),
            ["ControlThumbPressedColor"] = (Parse("#8B97AF"), Parse("#E9EDF2")),
            ["ControlSelectedColor"] = (Parse("#333742"), Parse("#DCEEFF")),
            ["ControlSelectedBorderColor"] = (Parse("#505766"), Parse("#78BDF0")),
            ["ControlFocusedBorderColor"] = (Parse("#4E5564"), Parse("#65ADE5")),
            ["ControlActiveColor"] = (Parse("#3E4450"), Parse("#D7E9F7")),
            ["ControlThumbBorderColor"] = (Parse("#EAEFF5"), Parse("#66717E")),
            ["ToastSurfaceColor"] = (Parse("#D8121216"), Parse("#F2FFFFFF")),
            ["PopupSurfaceColor"] = (Parse("#1C1C1C"), Parse("#F5F6F8")),
            ["WarningSurfaceColor"] = (Parse("#2A1810"), Parse("#FFF4E8")),
            ["WarningSurfaceSubtleColor"] = (Parse("#251A10"), Parse("#FFF8EF")),
            ["WarningTextColor"] = (Parse("#F5B041"), Parse("#8A4500")),
            ["SuccessSurfaceColor"] = (Parse("#142618"), Parse("#EAF8EF")),
            ["SuccessTextColor"] = (Parse("#82E0AA"), Parse("#176B38")),
            ["PlaceholderTextColor"] = (Parse("#8F8F8F"), Parse("#69727D"))
        };

    public static bool LoadAndApply()
    {
        var isLight = false;
        var glass = false;
        try
        {
            var gen = ConfigurationService.Instance.GetCurrent().General;
            isLight = gen.LightTheme;
            glass = gen.GlassEffect;
        }
        catch { }

        IsGlassEffectEnabled = glass;
        Apply(isLight, persist: false);
        return isLight;
    }

    public static void ApplyGlassEffect(bool enabled, bool persist = true)
    {
        IsGlassEffectEnabled = enabled;
        Apply(IsLightTheme, persist: false);

        if (persist)
        {
            try
            {
                ConfigurationService.Instance.Update(cfg => cfg.General.GlassEffect = enabled);
            }
            catch { }
        }
    }

    public static void Apply(bool isLight, bool persist = true)
    {
        IsLightTheme = isLight;
        TrayIconManager.ApplyMenuTheme(isLight);
        if (Application.Current is not null)
        {
            foreach (var (key, colors) in Palette)
            {
                var color = isLight ? colors.Light : colors.Dark;
                Application.Current.Resources[key] = color;
                SetBrush(key.Replace("Color", "Brush", StringComparison.Ordinal), color);
            }

            if (IsGlassEffectEnabled)
            {
                // In glass mode, leave the WPF window background completely transparent in dark mode
                // so the Windows 11 DWM Acrylic live composition shines through with maximum clarity and luminosity.
                var glassWindowBg = isLight ? Parse("#10F5F6F8") : Parse("#00000000");
                var glassCardBg = isLight ? Parse("#30FFFFFF") : Parse("#0EFFFFFF");

                Application.Current.Resources["WindowBgColor"] = glassWindowBg;
                SetBrush("WindowBgBrush", glassWindowBg);

                Application.Current.Resources["CardBgColor"] = glassCardBg;
                SetBrush("CardBgBrush", glassCardBg);

                Brush glassWindowBorder = isLight
                    ? new LinearGradientBrush(
                        new GradientStopCollection
                        {
                            new GradientStop(Color.FromArgb(90, 0, 0, 0), 0.0),
                            new GradientStop(Color.FromArgb(30, 0, 0, 0), 1.0)
                        },
                        new Point(0, 0), new Point(0, 1))
                    : new LinearGradientBrush(
                        new GradientStopCollection
                        {
                            new GradientStop(Color.FromArgb(55, 255, 255, 255), 0.0), // Soft glass rim on top
                            new GradientStop(Color.FromArgb(18, 255, 255, 255), 1.0)  // Subtle lower edge reflection
                        },
                        new Point(0, 0), new Point(0, 1));

                // Fluent frosted glass buttons with soft top sheen and high background translucency
                // so the acrylic blur seamlessly diffuses across the entire window like the Windows 11 Start Menu.
                Brush glassButtonRaised = isLight
                    ? new LinearGradientBrush(
                        new GradientStopCollection
                        {
                            new GradientStop(Color.FromArgb(140, 255, 255, 255), 0.0),
                            new GradientStop(Color.FromArgb(90, 240, 242, 246), 1.0)
                        },
                        new Point(0, 0), new Point(0, 1))
                    : new LinearGradientBrush(
                        new GradientStopCollection
                        {
                            new GradientStop(Color.FromArgb(24, 255, 255, 255), 0.0), // Delicate frosted top sheen
                            new GradientStop(Color.FromArgb(10, 255, 255, 255), 1.0)  // Translucent acrylic plate
                        },
                        new Point(0, 0), new Point(0, 1));

                Brush glassButtonRaisedBorder = isLight
                    ? new LinearGradientBrush(
                        new GradientStopCollection
                        {
                            new GradientStop(Color.FromArgb(70, 0, 0, 0), 0.0),
                            new GradientStop(Color.FromArgb(25, 0, 0, 0), 1.0)
                        },
                        new Point(0, 0), new Point(0, 1))
                    : new LinearGradientBrush(
                        new GradientStopCollection
                        {
                            new GradientStop(Color.FromArgb(38, 255, 255, 255), 0.0), // Soft glass bevel highlight
                            new GradientStop(Color.FromArgb(12, 255, 255, 255), 1.0)  // Subtle glass bevel edge
                        },
                        new Point(0, 0), new Point(0, 1));

                Brush glassButtonHover = isLight
                    ? new LinearGradientBrush(
                        new GradientStopCollection
                        {
                            new GradientStop(Color.FromArgb(200, 255, 255, 255), 0.0),
                            new GradientStop(Color.FromArgb(150, 245, 247, 250), 1.0)
                        },
                        new Point(0, 0), new Point(0, 1))
                    : new LinearGradientBrush(
                        new GradientStopCollection
                        {
                            new GradientStop(Color.FromArgb(50, 255, 255, 255), 0.0),
                            new GradientStop(Color.FromArgb(25, 255, 255, 255), 1.0)
                        },
                        new Point(0, 0), new Point(0, 1));

                Brush glassSelectedTile = isLight
                    ? new LinearGradientBrush(
                        new GradientStopCollection
                        {
                            new GradientStop(Color.FromArgb(160, 220, 240, 255), 0.0),
                            new GradientStop(Color.FromArgb(120, 180, 220, 250), 1.0)
                        },
                        new Point(0, 0), new Point(0, 1))
                    : new LinearGradientBrush(
                        new GradientStopCollection
                        {
                            new GradientStop(Color.FromArgb(85, 35, 120, 190), 0.0),
                            new GradientStop(Color.FromArgb(65, 15, 60, 110), 1.0)
                        },
                        new Point(0, 0), new Point(0, 1));

                var glassButtonSecond = isLight ? Parse("#70FFFFFF") : Parse("#20FFFFFF");
                var glassButtonMain = isLight ? Parse("#90FFFFFF") : Parse("#12FFFFFF");

                Application.Current.Resources["ButtonMainColor"] = glassButtonMain;
                SetBrush("ButtonMainBrush", glassButtonMain);

                Application.Current.Resources["ButtonSecondColor"] = glassButtonSecond;
                SetBrush("ButtonSecondBrush", glassButtonSecond);

                var glassBorderMain = isLight ? Parse("#30000000") : Parse("#24FFFFFF");
                var glassBorderSecond = isLight ? Parse("#18000000") : Parse("#14FFFFFF");

                Application.Current.Resources["BorderMainColor"] = glassBorderMain;
                SetBrush("BorderMainBrush", glassBorderMain);

                Application.Current.Resources["BorderSecondColor"] = glassBorderSecond;
                SetBrush("BorderSecondBrush", glassBorderSecond);

                var glassWindowSecond = isLight ? Parse("#F2F7F8FA") : Parse("#EA14161D");
                var glassToastSurface = isLight ? Parse("#CCF7F8FA") : Parse("#B8101218");

                Application.Current.Resources["PopupSurfaceColor"] = glassWindowSecond;
                SetBrush("PopupSurfaceBrush", glassWindowSecond);

                Application.Current.Resources["ToastSurfaceColor"] = glassToastSurface;
                SetBrush("ToastSurfaceBrush", glassToastSurface);

                SetBrush("ButtonHoverBrush", glassButtonHover);
                SetBrush("ButtonRaisedBgBrush", glassButtonRaised);
                SetBrush("ButtonRaisedBorderBrush", glassButtonRaisedBorder);
                SetBrush("SelectedTileGradientBrush", glassSelectedTile);
                SetBrush("WindowBorderBrush", glassWindowBorder);
            }
            else
            {
                var opaqueBorder = isLight ? Parse("#F5F6F8") : Parse("#1C1C1C");
                Application.Current.Resources["WindowBorderColor"] = opaqueBorder;
                SetBrush("WindowBorderBrush", opaqueBorder);

                var opaqueBorderMain = isLight ? Palette["BorderMainColor"].Light : Palette["BorderMainColor"].Dark;
                Application.Current.Resources["BorderMainColor"] = opaqueBorderMain;
                SetBrush("BorderMainBrush", opaqueBorderMain);

                var opaqueBorderSecond = isLight ? Palette["BorderSecondColor"].Light : Palette["BorderSecondColor"].Dark;
                Application.Current.Resources["BorderSecondColor"] = opaqueBorderSecond;
                SetBrush("BorderSecondBrush", opaqueBorderSecond);

                var opaqueButtonMain = isLight ? Palette["ButtonMainColor"].Light : Palette["ButtonMainColor"].Dark;
                Application.Current.Resources["ButtonMainColor"] = opaqueButtonMain;
                SetBrush("ButtonMainBrush", opaqueButtonMain);

                var opaqueButtonSecond = isLight ? Palette["ButtonSecondColor"].Light : Palette["ButtonSecondColor"].Dark;
                Application.Current.Resources["ButtonSecondColor"] = opaqueButtonSecond;
                SetBrush("ButtonSecondBrush", opaqueButtonSecond);

                SetBrush("SelectedTileGradientBrush", isLight ? Parse("#E7F3FC") : Parse("#2E2E2E"));
                SetBrush("ButtonRaisedBgBrush", isLight ? Parse("#FFFFFF") : Parse("#2E2E2E"));
                SetBrush("ButtonRaisedBorderBrush", isLight ? Parse("#C8CED6") : Parse("#424242"));
            }

            foreach (Window window in Application.Current.Windows)
            {
                WindowSystemShadow.UpdateGlassEffect(window, IsGlassEffectEnabled, isLight);
                window.InvalidateVisual();
            }
        }

        if (!persist)
            return;

        try
        {
            ConfigurationService.Instance.Update(cfg => cfg.General.LightTheme = isLight);
        }
        catch { }
    }

    private static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);

    private static void SetBrush(string key, Brush brush)
    {
        Application.Current.Resources[key] = brush;
    }

    private static void SetBrush(string key, Color color)
    {
        if (Application.Current.TryFindResource(key) is SolidColorBrush brush && !brush.IsFrozen)
        {
            brush.Color = color;
            return;
        }

        Application.Current.Resources[key] = new SolidColorBrush(color);
    }
}
