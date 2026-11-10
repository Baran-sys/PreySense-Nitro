using CommunityToolkit.Mvvm.ComponentModel;
using PreySense.Frontend.Services;

namespace PreySense.Frontend.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty]
    private bool _isLightTheme;

    [ObservableProperty]
    private bool _isGlassEffectEnabled;

    partial void OnIsLightThemeChanged(bool value)
    {
        ThemeManager.Apply(value);
        DiagnosticMessage = value ? "Theme: Light" : "Theme: Dark";
    }

    partial void OnIsGlassEffectEnabledChanged(bool value)
    {
        ThemeManager.ApplyGlassEffect(value);
        DiagnosticMessage = value ? "Liquid Glass: Enabled" : "Liquid Glass: Disabled";
    }
}
