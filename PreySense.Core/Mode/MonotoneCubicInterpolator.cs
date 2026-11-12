using System;
using System.Collections.Generic;

namespace PreySense.Mode;

/// <summary>
/// Fritsch-Carlson monotone cubic Hermite interpolation for fan curves.
/// Shared between runtime curve evaluation and WPF Bezier spline rendering so the
/// drawn curve and executed RPM targets are mathematically identical with zero overshoot.
/// </summary>
public static class MonotoneCubicInterpolator
{
    public static double[] ComputeTangents(double[] x, double[] y)
    {
        if (x is null) throw new ArgumentNullException(nameof(x));
        if (y is null) throw new ArgumentNullException(nameof(y));
        if (x.Length != y.Length) throw new ArgumentException("Coordinate arrays must have equal length.");

        int n = x.Length;
        var tangent = new double[n];
        if (n < 2)
            return tangent;

        var secant = new double[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            double dx = x[i + 1] - x[i];
            secant[i] = dx > 1e-9 ? (y[i + 1] - y[i]) / dx : 0.0;
        }

        tangent[0] = secant[0];
        tangent[n - 1] = secant[n - 2];
        for (int i = 1; i < n - 1; i++)
        {
            if (secant[i - 1] * secant[i] <= 0.0)
                tangent[i] = 0.0;
            else
                tangent[i] = 0.5 * (secant[i - 1] + secant[i]);
        }

        for (int i = 0; i < n - 1; i++)
        {
            if (Math.Abs(secant[i]) < 1e-9)
            {
                tangent[i] = 0.0;
                tangent[i + 1] = 0.0;
                continue;
            }

            double a = tangent[i] / secant[i];
            double b = tangent[i + 1] / secant[i];
            double r2 = a * a + b * b;
            if (r2 > 9.0)
            {
                double tau = 3.0 / Math.Sqrt(r2);
                tangent[i] = tau * a * secant[i];
                tangent[i + 1] = tau * b * secant[i];
            }
        }

        return tangent;
    }

    public static double Evaluate(double temp, double[] xs, double[] ys)
    {
        if (xs is null) throw new ArgumentNullException(nameof(xs));
        if (ys is null) throw new ArgumentNullException(nameof(ys));
        if (xs.Length != ys.Length) throw new ArgumentException("Coordinate arrays must have equal length.");
        if (xs.Length == 0) return 0.0;
        if (xs.Length == 1 || double.IsNaN(temp)) return ys[0];
        if (temp <= xs[0]) return ys[0];
        if (temp >= xs[^1]) return ys[^1];

        double[] tangents = ComputeTangents(xs, ys);
        return Evaluate(temp, xs, ys, tangents);
    }

    public static double Evaluate(double temp, double[] xs, double[] ys, double[] tangents)
    {
        if (xs is null) throw new ArgumentNullException(nameof(xs));
        if (ys is null) throw new ArgumentNullException(nameof(ys));
        if (tangents is null) throw new ArgumentNullException(nameof(tangents));
        if (xs.Length != ys.Length || xs.Length != tangents.Length)
            throw new ArgumentException("Coordinate and tangent arrays must have equal length.");
        if (xs.Length == 0) return 0.0;
        if (xs.Length == 1 || double.IsNaN(temp)) return ys[0];
        if (temp <= xs[0]) return ys[0];
        if (temp >= xs[^1]) return ys[^1];

        int segment = xs.Length - 2;
        for (int i = 0; i < xs.Length - 1; i++)
        {
            if (temp <= xs[i + 1])
            {
                segment = i;
                break;
            }
        }

        double x0 = xs[segment];
        double x1 = xs[segment + 1];
        double y0 = ys[segment];
        double y1 = ys[segment + 1];
        double h = x1 - x0;
        if (h <= 1e-12)
            return y0;

        double t = Math.Clamp((temp - x0) / h, 0.0, 1.0);
        double t2 = t * t;
        double t3 = t2 * t;
        double h00 = 2.0 * t3 - 3.0 * t2 + 1.0;
        double h10 = t3 - 2.0 * t2 + t;
        double h01 = -2.0 * t3 + 3.0 * t2;
        double h11 = t3 - t2;

        double value = h00 * y0 + h10 * h * tangents[segment] + h01 * y1 + h11 * h * tangents[segment + 1];
        double minY = Math.Min(y0, y1);
        double maxY = Math.Max(y0, y1);
        return Math.Clamp(value, minY, maxY);
    }

    public static double Evaluate(double temp, IReadOnlyList<FanCurvePoint> points)
    {
        if (points is null || points.Count == 0)
            return 0.0;
        if (points.Count == 1)
            return points[0].Rpm;

        var xs = new double[points.Count];
        var ys = new double[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            xs[i] = points[i].TempC;
            ys[i] = points[i].Rpm;
        }
        return Evaluate(temp, xs, ys);
    }

    /// <summary>
    /// Computes the two cubic Bezier control points matching the Fritsch-Carlson Hermite
    /// segment between (x0, y0) with slope m0 and (x1, y1) with slope m1.
    /// </summary>
    public static (double C1X, double C1Y, double C2X, double C2Y) GetBezierControlPoints(
        double x0, double y0, double m0,
        double x1, double y1, double m1)
    {
        double h = Math.Max(0.0, x1 - x0) / 3.0;
        double minY = Math.Min(y0, y1);
        double maxY = Math.Max(y0, y1);
        double c1y = Math.Clamp(y0 + h * m0, minY, maxY);
        double c2y = Math.Clamp(y1 - h * m1, minY, maxY);
        return (x0 + h, c1y, x1 - h, c2y);
    }
}
