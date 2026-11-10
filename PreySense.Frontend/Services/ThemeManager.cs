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
                // Deep dark obsidian smoked glass for Dark Mode; rich frosted translucent for Light Mode.
                // Low opacity on the window background lets the DWM acrylic blur shine through strongly
                // without washing out into milky white/gray.
                var glassWindowBg = isLight ? Parse("#80F5F6F8") : Parse("#4508080C");
                var glassCardBg = isLight ? Parse("#60FFFFFF") : Parse("#24101116");

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
                            new GradientStop(Color.FromArgb(90, 255, 255, 255), 0.0), // Crisp glass rim on top
                            new GradientStop(Color.FromArgb(25, 255, 255, 255), 1.0)  // Subtle lower edge reflection
                        },
                        new Point(0, 0), new Point(0, 1));

                // Polished smoked glass buttons with top specular sheen and dark crystal depth
                Brush glassButtonRaised = isLight
                    ? new LinearGradientBrush(
                        new GradientStopCollection
                        {
                            new GradientStop(Color.FromArgb(160, 255, 255, 255), 0.0),
                            new GradientStop(Color.FromArgb(110, 240, 242, 246), 1.0)
                        },
                        new Point(0, 0), new Point(0, 1))
                    : new LinearGradientBrush(
                        new GradientStopCollection
                        {
                            new GradientStop(Color.FromArgb(60, 36, 40, 50), 0.0), // Top specular frosted reflection
                            new GradientStop(Color.FromArgb(75, 12, 13, 18), 1.0)  // Deep dark obsidian glass body
                        },
                        new Point(0, 0), new Point(0, 1));

                Brush glassButtonRaisedBorder = isLight
                    ? new LinearGradientBrush(
                        new GradientStopCollection
                        {
                            new GradientStop(Color.FromArgb(90, 255, 255, 255), 0.0),
                            new GradientStop(Color.FromArgb(40, 0, 0, 0), 1.0)
                        },
                        new Point(0, 0), new Point(0, 1))
                    : new LinearGradientBrush(
                        new GradientStopCollection
                        {
                            new GradientStop(Color.FromArgb(85, 255, 255, 255), 0.0), // Glass bevel top highlight
                            new GradientStop(Color.FromArgb(20, 255, 255, 255), 1.0)  // Glass bevel bottom reflection
                        },
                        new Point(0, 0), new Point(0, 1));

                Brush glassButtonHover = isLight
                    ? new LinearGradientBrush(
                        new GradientStopCollection
                        {
                            new GradientStop(Color.FromArgb(220, 255, 255, 255), 0.0),
                            new GradientStop(Color.FromArgb(170, 245, 247, 250), 1.0)
                        },
                        new Point(0, 0), new Point(0, 1))
                    : new LinearGradientBrush(
                        new GradientStopCollection
                        {
                            new GradientStop(Color.FromArgb(85, 50, 56, 70), 0.0),
                            new GradientStop(Color.FromArgb(90, 18, 20, 26), 1.0)
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
                            new GradientStop(Color.FromArgb(90, 35, 120, 190), 0.0),
                            new GradientStop(Color.FromArgb(80, 15, 60, 110), 1.0)
                        },
                        new Point(0, 0), new Point(0, 1));

                var glassButtonSecond = isLight ? Parse("#70FFFFFF") : Parse("#450A0B10");
                var glassButtonMain = isLight ? Parse("#90FFFFFF") : Parse("#3514151C");

                Application.Current.Resources["ButtonMainColor"] = glassButtonMain;
                SetBrush("ButtonMainBrush", glassButtonMain);

                Application.Current.Resources["ButtonSecondColor"] = glassButtonSecond;
                SetBrush("ButtonSecondBrush", glassButtonSecond);

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
