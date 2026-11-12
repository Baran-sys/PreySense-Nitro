using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PreySense.Core.Configuration;
using PreySense.Core.Services;
using PreySense.Mode;

namespace PreySense.Tests;

[TestClass]
public class FanCalibrationAndModelTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PreySenseFanCalTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { }
    }

    [TestMethod]
    public void FanDiagnosticLogger_CreatesHeader_LogsRows_AndTogglesOnOff()
    {
        string logPath = Path.Combine(_tempDir, "fanlog.csv");
        var logger = new FanDiagnosticLogger(logPath, maxFileSizeBytes: 1024 * 1024, enabled: false);

        logger.Log("Balanced", "Custom", 62, 55, 3200, 2900, 35, 30, 3195, 2905, "Custom", false,
            new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        Assert.IsFalse(File.Exists(logPath), "Disabled logger must not create or write fanlog.csv.");

        logger.Enabled = true;
        logger.Log("Balanced", "Custom", 62, 55, 3200, 2900, 35, 30, 3195, 2905, "Custom", false,
            new DateTime(2026, 1, 1, 12, 0, 1, DateTimeKind.Utc));
        logger.Log("Turbo", "Max", 80, 74, 6200, 6100, 100, 100, 6180, 6090, "Max", true,
            new DateTime(2026, 1, 1, 12, 0, 2, DateTimeKind.Utc));

        logger.Enabled = false;
        logger.Log("Turbo", "Max", 81, 75, 6200, 6100, 100, 100, 6180, 6090, "Max", true,
            new DateTime(2026, 1, 1, 12, 0, 3, DateTimeKind.Utc));

        string[] lines = File.ReadAllLines(logPath);
        Assert.HasCount(3, lines);
        Assert.AreEqual(
            "TimestampUtc,PowerMode,ControlMode,CpuTempC,GpuTempC,CpuTargetRpm,GpuTargetRpm,SentCpuDuty,SentGpuDuty,MeasuredCpuRpm,MeasuredGpuRpm,EcState,EmergencyActive",
            lines[0]);
        Assert.AreEqual("2026-01-01T12:00:01.0000000Z,Balanced,Custom,62,55,3200,2900,35,30,3195,2905,Custom,false", lines[1]);
        Assert.AreEqual("2026-01-01T12:00:02.0000000Z,Turbo,Max,80,74,6200,6100,100,100,6180,6090,Max,true", lines[2]);
    }

    [TestMethod]
    public void FanDiagnosticLogger_RotatesToPrevCsv_WhenSizeExceedsLimit()
    {
        string logPath = Path.Combine(_tempDir, "fanlog.csv");
        var logger = new FanDiagnosticLogger(logPath, maxFileSizeBytes: 300, enabled: true);

        for (int i = 0; i < 8; i++)
        {
            logger.Log("Balanced", "Custom", 50 + i, 45 + i, 2500, 2400, 25, 24, 2505, 2405, "Custom", false,
                new DateTime(2026, 1, 1, 12, 0, i, DateTimeKind.Utc));
        }

        Assert.IsTrue(File.Exists(logPath));
        Assert.IsTrue(File.Exists(logger.PreviousLogFilePath));
        Assert.EndsWith("fanlog.prev.csv", logger.PreviousLogFilePath);

        string[] activeLines = File.ReadAllLines(logPath);
        string[] prevLines = File.ReadAllLines(logger.PreviousLogFilePath);
        Assert.AreEqual(FanDiagnosticLogger.CsvHeader, activeLines[0]);
        Assert.AreEqual(FanDiagnosticLogger.CsvHeader, prevLines[0]);
        Assert.IsGreaterThan(1, activeLines.Length);
        Assert.IsGreaterThan(1, prevLines.Length);
    }

    [TestMethod]
    public async Task FanDiagnosticLogger_IsThreadSafeUnderConcurrentWrites()
    {
        string logPath = Path.Combine(_tempDir, "fanlog.csv");
        var logger = new FanDiagnosticLogger(logPath, maxFileSizeBytes: 10 * 1024 * 1024, enabled: true);

        var tasks = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 25; i++)
            {
                logger.Log("Balanced", "Custom", 60 + worker, 50 + i, 3000, 2800, 30, 28, 3000, 2800, "Custom", false);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        string[] lines = File.ReadAllLines(logPath);
        Assert.HasCount(201, lines); // 1 header + 200 rows
        Assert.AreEqual(FanDiagnosticLogger.CsvHeader, lines[0]);
    }

    [TestMethod]
    public void SingleFanCalibration_NormalizesNoisyTachReadings_AndComputesThresholdsAndRoundTrips()
    {
        var cal = new SingleFanCalibration
        {
            Points =
            [
                new FanCalibrationPoint(100, 6200),
                new FanCalibrationPoint(0, 0),
                new FanCalibrationPoint(6, 50),
                new FanCalibrationPoint(10, 1820),
                new FanCalibrationPoint(20, 1790), // noisy tach dip below previous duty
                new FanCalibrationPoint(50, 3900),
                new FanCalibrationPoint(60, 3880), // noisy tach dip
                new FanCalibrationPoint(80, 5200)
            ]
        };

        cal.NormalizeMonotonic();

        Assert.IsTrue(cal.IsCalibrated);
        Assert.AreEqual(6, cal.StopThresholdDuty);
        Assert.AreEqual(10, cal.MinStableDuty);
        Assert.AreEqual(1820, cal.MinStableRpm);
        Assert.AreEqual(6200, cal.MaxRpm);

        // Verify sorted and monotonically non-decreasing
        for (int i = 1; i < cal.Points.Count; i++)
        {
            Assert.IsGreaterThan(cal.Points[i - 1].Duty, cal.Points[i].Duty);
            Assert.IsGreaterThanOrEqualTo(cal.Points[i - 1].Rpm, cal.Points[i].Rpm);
        }

        // Round-trip checks
        Assert.AreEqual(0, cal.RpmToDuty(0));
        Assert.AreEqual(0, cal.RpmToDuty(-100));
        Assert.AreEqual(10, cal.RpmToDuty(1820));
        Assert.AreEqual(50, cal.RpmToDuty(3900));
        Assert.AreEqual(80, cal.RpmToDuty(5200));
        Assert.AreEqual(100, cal.RpmToDuty(6200));
        Assert.AreEqual(100, cal.RpmToDuty(7000));

        // Interpolated midpoint between (60, 3900) and (80, 5200): duty 70 -> 4550 RPM
        Assert.AreEqual(4550, cal.DutyToRpm(70));
        Assert.AreEqual(70, cal.RpmToDuty(4550));

        // Monotonicity across full RPM sweep
        int prevDuty = 0;
        for (int rpm = 100; rpm <= 6500; rpm += 100)
        {
            int duty = cal.RpmToDuty(rpm);
            Assert.IsGreaterThanOrEqualTo(1, duty);
            Assert.IsLessThanOrEqualTo(100, duty);
            Assert.IsGreaterThanOrEqualTo(prevDuty, duty);
            if (rpm >= cal.MinStableRpm)
                Assert.IsGreaterThanOrEqualTo(cal.MinStableDuty, duty);
            prevDuty = duty;
        }
    }

    [TestMethod]
    public void SingleFanCalibration_UncalibratedFallsBackToPhysicalRpmFormula()
    {
        var uncalibrated = new SingleFanCalibration();
        Assert.IsFalse(uncalibrated.IsCalibrated);
        Assert.AreEqual(FanStage.DutyToPhysicalRpm(57, 6400), uncalibrated.DutyToRpm(57, 6400));
        Assert.AreEqual(FanStage.PhysicalRpmToDuty(4508, 6400), uncalibrated.RpmToDuty(4508, 6400));
        Assert.AreEqual(0, uncalibrated.RpmToDuty(0, 6400));
    }

    [TestMethod]
    public async Task FanCalibrationService_FullSweep_DetectsMinStableDuty_AndRestoresPreviousMode()
    {
        int currentDuty = 0;
        int sampleIndex = 0;
        int restoreCount = 0;
        var stepsReported = new List<FanCalibrationProgress>();

        var service = new FanCalibrationService(
            setFanSpeeds: (cpu, gpu) =>
            {
                currentDuty = cpu;
                sampleIndex = 0;
                return true;
            },
            readExactRpms: () =>
            {
                int idx = sampleIndex++;
                if (currentDuty == 0)
                    return (true, 2200, 2150); // Simulated EC Auto at duty 0
                if (currentDuty < 8)
                    return (true, 0, 0);
                if (currentDuty is 8 or 9)
                {
                    // Unstable spin-up jitter: spread = 240 >= 100
                    int jitter = idx switch { 0 => 1400, 1 => 1640, _ => 1520 };
                    return (true, jitter, jitter);
                }
                int baseCpu = 1800 + (currentDuty - 10) * 48;
                int baseGpu = 1750 + (currentDuty - 10) * 46;
                return (true, baseCpu + idx * 5, baseGpu + idx * 5);
            },
            readTemperatures: () => (52, 46),
            restorePreviousMode: () => restoreCount++,
            settleDelayMs: 0,
            sampleIntervalMs: 0,
            samplesPerStep: 3,
            delayAsync: (_, _) => Task.CompletedTask);

        var result = await service.RunAsync(new Progress<FanCalibrationProgress>(p =>
        {
            lock (stepsReported) stepsReported.Add(p);
        }));

        int expectedPointCount = FanCalibrationService.DutySchedule
            .Concat(FanCalibrationService.LowEndDescendingSchedule)
            .Distinct()
            .Count();

        Assert.IsTrue(result.Success, result.Message);
        Assert.AreEqual(FanCalibrationStatus.Succeeded, result.Status);
        Assert.IsNotNull(result.Data);
        Assert.IsTrue(result.Data.IsCalibrated);
        Assert.AreEqual(1, restoreCount);
        Assert.HasCount(expectedPointCount, result.Data.Cpu.Points);
        Assert.AreEqual(7, result.Data.Cpu.StopThresholdDuty);
        Assert.AreEqual(10, result.Data.Cpu.MinStableDuty);
        Assert.AreEqual(10, result.Data.Cpu.MinQuietDuty);
        Assert.AreEqual(1805, result.Data.Cpu.MinStableRpm);
        Assert.AreEqual(1805, result.Data.Cpu.MinQuietRpm);
        Assert.AreEqual(0, result.Data.Cpu.BlackoutMaxDuty);
        Assert.AreEqual(2200, result.Data.Cpu.EcAutoBaselineRpm);
        Assert.AreEqual(6125, result.Data.Cpu.MaxRpm);
    }

    [TestMethod]
    [DataRow(85, 50)]
    [DataRow(60, 80)]
    public async Task FanCalibrationService_ThermalAbort_RestoresPreviousMode(int cpuTemp, int gpuTemp)
    {
        int restoreCount = 0;
        int stepsExecuted = 0;

        var service = new FanCalibrationService(
            setFanSpeeds: (_, _) => { stepsExecuted++; return true; },
            readExactRpms: () => (true, 2000, 2000),
            readTemperatures: () => stepsExecuted >= 2 ? (cpuTemp, gpuTemp) : (55, 50),
            restorePreviousMode: () => restoreCount++,
            settleDelayMs: 0,
            sampleIntervalMs: 0,
            samplesPerStep: 3,
            delayAsync: (_, _) => Task.CompletedTask);

        var result = await service.RunAsync();

        Assert.IsFalse(result.Success);
        Assert.AreEqual(FanCalibrationStatus.ThermalAbort, result.Status);
        Assert.IsNull(result.Data);
        Assert.AreEqual(1, restoreCount, "Prior fan mode must ALWAYS be restored after thermal abort.");
    }

    [TestMethod]
    public async Task FanCalibrationService_Cancellation_RestoresPreviousMode()
    {
        int restoreCount = 0;
        using var cts = new CancellationTokenSource();
        int stepCount = 0;

        var service = new FanCalibrationService(
            setFanSpeeds: (_, _) =>
            {
                stepCount++;
                if (stepCount == 3)
                    cts.Cancel();
                return true;
            },
            readExactRpms: () => (true, 2200, 2100),
            readTemperatures: () => (50, 45),
            restorePreviousMode: () => restoreCount++,
            settleDelayMs: 0,
            sampleIntervalMs: 0,
            samplesPerStep: 3,
            delayAsync: (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });

        var result = await service.RunAsync(cancellationToken: cts.Token);

        Assert.IsFalse(result.Success);
        Assert.AreEqual(FanCalibrationStatus.Cancelled, result.Status);
        Assert.AreEqual(1, restoreCount, "Prior fan mode must ALWAYS be restored on cancellation.");
    }

    [TestMethod]
    public void MockHardwareService_AndInterfaceDefault_SupportExactFanRpms()
    {
        var mock = new MockHardwareService();
        Assert.IsTrue(mock.TryReadExactFanRpms(out int cpu, out int gpu));
        Assert.AreEqual(2400, cpu);
        Assert.AreEqual(2100, gpu);

        mock.ExactFanRpmProvider = () => (3456, 3123);
        Assert.IsTrue(mock.TryReadExactFanRpms(out cpu, out gpu));
        Assert.AreEqual(3456, cpu);
        Assert.AreEqual(3123, gpu);
    }

    [TestMethod]
    public void MonotoneCubicInterpolator_PassesThroughPoints_WithZeroOvershoot_AndMatchesBezierControlPoints()
    {
        double[] xs = [40, 55, 65, 65 + 10, 85, 95];
        // Includes flat plateau (2500 -> 2500) and sharp rise (2500 -> 5800)
        double[] ys = [1800, 2500, 2500, 4800, 5800, 6200];
        double[] tangents = MonotoneCubicInterpolator.ComputeTangents(xs, ys);

        // Exact pass-through and clamping
        Assert.AreEqual(1800.0, MonotoneCubicInterpolator.Evaluate(30, xs, ys), 1e-6);
        Assert.AreEqual(6200.0, MonotoneCubicInterpolator.Evaluate(100, xs, ys), 1e-6);
        for (int i = 0; i < xs.Length; i++)
            Assert.AreEqual(ys[i], MonotoneCubicInterpolator.Evaluate(xs[i], xs, ys), 1e-6);

        // Flat plateau must stay strictly at 2500 with zero overshoot/dip
        for (double t = 55.0; t <= 65.0; t += 0.5)
            Assert.AreEqual(2500.0, MonotoneCubicInterpolator.Evaluate(t, xs, ys), 1e-6);

        // Strict monotonicity across entire temperature range
        double prev = double.NegativeInfinity;
        for (double temp = 35.0; temp <= 100.0; temp += 0.25)
        {
            double val = MonotoneCubicInterpolator.Evaluate(temp, xs, ys);
            Assert.IsGreaterThanOrEqualTo(prev - 1e-9, val, $"Overshoot/dip detected at {temp}C: {val} < {prev}");
            prev = val;
        }

        // Exact equivalence between Evaluate(x) and cubic Bezier curve from GetBezierControlPoints
        for (int seg = 0; seg < xs.Length - 1; seg++)
        {
            double x0 = xs[seg], y0 = ys[seg], m0 = tangents[seg];
            double x1 = xs[seg + 1], y1 = ys[seg + 1], m1 = tangents[seg + 1];
            var (_, c1y, _, c2y) = MonotoneCubicInterpolator.GetBezierControlPoints(x0, y0, m0, x1, y1, m1);

            foreach (double t in new[] { 0.25, 0.5, 0.75 })
            {
                double u = 1.0 - t;
                double bezierY = u * u * u * y0 + 3 * u * u * t * c1y + 3 * u * t * t * c2y + t * t * t * y1;
                double hermiteY = MonotoneCubicInterpolator.Evaluate(x0 + t * (x1 - x0), xs, ys, tangents);
                Assert.AreEqual(hermiteY, bezierY, 1e-6);
            }
        }
    }

    [TestMethod]
    public void LegacyStages_MigrateLosslesslyToExplicitRpmPoints()
    {
        var stages = new FanStage[FanCurveProfile.StageCount];
        for (int i = 0; i < stages.Length; i++)
            stages[i] = new FanStage { CpuRiseC = 127, GpuRiseC = 127 };

        // Stage[0..2] rise -> Stage[1..3]
        stages[0].CpuRiseC = 45;
        stages[1].SetDuty(false, 10);
        stages[1].CpuRiseC = 55;
        stages[2].SetDuty(false, 20);
        stages[2].CpuRiseC = 60;
        stages[3].SetDuty(false, 25);

        // Specific case from plan:
        // Stage[3].CpuTempC = 65 / Stage[4].CpuDutyPercent = 27
        // Stage[4].CpuTempC = 73 / Stage[5].CpuDutyPercent = 57 -> (73, ~4508)
        stages[3].CpuRiseC = 65;
        stages[4].SetDuty(false, 27);
        stages[4].CpuRiseC = 73;
        stages[5].SetDuty(false, 57);
        stages[5].CpuRiseC = 85;
        stages[6].SetDuty(false, 90);

        var (cpuPoints, _) = FanCurveProfile.MigrateFromLegacyStages(stages);

        Assert.HasCount(6, cpuPoints);
        Assert.AreEqual(65, cpuPoints[3].TempC);
        Assert.AreEqual(FanStage.DutyToPhysicalRpm(27, FanStage.CpuMaxRpm), cpuPoints[3].Rpm);
        Assert.AreEqual(73, cpuPoints[4].TempC);
        Assert.AreEqual(4508, cpuPoints[4].Rpm);
        Assert.AreEqual(85, cpuPoints[5].TempC);
        Assert.AreEqual(FanStage.DutyToPhysicalRpm(90, FanStage.CpuMaxRpm), cpuPoints[5].Rpm);
    }

    [TestMethod]
    public void Config_PersistsFanDiagnosticsFlag_CalibrationData_AndRpmNativeCurve()
    {
        string configPath = Path.Combine(_tempDir, "preysense.json");
        var service = new ConfigurationService(configPath);
        var cfg = service.GetCurrent();

        cfg.General.FanDiagnosticsLogEnabled = true;
        cfg.Fans.Calibration = new FanCalibrationData
        {
            CalibratedAtUtc = new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc),
            Cpu = new SingleFanCalibration
            {
                Points = [new(0, 0), new(10, 1800), new(50, 3800), new(100, 6200)]
            },
            Gpu = new SingleFanCalibration
            {
                Points = [new(0, 0), new(10, 1850), new(50, 3900), new(100, 6100)]
            }
        };
        cfg.Fans.Calibration.Normalize();
        cfg.Fans.Balanced.RampUpSeconds = 8;
        cfg.Fans.Balanced.RampDownSeconds = 6;
        cfg.Fans.Balanced.HysteresisCelsius = 3;
        cfg.Fans.Balanced.MinRpmFloor = 1900;
        cfg.Fans.Balanced.UseEcAutoBelowMin = true;
        cfg.Fans.Balanced.Cpu = [new(45, 1900), new(73, 4500), new(90, 6200)];
        cfg.Fans.Balanced.Stages = null;

        service.Save(cfg);

        var reloaded = new ConfigurationService(configPath).GetCurrent();
        Assert.AreEqual(2, reloaded.SchemaVersion);
        Assert.IsTrue(reloaded.General.FanDiagnosticsLogEnabled);
        Assert.IsNotNull(reloaded.Fans.Calibration);
        Assert.IsTrue(reloaded.Fans.Calibration.IsCalibrated);
        Assert.AreEqual(6200, reloaded.Fans.Calibration.Cpu.MaxRpm);
        Assert.AreEqual(6100, reloaded.Fans.Calibration.Gpu.MaxRpm);
        Assert.AreEqual(8, reloaded.Fans.Balanced.RampUpSeconds);
        Assert.AreEqual(6, reloaded.Fans.Balanced.RampDownSeconds);
        Assert.AreEqual(3, reloaded.Fans.Balanced.HysteresisCelsius);
        Assert.AreEqual(1900, reloaded.Fans.Balanced.MinRpmFloor);
        Assert.IsTrue(reloaded.Fans.Balanced.UseEcAutoBelowMin);
        Assert.IsNull(reloaded.Fans.Balanced.Stages);
        Assert.HasCount(3, reloaded.Fans.Balanced.Cpu!);
        Assert.AreEqual(4500, reloaded.Fans.Balanced.Cpu![1].Rpm);

        // Verify legacy Stages is omitted from JSON when null
        string json = File.ReadAllText(configPath);
        using var doc = JsonDocument.Parse(json);
        var balancedEl = doc.RootElement.GetProperty("Fans").GetProperty("Balanced");
        Assert.IsFalse(balancedEl.TryGetProperty("Stages", out _));
    }

    [TestMethod]
    public void FanCurveProfile_EditsPreserveExactRpmPrecision_AcrossDragInsertTempAndRpmEdits()
    {
        var profile = new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            CpuPoints =
            [
                new FanCurvePoint(45, 1900),
                new FanCurvePoint(73, 4500),
                new FanCurvePoint(90, 6200)
            ],
            GpuPoints =
            [
                new FanCurvePoint(50, 2100),
                new FanCurvePoint(75, 4300)
            ]
        };
        profile.SyncStagesFromPoints();

        // Horizontal drag on point 0 (duty stays at 1%): must preserve 1900, 4500, and 6200 exactly.
        int duty0 = FanCurveProfile.ConvertRpmToDuty(1900, gpu: false);
        Assert.IsTrue(profile.UpdatePointFromGraph(gpu: false, index: 0, tempC: 48, dutyPercent: duty0));
        Assert.AreEqual(48, profile.CpuPoints[0].TempC);
        Assert.AreEqual(1900, profile.CpuPoints[0].Rpm);
        Assert.AreEqual(4500, profile.CpuPoints[1].Rpm);
        Assert.AreEqual(6200, profile.CpuPoints[2].Rpm);

        // Horizontal drag on point 1 (duty stays at 57%): 4500 must NOT quantize to 4508.
        int duty1 = FanCurveProfile.ConvertRpmToDuty(4500, gpu: false);
        Assert.IsTrue(profile.UpdatePointFromGraph(gpu: false, index: 1, tempC: 74, dutyPercent: duty1));
        Assert.AreEqual(74, profile.CpuPoints[1].TempC);
        Assert.AreEqual(4500, profile.CpuPoints[1].Rpm);
        Assert.AreEqual(6200, profile.CpuPoints[2].Rpm);

        // Right-click add point at (60C, 30%): must insert cleanly without quantizing 1900, 4500, 6200.
        Assert.IsTrue(profile.TryInsertPoint(gpu: false, celsius: 60, dutyPercent: 30, minCelsius: 30, maxCelsius: 100, calibration: null, out int inserted));
        Assert.AreEqual(1, inserted);
        Assert.HasCount(4, profile.CpuPoints);
        Assert.AreEqual(1900, profile.CpuPoints[0].Rpm);
        Assert.AreEqual(60, profile.CpuPoints[1].TempC);
        Assert.AreEqual(4500, profile.CpuPoints[2].Rpm);
        Assert.AreEqual(6200, profile.CpuPoints[3].Rpm);

        // Exact RPM and Temp field commits preserve exact precision.
        Assert.IsTrue(profile.UpdatePointRpm(gpu: false, index: 2, newRpm: 4555));
        Assert.IsTrue(profile.UpdatePointTemperature(gpu: false, index: 2, tempC: 76));
        Assert.AreEqual(76, profile.CpuPoints[2].TempC);
        Assert.AreEqual(4555, profile.CpuPoints[2].Rpm);
        Assert.AreEqual(1900, profile.CpuPoints[0].Rpm);
        Assert.AreEqual(6200, profile.CpuPoints[3].Rpm);
        // GPU points must remain untouched.
        Assert.HasCount(2, profile.GpuPoints);
        Assert.AreEqual(2100, profile.GpuPoints[0].Rpm);
        Assert.AreEqual(4300, profile.GpuPoints[1].Rpm);
    }

    [TestMethod]
    public void FanCurveProfile_AsymmetricCpuGpuCounts_AndSinglePointCurve_SyncWithoutDroppingPoints()
    {
        var profile = new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            CpuPoints =
            [
                new(40, 1800), new(50, 2200), new(60, 2800), new(70, 3600),
                new(78, 4500), new(85, 5400), new(92, 6200)
            ],
            GpuPoints =
            [
                new(45, 1900), new(55, 2400), new(65, 3200),
                new(72, 4100), new(80, 5100), new(88, 6000)
            ]
        };
        profile.Normalize(1);
        profile.SyncStagesFromPoints();

        var (_, cpuTemps, _, _) = PreySense.Frontend.Views.FanCurveWindow.BuildGraph(profile.Stages, gpu: false);
        var (_, gpuTemps, _, _) = PreySense.Frontend.Views.FanCurveWindow.BuildGraph(profile.Stages, gpu: true);
        CollectionAssert.AreEqual(new[] { 40, 50, 60, 70, 78, 85, 92 }, cpuTemps);
        CollectionAssert.AreEqual(new[] { 45, 55, 65, 72, 80, 88 }, gpuTemps);
        Assert.HasCount(7, profile.CpuPoints);
        Assert.HasCount(6, profile.GpuPoints);
        Assert.AreEqual(4500, profile.CpuPoints[4].Rpm);

        // Single-point curve must stay visible on graph (not hidden behind DisabledTripCelsius).
        var single = new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            CpuPoints = [new(60, 3000)],
            GpuPoints = [new(30, 2500)]
        };
        single.SyncStagesFromPoints();
        var (_, singleCpuTemps, _, _) = PreySense.Frontend.Views.FanCurveWindow.BuildGraph(single.Stages, gpu: false);
        var (_, singleGpuTemps, _, singleGpuLead) = PreySense.Frontend.Views.FanCurveWindow.BuildGraph(single.Stages, gpu: true);
        CollectionAssert.AreEqual(new[] { 60 }, singleCpuTemps);
        CollectionAssert.AreEqual(new[] { 30 }, singleGpuTemps);
        Assert.AreEqual(FanStage.PhysicalRpmToDuty(2500, FanStage.GpuMaxRpm), singleGpuLead);
    }

    [TestMethod]
    public void SingleFanCalibration_PlateauAndFlatTail_StayMonotonicWithoutDivisionByZero()
    {
        var cal = new SingleFanCalibration
        {
            Points =
            [
                new(0, 0),
                new(2, 2000),
                new(4, 2000),
                new(6, 2000),
                new(8, 2200),
                new(10, 2400),
                new(100, 6200)
            ]
        };
        cal.NormalizeMonotonic();

        Assert.AreEqual(2, cal.MinStableDuty);
        Assert.AreEqual(2000, cal.MinStableRpm);
        Assert.AreEqual(2000, cal.DutyToRpm(4));
        Assert.AreEqual(2, cal.RpmToDuty(2000));

        int prevDuty = 0;
        for (int rpm = 0; rpm <= 7000; rpm += 50)
        {
            int duty = cal.RpmToDuty(rpm);
            Assert.IsGreaterThanOrEqualTo(prevDuty, duty, $"Non-monotonic jump at {rpm} RPM");
            prevDuty = duty;
        }
    }

    [TestMethod]
    public async Task FanCalibrationService_SuppressesEcAutoBaseline_EvenWhenFirstSpinExceedsAutoRpm_AndIgnoresInvalidTemps()
    {
        int currentDuty = 0;
        int stepIdx = -1;
        var service = new FanCalibrationService(
            setFanSpeeds: (cpu, _) => { currentDuty = cpu; stepIdx++; return true; },
            readExactRpms: () =>
            {
                if (currentDuty == 0) return (true, 2200, 2200); // EC Auto spins at 2200 RPM
                if (currentDuty <= 7) return (true, 0, 0);        // Fan stalled at 0 RPM
                if (currentDuty == 50) return (true, 4000, -1);   // Transient GPU tach miss on one step
                int rpm = 2400 + (currentDuty - 8) * 40;          // Spin-up at 2400 RPM (> EC Auto 2200!)
                return (true, rpm, rpm);
            },
            readTemperatures: () => stepIdx == 1 ? (-1, -1) : (62, -1), // Invalid/missing sensor readings must not abort
            restorePreviousMode: () => { },
            settleDelayMs: 0,
            sampleIntervalMs: 0,
            samplesPerStep: 2,
            delayAsync: (_, _) => Task.CompletedTask);

        var result = await service.RunAsync();

        int expectedCpuCount = FanCalibrationService.DutySchedule
            .Concat(FanCalibrationService.LowEndDescendingSchedule)
            .Distinct()
            .Count();

        Assert.IsTrue(result.Success, result.Message);
        Assert.IsNotNull(result.Data);
        Assert.AreEqual(7, result.Data.Cpu.StopThresholdDuty);
        Assert.AreEqual(8, result.Data.Cpu.MinStableDuty);
        Assert.AreEqual(2400, result.Data.Cpu.MinStableRpm);
        Assert.AreEqual(2200, result.Data.Cpu.EcAutoBaselineRpm);
        Assert.AreEqual(0, result.Data.Cpu.Points.First(p => p.Duty == 2).Rpm); // Duty 2 must remain 0, not 2200!
        // Transient GPU -1 step should be skipped rather than injecting a 0 RPM point.
        Assert.HasCount(expectedCpuCount - 1, result.Data.Gpu.Points);
    }

    [TestMethod]
    public void ConfigurationService_NormalizesUnsortedCalibrationPointsOnLoad()
    {
        string configPath = Path.Combine(_tempDir, "preysense.json");
        var service = new ConfigurationService(configPath);
        var cfg = service.GetCurrent();
        cfg.Fans.Calibration = new FanCalibrationData
        {
            Cpu = new SingleFanCalibration
            {
                Points = [new(100, 6200), new(20, 2500), new(10, 2600), new(0, 0)]
            },
            Gpu = new SingleFanCalibration
            {
                Points = [new(0, 0), new(100, 6000)]
            }
        };
        service.Save(cfg);

        var reloaded = new ConfigurationService(configPath).GetCurrent();
        Assert.IsNotNull(reloaded.Fans.Calibration);
        Assert.AreEqual(0, reloaded.Fans.Calibration.Cpu.Points[0].Duty);
        Assert.AreEqual(10, reloaded.Fans.Calibration.Cpu.Points[1].Duty);
        Assert.AreEqual(2600, reloaded.Fans.Calibration.Cpu.Points[1].Rpm);
        Assert.AreEqual(2600, reloaded.Fans.Calibration.Cpu.Points[2].Rpm); // Monotonic max envelope applied
    }

    [TestMethod]
    public async Task FanCalibrationService_DetectsNitroKickZone_ViaDescendingScan_AndExcludesKickZoneFromRpmToDuty()
    {
        var executedDuties = new List<int>();
        var settleDelaysObserved = new List<int>();
        int currentDuty = 0;

        var service = new FanCalibrationService(
            setFanSpeeds: (cpu, _) =>
            {
                currentDuty = cpu;
                executedDuties.Add(cpu);
                return true;
            },
            readExactRpms: () =>
            {
                if (currentDuty == 0)
                    return (true, 1800, 1800);

                // Simulate Nitro ANV16-41: ascending 0->2 transient reads low (1700 RPM) during sweep,
                // while descending low-end verification reveals EC anti-stall kick (2727..3157 RPM) at 1..9%,
                // and true minimum quiet steady floor at Duty 10 = 2222 RPM.
                bool inDescendingPhase = executedDuties.Count > FanCalibrationService.DutySchedule.Length;
                if (currentDuty <= 4)
                {
                    int kickRpm = inDescendingPhase ? 3157 : 1700;
                    return (true, kickRpm, kickRpm);
                }
                if (currentDuty <= 9)
                    return (true, 2727, 2727);
                if (currentDuty == 10)
                    return (true, 2222, 2222);

                int rpm = 2222 + (currentDuty - 10) * 44;
                return (true, rpm, rpm);
            },
            readTemperatures: () => (54, 48),
            restorePreviousMode: () => { },
            settleDelayMs: 6000,
            sampleIntervalMs: 0,
            samplesPerStep: 2,
            delayAsync: (ms, _) =>
            {
                settleDelaysObserved.Add(ms);
                return Task.CompletedTask;
            });

        var result = await service.RunAsync();

        Assert.IsTrue(result.Success, result.Message);
        Assert.IsNotNull(result.Data);

        var expectedSchedule = FanCalibrationService.DutySchedule
            .Concat(FanCalibrationService.LowEndDescendingSchedule)
            .ToArray();
        CollectionAssert.AreEqual(expectedSchedule, executedDuties);
        Assert.AreEqual(6000, settleDelaysObserved[0]);
        Assert.AreEqual(8000, settleDelaysObserved[^1]);

        foreach (var fan in new[] { result.Data.Cpu, result.Data.Gpu })
        {
            Assert.IsTrue(fan.IsCalibrated);
            Assert.AreEqual(10, fan.MinQuietDuty);
            Assert.AreEqual(2222, fan.MinQuietRpm);
            Assert.AreEqual(10, fan.MinStableDuty);
            Assert.AreEqual(2222, fan.MinStableRpm);
            Assert.AreEqual(9, fan.BlackoutMaxDuty);
            Assert.AreEqual(1800, fan.EcAutoBaselineRpm);

            for (int d = 1; d <= fan.BlackoutMaxDuty; d++)
            {
                Assert.IsTrue(fan.IsDutyInKickZone(d), $"Duty {d}% should be flagged in kick zone.");
                Assert.IsFalse(fan.Points.Any(p => p.Duty == d), $"Kick-zone duty {d}% must be excluded from Points.");
            }
            Assert.IsFalse(fan.IsDutyInKickZone(0));
            Assert.IsFalse(fan.IsDutyInKickZone(10));
            Assert.AreEqual(0, fan.Points[0].Duty);
            Assert.AreEqual(0, fan.Points[0].Rpm);
            Assert.AreEqual(10, fan.Points[1].Duty);
            Assert.AreEqual(2222, fan.Points[1].Rpm);

            Assert.AreEqual(0.0, fan.RpmToDutyDouble(0), 1e-6);
            Assert.AreEqual(0, fan.RpmToDuty(0));
            for (int rpm = 1; rpm <= 6500; rpm += 25)
            {
                double dutyDouble = fan.RpmToDutyDouble(rpm);
                int dutyInt = fan.RpmToDuty(rpm);
                Assert.IsGreaterThanOrEqualTo(10.0, dutyDouble, $"RpmToDutyDouble({rpm})={dutyDouble} fell into KickZone 1..9!");
                Assert.IsGreaterThanOrEqualTo(10, dutyInt, $"RpmToDuty({rpm})={dutyInt} fell into KickZone 1..9!");
            }

            // Round-trip verification for MinQuietRpm and mid-curve duty
            Assert.AreEqual(2222, fan.DutyToRpm(10));
            Assert.AreEqual(10, fan.RpmToDuty(2222));
            int rpmAt50 = fan.DutyToRpm(50);
            Assert.AreEqual(50, fan.RpmToDuty(rpmAt50));
        }
    }

    [TestMethod]
    public async Task FanCalibrationService_CleanLinearFan_HasZeroBlackoutMaxDuty_AndLowMinQuietDuty()
    {
        int currentDuty = 0;
        var service = new FanCalibrationService(
            setFanSpeeds: (cpu, _) => { currentDuty = cpu; return true; },
            readExactRpms: () =>
            {
                if (currentDuty == 0) return (true, 1800, 1800);
                if (currentDuty < 2) return (true, 0, 0);
                int rpm = 1800 + (currentDuty - 2) * 45;
                return (true, rpm, rpm);
            },
            readTemperatures: () => (50, 45),
            restorePreviousMode: () => { },
            settleDelayMs: 0,
            sampleIntervalMs: 0,
            samplesPerStep: 2,
            delayAsync: (_, _) => Task.CompletedTask);

        var result = await service.RunAsync();

        Assert.IsTrue(result.Success, result.Message);
        Assert.IsNotNull(result.Data);
        Assert.AreEqual(0, result.Data.Cpu.BlackoutMaxDuty);
        Assert.AreEqual(2, result.Data.Cpu.MinQuietDuty);
        Assert.AreEqual(1800, result.Data.Cpu.MinQuietRpm);
        Assert.AreEqual(2, result.Data.Cpu.MinStableDuty);
        Assert.AreEqual(1800, result.Data.Cpu.MinStableRpm);
        Assert.AreEqual(1800, result.Data.Cpu.EcAutoBaselineRpm);
        Assert.IsFalse(result.Data.Cpu.IsDutyInKickZone(2));
        Assert.AreEqual(2, result.Data.Cpu.RpmToDuty(1800));
    }

    [TestMethod]
    public void FanCurveProfile_AndConfig_DefaultEcAutoAndSafeQuietIdleBase_RoundTripWithKickZoneCalibration()
    {
        foreach (byte mode in new byte[] { 0, 1, 4, 5, 6 })
        {
            var profile = FanCurveProfile.CreateDefault(mode);
            Assert.IsTrue(profile.UseEcAutoBelowMin, $"Mode {mode} should default UseEcAutoBelowMin=true.");
            Assert.IsTrue(profile.SafeQuietIdleBase, $"Mode {mode} should default SafeQuietIdleBase=true.");
        }

        string configPath = Path.Combine(_tempDir, "preysense.json");
        var service = new ConfigurationService(configPath);
        var cfg = service.GetCurrent();
        Assert.IsTrue(cfg.Fans.Balanced.UseEcAutoBelowMin);
        Assert.IsTrue(cfg.Fans.Balanced.SafeQuietIdleBase);
        Assert.IsTrue(cfg.Fans.Turbo.UseEcAutoBelowMin);
        Assert.IsTrue(cfg.Fans.Turbo.SafeQuietIdleBase);

        cfg.Fans.Calibration = new FanCalibrationData
        {
            CalibratedAtUtc = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc),
            Cpu = new SingleFanCalibration
            {
                MinQuietDuty = 10,
                MinQuietRpm = 2222,
                BlackoutMaxDuty = 9,
                EcAutoBaselineRpm = 1800,
                Points = [new(0, 0), new(5, 2900), new(10, 2222), new(50, 4000), new(100, 6200)]
            },
            Gpu = new SingleFanCalibration
            {
                MinQuietDuty = 10,
                MinQuietRpm = 2180,
                BlackoutMaxDuty = 9,
                EcAutoBaselineRpm = 1800,
                Points = [new(0, 0), new(10, 2180), new(50, 3950), new(100, 6100)]
            }
        };
        cfg.Fans.Turbo.UseEcAutoBelowMin = false;
        cfg.Fans.Turbo.SafeQuietIdleBase = false;
        cfg.Fans.Performance.UseEcAutoBelowMin = true;
        cfg.Fans.Performance.SafeQuietIdleBase = false;
        service.Save(cfg);

        var reloaded = new ConfigurationService(configPath).GetCurrent();
        Assert.IsNotNull(reloaded.Fans.Calibration);
        Assert.AreEqual(10, reloaded.Fans.Calibration.Cpu.MinQuietDuty);
        Assert.AreEqual(2222, reloaded.Fans.Calibration.Cpu.MinQuietRpm);
        Assert.AreEqual(9, reloaded.Fans.Calibration.Cpu.BlackoutMaxDuty);
        Assert.AreEqual(1800, reloaded.Fans.Calibration.Cpu.EcAutoBaselineRpm);
        Assert.IsFalse(reloaded.Fans.Calibration.Cpu.Points.Any(p => p.Duty == 5));
        Assert.IsFalse(reloaded.Fans.Turbo.UseEcAutoBelowMin);
        Assert.IsFalse(reloaded.Fans.Turbo.SafeQuietIdleBase);
        Assert.IsTrue(reloaded.Fans.Performance.UseEcAutoBelowMin);
        Assert.IsFalse(reloaded.Fans.Performance.SafeQuietIdleBase);
        Assert.IsTrue(reloaded.Fans.Balanced.UseEcAutoBelowMin);
        Assert.IsTrue(reloaded.Fans.Balanced.SafeQuietIdleBase);
    }

    [TestMethod]
    public void SingleFanCalibration_NormalizeMonotonic_PreservesValidMinQuietAndBlackout_AndRejectsInvalidKickZoneOrStoppedMinQuiet()
    {
        // Case A: External JSON loads sparse points (0, 0), (10, 2222)... with MinQuietDuty=10, BlackoutMaxDuty=9.
        var validExternal = new SingleFanCalibration
        {
            MinQuietDuty = 10,
            MinQuietRpm = 2222,
            MinStableDuty = 10,
            MinStableRpm = 2222,
            BlackoutMaxDuty = 9,
            EcAutoBaselineRpm = 1800,
            Points = [new(0, 0), new(10, 2222), new(50, 4000), new(100, 6200)]
        };
        validExternal.NormalizeMonotonic();

        Assert.AreEqual(10, validExternal.MinQuietDuty);
        Assert.AreEqual(2222, validExternal.MinQuietRpm);
        Assert.AreEqual(9, validExternal.BlackoutMaxDuty);
        Assert.AreEqual(1800, validExternal.EcAutoBaselineRpm);
        Assert.AreEqual(10, validExternal.MinStableDuty);
        Assert.AreEqual(2222, validExternal.MinStableRpm);

        // Case B: External JSON has stale MinQuietDuty inside BlackoutMaxDuty (5 <= 9).
        // Must NOT accept duty 5 or shrink BlackoutMaxDuty from 9 to 4.
        var kickZoneMinQuiet = new SingleFanCalibration
        {
            MinQuietDuty = 5,
            MinQuietRpm = 2900,
            MinStableDuty = 5,
            MinStableRpm = 2900,
            BlackoutMaxDuty = 9,
            EcAutoBaselineRpm = 1800,
            Points = [new(0, 0), new(10, 2222), new(100, 6200)]
        };
        kickZoneMinQuiet.NormalizeMonotonic();

        Assert.AreEqual(9, kickZoneMinQuiet.BlackoutMaxDuty);
        Assert.AreEqual(10, kickZoneMinQuiet.MinQuietDuty);
        Assert.AreEqual(2222, kickZoneMinQuiet.MinQuietRpm);
        Assert.AreEqual(10, kickZoneMinQuiet.MinStableDuty);
        Assert.AreEqual(2222, kickZoneMinQuiet.MinStableRpm);

        // Case C: External JSON has MinQuietDuty pointing to a stopped (0 RPM) point.
        // Must update MinQuietDuty/MinQuietRpm to the true first spinning point (12, 2200).
        var stoppedMinQuiet = new SingleFanCalibration
        {
            MinQuietDuty = 8,
            MinQuietRpm = 2000,
            BlackoutMaxDuty = 0,
            Points = [new(0, 0), new(8, 0), new(12, 2200), new(100, 6200)]
        };
        stoppedMinQuiet.NormalizeMonotonic();

        Assert.AreEqual(8, stoppedMinQuiet.StopThresholdDuty);
        Assert.AreEqual(12, stoppedMinQuiet.MinQuietDuty);
        Assert.AreEqual(2200, stoppedMinQuiet.MinQuietRpm);
        Assert.AreEqual(12, stoppedMinQuiet.MinStableDuty);
        Assert.AreEqual(2200, stoppedMinQuiet.MinStableRpm);
    }

    [TestMethod]
    public void SingleFanCalibration_TwoPointAndSinglePointCurves_NeverDivideByZeroOrEnterKickZone()
    {
        // Single-point (0, 0) only -> uncalibrated fallback, no divide-by-zero
        var single = new SingleFanCalibration
        {
            MinQuietDuty = 10,
            BlackoutMaxDuty = 9,
            Points = [new(0, 0)]
        };
        single.NormalizeMonotonic();
        Assert.IsFalse(single.IsCalibrated);
        Assert.AreEqual(0, single.DutyToRpm(0));
        Assert.AreEqual(FanStage.DutyToPhysicalRpm(50, FanStage.CpuMaxRpm), single.DutyToRpm(50));
        Assert.AreEqual(0.0, single.RpmToDutyDouble(0), 1e-6);
        Assert.AreEqual(FanStage.PhysicalRpmToDuty(3000, FanStage.CpuMaxRpm), single.RpmToDuty(3000));

        // Two-point (0, 0), (10, 2222) with BlackoutMaxDuty = 9, MinQuietDuty = 10
        var twoPoint = new SingleFanCalibration
        {
            MinQuietDuty = 10,
            MinQuietRpm = 2222,
            BlackoutMaxDuty = 9,
            EcAutoBaselineRpm = 1800,
            Points = [new(0, 0), new(10, 2222)]
        };
        twoPoint.NormalizeMonotonic();
        Assert.IsTrue(twoPoint.IsCalibrated);
        Assert.AreEqual(0, twoPoint.DutyToRpm(0));
        for (int duty = 1; duty <= 100; duty++)
            Assert.AreEqual(2222, twoPoint.DutyToRpm(duty), $"Duty {duty} should clamp to 2222 RPM");

        Assert.AreEqual(0.0, twoPoint.RpmToDutyDouble(0), 1e-6);
        for (int rpm = 1; rpm <= 6500; rpm += 50)
        {
            double dutyDouble = twoPoint.RpmToDutyDouble(rpm);
            int dutyInt = twoPoint.RpmToDuty(rpm);
            Assert.IsGreaterThanOrEqualTo(10.0, dutyDouble, $"RpmToDutyDouble({rpm})={dutyDouble} fell into 1..9!");
            Assert.IsGreaterThanOrEqualTo(10, dutyInt, $"RpmToDuty({rpm})={dutyInt} fell into 1..9!");
        }
    }

    [TestMethod]
    public async Task FanCalibrationService_StopAtDuty10AndKickAtDuty2To8_Blackouts1To11AndProtectsDuty12()
    {
        int currentDuty = 0;
        var service = new FanCalibrationService(
            setFanSpeeds: (cpu, _) => { currentDuty = cpu; return true; },
            readExactRpms: () =>
            {
                if (currentDuty == 0) return (true, 1800, 1800);
                if (currentDuty <= 8) return (true, 2700, 2700); // EC anti-stall kick
                if (currentDuty <= 11) return (true, 0, 0);      // Dead stop zone at 9..11 (incl. 10)
                if (currentDuty == 12) return (true, 2200, 2200);
                int rpm = 2200 + (currentDuty - 12) * 45;
                return (true, rpm, rpm);
            },
            readTemperatures: () => (50, 45),
            restorePreviousMode: () => { },
            settleDelayMs: 0,
            sampleIntervalMs: 0,
            samplesPerStep: 2,
            delayAsync: (_, _) => Task.CompletedTask);

        var result = await service.RunAsync();
        Assert.IsTrue(result.Success, result.Message);
        Assert.IsNotNull(result.Data);

        var cpu = result.Data.Cpu;
        Assert.AreEqual(12, cpu.MinQuietDuty);
        Assert.AreEqual(2200, cpu.MinQuietRpm);
        Assert.AreEqual(11, cpu.BlackoutMaxDuty);
        for (int d = 1; d <= 11; d++)
        {
            Assert.IsTrue(cpu.IsDutyInKickZone(d));
            Assert.IsFalse(cpu.Points.Any(p => p.Duty == d));
        }
        for (int rpm = 1; rpm <= 6500; rpm += 50)
        {
            Assert.IsGreaterThanOrEqualTo(12.0, cpu.RpmToDutyDouble(rpm));
            Assert.IsGreaterThanOrEqualTo(12, cpu.RpmToDuty(rpm));
        }
    }

    [TestMethod]
    public async Task FanCalibrationService_NoLowSpreadCandidate_FallsBackToLowestMeanRpmCleanly()
    {
        int currentDuty = 0;
        int sampleIndex = 0;
        var service = new FanCalibrationService(
            setFanSpeeds: (cpu, _) => { currentDuty = cpu; sampleIndex = 0; return true; },
            readExactRpms: () =>
            {
                int idx = sampleIndex++;
                if (currentDuty == 0) return (true, 1800, 1800);
                if (currentDuty < 10) return (true, 0, 0);
                // Every spinning step has Spread = 120 >= MaxStableSpreadRpm (100)
                int baseRpm = 2200 + (currentDuty - 10) * 40;
                int delta = idx == 0 ? 0 : 120;
                return (true, baseRpm + delta, baseRpm + delta);
            },
            readTemperatures: () => (50, 45),
            restorePreviousMode: () => { },
            settleDelayMs: 0,
            sampleIntervalMs: 0,
            samplesPerStep: 2,
            delayAsync: (_, _) => Task.CompletedTask);

        var result = await service.RunAsync();
        Assert.IsTrue(result.Success, result.Message);
        Assert.IsNotNull(result.Data);
        Assert.AreEqual(10, result.Data.Cpu.MinQuietDuty);
        Assert.AreEqual(2260, result.Data.Cpu.MinQuietRpm);
    }

    [TestMethod]
    public void FanCurveWindow_RowRpmZero_StaysZeroWithoutClampingUp_AndPositiveBelowFloorClampsToMin()
    {
        if (Environment.GetEnvironmentVariable("PREYSENSE_FANCURVE_SMOKE_CHILD") != "1")
        {
            var start = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("vstest");
            start.ArgumentList.Add(typeof(FanCalibrationAndModelTests).Assembly.Location);
            start.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName=" +
                typeof(FanCalibrationAndModelTests).FullName + "." + nameof(FanCurveWindow_RowRpmZero_StaysZeroWithoutClampingUp_AndPositiveBelowFloorClampsToMin));
            start.Environment["PREYSENSE_FANCURVE_SMOKE_CHILD"] = "1";
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("Isolated FanCurveWindow test timed out.");
            }
            Assert.AreEqual(0, process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
            return;
        }

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var app = new PreySense.Frontend.App();
            try
            {
                app.InitializeComponent();
                var window = new PreySense.Frontend.Views.FanCurveWindow();
                try
                {
                    window.LoadProfile();
                    var customOption = (System.Windows.Controls.RadioButton)window.FindName("CustomOption");
                    var curve = (PreySense.Frontend.Controls.WpfFanCurveControl)window.FindName("Curve");
                    var rowRpm = (PreySense.Frontend.Controls.SettingValueRow)window.FindName("RowRpm");
                    var rowDuty = (PreySense.Frontend.Controls.SettingValueRow)window.FindName("RowDuty");

                    customOption.IsChecked = true;
                    curve.SelectedIndex = 0;

                    var text = (System.Windows.Controls.TextBox)rowRpm.FindName("InnerValueBox");

                    // 1. Entering 500 (> 0 and below floor) must clamp up to active floor (>= 1800)
                    text.Text = "500";
                    rowRpm.CommitText(notify: true);
                    Assert.IsGreaterThanOrEqualTo(1800d, rowRpm.Value);
                    Assert.IsGreaterThan(0d, rowDuty.Value);

                    // 2. Entering 0 must stay 0 (NOT clamp up to MinQuietRpm / 1800)
                    text.Text = "0";
                    rowRpm.CommitText(notify: true);
                    Assert.AreEqual(0d, rowRpm.Value);
                    Assert.AreEqual(0d, rowDuty.Value);

                    // 3. Entering 4500 must commit 4500
                    text.Text = "4500";
                    rowRpm.CommitText(notify: true);
                    Assert.AreEqual(4500d, rowRpm.Value);
                    Assert.IsGreaterThan(0d, rowDuty.Value);

                    // 4. Entering 0 again from 4500 must drop back to 0 cleanly
                    text.Text = "0";
                    rowRpm.CommitText(notify: true);
                    Assert.AreEqual(0d, rowRpm.Value);
                    Assert.AreEqual(0d, rowDuty.Value);
                }
                finally { window.Close(); }
            }
            catch (Exception ex) { failure = ex; }
            finally { app.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "FanCurveWindow test timed out.");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [TestMethod]
    public void FanCalibrationData_RecommendedIdleEcMode_DefaultsToBalancedWhenUncalibrated_AndPreservesFieldsInCopyAndNormalize()
    {
        var uncalibrated = new FanCalibrationData();
        Assert.IsFalse(uncalibrated.IsCalibrated);
        Assert.AreEqual((byte)0x01, uncalibrated.RecommendedIdleEcMode);
        Assert.IsFalse(uncalibrated.PreferManualZeroIdle);
        Assert.IsFalse(uncalibrated.HasIdleProbeData);

        var cal = new FanCalibrationData
        {
            CalibratedAtUtc = new DateTime(2026, 11, 12, 14, 0, 0, DateTimeKind.Utc),
            RecommendedIdleEcMode = 0x06,
            QuietAutoCpuRpm = 2500,
            QuietAutoGpuRpm = 2480,
            BalancedAutoCpuRpm = 2200,
            BalancedAutoGpuRpm = 2180,
            EcoAutoCpuRpm = 2000,
            EcoAutoGpuRpm = 1980,
            ManualZeroCpuRpm = 1800,
            ManualZeroGpuRpm = 1790,
            PreferManualZeroIdle = true,
            Cpu = new SingleFanCalibration { Points = [new(0, 0), new(10, 2200), new(100, 6200)] },
            Gpu = new SingleFanCalibration { Points = [new(0, 0), new(10, 2150), new(100, 6100)] }
        };

        var copy = cal.Copy();
        Assert.AreEqual((byte)0x06, copy.RecommendedIdleEcMode);
        Assert.AreEqual(2500, copy.QuietAutoCpuRpm);
        Assert.AreEqual(2480, copy.QuietAutoGpuRpm);
        Assert.AreEqual(2200, copy.BalancedAutoCpuRpm);
        Assert.AreEqual(2180, copy.BalancedAutoGpuRpm);
        Assert.AreEqual(2000, copy.EcoAutoCpuRpm);
        Assert.AreEqual(1980, copy.EcoAutoGpuRpm);
        Assert.AreEqual(1800, copy.ManualZeroCpuRpm);
        Assert.AreEqual(1790, copy.ManualZeroGpuRpm);
        Assert.IsTrue(copy.PreferManualZeroIdle);

        copy.Normalize();
        Assert.AreEqual((byte)0x06, copy.RecommendedIdleEcMode);
        Assert.AreEqual(1800, copy.ManualZeroCpuRpm);

        // Invalid idle mode and negative RPMs sanitize safely
        copy.RecommendedIdleEcMode = 0x05;
        copy.QuietAutoCpuRpm = -100;
        copy.ManualZeroGpuRpm = -5;
        copy.Normalize();
        Assert.AreEqual((byte)0x01, copy.RecommendedIdleEcMode);
        Assert.AreEqual(0, copy.QuietAutoCpuRpm);
        Assert.AreEqual(0, copy.ManualZeroGpuRpm);
    }

    [TestMethod]
    public async Task FanCalibrationService_IdleProbe_MeasuresAutoModesAndManualZero_AndRestoresOriginalPowerMode()
    {
        async Task<FanCalibrationResult> RunProbeCaseAsync(
            byte initialMode,
            int balancedRpm,
            int quietRpm,
            int ecoRpm,
            int manualZeroRpm,
            Action<byte> onModeObserved,
            Action<int> onRestoreCount)
        {
            byte activeMode = initialMode;
            bool inManualZero = false;
            int currentDuty = 0;
            int restoreCount = 0;

            var service = new FanCalibrationService(
                setFanSpeeds: (cpu, _) =>
                {
                    inManualZero = false;
                    currentDuty = cpu;
                    return true;
                },
                readExactRpms: () =>
                {
                    if (inManualZero)
                        return (true, manualZeroRpm, manualZeroRpm);

                    if (currentDuty == 0)
                    {
                        int rpm = activeMode switch
                        {
                            0x01 => balancedRpm,
                            0x00 => quietRpm,
                            0x06 => ecoRpm,
                            _ => balancedRpm
                        };
                        return (true, rpm, rpm);
                    }

                    if (currentDuty < 10)
                        return (true, 0, 0);

                    int sweepRpm = 2200 + (currentDuty - 10) * 44;
                    return (true, sweepRpm, sweepRpm);
                },
                readTemperatures: () => (52, 46),
                restorePreviousMode: () => restoreCount++,
                settleDelayMs: 0,
                sampleIntervalMs: 0,
                samplesPerStep: 2,
                delayAsync: (_, _) => Task.CompletedTask,
                setPowerModeForProbe: mode =>
                {
                    activeMode = mode;
                    inManualZero = false;
                    return true;
                },
                setManualZeroFans: () =>
                {
                    inManualZero = true;
                    return true;
                },
                getCurrentPowerMode: () => activeMode);

            var result = await service.RunAsync();
            onModeObserved(activeMode);
            onRestoreCount(restoreCount);
            return result;
        }

        // Case 1: Balanced=2200, Quiet=2500, Eco=2000, Manual 0%=0 RPM (stall -> ignored)
        byte finalMode1 = 0xFF;
        int restores1 = 0;
        var res1 = await RunProbeCaseAsync(
            initialMode: 0x05,
            balancedRpm: 2200,
            quietRpm: 2500,
            ecoRpm: 2000,
            manualZeroRpm: 0,
            m => finalMode1 = m,
            r => restores1 = r);

        Assert.IsTrue(res1.Success, res1.Message);
        Assert.IsNotNull(res1.Data);
        Assert.AreEqual((byte)0x05, finalMode1);
        Assert.AreEqual(1, restores1);
        Assert.AreEqual((byte)0x06, res1.Data.RecommendedIdleEcMode);
        Assert.AreEqual(2200, res1.Data.BalancedAutoCpuRpm);
        Assert.AreEqual(2200, res1.Data.BalancedAutoGpuRpm);
        Assert.AreEqual(2500, res1.Data.QuietAutoCpuRpm);
        Assert.AreEqual(2500, res1.Data.QuietAutoGpuRpm);
        Assert.AreEqual(2000, res1.Data.EcoAutoCpuRpm);
        Assert.AreEqual(2000, res1.Data.EcoAutoGpuRpm);
        Assert.AreEqual(0, res1.Data.ManualZeroCpuRpm);
        Assert.AreEqual(0, res1.Data.ManualZeroGpuRpm);
        Assert.IsFalse(res1.Data.PreferManualZeroIdle);

        // Case 2: Same Eco=2000, Manual 0%=1800 RPM (>= 1000 and 3600 < 4000 - 150 -> PreferManualZeroIdle = true)
        byte finalMode2 = 0xFF;
        int restores2 = 0;
        var res2 = await RunProbeCaseAsync(
            initialMode: 0x04,
            balancedRpm: 2200,
            quietRpm: 2500,
            ecoRpm: 2000,
            manualZeroRpm: 1800,
            m => finalMode2 = m,
            r => restores2 = r);

        Assert.IsTrue(res2.Success, res2.Message);
        Assert.IsNotNull(res2.Data);
        Assert.AreEqual((byte)0x04, finalMode2);
        Assert.AreEqual(1, restores2);
        Assert.AreEqual((byte)0x06, res2.Data.RecommendedIdleEcMode);
        Assert.AreEqual(1800, res2.Data.ManualZeroCpuRpm);
        Assert.AreEqual(1800, res2.Data.ManualZeroGpuRpm);
        Assert.IsTrue(res2.Data.PreferManualZeroIdle);

        // Case 3: Tie-breaker prefers Balanced (0x01) over Quiet (0x00), and rejects Manual 0% within 150 RPM sum threshold
        byte finalMode3 = 0xFF;
        var res3 = await RunProbeCaseAsync(
            initialMode: 0x01,
            balancedRpm: 2200,
            quietRpm: 2200,
            ecoRpm: 2400,
            manualZeroRpm: 2150,
            m => finalMode3 = m,
            _ => { });

        Assert.IsTrue(res3.Success, res3.Message);
        Assert.IsNotNull(res3.Data);
        Assert.AreEqual((byte)0x01, finalMode3);
        Assert.AreEqual((byte)0x01, res3.Data.RecommendedIdleEcMode);
        Assert.IsFalse(res3.Data.PreferManualZeroIdle);
    }

    [TestMethod]
    public async Task FanCalibrationService_IdleProbe_RestoresOriginalPowerMode_OnThermalAbortAndCancellation()
    {
        // Thermal abort mid-probe
        byte activeMode = 0x04;
        int restoreCount = 0;
        int probeSwitches = 0;

        var abortService = new FanCalibrationService(
            setFanSpeeds: (_, _) => true,
            readExactRpms: () => (true, 2200, 2200),
            readTemperatures: () => probeSwitches >= 2 ? (85, 50) : (52, 46),
            restorePreviousMode: () => restoreCount++,
            settleDelayMs: 0,
            sampleIntervalMs: 0,
            samplesPerStep: 2,
            delayAsync: (_, _) => Task.CompletedTask,
            setPowerModeForProbe: mode =>
            {
                activeMode = mode;
                probeSwitches++;
                return true;
            },
            setManualZeroFans: () => true,
            getCurrentPowerMode: () => activeMode);

        var abortResult = await abortService.RunAsync();
        Assert.IsFalse(abortResult.Success);
        Assert.AreEqual(FanCalibrationStatus.ThermalAbort, abortResult.Status);
        Assert.AreEqual((byte)0x04, activeMode, "Original power mode must be restored on thermal abort during idle probe.");
        Assert.AreEqual(1, restoreCount);

        // Cancellation mid-probe
        byte cancelMode = 0x05;
        int cancelRestores = 0;
        int cancelSwitches = 0;
        using var cts = new CancellationTokenSource();

        var cancelService = new FanCalibrationService(
            setFanSpeeds: (_, _) => true,
            readExactRpms: () => (true, 2200, 2200),
            readTemperatures: () => (52, 46),
            restorePreviousMode: () => cancelRestores++,
            settleDelayMs: 0,
            sampleIntervalMs: 0,
            samplesPerStep: 2,
            delayAsync: (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            setPowerModeForProbe: mode =>
            {
                cancelMode = mode;
                cancelSwitches++;
                if (cancelSwitches == 2)
                    cts.Cancel();
                return true;
            },
            setManualZeroFans: () => true,
            getCurrentPowerMode: () => cancelMode);

        var cancelResult = await cancelService.RunAsync(cancellationToken: cts.Token);
        Assert.IsFalse(cancelResult.Success);
        Assert.AreEqual(FanCalibrationStatus.Cancelled, cancelResult.Status);
        Assert.AreEqual((byte)0x05, cancelMode, "Original power mode must be restored on cancellation during idle probe.");
        Assert.AreEqual(1, cancelRestores);
    }

    [TestMethod]
    public async Task FanCalibrationService_WithMockHardwareService_RunsIdleProbeAndRestoresState()
    {
        var mock = new MockHardwareService();
        mock.SetPowerMode(0x04);
        mock.ExactFanRpmProvider = () =>
        {
            if (mock.ManualZeroFansActive)
                return (1800, 1800);

            return mock.CurrentPowerMode switch
            {
                0x00 => (2500, 2500),
                0x06 => (2000, 2000),
                _ => (2200, 2200)
            };
        };

        int restoreCount = 0;
        var service = new FanCalibrationService(
            mock,
            readTemperatures: () => (50, 45),
            restorePreviousMode: () => restoreCount++,
            settleDelayMs: 0,
            sampleIntervalMs: 0,
            samplesPerStep: 2,
            delayAsync: (_, _) => Task.CompletedTask);

        var result = await service.RunAsync();
        Assert.IsTrue(result.Success, result.Message);
        Assert.IsNotNull(result.Data);
        Assert.AreEqual(1, mock.SetManualZeroFansCallCount);
        Assert.IsFalse(mock.ManualZeroFansActive);
        Assert.AreEqual((byte)0x04, mock.CurrentPowerMode);
        Assert.AreEqual(1, restoreCount);
        Assert.AreEqual((byte)0x06, result.Data.RecommendedIdleEcMode);
        Assert.IsTrue(result.Data.PreferManualZeroIdle);
        Assert.AreEqual(1800, result.Data.ManualZeroCpuRpm);
    }

    [TestMethod]
    public void FanCurveWindow_FormatCalibrationStatusSummary_DisplaysAutoBaselineAndMinQuietRpm()
    {
        var cal = new FanCalibrationData
        {
            CalibratedAtUtc = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc),
            RecommendedIdleEcMode = 0x06,
            EcoAutoCpuRpm = 2000,
            EcoAutoGpuRpm = 1980,
            Cpu = new SingleFanCalibration
            {
                MinQuietDuty = 10,
                MinQuietRpm = 2222,
                EcAutoBaselineRpm = 2150,
                Points = [new(0, 0), new(10, 2222), new(100, 6200)]
            },
            Gpu = new SingleFanCalibration
            {
                MinQuietDuty = 10,
                MinQuietRpm = 2180,
                EcAutoBaselineRpm = 2100,
                Points = [new(0, 0), new(10, 2180), new(100, 6100)]
            }
        };
        cal.Normalize();

        string text = PreySense.Frontend.Views.FanCurveWindow.FormatCalibrationStatusSummary(cal);
        Assert.Contains("2222-6200 RPM", text);
        Assert.Contains("Auto (~2150)", text);
        Assert.Contains("Idle: Eco Auto (~2000/1980 RPM)", text);

        cal.PreferManualZeroIdle = true;
        cal.ManualZeroCpuRpm = 1800;
        cal.ManualZeroGpuRpm = 1790;
        string manualZeroText = PreySense.Frontend.Views.FanCurveWindow.FormatCalibrationStatusSummary(cal);
        Assert.Contains("Idle: Manual 0% (~1800/1790 RPM)", manualZeroText);
    }

    [TestMethod]
    public void WmiController_SetManualZeroFansWmi_UsesManualMaskAndZeroSpeed_AndInterfaceDefaultIsFalse()
    {
        IHardwareService defaultImpl = new DefaultManualZeroHardwareStub();
        Assert.IsFalse(defaultImpl.TrySetManualZeroFans());
        Assert.IsTrue(new MockHardwareService().TrySetManualZeroFans());

        DirectoryInfo? dir = new(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PreySense.slnx")))
            dir = dir.Parent;
        string root = dir?.FullName ?? AppDomain.CurrentDomain.BaseDirectory;
        string fansCs = File.ReadAllText(Path.Combine(root, "PreySense.Core", "Hardware", "WmiController.Fans.cs"));

        Assert.Contains("public bool SetManualZeroFansWmi()", fansCs);
        Assert.Contains("SetFanBehaviorMask(0xC30009ul)", fansCs);
        Assert.Contains("SetCpuFanSpeed(0)", fansCs);
        Assert.Contains("SetGpuFanSpeed(0)", fansCs);
    }

    [TestMethod]
    public void WmiController_SetManualZeroFansWmi_TransitionsBackToAutoAndCustomWithoutInvalidate_AndHandlesPartialFailure()
    {
        var sent = new List<(string Method, ulong Input)>();
        bool failZeroSpeed = false;
        using var wmi = new PreySense.Helpers.WmiController
        {
            CommandOverride = (method, input) =>
            {
                if (failZeroSpeed && method == PreySense.Helpers.AcerWmi.GamingMethods.SetFanSpeed && input == 1ul)
                    return (false, 1ul);
                sent.Add((method, input));
                return (true, 0ul);
            }
        };

        // 1. SetManualZeroFansWmi -> SetFanControlWmi(2, 0, 0) WITHOUT InvalidateFanTargets must NOT skip resetting 0xC30009 -> 0x410009.
        Assert.IsTrue(wmi.SetManualZeroFansWmi());
        CollectionAssert.AreEqual(
            new[]
            {
                (PreySense.Helpers.AcerWmi.GamingMethods.SetFanBehavior, 0xC30009ul),
                (PreySense.Helpers.AcerWmi.GamingMethods.SetFanSpeed, 1ul),
                (PreySense.Helpers.AcerWmi.GamingMethods.SetFanSpeed, 4ul)
            },
            sent);

        sent.Clear();
        Assert.IsTrue(wmi.SetFanControlWmi(2, 0, 0));
        CollectionAssert.AreEqual(
            new[] { (PreySense.Helpers.AcerWmi.GamingMethods.SetFanBehavior, 0x410009ul) },
            sent,
            "SetFanControlWmi(2, 0, 0) after Manual 0% must restore 0x410009 Auto even without InvalidateFanTargets.");

        // 2. SetManualZeroFansWmi -> SetFanControlWmi(0) WITHOUT InvalidateFanTargets must write Auto behavior 0x410009.
        sent.Clear();
        Assert.IsTrue(wmi.SetManualZeroFansWmi());
        sent.Clear();
        Assert.IsTrue(wmi.SetFanControlWmi(0, 0, 0));
        CollectionAssert.AreEqual(
            new[] { (PreySense.Helpers.AcerWmi.GamingMethods.SetFanBehavior, 0x410009ul) },
            sent);

        // 3. SetManualZeroFansWmi -> SetFanControlWmi(2, 100, 100) WITHOUT InvalidateFanTargets must write 0xC30009 + 100/100%.
        sent.Clear();
        Assert.IsTrue(wmi.SetManualZeroFansWmi());
        sent.Clear();
        Assert.IsTrue(wmi.SetFanControlWmi(2, 100, 100));
        Assert.HasCount(3, sent);
        Assert.AreEqual((PreySense.Helpers.AcerWmi.GamingMethods.SetFanBehavior, 0xC30009ul), sent[0]);
        Assert.AreEqual(PreySense.Helpers.AcerWmi.GamingMethods.SetFanSpeed, sent[1].Method);
        Assert.AreEqual(PreySense.Helpers.AcerWmi.GamingMethods.SetFanSpeed, sent[2].Method);

        // 4. Partial failure in SetManualZeroFansWmi (0xC30009 succeeds, SetCpuFanSpeed(0) fails) invalidates targets
        // so a subsequent SetFanControlWmi(2, 0, 0) resets EC back to 0x410009 Auto.
        Assert.IsTrue(wmi.SetFanControlWmi(2, 0, 0));
        sent.Clear();
        failZeroSpeed = true;
        Assert.IsFalse(wmi.SetManualZeroFansWmi());
        failZeroSpeed = false;
        sent.Clear();
        Assert.IsTrue(wmi.SetFanControlWmi(2, 0, 0));
        CollectionAssert.AreEqual(
            new[] { (PreySense.Helpers.AcerWmi.GamingMethods.SetFanBehavior, 0x410009ul) },
            sent);
    }

    [TestMethod]
    public async Task FanCalibrationService_IdleProbe_ManualZeroAbortOrCancel_StillInvalidatesAndRestores()
    {
        // 1. Thermal abort during Manual 0% probe step must still invoke invalidateFanTargets and restorePreviousMode.
        byte activeMode = 0x05;
        bool manualZeroStarted = false;
        int invalidateCalls = 0;
        int restoreCalls = 0;

        var abortService = new FanCalibrationService(
            setFanSpeeds: (_, _) => true,
            readExactRpms: () => (true, 2200, 2200),
            readTemperatures: () => manualZeroStarted ? (85, 50) : (52, 46),
            restorePreviousMode: () => restoreCalls++,
            settleDelayMs: 0,
            sampleIntervalMs: 0,
            samplesPerStep: 2,
            delayAsync: (_, _) => Task.CompletedTask,
            setPowerModeForProbe: mode => { activeMode = mode; return true; },
            setManualZeroFans: () => { manualZeroStarted = true; return true; },
            getCurrentPowerMode: () => activeMode,
            invalidateFanTargets: () => invalidateCalls++);

        var abortResult = await abortService.RunAsync();
        Assert.AreEqual(FanCalibrationStatus.ThermalAbort, abortResult.Status);
        Assert.AreEqual((byte)0x05, activeMode);
        Assert.IsGreaterThanOrEqualTo(2, invalidateCalls);
        Assert.AreEqual(1, restoreCalls);

        // 2. Cancellation during Manual 0% probe step must also invoke invalidateFanTargets and restorePreviousMode.
        using var cts = new CancellationTokenSource();
        int cancelInvalidate = 0;
        int cancelRestore = 0;
        byte cancelMode = 0x04;

        var cancelService = new FanCalibrationService(
            setFanSpeeds: (_, _) => true,
            readExactRpms: () => (true, 2200, 2200),
            readTemperatures: () => (52, 46),
            restorePreviousMode: () => cancelRestore++,
            settleDelayMs: 0,
            sampleIntervalMs: 0,
            samplesPerStep: 2,
            delayAsync: (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            setPowerModeForProbe: mode => { cancelMode = mode; return true; },
            setManualZeroFans: () => { cts.Cancel(); return true; },
            getCurrentPowerMode: () => cancelMode,
            invalidateFanTargets: () => cancelInvalidate++);

        var cancelResult = await cancelService.RunAsync(cancellationToken: cts.Token);
        Assert.AreEqual(FanCalibrationStatus.Cancelled, cancelResult.Status);
        Assert.AreEqual((byte)0x04, cancelMode);
        Assert.IsGreaterThanOrEqualTo(2, cancelInvalidate);
        Assert.AreEqual(1, cancelRestore);
    }

    private sealed class DefaultManualZeroHardwareStub : IHardwareService
    {
        public PowerSourceKind GetPowerSource() => PowerSourceKind.Ac;
        public byte CurrentPowerMode => 0x01;
        public bool SetPowerMode(byte mode) => true;
        public event EventHandler<byte>? PowerModeChanged { add { } remove { } }
        public bool SetFanMode(byte cpuMode, byte gpuMode) => true;
        public bool SetFanSpeeds(byte cpuPercentage, byte gpuPercentage) => true;
        public int GetBatteryChargeLimit() => 1;
        public bool SetBatteryChargeLimit(int limitMode) => true;
        public int? GetGpuMuxMode() => 1;
        public bool SetGpuMuxMode(int muxMode) => true;
        public bool IsBiosDiscreteGpuActive => false;
        public bool IsDdsAvailable => false;
        public bool IsDdsSupported() => false;
        public PreySense.Gpu.DdsStatus? GetDdsStatus() => null;
        public bool HasDdsBlockers() => false;
        public (bool Success, string Message) SetDdsMode(PreySense.Gpu.DdsDisplayMode mode) => (false, string.Empty);
        public (bool Success, string Message) RestartDiscreteGpu() => (false, string.Empty);
        public void SetRgbStatic(byte zone, System.Drawing.Color color) { }
        public void SetRgbAllZones(System.Drawing.Color z1, System.Drawing.Color z2, System.Drawing.Color z3, System.Drawing.Color z4) { }
        public void SetRgbAllZones(System.Drawing.Color z1, System.Drawing.Color z2, System.Drawing.Color z3, System.Drawing.Color z4, byte brightness) { }
        public void SetRgbHardwareEffect(int mode, byte r, byte g, byte b, byte brightness, byte speed, byte direction) { }
        public void SetRgbBrightness(byte brightness) { }
        public int? GetKeyboardBrightness() => 100;
        public void SetCoverLogoLighting(int mode, System.Drawing.Color color, byte brightness, byte speed, bool enabled = true) { }
        public bool GetLcdOverdrive() => false;
        public bool SetLcdOverdrive(bool enable) => false;
        public bool GetBootSound() => false;
        public void SetBootSound(bool enable) { }
        public bool GetKbdBacklightTimeout() => false;
        public void SetKbdBacklightTimeout(bool enable) { }
        public void SetKbdBacklightTimeoutSeconds(int seconds) { }
        public int GetUsbChargingLevel() => 0;
        public void SetUsbChargingLevel(int percent) { }
        public bool GetBlueLightShield() => false;
        public int GetBlueLightShieldMode() => 0;
        public void SetBlueLightShield(bool enable) { }
        public void SetBlueLightShield(int mode) { }
        public int GetDisplayRefreshRate() => 60;
        public bool SetDisplayRefreshRate(int hz) => false;
        public List<int> GetSupportedRefreshRates() => [];
        public List<PreySense.Gpu.GpuProcessInfo> GetActiveGpuProcesses() => [];
        public bool KillProcess(uint pid) => false;
    }
}
