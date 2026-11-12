using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PreySense.Mode;

namespace PreySense.Frontend.Controls;

/// <summary>Editable duty graph for active EC fan stages (X = °C legend, Y = duty %).</summary>
public sealed class WpfFanCurveControl : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(int[]), typeof(WpfFanCurveControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TemperaturesProperty = DependencyProperty.Register(
        nameof(Temperatures), typeof(int[]), typeof(WpfFanCurveControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LeadInDutyProperty = DependencyProperty.Register(
        nameof(LeadInDuty), typeof(int?), typeof(WpfFanCurveControl), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(WpfFanCurveControl), new FrameworkPropertyMetadata(Brushes.SkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(WpfFanCurveControl), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(WpfFanCurveControl), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SurfaceProperty = DependencyProperty.Register(
        nameof(Surface), typeof(Brush), typeof(WpfFanCurveControl), new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public int[] Values { get => (int[]?)GetValue(ValuesProperty) ?? Array.Empty<int>(); set => SetValue(ValuesProperty, value); }
    public int[] Temperatures { get => (int[]?)GetValue(TemperaturesProperty) ?? Array.Empty<int>(); set => SetValue(TemperaturesProperty, value); }
    /// <summary>Duty held from the left edge until the first stage point (pre-first-rise floor).</summary>
    public int? LeadInDuty { get => (int?)GetValue(LeadInDutyProperty); set => SetValue(LeadInDutyProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush Surface { get => (Brush)GetValue(SurfaceProperty); set => SetValue(SurfaceProperty, value); }

    private int _selectedIndex = -1;
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_selectedIndex == value) return;
            _selectedIndex = value;
            InvalidateVisual();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? Edited;
    public event EventHandler? SelectionChanged;
    public event EventHandler<(int Celsius, int Duty)>? PointAddRequested;

    private bool _editingPoint, _dragging;
    private Point _dragOrigin;
    private int _dragStartDuty;
    private int _dragStartCelsius;

    public WpfFanCurveControl()
    {
        Focusable = true;
        Cursor = Cursors.Arrow;
        SetResourceReference(AccentProperty, "AccentBrush");
        SetResourceReference(TextBrushProperty, "TextSecondaryBrush");
        SetResourceReference(GridBrushProperty, "BorderSecondBrush");
        SetResourceReference(SurfaceProperty, "CardBgBrush");
    }

    public const int MinCelsius = 30;
    public const int MaxCelsius = 100;

    private Rect Plot => new(42, 14, Math.Max(1, ActualWidth - 58), Math.Max(1, ActualHeight - 42));
    private int PointCount => Values.Length;

    private double CelsiusToX(int celsius)
    {
        var r = Plot;
        double t = Math.Clamp(celsius, MinCelsius, MaxCelsius);
        return r.Left + r.Width * (t - MinCelsius) / (MaxCelsius - MinCelsius);
    }

    private Point At(int index, int duty)
    {
        int celsius = index < Temperatures.Length ? Temperatures[index] : MinCelsius;
        return new(CelsiusToX(celsius), Plot.Bottom - Plot.Height * Math.Clamp(duty, 0, 100) / 100.0);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRectangle(Surface, null, new Rect(RenderSize));
        var plot = Plot;
        var thin = new Pen(GridBrush, 1);
        for (int percent = 0; percent <= 100; percent += 20)
        {
            double y = plot.Bottom - plot.Height * percent / 100;
            dc.DrawLine(thin, new(plot.Left, y), new(plot.Right, y));
            Label(dc, percent + "%", 2, y - 7);
        }
        // X legend uses each visible stage's °C, not a fixed 5/10°C grid.
        for (int i = 0; i < Temperatures.Length; i++)
        {
            double x = CelsiusToX(Temperatures[i]);
            dc.DrawLine(thin, new(x, plot.Top), new(x, plot.Bottom));
            Label(dc, Temperatures[i] + "°", x - 10, plot.Bottom + 7);
        }
        if (PointCount == 0) return;
        var line = new Pen(Accent, 2)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        int leadDuty = LeadInDuty ?? Values[0];
        double leadY = plot.Bottom - plot.Height * Math.Clamp(leadDuty, 0, 100) / 100.0;
        var pts = new Point[PointCount];
        for (int i = 0; i < PointCount; i++)
            pts[i] = At(i, Values[i]);

        var geom = BuildSmoothCurveGeometry(plot, leadY, pts);
        dc.DrawGeometry(null, line, geom);
        for (int i = 0; i < PointCount; i++)
            dc.DrawEllipse(i == SelectedIndex ? Accent : Surface, line, pts[i], i == SelectedIndex ? 5 : 3, i == SelectedIndex ? 5 : 3);
    }

    private static StreamGeometry BuildSmoothCurveGeometry(Rect plot, double leadY, Point[] pts)
    {
        var geom = new StreamGeometry();
        using var ctx = geom.Open();
        Point first = pts[0];
        Point last = pts[^1];

        Point[] curvePts;
        if (first.X > plot.Left + 0.5)
        {
            curvePts = new Point[pts.Length + 1];
            curvePts[0] = new Point(plot.Left, leadY);
            Array.Copy(pts, 0, curvePts, 1, pts.Length);
        }
        else
        {
            curvePts = pts;
        }

        ctx.BeginFigure(curvePts[0], isFilled: false, isClosed: false);

        int n = curvePts.Length;
        if (n >= 2)
        {
            var xs = new double[n];
            var ys = new double[n];
            for (int i = 0; i < n; i++)
            {
                xs[i] = curvePts[i].X;
                ys[i] = curvePts[i].Y;
            }

            double[] tangent = MonotoneCubicInterpolator.ComputeTangents(xs, ys);
            for (int i = 0; i < n - 1; i++)
            {
                var (c1x, c1y, c2x, c2y) = MonotoneCubicInterpolator.GetBezierControlPoints(
                    xs[i], ys[i], tangent[i],
                    xs[i + 1], ys[i + 1], tangent[i + 1]);
                ctx.BezierTo(new Point(c1x, c1y), new Point(c2x, c2y), curvePts[i + 1], isStroked: true, isSmoothJoin: true);
            }
        }

        if (last.X < plot.Right)
            ctx.LineTo(new(plot.Right, last.Y), isStroked: true, isSmoothJoin: true);

        geom.Freeze();
        return geom;
    }

    private void Label(DrawingContext dc, string text, double x, double y)
    {
        var font = new Typeface((FontFamily?)TryFindResource("AppFontFamily") ?? new FontFamily("Segoe UI"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        dc.DrawText(new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            font, 10, TextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new(x, y));
    }

    private int HitPoint(Point p)
    {
        if (PointCount == 0) return -1;
        int hit = -1;
        double distance = 12 * 12;
        for (int i = 0; i < PointCount; i++)
        {
            Vector delta = p - At(i, Values[i]);
            if (delta.LengthSquared <= distance) { hit = i; distance = delta.LengthSquared; }
        }
        return hit;
    }

    public static void SetDuty(int[] values, int index, int duty)
    {
        if (values is null || index < 0 || index >= values.Length) return;
        int min = index == 0 ? 0 : values[index - 1];
        int max = index == values.Length - 1 ? 100 : values[index + 1];
        if (min > max) (min, max) = (max, min);
        values[index] = Math.Clamp(duty, min, max);
    }

    public static (int Min, int Max) DutyBounds(int[] values, int index)
    {
        if (values is null || index < 0 || index >= values.Length) return (0, 100);
        int min = index == 0 ? 0 : values[index - 1];
        int max = index == values.Length - 1 ? 100 : values[index + 1];
        return min <= max ? (min, max) : (max, min);
    }

    public static void SetTemperature(int[] temperatures, int index, int celsius)
    {
        if (temperatures is null || index < 0 || index >= temperatures.Length) return;
        var (min, max) = TemperatureBounds(temperatures, index);
        temperatures[index] = Math.Clamp(celsius, min, max);
    }

    public static (int Min, int Max) TemperatureBounds(int[] temperatures, int index)
    {
        if (temperatures is null || index < 0 || index >= temperatures.Length)
            return (MinCelsius, MaxCelsius);
        // Strictly unique X: cannot share a °C with the previous or next point.
        int min = index == 0 ? MinCelsius : temperatures[index - 1] + 1;
        int max = index == temperatures.Length - 1 ? MaxCelsius : temperatures[index + 1] - 1;
        if (min > max) return (temperatures[index], temperatures[index]);
        return (min, max);
    }

    private int XToCelsius(double x)
    {
        var r = Plot;
        if (r.Width <= 0) return MinCelsius;
        double t = (x - r.Left) / r.Width;
        return (int)Math.Round(MinCelsius + Math.Clamp(t, 0, 1) * (MaxCelsius - MinCelsius));
    }

    private int YToDuty(double y)
    {
        var r = Plot;
        if (r.Height <= 0) return 0;
        double t = (r.Bottom - y) / r.Height;
        return (int)Math.Round(Math.Clamp(t, 0, 1) * 100.0);
    }

    /// <summary>Raise any descending duties so the visible curve stays nondecreasing.</summary>
    public static void EnforceNondecreasing(int[] values)
    {
        if (values is null || values.Length == 0) return;
        values[0] = Math.Clamp(values[0], 0, 100);
        for (int i = 1; i < values.Length; i++)
            values[i] = Math.Clamp(values[i], values[i - 1], 100);
    }

    private bool BeginPointEdit(Point p)
    {
        int hit = HitPoint(p);
        if (hit < 0)
        {
            if (SelectedIndex >= 0) SelectedIndex = -1;
            return false;
        }
        SelectedIndex = hit;
        _dragOrigin = p;
        _dragStartDuty = Values[hit];
        _dragStartCelsius = hit < Temperatures.Length ? Temperatures[hit] : MinCelsius;
        _editingPoint = true;
        _dragging = false;
        return true;
    }

    private void DragPoint(Point p)
    {
        if (!_editingPoint || PointCount == 0 || SelectedIndex < 0) return;
        double deltaX = p.X - _dragOrigin.X;
        double deltaY = _dragOrigin.Y - p.Y;
        if (!_dragging
            && Math.Abs(deltaX) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(deltaY) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _dragging = true;
        int previousDuty = Values[SelectedIndex];
        int previousCelsius = SelectedIndex < Temperatures.Length ? Temperatures[SelectedIndex] : MinCelsius;
        SetDuty(Values, SelectedIndex, _dragStartDuty + (int)Math.Round(deltaY / Plot.Height * 100));
        if (SelectedIndex < Temperatures.Length)
            SetTemperature(Temperatures, SelectedIndex, XToCelsius(_dragOrigin.X + deltaX));
        if (Values[SelectedIndex] == previousDuty
            && (SelectedIndex >= Temperatures.Length || Temperatures[SelectedIndex] == previousCelsius))
            return;
        InvalidateVisual();
        Edited?.Invoke(this, EventArgs.Empty);
    }

    private void EndPointEdit(bool cancel = false)
    {
        if (cancel && _editingPoint && _dragging && PointCount > 0 && SelectedIndex >= 0)
        {
            SetDuty(Values, SelectedIndex, _dragStartDuty);
            if (SelectedIndex < Temperatures.Length)
                SetTemperature(Temperatures, SelectedIndex, _dragStartCelsius);
            InvalidateVisual();
            Edited?.Invoke(this, EventArgs.Empty);
        }
        _editingPoint = _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsEnabled || !BeginPointEdit(e.GetPosition(this))) return;
        Focus(); CaptureMouse(); e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsEnabled) { Cursor = Cursors.Arrow; return; }
        if (IsMouseCaptured) DragPoint(e.GetPosition(this));
        else Cursor = HitPoint(e.GetPosition(this)) >= 0 ? Cursors.SizeAll : Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) { EndPointEdit(); e.Handled = true; }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (!IsEnabled || _editingPoint || IsMouseCaptured) return;
        Point p = e.GetPosition(this);
        if (!Plot.Contains(p)) return;
        int hit = HitPoint(p);
        if (hit >= 0)
        {
            SelectedIndex = hit;
            e.Handled = true;
            return;
        }
        if (PointCount >= FanCurveProfile.StageCount - 1) return;
        Focus();
        PointAddRequested?.Invoke(this, (XToCelsius(p.X), YToDuty(p.Y)));
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _editingPoint = _dragging = false;
        base.OnLostMouseCapture(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!IsEnabled) return;
        if (e.Key == Key.Escape)
        {
            if (_editingPoint) { EndPointEdit(cancel: true); e.Handled = true; return; }
            if (SelectedIndex >= 0) { SelectedIndex = -1; e.Handled = true; return; }
        }
        if (PointCount == 0 || _editingPoint) return;
        if (e.Key == Key.Left) SelectedIndex = SelectedIndex < 0 ? 0 : Math.Max(0, SelectedIndex - 1);
        else if (e.Key == Key.Right) SelectedIndex = SelectedIndex < 0 ? 0 : Math.Min(PointCount - 1, SelectedIndex + 1);
        else if (e.Key is Key.Up or Key.Down)
        {
            if (SelectedIndex < 0) return;
            SetDuty(Values, SelectedIndex, Values[SelectedIndex] + (e.Key == Key.Up ? 1 : -1));
            InvalidateVisual();
            Edited?.Invoke(this, EventArgs.Empty);
        }
        else return;
        e.Handled = true;
    }
}
