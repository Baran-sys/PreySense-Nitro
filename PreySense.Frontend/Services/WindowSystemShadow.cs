using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
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
            WindowChrome.SetWindowChrome(window, null!);
            return;
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            Apply(window);
        else
            window.SourceInitialized += Window_SourceInitialized;
    }

    private static void Window_SourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window)
            return;
        window.SourceInitialized -= Window_SourceInitialized;
        Apply(window);
    }

    private static void Apply(Window window)
    {
        // AllowsTransparency disables DWM shadows; leave those windows alone.
        if (window.AllowsTransparency)
            return;

        bool glass = ThemeManager.IsGlassEffectEnabled;
        bool isLight = ThemeManager.IsLightTheme;

        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            // Glass frame thickness: -1 for full DWM Acrylic blur backdrop, or 1px bottom for standard opaque shadow.
            GlassFrameThickness = glass ? new Thickness(-1) : new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        });

        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        int darkMode = isLight ? 0 : 1;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));

        int backdrop = glass ? DwmsbtTransientWindow : DwmsbtNone;
        _ = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));

        // Win11: round the HWND like a normal app window (~8px). Ignored on older OS builds.
        int preference = DwmWcpRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));

        // Hide the system 1px border (same look as matching border to window background).
        uint borderColor = DwmwaColorNone;
        _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref borderColor, sizeof(uint));

        // Ensure native Windows DWM open/close transitions are explicitly enabled.
        int disableTransitions = 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaTransitionsForcedDisabled, ref disableTransitions, sizeof(int));

        // Strip WS_CAPTION and WS_SYSMENU to prevent Windows DWM from rendering system caption buttons (minimize, close),
        // leaving PreySense's own title bar buttons exclusively.
        nint style = GetWindowLongPtr(hwnd, GwlStyle);
        nint desiredStyle = style & ~(WsCaption | WsSysMenu | WsMaximizeBox);
        if (desiredStyle != style)
        {
            _ = SetWindowLongPtr(hwnd, GwlStyle, desiredStyle);
            _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
    }

    /// <summary>
    /// Dynamically updates the DWM Acrylic blur backdrop and WindowChrome on an existing window.
    /// </summary>
    public static void UpdateGlassEffect(Window window, bool glass, bool isLight)
    {
        if (window.AllowsTransparency)
            return;

        var chrome = WindowChrome.GetWindowChrome(window);
        if (chrome != null)
        {
            chrome.GlassFrameThickness = glass ? new Thickness(-1) : new Thickness(0, 0, 0, 1);
        }
        else
        {
            WindowChrome.SetWindowChrome(window, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(0),
                GlassFrameThickness = glass ? new Thickness(-1) : new Thickness(0, 0, 0, 1),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });
        }

        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        int darkMode = isLight ? 0 : 1;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));

        int backdrop = glass ? DwmsbtTransientWindow : DwmsbtNone;
        _ = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdrop, sizeof(int));

        int preference = DwmWcpRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));

        uint borderColor = DwmwaColorNone;
        _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref borderColor, sizeof(uint));

        // Ensure system caption buttons remain stripped during live glass toggling
        nint style = GetWindowLongPtr(hwnd, GwlStyle);
        nint desiredStyle = style & ~(WsCaption | WsSysMenu | WsMaximizeBox);
        if (desiredStyle != style)
        {
            _ = SetWindowLongPtr(hwnd, GwlStyle, desiredStyle);
            _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
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
    private const int DwmsbtTransientWindow = 3; // Acrylic blur

    private const int GwlStyle = -16;
    private const nint WsCaption = 0x00C00000;
    private const nint WsSysMenu = 0x00080000;
    private const nint WsMinimizeBox = 0x00020000;
    private const nint WsMaximizeBox = 0x00010000;
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
