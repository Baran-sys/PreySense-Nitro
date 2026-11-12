using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace PreySense.Mode;

public sealed record FanCalibrationPoint(int Duty, int Rpm);

public sealed class SingleFanCalibration
{
    public int StopThresholdDuty { get; set; }
    public int MinStableDuty { get; set; }
    public int MinStableRpm { get; set; }
    public int MinQuietDuty { get; set; }
    public int MinQuietRpm { get; set; }
    public int BlackoutMaxDuty { get; set; }
    public int EcAutoBaselineRpm { get; set; }
    public int MaxRpm { get; set; }
    public List<FanCalibrationPoint> Points { get; set; } = new();

    [JsonIgnore]
    public bool IsCalibrated => Points is { Count: >= 2 } && MaxRpm > 0;

    public bool IsDutyInKickZone(int duty) => BlackoutMaxDuty > 0 && duty >= 1 && duty <= BlackoutMaxDuty;

    public SingleFanCalibration Copy() => new()
    {
        StopThresholdDuty = StopThresholdDuty,
        MinStableDuty = MinStableDuty,
        MinStableRpm = MinStableRpm,
        MinQuietDuty = MinQuietDuty,
        MinQuietRpm = MinQuietRpm,
        BlackoutMaxDuty = BlackoutMaxDuty,
        EcAutoBaselineRpm = EcAutoBaselineRpm,
        MaxRpm = MaxRpm,
        Points = Points?.Select(p => new FanCalibrationPoint(p.Duty, p.Rpm)).ToList() ?? new()
    };

    /// <summary>
    /// Sorts points by Duty ascending, strips anti-stall kick-zone duties (1..BlackoutMaxDuty),
    /// enforces a non-decreasing RPM envelope across increasing Duty, and computes
    /// StopThresholdDuty, MinQuietDuty/Rpm, MinStableDuty/Rpm, and MaxRpm.
    /// </summary>
    public void NormalizeMonotonic()
    {
        BlackoutMaxDuty = Math.Clamp(BlackoutMaxDuty, 0, 100);
        MinQuietDuty = Math.Clamp(MinQuietDuty, 0, 100);
        MinQuietRpm = Math.Max(0, MinQuietRpm);
        EcAutoBaselineRpm = Math.Max(0, EcAutoBaselineRpm);

        Points ??= new List<FanCalibrationPoint>();
        if (Points.Count == 0)
        {
            StopThresholdDuty = 0;
            MinStableDuty = 0;
            MinStableRpm = 0;
            MinQuietDuty = 0;
            MinQuietRpm = 0;
            MaxRpm = 0;
            return;
        }

        List<FanCalibrationPoint> sourcePoints = Points;
        if (BlackoutMaxDuty > 0)
        {
            var stripped = Points
                .Where(p => !IsDutyInKickZone(Math.Clamp(p.Duty, 0, 100)))
                .ToList();
            if (stripped.Count > 0)
                sourcePoints = stripped;
        }

        var sorted = sourcePoints
            .Select(p => new FanCalibrationPoint(Math.Clamp(p.Duty, 0, 100), Math.Max(0, p.Rpm)))
            .GroupBy(p => p.Duty)
            .OrderBy(g => g.Key)
            .Select(g => new FanCalibrationPoint(
                g.Key,
                (int)Math.Round(g.Average(x => x.Rpm), MidpointRounding.AwayFromZero)))
            .ToList();

        int runningMax = 0;
        for (int i = 0; i < sorted.Count; i++)
        {
            runningMax = Math.Max(runningMax, sorted[i].Rpm);
            sorted[i] = new FanCalibrationPoint(sorted[i].Duty, runningMax);
        }

        Points = sorted;
        MaxRpm = sorted[^1].Rpm;

        int stopThreshold = 0;
        foreach (var p in sorted)
        {
            if (p.Rpm < 100)
                stopThreshold = p.Duty;
        }
        StopThresholdDuty = stopThreshold;

        // Keep MinStableDuty/MinStableRpm synchronized to MinQuietDuty/MinQuietRpm when valid.
        if (MinQuietDuty is >= 1 and <= 100 && MinQuietDuty > BlackoutMaxDuty)
        {
            int rpmAtQuiet = InterpolateDutyToRpm(sorted, MinQuietDuty);
            if (rpmAtQuiet >= 200)
            {
                MinQuietRpm = rpmAtQuiet;
                MinStableDuty = MinQuietDuty;
                MinStableRpm = MinQuietRpm;
                return;
            }
        }

        // Preserve an explicitly detected MinStableDuty if it maps to a valid spinning point outside kick zone.
        if (MinStableDuty is >= 1 and <= 100 && MinStableDuty > BlackoutMaxDuty)
        {
            int rpmAtPreset = InterpolateDutyToRpm(sorted, MinStableDuty);
            if (rpmAtPreset >= 200)
            {
                MinStableRpm = rpmAtPreset;
                MinQuietDuty = MinStableDuty;
                MinQuietRpm = MinStableRpm;
                return;
            }
        }

        var firstStable = sorted.FirstOrDefault(p => p.Duty >= 1 && p.Duty > BlackoutMaxDuty && p.Rpm >= 200)
            ?? sorted.FirstOrDefault(p => p.Duty >= 1 && p.Duty > BlackoutMaxDuty && p.Rpm >= 100)
            ?? sorted.FirstOrDefault(p => p.Duty >= 1 && p.Rpm >= 200)
            ?? sorted.FirstOrDefault(p => p.Duty >= 1 && p.Rpm >= 100);
        if (firstStable != null)
        {
            MinStableDuty = firstStable.Duty;
            MinStableRpm = firstStable.Rpm;
            MinQuietDuty = MinStableDuty;
            MinQuietRpm = MinStableRpm;
            if (BlackoutMaxDuty >= MinQuietDuty)
                BlackoutMaxDuty = Math.Max(0, MinQuietDuty - 1);
        }
        else
        {
            MinStableDuty = 0;
            MinStableRpm = 0;
            MinQuietDuty = 0;
            MinQuietRpm = 0;
        }
    }

    public int DutyToRpm(double duty, int defaultMaxRpm = FanStage.CpuMaxRpm)
    {
        double clampedDuty = Math.Clamp(duty, 0.0, 100.0);
        if (!IsCalibrated)
            return FanStage.DutyToPhysicalRpm((int)Math.Round(clampedDuty, MidpointRounding.AwayFromZero), defaultMaxRpm);

        if (BlackoutMaxDuty > 0 && clampedDuty > 0.0 && clampedDuty <= BlackoutMaxDuty)
        {
            int minSafeDuty = Math.Clamp(Math.Max(MinQuietDuty, BlackoutMaxDuty + 1), 1, 100);
            clampedDuty = Math.Max(clampedDuty, minSafeDuty);
        }

        return InterpolateDutyToRpm(Points, clampedDuty);
    }

    public double RpmToDutyDouble(double rpm, int defaultMaxRpm = FanStage.CpuMaxRpm)
    {
        if (rpm <= 0.0)
            return 0.0;

        if (!IsCalibrated)
            return FanStage.PhysicalRpmToDuty((int)Math.Round(rpm, MidpointRounding.AwayFromZero), defaultMaxRpm);

        var pts = Points;
        int minSafeDuty = BlackoutMaxDuty > 0
            ? Math.Clamp(Math.Max(MinQuietDuty, BlackoutMaxDuty + 1), 1, 100)
            : 1;

        if (rpm >= pts[^1].Rpm)
            return Math.Clamp(pts[^1].Duty, minSafeDuty, 100.0);

        double result;
        if (rpm <= pts[0].Rpm)
        {
            if (pts[0].Rpm <= 0)
            {
                result = Math.Max(minSafeDuty, pts[0].Duty);
            }
            else
            {
                double topDuty = Math.Max(minSafeDuty, pts[0].Duty);
                result = minSafeDuty + (rpm / pts[0].Rpm) * (topDuty - minSafeDuty);
            }
        }
        else
        {
            result = Math.Clamp(pts[^1].Duty, minSafeDuty, 100.0);
            for (int i = 0; i < pts.Count - 1; i++)
            {
                var a = pts[i];
                var b = pts[i + 1];
                if (rpm >= a.Rpm && rpm <= b.Rpm && b.Rpm > a.Rpm)
                {
                    double t = (rpm - a.Rpm) / (double)(b.Rpm - a.Rpm);
                    result = a.Duty + t * (b.Duty - a.Duty);
                    break;
                }
            }
        }

        int effectiveMinDuty = MinQuietDuty > 0 ? MinQuietDuty : MinStableDuty;
        int effectiveMinRpm = MinQuietRpm > 0 ? MinQuietRpm : MinStableRpm;
        if (effectiveMinDuty > 0 && effectiveMinRpm > 0 && rpm >= effectiveMinRpm)
            result = Math.Clamp(result, effectiveMinDuty, 100.0);

        if (BlackoutMaxDuty > 0)
            result = Math.Clamp(result, minSafeDuty, 100.0);

        return Math.Clamp(result, 1.0, 100.0);
    }

    public int RpmToDuty(int rpm, int defaultMaxRpm = FanStage.CpuMaxRpm)
    {
        if (rpm <= 0)
            return 0;

        return Math.Clamp(
            (int)Math.Round(RpmToDutyDouble(rpm, defaultMaxRpm), MidpointRounding.AwayFromZero),
            1,
            100);
    }

    private static int InterpolateDutyToRpm(IReadOnlyList<FanCalibrationPoint> pts, double duty)
    {
        if (pts.Count == 0) return 0;
        if (duty <= pts[0].Duty) return pts[0].Rpm;
        if (duty >= pts[^1].Duty) return pts[^1].Rpm;

        for (int i = 0; i < pts.Count - 1; i++)
        {
            var a = pts[i];
            var b = pts[i + 1];
            if (duty >= a.Duty && duty <= b.Duty)
            {
                double span = b.Duty - a.Duty;
                if (span <= 0.0)
                    return a.Rpm;

                double t = (duty - a.Duty) / span;
                return (int)Math.Round(a.Rpm + t * (b.Rpm - a.Rpm), MidpointRounding.AwayFromZero);
            }
        }

        return pts[^1].Rpm;
    }
}

public sealed class FanCalibrationData
{
    public SingleFanCalibration Cpu { get; set; } = new();
    public SingleFanCalibration Gpu { get; set; } = new();
    public DateTime? CalibratedAtUtc { get; set; }
    public byte RecommendedIdleEcMode { get; set; } = 0x01;
    public int QuietAutoCpuRpm { get; set; }
    public int QuietAutoGpuRpm { get; set; }
    public int BalancedAutoCpuRpm { get; set; }
    public int BalancedAutoGpuRpm { get; set; }
    public int EcoAutoCpuRpm { get; set; }
    public int EcoAutoGpuRpm { get; set; }
    public int ManualZeroCpuRpm { get; set; }
    public int ManualZeroGpuRpm { get; set; }
    public bool PreferManualZeroIdle { get; set; }

    [JsonIgnore]
    public bool IsCalibrated => Cpu is { IsCalibrated: true } && Gpu is { IsCalibrated: true };

    [JsonIgnore]
    public bool HasIdleProbeData =>
        QuietAutoCpuRpm > 0 || QuietAutoGpuRpm > 0 ||
        BalancedAutoCpuRpm > 0 || BalancedAutoGpuRpm > 0 ||
        EcoAutoCpuRpm > 0 || EcoAutoGpuRpm > 0 ||
        ManualZeroCpuRpm > 0 || ManualZeroGpuRpm > 0 ||
        PreferManualZeroIdle;

    public (int CpuRpm, int GpuRpm) GetIdleAutoRpms(byte mode) => mode switch
    {
        0x00 => (QuietAutoCpuRpm, QuietAutoGpuRpm),
        0x06 => (EcoAutoCpuRpm, EcoAutoGpuRpm),
        _ => (BalancedAutoCpuRpm, BalancedAutoGpuRpm)
    };

    public int GetRecommendedIdleAutoRpm(bool gpu)
    {
        var (cpuRpm, gpuRpm) = GetIdleAutoRpms(RecommendedIdleEcMode);
        return gpu ? gpuRpm : cpuRpm;
    }

    public void Normalize()
    {
        Cpu ??= new SingleFanCalibration();
        Gpu ??= new SingleFanCalibration();
        Cpu.NormalizeMonotonic();
        Gpu.NormalizeMonotonic();
        if (RecommendedIdleEcMode is not (0x00 or 0x01 or 0x06))
            RecommendedIdleEcMode = 0x01;
        QuietAutoCpuRpm = Math.Max(0, QuietAutoCpuRpm);
        QuietAutoGpuRpm = Math.Max(0, QuietAutoGpuRpm);
        BalancedAutoCpuRpm = Math.Max(0, BalancedAutoCpuRpm);
        BalancedAutoGpuRpm = Math.Max(0, BalancedAutoGpuRpm);
        EcoAutoCpuRpm = Math.Max(0, EcoAutoCpuRpm);
        EcoAutoGpuRpm = Math.Max(0, EcoAutoGpuRpm);
        ManualZeroCpuRpm = Math.Max(0, ManualZeroCpuRpm);
        ManualZeroGpuRpm = Math.Max(0, ManualZeroGpuRpm);
    }

    public FanCalibrationData Copy() => new()
    {
        Cpu = Cpu?.Copy() ?? new SingleFanCalibration(),
        Gpu = Gpu?.Copy() ?? new SingleFanCalibration(),
        CalibratedAtUtc = CalibratedAtUtc,
        RecommendedIdleEcMode = RecommendedIdleEcMode,
        QuietAutoCpuRpm = QuietAutoCpuRpm,
        QuietAutoGpuRpm = QuietAutoGpuRpm,
        BalancedAutoCpuRpm = BalancedAutoCpuRpm,
        BalancedAutoGpuRpm = BalancedAutoGpuRpm,
        EcoAutoCpuRpm = EcoAutoCpuRpm,
        EcoAutoGpuRpm = EcoAutoGpuRpm,
        ManualZeroCpuRpm = ManualZeroCpuRpm,
        ManualZeroGpuRpm = ManualZeroGpuRpm,
        PreferManualZeroIdle = PreferManualZeroIdle
    };
}
