using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace PreySense.Frontend.Services;

/// <summary>
/// Enables the Windows DWM drop shadow (and Win11 rounded corners) for borderless
/// opaque windows. Replaces WPF <see cref="System.Windows.Media.Effects.DropShadowEffect"/>,
/// which cannot match system chrome when <c>AllowsTransparency</c> is true.
/// </summary>
public static class WindowSystemShadow
{
    public static readonly DependencyProperty EnableProperty = DependencyProperty.RegisterAttached(
        "Enable",
        typeof(bool),
        typeof(WindowSystemShadow),
        new PropertyMetadata(false, OnEnableChanged));

    public static void SetEnable(DependencyObject element, bool value) =>
        element.SetValue(EnableProperty, value);

    public static bool GetEnable(DependencyObject element) =>
        (bool)element.GetValue(EnableProperty);

    private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window)
            return;

        if (e.NewValue is not true)
        {
            window.SourceInitialized -= Window_SourceInitialized;
            window.IsVisibleChanged -= Window_IsVisibleChanged;
            WindowChrome.SetWindowChrome(window, null!);
            return;
        }

        window.IsVisibleChanged -= Window_IsVisibleChanged;
        window.IsVisibleChanged += Window_IsVisibleChanged;

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            ApplyBackdropCore(window, ThemeManager.IsGlassEffectEnabled, ThemeManager.IsLightTheme);
        else
            window.SourceInitialized += Window_SourceInitialized;
    }

    private static void Window_SourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window)
            return;
        window.SourceInitialized -= Window_SourceInitialized;
        ApplyBackdropCore(window, ThemeManager.IsGlassEffectEnabled, ThemeManager.IsLightTheme);
    }

    private static void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is Window window && e.NewValue is true && new WindowInteropHelper(window).Handle != IntPtr.Zero)
            window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                if (window.IsVisible && new WindowInteropHelper(window).Handle != IntPtr.Zero)
                    ApplyDwmAttributesOnly(window, ThemeManager.IsGlassEffectEnabled, ThemeManager.IsLightTheme);
            });
    }

    /// <summary>
    /// Computes the native window style ensuring Windows system caption buttons (close, minimize, maximize)
    /// are strictly suppressed in both normal and liquid glass modes, leaving PreySense's custom XAML header exclusively.
    /// </summary>
    public static nint CalculateWindowStyle(nint currentStyle) =>
        currentStyle & ~(WsCaption | WsSysMenu | WsMaximizeBox);

    /// <summary>
    /// Dynamically updates the DWM Acrylic blur backdrop and WindowChrome on an existing window.
    /// </summary>
    public static void UpdateGlassEffect(Window window, bool glass, bool isLight)
    {
        ApplyBackdropCore(window, glass, isLight);
    }

    private static void ApplyBackdropCore(Window window, bool glass, bool isLight)
    {
        // AllowsTransparency disables DWM shadows and system backdrop; leave those windows alone.
        if (window.AllowsTransparency || !GetEnable(window))
            return;

        var glassFrame = glass ? new Thickness(-1) : new Thickness(0, 0, 0, 1);
        var chrome = WindowChrome.GetWindowChrome(window);
        if (chrome != null)
        {
            if (chrome.GlassFrameThickness != glassFrame)
                chrome.GlassFrameThickness = glassFrame;
        }
        else
        {
            WindowChrome.SetWindowChrome(window, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(0),
                // Glass frame thickness: -1 for full DWM Acrylic blur backdrop, or 1px bottom for standard opaque shadow.
                GlassFrameThickness = glassFrame,
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });
        }

        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        ApplyDwmAttributesOnly(window, glass, isLight);

        // Strip WS_CAPTION, WS_SYSMENU, and WS_MAXIMIZEBOX so Windows DWM never renders native caption buttons
        // (close, minimize, maximize) in either normal or liquid glass mode, leaving PreySense's custom XAML header exclusively.
        nint style = GetWindowLongPtr(hwnd, GwlStyle);
        nint desiredStyle = CalculateWindowStyle(style);
        if (desiredStyle != style)
        {
            _ = SetWindowLongPtr(hwnd, GwlStyle, desiredStyle);
            _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
    }

    private static void ApplyDwmAttributesOnly(Window window, bool glass, bool isLight)
    {
        if (window.AllowsTransparency || !GetEnable(window))
            return;

        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
        {
            target.BackgroundColor = glass
                ? Colors.Transparent
                : (isLight ? Color.FromRgb(0xF5, 0xF6, 0xF8) : Color.FromRgb(0x1C, 0x1C, 0x1C));
        }

        int darkMode = isLight ? 0 : 1;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));

        // Disable DWM's DWMSBT_TRANSIENTWINDOW (which turns flat gray when unfocused or toggled live)
        // and use WCA_ACCENT_POLICY (ACCENT_ENABLE_ACRYLICBLURBEHIND) for real-time Windows 11 Start-Menu-style live blur.
        int backdrop = DwmsbtNone;
        _ = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));

        ApplyAccentPolicy(hwnd, glass, isLight);

        // Win11: round the HWND like a normal app window (~8px). Ignored on older OS builds.
        int preference = DwmWcpRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));

        // Hide the system 1px border (same look as matching border to window background).
        uint borderColor = DwmwaColorNone;
        _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref borderColor, sizeof(uint));

        // Ensure native Windows DWM open/close transitions are explicitly enabled.
        int disableTransitions = 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaTransitionsForcedDisabled, ref disableTransitions, sizeof(int));
    }

    private static void ApplyAccentPolicy(IntPtr hwnd, bool glass, bool isLight)
    {
        // ABGR color format: 0xAABBGGRR
        // Dark: neutral charcoal (#202020) at ~20% opacity (0x32) to match Windows 11 Start Menu live blur luminosity,
        // allowing radiant background light and wallpaper colors to diffuse softly through the window.
        // Light: frosted white (#F4F6FA) at ~33% opacity (0x55).
        uint abgrTint = isLight ? 0x55FAF6F4u : 0x32202020u;
        var accent = new AccentPolicy
        {
            AccentState = glass ? AccentEnableAcrylicBlurBehind : AccentDisabled,
            AccentFlags = glass ? 2 : 0,
            GradientColor = glass ? unchecked((int)abgrTint) : 0,
            AnimationId = 0
        };

        int size = Marshal.SizeOf<AccentPolicy>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, buffer, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = buffer,
                SizeOfData = size
            };
            _ = SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const int WmNcCalcSize = 0x0083;
    private const int WmNcActivate = 0x0086;

    private static IntPtr BorderlessWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmNcCalcSize && wParam != IntPtr.Zero)
        {
            handled = true;
            return IntPtr.Zero;
        }
        if (msg == WmNcActivate)
        {
            handled = true;
            return DefWindowProc(hwnd, msg, wParam, (IntPtr)(-1));
        }
        return IntPtr.Zero;
    }

    private const int DwmwaTransitionsForcedDisabled = 3;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmWcpRound = 2;
    private const int DwmwaBorderColor = 34;
    private const uint DwmwaColorNone = 0xFFFFFFFE;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtNone = 1;
    private const int WcaAccentPolicy = 19;
    private const int AccentDisabled = 0;
    private const int AccentEnableAcrylicBlurBehind = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    private const int GwlStyle = -16;
    public const nint WsCaption = 0x00C00000;
    public const nint WsSysMenu = 0x00080000;
    public const nint WsMinimizeBox = 0x00020000;
    public const nint WsMaximizeBox = 0x00010000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(IntPtr hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, int uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attr, ref uint attrValue, int attrSize);
}
