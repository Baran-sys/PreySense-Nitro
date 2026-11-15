using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PreySense.Frontend.Services;

/// <summary>
/// Applies our live <c>WCA_ACCENT_POLICY</c> Acrylic blur backdrop (matching the main window)
/// to a <see cref="Popup"/> HWND when Glass Mode is active. Requires
/// <see cref="Popup.AllowsTransparency"/> to be <c>false</c> while Glass Mode is active so DWM
/// owns the blur composition behind the popup.
/// </summary>
public static class PopupGlassBackdrop
{
    public enum BackdropKind
    {
        None = 0,
        Acrylic = 3
    }

    public static BackdropKind PreferredBackdrop(bool glass) => glass ? BackdropKind.Acrylic : BackdropKind.None;

    private static readonly List<WeakReference<Popup>> Popups = new();

    public static readonly DependencyProperty EnableProperty = DependencyProperty.RegisterAttached(
        "Enable",
        typeof(bool),
        typeof(PopupGlassBackdrop),
        new PropertyMetadata(false, OnEnableChanged));

    private static readonly DependencyProperty ShadowEffectProperty = DependencyProperty.RegisterAttached(
        "ShadowEffect",
        typeof(Effect),
        typeof(PopupGlassBackdrop));

    public static void SetEnable(DependencyObject element, bool value) =>
        element.SetValue(EnableProperty, value);

    public static bool GetEnable(DependencyObject element) =>
        (bool)element.GetValue(EnableProperty);

    internal static void RefreshTheme()
    {
        for (int index = Popups.Count - 1; index >= 0; index--)
        {
            if (!Popups[index].TryGetTarget(out var popup))
            {
                Popups.RemoveAt(index);
                continue;
            }

            if (!popup.Dispatcher.CheckAccess() || !GetEnable(popup))
                continue;

            ConfigureForTheme(popup);
            if (popup.IsOpen && !popup.AllowsTransparency)
                ApplyAcrylic(popup);
        }
    }

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Popup popup)
            return;

        if (e.NewValue is true)
        {
            Popups.RemoveAll(reference => !reference.TryGetTarget(out var existing) || ReferenceEquals(existing, popup));
            Popups.Add(new WeakReference<Popup>(popup));
            ConfigureForTheme(popup);
            popup.Opened -= Popup_Opened;
            popup.Opened += Popup_Opened;
            popup.Closed -= Popup_Closed;
            popup.Closed += Popup_Closed;

            // Template child may not be instantiated yet when attached via style setter.
            popup.Dispatcher.BeginInvoke(
                () => { if (GetEnable(popup)) ConfigureForTheme(popup); },
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else
        {
            Popups.RemoveAll(reference => !reference.TryGetTarget(out var existing) || ReferenceEquals(existing, popup));
            popup.Opened -= Popup_Opened;
            popup.Closed -= Popup_Closed;
            if (!popup.IsOpen)
                popup.AllowsTransparency = true;
            if (popup.Child is UIElement host && popup.GetValue(ShadowEffectProperty) is Effect saved)
                host.Effect = saved;
        }
    }

    private static void Popup_Opened(object? sender, EventArgs e)
    {
        if (sender is not Popup popup)
            return;

        SyncHostEffect(popup);
        if (!ThemeManager.IsGlassEffectEnabled || popup.AllowsTransparency)
            return;

        ApplyAcrylic(popup);
        popup.Dispatcher.BeginInvoke(() => ApplyAcrylic(popup), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static void Popup_Closed(object? sender, EventArgs e)
    {
        if (sender is Popup popup)
            ConfigureForTheme(popup);
    }

    private static void ConfigureForTheme(Popup popup)
    {
        bool glass = ThemeManager.IsGlassEffectEnabled;
        if (!popup.IsOpen)
            popup.AllowsTransparency = !glass;

        SyncHostEffect(popup);
    }

    private static void SyncHostEffect(Popup popup)
    {
        if (popup.Child is not UIElement host)
            return;

        if (ThemeManager.IsGlassEffectEnabled)
        {
            if (host.Effect is not null && popup.GetValue(ShadowEffectProperty) is null)
                popup.SetValue(ShadowEffectProperty, host.Effect);
            host.Effect = null;
        }
        else if (popup.GetValue(ShadowEffectProperty) is Effect saved)
        {
            host.Effect = saved;
        }
    }

    private static void ApplyAcrylic(Popup popup)
    {
        if (popup.Child is not Visual child)
            return;

        if (PresentationSource.FromVisual(child) is not HwndSource source)
            return;

        WindowSystemShadow.ApplyPopupBackdrop(source, ThemeManager.IsGlassEffectEnabled, ThemeManager.IsLightTheme);
    }
}
