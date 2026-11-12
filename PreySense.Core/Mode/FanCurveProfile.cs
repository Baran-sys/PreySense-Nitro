using System;
using System.Collections.Generic;
using System.Linq;

namespace PreySense.Mode;

public enum FanControlMode { Auto = 0, Max = 1, Custom = 2 }

public sealed class FanCurveProfile
{
    public const int StageCount = 12;
    public static bool ValidTemperatures(int cpu, int gpu) => cpu is > 0 and <= 125;
    public static int CoolingTemperature(int cpu, int gpu) => gpu is > 0 and <= 125 ? Math.Max(cpu, gpu) : cpu;

    public FanControlMode ControlMode { get; set; } = FanControlMode.Auto;
    public int RampUpSeconds { get; set; } = 2;
    public int RampDownSeconds { get; set; } = 4;
    public int HysteresisCelsius { get; set; } = 2;
    public int MinRpmFloor { get; set; } = 1800;
    public bool UseEcAutoBelowMin { get; set; } = true;
    public bool SafeQuietIdleBase { get; set; } = true;
    public FanStage[] Stages { get; set; } = DefaultStages(1);
    public List<FanCurvePoint> CpuPoints { get; set; } = new();
    public List<FanCurvePoint> GpuPoints { get; set; } = new();

    /// <summary>Factory ZBJ-KQ EC 1.08 variant-0 stage tables (RPM targets, rise/fall °C).</summary>
    public static FanStage[] DefaultStages(byte mode) => mode switch
    {
        5 => Clone(Turbo),
        4 => Clone(Performance),
        0 or 6 => Clone(QuietEco),
        _ => Clone(Balanced)
    };

    public static FanCurveProfile CreateDefault(byte mode)
    {
        var stages = DefaultStages(mode);
        var (cpuPoints, gpuPoints) = MigrateFromLegacyStages(stages);
        return new()
        {
            ControlMode = FanControlMode.Auto,
            RampUpSeconds = 2,
            RampDownSeconds = 4,
            HysteresisCelsius = 2,
            MinRpmFloor = 1800,
            UseEcAutoBelowMin = true,
            SafeQuietIdleBase = true,
            Stages = stages,
            CpuPoints = cpuPoints,
            GpuPoints = gpuPoints
        };
    }

    public FanCurveProfile Copy() => new()
    {
        ControlMode = ControlMode,
        RampUpSeconds = RampUpSeconds,
        RampDownSeconds = RampDownSeconds,
        HysteresisCelsius = HysteresisCelsius,
        MinRpmFloor = MinRpmFloor,
        UseEcAutoBelowMin = UseEcAutoBelowMin,
        SafeQuietIdleBase = SafeQuietIdleBase,
        Stages = Clone(Stages),
        CpuPoints = CpuPoints?.Select(p => p.Copy()).ToList() ?? new(),
        GpuPoints = GpuPoints?.Select(p => p.Copy()).ToList() ?? new()
    };

    public void Normalize() => Normalize(1);

    public void Normalize(byte mode)
    {
        RampUpSeconds = Math.Clamp(RampUpSeconds, 0, 10);
        RampDownSeconds = Math.Clamp(RampDownSeconds, 0, 10);
        HysteresisCelsius = Math.Clamp(HysteresisCelsius, 0, 5);
        MinRpmFloor = Math.Clamp(MinRpmFloor, 0, 4000);
        if (!Enum.IsDefined(ControlMode)) ControlMode = FanControlMode.Auto;
        Stages = NormalizeStages(Stages, mode);
        CpuPoints = NormalizePoints(CpuPoints, Stages, gpu: false);
        GpuPoints = NormalizePoints(GpuPoints, Stages, gpu: true);
    }

    /// <summary>
    /// Losslessly converts the legacy offset stage table (Stage[i-1].RiseC, Stage[i].Duty)
    /// into explicit (TempC, Rpm) points using the active fan calibration or physical linear fallback.
    /// </summary>
    public static (List<FanCurvePoint> CpuPoints, List<FanCurvePoint> GpuPoints) MigrateFromLegacyStages(
        FanStage[] stages,
        FanCalibrationData? calibration = null)
    {
        return (
            MigrateSensorPoints(stages, gpu: false, calibration?.Cpu),
            MigrateSensorPoints(stages, gpu: true, calibration?.Gpu)
        );
    }

    public static int GetMaxRpm(bool gpu, FanCalibrationData? calibration = null)
    {
        var fanCal = gpu ? calibration?.Gpu : calibration?.Cpu;
        return fanCal is { IsCalibrated: true }
            ? fanCal.MaxRpm
            : (gpu ? FanStage.GpuMaxRpm : FanStage.CpuMaxRpm);
    }

    public static int ConvertDutyToRpm(int duty, bool gpu, FanCalibrationData? calibration = null)
    {
        int defaultMax = gpu ? FanStage.GpuMaxRpm : FanStage.CpuMaxRpm;
        var fanCal = gpu ? calibration?.Gpu : calibration?.Cpu;
        return fanCal is { IsCalibrated: true }
            ? fanCal.DutyToRpm(duty, defaultMax)
            : FanStage.DutyToPhysicalRpm(duty, defaultMax);
    }

    public static int ConvertRpmToDuty(int rpm, bool gpu, FanCalibrationData? calibration = null)
    {
        int defaultMax = gpu ? FanStage.GpuMaxRpm : FanStage.CpuMaxRpm;
        var fanCal = gpu ? calibration?.Gpu : calibration?.Cpu;
        return fanCal is { IsCalibrated: true }
            ? fanCal.RpmToDuty(rpm, defaultMax)
            : FanStage.PhysicalRpmToDuty(rpm, defaultMax);
    }

    public bool UpdatePointFromGraph(bool gpu, int index, int tempC, int dutyPercent, FanCalibrationData? calibration = null)
    {
        var pts = gpu ? GpuPoints : CpuPoints;
        if (index < 0 || index >= pts.Count)
            return false;

        int minTemp = index == 0 ? 30 : pts[index - 1].TempC + 1;
        int maxTemp = index == pts.Count - 1 ? 100 : pts[index + 1].TempC - 1;
        pts[index].TempC = minTemp <= maxTemp ? Math.Clamp(tempC, minTemp, maxTemp) : pts[index].TempC;

        int clampedDuty = Math.Clamp(dutyPercent, 0, 100);
        int currentDuty = ConvertRpmToDuty(pts[index].Rpm, gpu, calibration);
        if (clampedDuty != currentDuty)
        {
            int maxRpmLimit = GetMaxRpm(gpu, calibration);
            int minRpm = index == 0 ? 0 : pts[index - 1].Rpm;
            int maxRpm = index == pts.Count - 1 ? maxRpmLimit : pts[index + 1].Rpm;
            if (minRpm > maxRpm) (minRpm, maxRpm) = (maxRpm, minRpm);
            int newRpm = ConvertDutyToRpm(clampedDuty, gpu, calibration);
            pts[index].Rpm = Math.Clamp(newRpm, minRpm, maxRpm);
        }

        SyncStagesFromPoints(calibration);
        return true;
    }

    public bool UpdatePointTemperature(bool gpu, int index, int tempC, FanCalibrationData? calibration = null)
    {
        var pts = gpu ? GpuPoints : CpuPoints;
        if (index < 0 || index >= pts.Count)
            return false;

        int minTemp = index == 0 ? 30 : pts[index - 1].TempC + 1;
        int maxTemp = index == pts.Count - 1 ? 100 : pts[index + 1].TempC - 1;
        pts[index].TempC = minTemp <= maxTemp ? Math.Clamp(tempC, minTemp, maxTemp) : pts[index].TempC;

        SyncStagesFromPoints(calibration);
        return true;
    }

    public bool UpdatePointRpm(bool gpu, int index, int newRpm, FanCalibrationData? calibration = null)
    {
        var pts = gpu ? GpuPoints : CpuPoints;
        if (index < 0 || index >= pts.Count)
            return false;

        int maxRpmLimit = GetMaxRpm(gpu, calibration);
        pts[index].Rpm = Math.Clamp(newRpm, 0, maxRpmLimit);
        for (int i = index + 1; i < pts.Count; i++)
        {
            if (pts[i].Rpm < pts[i - 1].Rpm)
                pts[i].Rpm = pts[i - 1].Rpm;
        }
        for (int i = index - 1; i >= 0; i--)
        {
            if (pts[i].Rpm > pts[i + 1].Rpm)
                pts[i].Rpm = pts[i + 1].Rpm;
        }

        SyncStagesFromPoints(calibration);
        return true;
    }

    public bool TryInsertPoint(
        bool gpu,
        int celsius,
        int dutyPercent,
        int minCelsius,
        int maxCelsius,
        FanCalibrationData? calibration,
        out int insertedIndex)
    {
        insertedIndex = -1;
        var pts = gpu ? GpuPoints : CpuPoints;
        if (pts.Count >= StageCount - 1)
            return false;

        int targetC = Math.Clamp(celsius, minCelsius, maxCelsius);
        int insertPos = 0;
        while (insertPos < pts.Count && pts[insertPos].TempC < targetC)
            insertPos++;

        if (insertPos < pts.Count && pts[insertPos].TempC == targetC)
        {
            int prevBound = insertPos == 0 ? minCelsius - 1 : pts[insertPos - 1].TempC;
            int nextBound = insertPos + 1 < pts.Count ? pts[insertPos + 1].TempC : maxCelsius + 1;
            if (targetC + 1 < nextBound)
            {
                targetC++;
                insertPos++;
            }
            else if (targetC - 1 > prevBound)
            {
                targetC--;
            }
            else
            {
                return false;
            }
        }

        int maxRpmLimit = GetMaxRpm(gpu, calibration);
        int minRpm = insertPos == 0 ? 0 : pts[insertPos - 1].Rpm;
        int maxRpm = insertPos == pts.Count ? maxRpmLimit : pts[insertPos].Rpm;
        if (minRpm > maxRpm) (minRpm, maxRpm) = (maxRpm, minRpm);
        int newRpm = Math.Clamp(
            ConvertDutyToRpm(Math.Clamp(dutyPercent, 0, 100), gpu, calibration),
            minRpm,
            maxRpm);

        pts.Insert(insertPos, new FanCurvePoint(targetC, newRpm));
        SyncStagesFromPoints(calibration);
        insertedIndex = insertPos;
        return true;
    }

    public void SyncStagesFromPoints(FanCalibrationData? calibration = null)
    {
        if (Stages == null || Stages.Length < StageCount)
            Stages = NormalizeStages(Stages, 1);

        SyncSensorStages(CpuPoints, gpu: false, calibration?.Cpu);
        SyncSensorStages(GpuPoints, gpu: true, calibration?.Gpu);
    }

    private void SyncSensorStages(List<FanCurvePoint>? points, bool gpu, SingleFanCalibration? fanCal)
    {
        if (points == null || points.Count == 0)
            return;

        int defaultMax = gpu ? FanStage.GpuMaxRpm : FanStage.CpuMaxRpm;
        int ConvertPointRpmToDuty(int rpm) => fanCal is { IsCalibrated: true }
            ? fanCal.RpmToDuty(rpm, defaultMax)
            : FanStage.PhysicalRpmToDuty(rpm, defaultMax);

        int visibleCount = Math.Min(points.Count, StageCount - 1);
        Stages[0].SetDuty(gpu, points[0].TempC <= 30 ? ConvertPointRpmToDuty(points[0].Rpm) : 0);

        for (int i = 0; i < visibleCount; i++)
        {
            int temp = Math.Clamp(points[i].TempC, 30, 100);
            int duty = ConvertPointRpmToDuty(points[i].Rpm);
            if (gpu) Stages[i].GpuRiseC = temp;
            else Stages[i].CpuRiseC = temp;
            Stages[i + 1].SetDuty(gpu, duty);
        }

        int tailDuty = ConvertPointRpmToDuty(points[visibleCount - 1].Rpm);
        for (int i = visibleCount; i < StageCount; i++)
        {
            if (gpu) Stages[i].GpuRiseC = FanStage.DisabledTripCelsius;
            else Stages[i].CpuRiseC = FanStage.DisabledTripCelsius;
            if (i > visibleCount)
                Stages[i].SetDuty(gpu, tailDuty);
        }

        for (int i = 0; i < StageCount; i++)
            Stages[i].Normalize();
    }

    private static List<FanCurvePoint> MigrateSensorPoints(
        FanStage[]? stages,
        bool gpu,
        SingleFanCalibration? fanCal)
    {
        var points = new List<FanCurvePoint>();
        if (stages is null || stages.Length == 0)
            return points;

        int defaultMax = gpu ? FanStage.GpuMaxRpm : FanStage.CpuMaxRpm;
        int ConvertDutyToRpm(int duty) => fanCal is { IsCalibrated: true }
            ? fanCal.DutyToRpm(duty, defaultMax)
            : FanStage.DutyToPhysicalRpm(duty, defaultMax);

        for (int i = 1; i < stages.Length; i++)
        {
            if (!stages[i - 1].HasActiveRise(gpu))
                break;

            int tempC = Math.Clamp(stages[i - 1].RiseCelsius(gpu), 30, 100);
            if (points.Count > 0 && tempC <= points[^1].TempC)
            {
                if (points[^1].TempC >= 100)
                    continue;
                tempC = points[^1].TempC + 1;
            }

            int duty = stages[i].DutyPercent(gpu);
            int rpm = ConvertDutyToRpm(duty);
            if (points.Count > 0 && rpm < points[^1].Rpm)
                rpm = points[^1].Rpm;

            points.Add(new FanCurvePoint(tempC, rpm));
        }

        if (points.Count == 0)
        {
            int rpm = ConvertDutyToRpm(stages[0].DutyPercent(gpu));
            points.Add(new FanCurvePoint(30, rpm));
        }

        return points;
    }

    private static List<FanCurvePoint> NormalizePoints(List<FanCurvePoint>? source, FanStage[] stages, bool gpu)
    {
        if (source is null || source.Count == 0)
            return MigrateSensorPoints(stages, gpu, null);

        var ordered = source
            .Where(p => p != null)
            .Select(p => new FanCurvePoint(Math.Clamp(p.TempC, 30, 100), Math.Clamp(p.Rpm, 0, 10000)))
            .OrderBy(p => p.TempC)
            .ToList();

        if (ordered.Count == 0)
            return MigrateSensorPoints(stages, gpu, null);

        var result = new List<FanCurvePoint>(ordered.Count);
        foreach (var pt in ordered)
        {
            if (result.Count > 0 && pt.TempC == result[^1].TempC)
            {
                result[^1].Rpm = Math.Max(result[^1].Rpm, pt.Rpm);
                continue;
            }

            int rpm = result.Count > 0 ? Math.Max(result[^1].Rpm, pt.Rpm) : pt.Rpm;
            result.Add(new FanCurvePoint(pt.TempC, rpm));
        }

        return result;
    }

    private static FanStage[] NormalizeStages(FanStage[]? source, byte mode)
    {
        var result = DefaultStages(mode);
        if (source is not { Length: > 0 }) return result;
        for (int i = 0; i < StageCount && i < source.Length; i++)
        {
            result[i] = source[i]?.Copy() ?? result[i];
            result[i].Normalize();
        }
        // Compact storage ends at a terminal plateau. Never restore factory trips
        // into its omitted tail: a hand-edited shorter curve must stay terminal.
        if (source.Length < StageCount)
        {
            var terminal = result[source.Length - 1];
            terminal.CpuRiseC = terminal.GpuRiseC = FanStage.DisabledTripCelsius;
            terminal.Normalize();
            for (int i = source.Length; i < StageCount; i++) result[i] = terminal.Copy();
        }
        return result;
    }

    private static FanStage[] Clone(FanStage[] source)
    {
        var copy = new FanStage[source.Length];
        for (int i = 0; i < source.Length; i++) copy[i] = source[i].Copy();
        return copy;
    }

    private static FanStage S(int cpuRise, int cpuFall, int gpuRise, int gpuFall, int cpuRpm, int gpuRpm) => new()
    {
        CpuRiseC = cpuRise, CpuFallC = cpuFall, GpuRiseC = gpuRise, GpuFallC = gpuFall,
        CpuTargetRpm = cpuRpm, GpuTargetRpm = gpuRpm
    };

    // Quiet and Eco share the same EC table.
    private static readonly FanStage[] QuietEco =
    [
        S(57, 0, 57, 0, 0, 0),
        S(64, 40, 62, 40, 1800, 1850),
        S(70, 56, 66, 56, 1970, 1980),
        S(76, 65, 71, 61, 2150, 2150),
        S(81, 71, 75, 65, 2350, 2350),
        S(127, 77, 80, 70, 2550, 2550),
        S(127, 127, 127, 74, 2850, 2850),
        S(127, 127, 127, 127, 2850, 2850),
        S(127, 127, 127, 127, 2850, 2850),
        S(127, 127, 127, 127, 2850, 2850),
        S(127, 127, 127, 127, 2850, 2850),
        S(127, 127, 127, 127, 6400, 6400),
    ];

    private static readonly FanStage[] Balanced =
    [
        S(56, 0, 50, 0, 0, 0),
        S(65, 40, 56, 40, 1800, 1850),
        S(72, 55, 62, 51, 1970, 1980),
        S(78, 64, 68, 57, 2350, 2350),
        S(84, 71, 73, 63, 2550, 2550),
        S(89, 77, 77, 69, 3180, 3170),
        S(127, 83, 127, 74, 3600, 3600),
        S(127, 127, 127, 127, 3600, 3600),
        S(127, 127, 127, 127, 3600, 3600),
        S(127, 127, 127, 127, 3600, 3600),
        S(127, 127, 127, 127, 3600, 3600),
        S(127, 127, 127, 127, 6400, 6400),
    ];

    private static readonly FanStage[] Performance =
    [
        S(50, 0, 50, 0, 0, 0),
        S(54, 40, 56, 40, 1970, 1980),
        S(60, 51, 61, 51, 2350, 2350),
        S(65, 55, 66, 57, 2550, 2550),
        S(70, 61, 71, 62, 3180, 3170),
        S(75, 66, 76, 67, 3900, 3900),
        S(127, 71, 127, 72, 4350, 4350),
        S(127, 127, 127, 127, 4350, 4350),
        S(127, 127, 127, 127, 4350, 4350),
        S(127, 127, 127, 127, 4350, 4350),
        S(127, 127, 127, 127, 4350, 4350),
        S(127, 127, 127, 127, 6400, 6400),
    ];

    private static readonly FanStage[] Turbo =
    [
        S(55, 0, 52, 0, 0, 0),
        S(60, 40, 55, 40, 2150, 2150),
        S(65, 56, 58, 53, 2350, 2350),
        S(70, 61, 61, 56, 2550, 2550),
        S(74, 66, 64, 59, 3180, 3170),
        S(78, 71, 67, 62, 3950, 3900),
        S(95, 75, 86, 65, 4950, 4950),
        S(127, 85, 127, 78, 5500, 5500),
        S(127, 127, 127, 127, 5500, 5500),
        S(127, 127, 127, 127, 5500, 5500),
        S(127, 127, 127, 127, 5500, 5500),
        S(127, 127, 127, 127, 6400, 6400),
    ];
}

public static class FanCurveStorage
{
    private static PreySense.Core.Configuration.ModeFanCurveConfig GetModeFanConfig(
        PreySense.Core.Configuration.PreySenseConfig cfg, byte mode) => mode switch
    {
        0 => cfg.Fans.Quiet,
        6 => cfg.Fans.Eco,
        4 => cfg.Fans.Performance,
        5 => cfg.Fans.Turbo,
        _ => cfg.Fans.Balanced
    };

    public static FanCurveProfile Load(byte mode)
    {
        var profile = FanCurveProfile.CreateDefault(mode);
        try
        {
            var cfg = PreySense.Core.Configuration.ConfigurationService.Instance.GetCurrent();
            var fanMode = GetModeFanConfig(cfg, mode);
            profile.ControlMode = (FanControlMode)Math.Clamp(fanMode.ControlMode, 0, 2);
            profile.RampUpSeconds = Math.Clamp(fanMode.RampUpSeconds, 0, 10);
            profile.RampDownSeconds = Math.Clamp(fanMode.RampDownSeconds, 0, 10);
            profile.HysteresisCelsius = Math.Clamp(fanMode.HysteresisCelsius, 0, 5);
            profile.MinRpmFloor = Math.Clamp(fanMode.MinRpmFloor, 0, 4000);
            profile.UseEcAutoBelowMin = fanMode.UseEcAutoBelowMin;
            profile.SafeQuietIdleBase = fanMode.SafeQuietIdleBase;

            bool hasRpmPoints = (fanMode.Cpu is { Count: > 0 }) || (fanMode.Gpu is { Count: > 0 });
            if (hasRpmPoints)
            {
                if (fanMode.Cpu is { Count: > 0 })
                    profile.CpuPoints = fanMode.Cpu.Select(p => p.Copy()).ToList();
                if (fanMode.Gpu is { Count: > 0 })
                    profile.GpuPoints = fanMode.Gpu.Select(p => p.Copy()).ToList();
                profile.Normalize(mode);
                profile.SyncStagesFromPoints(cfg.Fans.Calibration);
                return profile;
            }

            if (fanMode.Stages is { Length: > 0 } stages)
            {
                profile.Stages = stages.Select(stage => stage?.ToFanStage() ?? new FanStage
                {
                    CpuRiseC = FanStage.DisabledTripCelsius,
                    GpuRiseC = FanStage.DisabledTripCelsius
                }).ToArray();
                profile.Normalize(mode);
                var (cpuPts, gpuPts) = FanCurveProfile.MigrateFromLegacyStages(profile.Stages, cfg.Fans.Calibration);
                profile.CpuPoints = cpuPts;
                profile.GpuPoints = gpuPts;
                profile.Normalize(mode);
                return profile;
            }
        }
        catch { }
        profile.Normalize(mode);
        return profile;
    }

    public static FanControlMode DecodeControlMode(int? saved, bool legacyEnabled, bool legacyMax, int? legacyGlobalMode)
    {
        if (saved.HasValue)
            return saved.Value is >= 0 and <= 2 ? (FanControlMode)saved.Value : FanControlMode.Auto;
        if (legacyGlobalMode == 0 || !legacyEnabled) return FanControlMode.Auto;
        return legacyMax ? FanControlMode.Max : FanControlMode.Custom;
    }

    public static void Save(byte mode, FanCurveProfile profile)
    {
        var saved = profile.Copy();
        saved.Normalize(mode);
        try
        {
            PreySense.Core.Configuration.ConfigurationService.Instance.Update(cfg =>
            {
                var fanMode = GetModeFanConfig(cfg, mode);
                fanMode.ControlMode = (int)saved.ControlMode;
                fanMode.RampUpSeconds = saved.RampUpSeconds;
                fanMode.RampDownSeconds = saved.RampDownSeconds;
                fanMode.HysteresisCelsius = saved.HysteresisCelsius;
                fanMode.MinRpmFloor = saved.MinRpmFloor;
                fanMode.UseEcAutoBelowMin = saved.UseEcAutoBelowMin;
                fanMode.SafeQuietIdleBase = saved.SafeQuietIdleBase;
                fanMode.Cpu = saved.CpuPoints.Select(p => p.Copy()).ToList();
                fanMode.Gpu = saved.GpuPoints.Select(p => p.Copy()).ToList();
                fanMode.Stages = null;
            });
        }
        catch { }
    }
}
