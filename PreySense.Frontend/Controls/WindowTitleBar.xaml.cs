using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace PreySense.Frontend.Controls
{
    public partial class WindowTitleBar : UserControl
    {
        public const string Brand = "PreySense";

        private bool _isDragging;
        private POINT _dragStartCursor;
        private RECT _dragStartRect;
        private IntPtr _dragHwnd;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        public WindowTitleBar()
        {
            InitializeComponent();
            UpdateCaption();
        }

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register(
                nameof(Title),
                typeof(string),
                typeof(WindowTitleBar),
                new PropertyMetadata(string.Empty, OnCaptionChanged));

        public string Title
        {
            get => (string)GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public static readonly DependencyProperty SubtitleProperty =
            DependencyProperty.Register(
                nameof(Subtitle),
                typeof(string),
                typeof(WindowTitleBar),
                new PropertyMetadata(string.Empty, OnCaptionChanged));

        public string Subtitle
        {
            get => (string)GetValue(SubtitleProperty);
            set => SetValue(SubtitleProperty, value);
        }

        public static readonly DependencyProperty ActionsProperty =
            DependencyProperty.Register(
                nameof(Actions),
                typeof(object),
                typeof(WindowTitleBar),
                new PropertyMetadata(null));

        public object? Actions
        {
            get => GetValue(ActionsProperty);
            set => SetValue(ActionsProperty, value);
        }

        public event RoutedEventHandler? CloseClick;

        private static void OnCaptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((WindowTitleBar)d).UpdateCaption();
        }

        private void UpdateCaption()
        {
            if (TitleText == null || SubtitleText == null)
                return;

            ApplySegment(TitleText, Title);
            ApplySegment(SubtitleText, Subtitle);
        }

        private static void ApplySegment(TextBlock text, string? value)
        {
            string formatted = FormatSegment(value);
            text.SetCurrentValue(TextBlock.TextProperty, formatted);
            text.Visibility = string.IsNullOrEmpty(formatted) ? Visibility.Collapsed : Visibility.Visible;
        }

        internal static string FormatSegment(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string text = value.Trim();
            if (text.StartsWith('•') || text.StartsWith('—') || text.StartsWith('-'))
                return " " + text;

            return " • " + text;
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left || IsInsideButton(e.OriginalSource as DependencyObject))
                return;

            var window = Window.GetWindow(this);
            if (window == null)
                return;

            _dragHwnd = new WindowInteropHelper(window).Handle;
            if (_dragHwnd != IntPtr.Zero && GetCursorPos(out _dragStartCursor) && GetWindowRect(_dragHwnd, out _dragStartRect))
            {
                _isDragging = true;
                TitleBarGrid.CaptureMouse();
                e.Handled = true;
            }
        }

        private void TitleBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging || _dragHwnd == IntPtr.Zero)
                return;

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndDrag();
                return;
            }

            if (GetCursorPos(out POINT cur) && GetWindowRect(_dragHwnd, out RECT currentRect))
            {
                int newX = currentRect.Left + (cur.X - _dragStartCursor.X);
                int newY = currentRect.Top + (cur.Y - _dragStartCursor.Y);
                _dragStartCursor = cur;
                SetWindowPos(_dragHwnd, IntPtr.Zero, newX, newY, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }

        private void TitleBar_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                EndDrag();
                e.Handled = true;
            }
        }

        private void TitleBar_LostMouseCapture(object sender, MouseEventArgs e)
        {
            _isDragging = false;
            _dragHwnd = IntPtr.Zero;
        }

        private void EndDrag()
        {
            _isDragging = false;
            _dragHwnd = IntPtr.Zero;
            if (TitleBarGrid.IsMouseCaptured)
                TitleBarGrid.ReleaseMouseCapture();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (CloseClick != null)
            {
                CloseClick(this, e);
                return;
            }

            Window.GetWindow(this)?.Close();
        }

        private static bool IsInsideButton(DependencyObject? source)
        {
            while (source != null)
            {
                if (source is Button)
                    return true;
                source = VisualTreeHelper.GetParent(source);
            }

            return false;
        }
    }
}
