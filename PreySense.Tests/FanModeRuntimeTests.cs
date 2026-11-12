using System.Reflection;
using System.Runtime.CompilerServices;
using PreySense.Core.Services;
using PreySense.Frontend.ViewModels;
using PreySense.Mode;

namespace PreySense.Tests;

[TestClass]
public class FanModeRuntimeTests
{
    // Skip the UI constructor, which starts real hotkey watchers and startup work.
    // Supply only the state used by the fan controller, backed entirely by a fake.
    private static MainViewModel Create(RecordingFans hardware)
    {
        var vm = (MainViewModel)RuntimeHelpers.GetUninitializedObject(typeof(MainViewModel));
        Set(vm, "_hw", hardware);
        Set(vm, "_dispatcher", System.Windows.Threading.Dispatcher.CurrentDispatcher);
        Set(vm, "_fanControlGate", new object());
        Set(vm, "_fanFollower", new FanCurveFollower());
        vm.FanWriteBackoffMs = [0, 0, 0];
        return vm;
    }
    private static FanStage[] FixedDutyStages(int cpuPercent, int gpuPercent)
    {
        int cpuRpm = cpuPercent * FanStage.CpuMaxRpm / 100;
        int gpuRpm = gpuPercent * FanStage.GpuMaxRpm / 100;
        var stages = new FanStage[FanCurveProfile.StageCount];
        for (int i = 0; i < stages.Length; i++)
        {
            stages[i] = new FanStage
            {
                CpuRiseC = FanStage.DisabledTripCelsius,
                CpuFallC = FanStage.DisabledTripCelsius,
                GpuRiseC = FanStage.DisabledTripCelsius,
                GpuFallC = FanStage.DisabledTripCelsius,
                CpuTargetRpm = cpuRpm,
                GpuTargetRpm = gpuRpm
            };
        }
        return stages;
    }
    private static void Set(object target, string field, object value) =>
        target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);
    private static void Run(MainViewModel vm, string method, params object[] args) =>
        typeof(MainViewModel).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, args);
    private static void Select(MainViewModel vm, FanControlMode mode)
    {
        Run(vm, "ResetFanFollower");
        Set(vm, "_runningFanProfileMode", (byte)1);
        Set(vm, "_runningFanProfile", new FanCurveProfile
        {
            ControlMode = mode,
            Stages = FixedDutyStages(40, 50)
        });
    }
    [TestMethod]
    public void MaxOperatesIndependentlyAndReturnsToAutoWithoutRepeatedWrites()
    {
        var hardware = new RecordingFans();
        var vm = Create(hardware);
        Select(vm, FanControlMode.Max);
        Run(vm, "ProcessFanCurves", hardware.GetCurrentTelemetry());
        Run(vm, "ProcessFanCurves", hardware.GetCurrentTelemetry());
        CollectionAssert.AreEqual(new[] { "mode:1" }, hardware.Calls);
        Assert.AreEqual(100, vm.AppliedCpuFanPercent);
        Select(vm, FanControlMode.Auto);
        Run(vm, "ProcessFanCurves", hardware.GetCurrentTelemetry());
        Run(vm, "ProcessFanCurves", hardware.GetCurrentTelemetry());
        CollectionAssert.AreEqual(new[] { "mode:1", "mode:0" }, hardware.Calls);
        Assert.IsNull(vm.AppliedCpuFanPercent);
    }
    [TestMethod]
    public void CustomTargetsAreIndependentOfMaxAndReleaseOnExit()
    {
        var hardware = new RecordingFans();
        var vm = Create(hardware);
        Select(vm, FanControlMode.Max);
        Run(vm, "ProcessFanCurves", hardware.GetCurrentTelemetry());
        Select(vm, FanControlMode.Custom);
        Run(vm, "ProcessFanCurves", hardware.GetCurrentTelemetry());
        Run(vm, "ProcessFanCurves", hardware.GetCurrentTelemetry());
        Assert.AreEqual(50, vm.AppliedCpuFanPercent);
        Assert.AreEqual(50, vm.AppliedGpuFanPercent);
        Run(vm, "ReleaseCustomFans");
        CollectionAssert.AreEqual(new[] { "mode:1", "curve:50/50", "mode:0" }, hardware.Calls);
        Assert.IsNull(vm.AppliedGpuFanPercent);
    }
    [TestMethod]
    public void HotSamplesUseSavedCurveAndMissingSamplesDoNotSwitchToAuto()
    {
        var hardware = new RecordingFans();
        var vm = Create(hardware);
        Select(vm, FanControlMode.Custom);
        var normal = hardware.GetCurrentTelemetry();
        Run(vm, "ProcessFanCurves", normal with { CpuTemp = 84, GpuTemp = 78 });
        Run(vm, "ProcessFanCurves", normal with { CpuTemp = -1, GpuTemp = -1 });
        Run(vm, "ProcessFanCurves", normal);
        CollectionAssert.AreEqual(new[] { "curve:50/50" }, hardware.Calls);
        Assert.AreEqual(0, hardware.PowerModeReads);
        Assert.AreEqual(50, vm.AppliedCpuFanPercent);
    }

    [TestMethod]
    public void FailedFanWriteRetriesThreeTimesEmitsNoticeAndRecoversAutomaticallyAfter10Seconds()
    {
        var nowUtc = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
        var hardware = new RecordingFans { FailWrites = true };
        var vm = Create(hardware);
        vm.FanUtcNow = () => nowUtc;
        var notices = new List<string>();
        vm.FanNoticeRaised += (_, msg) => notices.Add(msg);

        Select(vm, FanControlMode.Custom);
        Run(vm, "ProcessFanCurves", hardware.GetCurrentTelemetry());

        // 1 initial attempt + 3 retries = 4 calls, visible notice emitted, backoff active.
        CollectionAssert.AreEqual(new[] { "curve:50/50", "curve:50/50", "curve:50/50", "curve:50/50" }, hardware.Calls);
        Assert.HasCount(1, notices);
        Assert.IsNull(vm.AppliedCpuFanPercent);

        // During the 10s backoff window, hardware is not hammered even when writes would succeed.
        hardware.FailWrites = false;
        nowUtc = nowUtc.AddSeconds(5);
        Run(vm, "ProcessFanCurves", hardware.GetCurrentTelemetry());
        Assert.HasCount(4, hardware.Calls);
        Assert.IsNull(vm.AppliedCpuFanPercent);

        // After 10s backoff expires, Custom mode automatically resumes and writes to hardware.
        nowUtc = nowUtc.AddSeconds(6);
        Run(vm, "ProcessFanCurves", hardware.GetCurrentTelemetry());
        Assert.HasCount(5, hardware.Calls);
        Assert.AreEqual("curve:50/50", hardware.Calls[^1]);
        Assert.AreEqual(50, vm.AppliedCpuFanPercent);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task FailedMaxWriteIsReportedByApplyInsteadOfClaimingSuccess()
    {
        var store = PreySense.Core.Configuration.ConfigurationService.Instance;
        var original = store.GetCurrent();
        var nowUtc = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
        var hardware = new RecordingFans { FailWrites = true };
        var vm = Create(hardware);
        vm.FanUtcNow = () => nowUtc;
        try
        {
            store.Update(config => config.Fans.Balanced.ControlMode = (int)FanControlMode.Max);
            await Assert.ThrowsAsync<InvalidOperationException>(() => vm.ApplyFanProfileAsync());
            CollectionAssert.AreEqual(new[] { "mode:1", "mode:1", "mode:1", "mode:1" }, hardware.Calls);
            for (int i = 0; i < 5; i++) Run(vm, "ProcessFanCurves", hardware.GetCurrentTelemetry());
            CollectionAssert.AreEqual(new[] { "mode:1", "mode:1", "mode:1", "mode:1" }, hardware.Calls);
        }
        finally { Run(vm, "DisposeFanTelemetry"); store.Save(original); }
    }

    [TestMethod]
    public void AutoFanWriteRetriesAndEnters10SecondBackoffWithoutHammering()
    {
        var nowUtc = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
        var hardware = new RecordingFans { FailWrites = true };
        var vm = Create(hardware);
        vm.FanUtcNow = () => nowUtc;
        Select(vm, FanControlMode.Auto);
        for (int i = 0; i < 5; i++)
            Run(vm, "ProcessFanCurves", hardware.GetCurrentTelemetry());
        CollectionAssert.AreEqual(new[] { "mode:0", "mode:0", "mode:0", "mode:0" }, hardware.Calls);
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(0)]
    public void CustomControlWritesFromCpuTemperatureWhenGpuIsAsleep(int gpuTemperature)
    {
        var hardware = new RecordingFans();
        var vm = Create(hardware);
        Select(vm, FanControlMode.Custom);
        var temperatures = hardware.GetCurrentTelemetry() with { CpuTemp = 78, GpuTemp = gpuTemperature, CpuRpm = -1, GpuRpm = -1 };
        Run(vm, "ProcessFanCurves", temperatures);
        Run(vm, "ProcessFanCurves", temperatures);
        CollectionAssert.AreEqual(new[] { "curve:50/50" }, hardware.Calls);
        Assert.AreEqual(0, hardware.PowerModeReads);
    }

    [TestMethod]
    [DoNotParallelize]
    public void OnlyCustomControlSubscribesToSharedTemperatureSamples()
    {
        TelemetryCoordinator.Reset();
        var hardware = new RecordingFans();
        var vm = Create(hardware);
        try
        {
            int samples = 0;
            TelemetryCoordinator.ConfigureSampler(() =>
            {
                samples++;
                return TelemetrySnapshot.Empty with { CpuTemp = 78, GpuTemp = -1, IsValid = true, IsFanOnly = true };
            });
            Select(vm, FanControlMode.Auto);
            Run(vm, "UpdateFanDemand");
            TelemetryCoordinator.Poll();
            Assert.AreEqual(0, samples);
            Select(vm, FanControlMode.Custom);
            Run(vm, "UpdateFanDemand");
            Assert.AreEqual(
                TelemetryMetrics.Temperatures | TelemetryMetrics.CpuFanRpm | TelemetryMetrics.GpuFanRpm,
                TelemetryCoordinator.RequestedMetrics);
            TelemetryCoordinator.Poll();
            Assert.AreEqual(1, samples);
            CollectionAssert.AreEqual(new[] { "curve:50/50" }, hardware.Calls);
            Select(vm, FanControlMode.Max);
            Run(vm, "UpdateFanDemand");
            Assert.AreEqual(TelemetryMetrics.None, TelemetryCoordinator.RequestedMetrics);
        }
        finally { Run(vm, "DisposeFanTelemetry"); TelemetryCoordinator.Reset(); }
    }

    [TestMethod]
    public void EcReclaimWatchdog_InvalidatesAndForceRewritesAfter15SecondsSteadyDivergence()
    {
        var nowUtc = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
        long nowMs = 1000;
        var hardware = new RecordingFans();
        var vm = Create(hardware);
        vm.FanUtcNow = () => nowUtc;
        vm.FanTickMs = () => nowMs;
        var cal = new FanCalibrationData
        {
            Cpu = new SingleFanCalibration { Points = [new(0, 0), new(10, 1800), new(50, 3800), new(100, 6200)] },
            Gpu = new SingleFanCalibration { Points = [new(0, 0), new(10, 1800), new(50, 3800), new(100, 6200)] }
        };
        cal.Normalize();
        vm.FanCalibrationProvider = () => cal;

        Run(vm, "ResetFanFollower");
        Set(vm, "_runningFanProfileMode", (byte)1);
        Set(vm, "_runningFanProfile", new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            RampUpSeconds = 0,
            RampDownSeconds = 0,
            HysteresisCelsius = 2,
            MinRpmFloor = 1800,
            CpuPoints = [new(40, 1800), new(70, 4000), new(90, 5800)],
            GpuPoints = [new(40, 1800), new(70, 4000), new(90, 5800)]
        });

        var baseTelemetry = hardware.GetCurrentTelemetry() with { CpuTemp = 70, GpuTemp = 70, CpuRpm = 4000, GpuRpm = 4000 };
        Run(vm, "ProcessFanCurves", baseTelemetry);
        Assert.HasCount(1, hardware.Calls);

        // Simulate EC reclaiming fans at 2200 RPM (divergence 1800 > 600) starting at s=1 for 14s (15 ticks): no watchdog rewrite yet.
        for (int s = 1; s <= 15; s++)
        {
            nowUtc = nowUtc.AddSeconds(1);
            nowMs += 1000;
            Run(vm, "ProcessFanCurves", baseTelemetry with { CpuRpm = 2200, GpuRpm = 4000 });
        }
        int invalidatesBefore = hardware.InvalidateCount;
        int callsBefore = hardware.Calls.Count;

        // At 15s of sustained steady divergence (s=16, 15s after s=1), watchdog triggers InvalidateFanTargets + force rewrite.
        nowUtc = nowUtc.AddSeconds(1);
        nowMs += 1000;
        Run(vm, "ProcessFanCurves", baseTelemetry with { CpuRpm = 2200, GpuRpm = 4000 });
        Assert.AreEqual(invalidatesBefore + 1, hardware.InvalidateCount);
        Assert.HasCount(callsBefore + 1, hardware.Calls);

        // Inside 15s cooldown (e.g. +5s), watchdog does not spam rewrites.
        nowUtc = nowUtc.AddSeconds(5);
        nowMs += 5000;
        Run(vm, "ProcessFanCurves", baseTelemetry with { CpuRpm = 2200, GpuRpm = 4000 });
        Assert.HasCount(callsBefore + 1, hardware.Calls);

        // After 15s cooldown + 15s sustained divergence, watchdog can trigger again.
        for (int s = 0; s < 16; s++)
        {
            nowUtc = nowUtc.AddSeconds(1);
            nowMs += 1000;
            Run(vm, "ProcessFanCurves", baseTelemetry with { CpuRpm = 2200, GpuRpm = 4000 });
        }
        Assert.HasCount(callsBefore + 2, hardware.Calls);
    }

    [TestMethod]
    public void ThermalEmergencyAndSmartBoostTier2Floor_EnforcedInRuntimeLoop()
    {
        var hardware = new RecordingFans();
        var vm = Create(hardware);
        var cal = new FanCalibrationData
        {
            Cpu = new SingleFanCalibration { Points = [new(0, 0), new(10, 1800), new(50, 3800), new(100, 6200)] },
            Gpu = new SingleFanCalibration { Points = [new(0, 0), new(10, 1800), new(50, 3800), new(100, 6200)] }
        };
        cal.Normalize();
        vm.FanCalibrationProvider = () => cal;

        long nowMs = 1000;
        vm.FanTickMs = () => nowMs;
        var smartBoost = new SmartBoostController(_ => { }, () => nowMs);
        smartBoost.SetBaseMode(2);
        smartBoost.ProcessTelemetry(88, nowMs); // Tier 2 (ModerateThrottleCelsius = 88)
        Assert.IsGreaterThanOrEqualTo(2, smartBoost.CurrentTier);
        Set(vm, "_smartBoost", smartBoost);

        Run(vm, "ResetFanFollower");
        Set(vm, "_runningFanProfileMode", (byte)1);
        Set(vm, "_runningFanProfile", new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            RampUpSeconds = 0,
            RampDownSeconds = 0,
            HysteresisCelsius = 2,
            MinRpmFloor = 1800,
            CpuPoints = [new(40, 1800), new(60, 2500), new(90, 5800)],
            GpuPoints = [new(40, 1800), new(60, 2500), new(90, 5800)]
        });

        // SmartBoost Tier >= 2 enforces >= 4500 RPM floor on CPU while keeping GPU at 2500 RPM.
        var baseTelemetry = hardware.GetCurrentTelemetry() with { CpuTemp = 60, GpuTemp = 60, CpuRpm = 4500, GpuRpm = 2500 };
        Run(vm, "ProcessFanCurves", baseTelemetry);
        var follower = (FanCurveFollower)typeof(MainViewModel).GetField("_fanFollower", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
        Assert.IsGreaterThanOrEqualTo(4500.0, follower.LastTargetCpuRpm);
        Assert.AreEqual(2500.0, follower.LastTargetGpuRpm, 1.0);
        Assert.IsGreaterThan(vm.AppliedGpuFanPercent!.Value, vm.AppliedCpuFanPercent!.Value);

        // Thermal Emergency (Cpu >= 95 or Gpu >= 87) forces 100/100 immediately.
        nowMs += 1000;
        Run(vm, "ProcessFanCurves", baseTelemetry with { CpuTemp = 95, GpuTemp = 60 });
        Assert.AreEqual(100, vm.AppliedCpuFanPercent);
        Assert.AreEqual(100, vm.AppliedGpuFanPercent);
        Assert.IsTrue(follower.EmergencyActive);

        // Holds emergency at Cpu=90, Gpu=82; releases when both Cpu <= 88 and Gpu <= 80.
        nowMs += 1000;
        Run(vm, "ProcessFanCurves", baseTelemetry with { CpuTemp = 90, GpuTemp = 82 });
        Assert.IsTrue(follower.EmergencyActive);
        Assert.AreEqual(100, vm.AppliedCpuFanPercent);

        nowMs += 1000;
        Run(vm, "ProcessFanCurves", baseTelemetry with { CpuTemp = 88, GpuTemp = 80 });
        Assert.IsFalse(follower.EmergencyActive);
    }

    [TestMethod]
    [DoNotParallelize]
    public void FanDiagnosticLogger_KeepsFanDemandActiveInAutoAndMaxModes()
    {
        TelemetryCoordinator.Reset();
        bool prevEnabled = FanDiagnosticLogger.Shared.Enabled;
        var hardware = new RecordingFans();
        var vm = Create(hardware);
        try
        {
            Select(vm, FanControlMode.Auto);
            vm.IsFanDiagnosticsLogEnabled = false;
            Run(vm, "UpdateFanDemand");
            Assert.AreEqual(TelemetryMetrics.None, TelemetryCoordinator.RequestedMetrics);

            vm.IsFanDiagnosticsLogEnabled = true;
            Run(vm, "UpdateFanDemand");
            Assert.AreEqual(
                TelemetryMetrics.Temperatures | TelemetryMetrics.CpuFanRpm | TelemetryMetrics.GpuFanRpm,
                TelemetryCoordinator.RequestedMetrics);
        }
        finally
        {
            FanDiagnosticLogger.Shared.Enabled = prevEnabled;
            Run(vm, "DisposeFanTelemetry");
            TelemetryCoordinator.Reset();
        }
    }

    [TestMethod]
    public void ThermalEmergency_OverridesAutoModeAt95CpuOr87Gpu_AndRestoresAutoOnCooldown()
    {
        var hardware = new RecordingFans();
        var vm = Create(hardware);
        var follower = (FanCurveFollower)typeof(MainViewModel).GetField("_fanFollower", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
        var requiresTempProp = typeof(MainViewModel).GetProperty("RequiresFanTemperatures", BindingFlags.NonPublic | BindingFlags.Instance)!;

        Select(vm, FanControlMode.Auto);
        var normal = hardware.GetCurrentTelemetry() with { CpuTemp = 60, GpuTemp = 55 };
        Run(vm, "ProcessFanCurves", normal);
        CollectionAssert.AreEqual(new[] { "mode:0" }, hardware.Calls);
        Assert.IsFalse(follower.EmergencyActive);
        Assert.IsFalse((bool)requiresTempProp.GetValue(vm)!);

        // CPU >= 95C in Auto mode must force 100/100 immediately.
        Run(vm, "ProcessFanCurves", normal with { CpuTemp = 95, GpuTemp = 55 });
        Assert.IsTrue(follower.EmergencyActive);
        Assert.AreEqual(100, vm.AppliedCpuFanPercent);
        Assert.AreEqual(100, vm.AppliedGpuFanPercent);
        Assert.AreEqual("curve:100/100", hardware.Calls[^1]);
        Assert.IsTrue((bool)requiresTempProp.GetValue(vm)!);

        // Transient sensor drop (-1) during active emergency must hold 100/100 instead of reverting to Auto.
        int callCount = hardware.Calls.Count;
        Run(vm, "ProcessFanCurves", normal with { CpuTemp = -1, GpuTemp = -1 });
        Assert.IsTrue(follower.EmergencyActive);
        Assert.HasCount(callCount, hardware.Calls);

        // Above recovery hysteresis (CPU=89) holds 100/100; at CPU=88, GPU=80 releases and restores Auto.
        Run(vm, "ProcessFanCurves", normal with { CpuTemp = 89, GpuTemp = 60 });
        Assert.IsTrue(follower.EmergencyActive);
        Run(vm, "ProcessFanCurves", normal with { CpuTemp = 88, GpuTemp = 60 });
        Assert.IsFalse(follower.EmergencyActive);
        Assert.IsNull(vm.AppliedCpuFanPercent);
        Assert.AreEqual("mode:0", hardware.Calls[^1]);
        Assert.IsFalse((bool)requiresTempProp.GetValue(vm)!);

        // GPU >= 87C in Auto mode also triggers emergency and releases at GPU <= 80C.
        Run(vm, "ProcessFanCurves", normal with { CpuTemp = 70, GpuTemp = 87 });
        Assert.IsTrue(follower.EmergencyActive);
        Assert.AreEqual("curve:100/100", hardware.Calls[^1]);
        Run(vm, "ProcessFanCurves", normal with { CpuTemp = 70, GpuTemp = 80 });
        Assert.IsFalse(follower.EmergencyActive);
        Assert.AreEqual("mode:0", hardware.Calls[^1]);
    }

    [TestMethod]
    public void MaxModeWriteFailure_KeepsBackoffDemandActiveUntil10SecondRecovery()
    {
        var nowUtc = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
        var hardware = new RecordingFans { FailWrites = true };
        var vm = Create(hardware);
        vm.FanUtcNow = () => nowUtc;
        var requiresTempProp = typeof(MainViewModel).GetProperty("RequiresFanTemperatures", BindingFlags.NonPublic | BindingFlags.Instance)!;

        Select(vm, FanControlMode.Max);
        Run(vm, "ProcessFanCurves", hardware.GetCurrentTelemetry());
        Assert.HasCount(4, hardware.Calls);
        Assert.IsTrue((bool)requiresTempProp.GetValue(vm)!, "Backoff must keep fan demand active so 10s recovery runs in Auto/Max.");

        hardware.FailWrites = false;
        nowUtc = nowUtc.AddSeconds(11);
        Run(vm, "ProcessFanCurves", hardware.GetCurrentTelemetry());
        Assert.AreEqual("mode:1", hardware.Calls[^1]);
        Assert.AreEqual(100, vm.AppliedCpuFanPercent);
        Assert.IsFalse((bool)requiresTempProp.GetValue(vm)!);
    }

    [TestMethod]
    public void SafeQuietIdleBase_EngagesInTurboIdle_AndImmediatelyRestoresNativeModeOnLoadEmergencyOrExit()
    {
        var hardware = new RecordingFans { ActiveMode = 5 }; // Turbo (0x05)
        var vm = Create(hardware);
        long nowMs = 1000;
        vm.FanTickMs = () => nowMs;
        var cal = new FanCalibrationData
        {
            Cpu = new SingleFanCalibration
            {
                BlackoutMaxDuty = 9,
                MinQuietDuty = 10,
                MinQuietRpm = 2220,
                Points = [new(0, 0), new(10, 2220), new(50, 4000), new(100, 6200)]
            },
            Gpu = new SingleFanCalibration
            {
                BlackoutMaxDuty = 9,
                MinQuietDuty = 10,
                MinQuietRpm = 2220,
                Points = [new(0, 0), new(10, 2220), new(50, 4000), new(100, 6200)]
            }
        };
        cal.Normalize();
        vm.FanCalibrationProvider = () => cal;

        Run(vm, "ResetFanFollower");
        Set(vm, "_runningFanProfileMode", (byte)5);
        Set(vm, "_runningFanProfile", new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            RampUpSeconds = 0,
            RampDownSeconds = 0,
            HysteresisCelsius = 2,
            MinRpmFloor = 1800,
            UseEcAutoBelowMin = true,
            SafeQuietIdleBase = true,
            CpuPoints = [new(59, 0), new(60, 2220), new(85, 5500)],
            GpuPoints = [new(59, 0), new(60, 2220), new(85, 5500)]
        });

        // 1. Both CPU & GPU < 60C (0 RPM target) in Turbo -> engages SafeQuietIdleBase(true, 5, 1) and sets 0% Auto.
        var cold = hardware.GetCurrentTelemetry() with { CpuTemp = 48, GpuTemp = 44, CpuRpm = 1800, GpuRpm = 1800 };
        Run(vm, "ProcessFanCurves", cold);
        Assert.IsTrue(hardware.QuietIdleBaseActive);
        Assert.AreEqual((byte)5, hardware.LastQuietIdleUserMode);
        Assert.AreEqual((byte)0x01, hardware.LastQuietBaseMode);
        Assert.AreEqual(1, hardware.QuietIdleBaseCallCount);
        CollectionAssert.AreEqual(new[] { "quietIdle:true/5", "curve:0/0" }, hardware.Calls);

        // Repeated cold ticks do NOT spam WMI.
        nowMs += 1000;
        Run(vm, "ProcessFanCurves", cold);
        Assert.AreEqual(1, hardware.QuietIdleBaseCallCount);
        Assert.HasCount(2, hardware.Calls);

        // 2. CPU warms >= 60 + 7 (68C) -> IMMEDIATELY disengages QuietIdleBase BEFORE applying active fan curve >= 10%.
        nowMs += 1000;
        Run(vm, "ProcessFanCurves", cold with { CpuTemp = 68 });
        Assert.IsFalse(hardware.QuietIdleBaseActive);
        Assert.AreEqual(2, hardware.QuietIdleBaseCallCount);
        Assert.AreEqual("quietIdle:false/5", hardware.Calls[2]);
        Assert.StartsWith("curve:", hardware.Calls[3]);
        Assert.IsGreaterThanOrEqualTo(10, vm.AppliedCpuFanPercent!.Value);

        // 3. Cool back down -> re-engages SafeQuietIdleBase; GPU warming >= 60C also immediately disengages BEFORE fan write.
        for (int i = 0; i < 6 && !hardware.QuietIdleBaseActive; i++)
        {
            nowMs += 1000;
            Run(vm, "ProcessFanCurves", cold);
        }
        Assert.IsTrue(hardware.QuietIdleBaseActive);

        nowMs += 1000;
        Run(vm, "ProcessFanCurves", cold with { GpuTemp = 70 });
        Assert.IsFalse(hardware.QuietIdleBaseActive);
        Assert.AreEqual("quietIdle:false/5", hardware.Calls[^2]);
        Assert.IsGreaterThanOrEqualTo(10, vm.AppliedGpuFanPercent!.Value);

        // 4. Cool back down -> Thermal Emergency (>= 95C) immediately disengages BEFORE 100/100.
        for (int i = 0; i < 6 && !hardware.QuietIdleBaseActive; i++)
        {
            nowMs += 1000;
            Run(vm, "ProcessFanCurves", cold);
        }
        Assert.IsTrue(hardware.QuietIdleBaseActive);

        nowMs += 1000;
        Run(vm, "ProcessFanCurves", cold with { CpuTemp = 95 });
        Assert.IsFalse(hardware.QuietIdleBaseActive);
        Assert.AreEqual("quietIdle:false/5", hardware.Calls[^2]);
        Assert.AreEqual("curve:100/100", hardware.Calls[^1]);

        // Recover from emergency, cool down into QuietIdleBase, then ReleaseCustomFans() disengages cleanly.
        for (int i = 0; i < 8 && !hardware.QuietIdleBaseActive; i++)
        {
            nowMs += 1000;
            Run(vm, "ProcessFanCurves", cold);
        }
        Assert.IsTrue(hardware.QuietIdleBaseActive);
        Run(vm, "ReleaseCustomFans");
        Assert.IsFalse(hardware.QuietIdleBaseActive);
        Assert.AreEqual("quietIdle:false/5", hardware.Calls[^2]);
        Assert.AreEqual("mode:0", hardware.Calls[^1]);
    }

    [TestMethod]
    public void SafeQuietIdleBase_SkippedInQuietAndEcoModes_AndDisengagesOnAutoOrMaxSwitch()
    {
        // Quiet (0x00) and Eco (0x06) never call SetQuietIdleBase(true, ...).
        foreach (byte mode in new byte[] { 0x00, 0x06 })
        {
            var hw = new RecordingFans { ActiveMode = mode };
            var vm = Create(hw);
            Set(vm, "_runningFanProfileMode", mode);
            Set(vm, "_runningFanProfile", new FanCurveProfile
            {
                ControlMode = FanControlMode.Custom,
                MinRpmFloor = 1800,
                UseEcAutoBelowMin = true,
                SafeQuietIdleBase = true,
                CpuPoints = [new(59, 0), new(60, 2200)],
                GpuPoints = [new(59, 0), new(60, 2200)]
            });

            Run(vm, "ProcessFanCurves", hw.GetCurrentTelemetry() with { CpuTemp = 45, GpuTemp = 42 });
            Assert.IsFalse(hw.QuietIdleBaseActive);
            Assert.AreEqual(0, hw.QuietIdleBaseCallCount);
        }

        // Switching from Custom Idle (Performance 0x04) to Auto/Max immediately disengages QuietIdleBase.
        var perfHw = new RecordingFans { ActiveMode = 0x04 };
        var perfVm = Create(perfHw);
        Set(perfVm, "_runningFanProfileMode", (byte)0x04);
        Set(perfVm, "_runningFanProfile", new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            MinRpmFloor = 1800,
            UseEcAutoBelowMin = true,
            SafeQuietIdleBase = true,
            CpuPoints = [new(59, 0), new(60, 2200)],
            GpuPoints = [new(59, 0), new(60, 2200)]
        });

        Run(perfVm, "ProcessFanCurves", perfHw.GetCurrentTelemetry() with { CpuTemp = 45, GpuTemp = 42 });
        Assert.IsTrue(perfHw.QuietIdleBaseActive);
        Assert.AreEqual((byte)0x01, perfHw.LastQuietBaseMode);

        Set(perfVm, "_runningFanProfile", new FanCurveProfile { ControlMode = FanControlMode.Max });
        Run(perfVm, "ProcessFanCurves", perfHw.GetCurrentTelemetry() with { CpuTemp = 45, GpuTemp = 42 });
        Assert.IsFalse(perfHw.QuietIdleBaseActive);
        Assert.AreEqual("quietIdle:false/4", perfHw.Calls[^2]);
        Assert.AreEqual("mode:1", perfHw.Calls[^1]);

        // RestoreFanMode() also disengages QuietIdleBase if currently engaged.
        Set(perfVm, "_runningFanProfile", new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            MinRpmFloor = 1800,
            UseEcAutoBelowMin = true,
            SafeQuietIdleBase = true,
            CpuPoints = [new(59, 0), new(60, 2200)],
            GpuPoints = [new(59, 0), new(60, 2200)]
        });
        Run(perfVm, "ProcessFanCurves", perfHw.GetCurrentTelemetry() with { CpuTemp = 45, GpuTemp = 42 });
        Assert.IsTrue(perfHw.QuietIdleBaseActive);
        Run(perfVm, "RestoreFanMode");
        Assert.IsFalse(perfHw.QuietIdleBaseActive);
    }

    [TestMethod]
    public void SafeQuietIdleBase_BalancedStaysNativeUnlessCalibratedQuieter_AndManualZeroIdleEngagesCleanly()
    {
        var idleProfile = new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            RampUpSeconds = 0,
            RampDownSeconds = 0,
            HysteresisCelsius = 0,
            MinRpmFloor = 1800,
            UseEcAutoBelowMin = true,
            SafeQuietIdleBase = true,
            CpuPoints = [new(59, 0), new(60, 2220)],
            GpuPoints = [new(59, 0), new(60, 2220)]
        };

        // 1. Balanced (0x01) uncalibrated MUST NOT switch to Quiet (0x00).
        var balancedHw = new RecordingFans { ActiveMode = 0x01 };
        var balancedVm = Create(balancedHw);
        Set(balancedVm, "_runningFanProfileMode", (byte)0x01);
        Set(balancedVm, "_runningFanProfile", idleProfile.Copy());
        Run(balancedVm, "ProcessFanCurves", balancedHw.GetCurrentTelemetry() with { CpuTemp = 45, GpuTemp = 42 });
        Assert.IsFalse(balancedHw.QuietIdleBaseActive);
        Assert.AreEqual(0, balancedHw.QuietIdleBaseCallCount);
        CollectionAssert.AreEqual(new[] { "curve:0/0" }, balancedHw.Calls);

        // 2. Balanced (0x01) calibrated with RecommendedIdleEcMode = 0x01 also stays in Balanced (0x01).
        var calBalancedBest = new FanCalibrationData
        {
            RecommendedIdleEcMode = 0x01,
            BalancedAutoCpuRpm = 2150,
            BalancedAutoGpuRpm = 2150,
            QuietAutoCpuRpm = 2470,
            QuietAutoGpuRpm = 2580,
            Cpu = new SingleFanCalibration { Points = [new(0, 0), new(10, 2220), new(100, 6200)] },
            Gpu = new SingleFanCalibration { Points = [new(0, 0), new(10, 2220), new(100, 6200)] }
        };
        calBalancedBest.Normalize();
        balancedVm.FanCalibrationProvider = () => calBalancedBest;
        Run(balancedVm, "ProcessFanCurves", balancedHw.GetCurrentTelemetry() with { CpuTemp = 45, GpuTemp = 42 });
        Assert.IsFalse(balancedHw.QuietIdleBaseActive);
        Assert.AreEqual(0, balancedHw.QuietIdleBaseCallCount);

        // 3. Balanced (0x01) calibrated with RecommendedIdleEcMode = 0x00 DOES engage Quiet (0x00).
        var calQuietBest = calBalancedBest.Copy();
        calQuietBest.RecommendedIdleEcMode = 0x00;
        balancedVm.FanCalibrationProvider = () => calQuietBest;
        Run(balancedVm, "ProcessFanCurves", balancedHw.GetCurrentTelemetry() with { CpuTemp = 45, GpuTemp = 42 });
        Assert.IsTrue(balancedHw.QuietIdleBaseActive);
        Assert.AreEqual((byte)0x01, balancedHw.LastQuietIdleUserMode);
        Assert.AreEqual((byte)0x00, balancedHw.LastQuietBaseMode);

        // 4. Direct SetQuietIdleBase(true, 0x01, 0x01) returns false; SetQuietIdleBase(true, 0x05, 0x01) returns true.
        var directMock = new MockHardwareService();
        Assert.IsFalse(directMock.SetQuietIdleBase(true, 0x01));
        Assert.IsFalse(directMock.QuietIdleBaseActive);
        Assert.IsTrue(directMock.SetQuietIdleBase(true, 0x05, 0x01));
        Assert.IsTrue(directMock.QuietIdleBaseActive);
        Assert.AreEqual((byte)0x01, directMock.LastQuietBaseMode);

        // 5. Turbo (0x05) calibrated with Eco (0x06) quietest + PreferManualZeroIdle = true engages both cleanly without spam.
        var turboHw = new RecordingFans { ActiveMode = 0x05 };
        var turboVm = Create(turboHw);
        long nowMs = 1000;
        turboVm.FanTickMs = () => nowMs;
        var calEcoManualZero = new FanCalibrationData
        {
            RecommendedIdleEcMode = 0x06,
            EcoAutoCpuRpm = 1950,
            EcoAutoGpuRpm = 1950,
            ManualZeroCpuRpm = 1750,
            ManualZeroGpuRpm = 1750,
            PreferManualZeroIdle = true,
            Cpu = new SingleFanCalibration { Points = [new(0, 0), new(10, 2220), new(100, 6200)] },
            Gpu = new SingleFanCalibration { Points = [new(0, 0), new(10, 2220), new(100, 6200)] }
        };
        calEcoManualZero.Normalize();
        turboVm.FanCalibrationProvider = () => calEcoManualZero;
        Set(turboVm, "_runningFanProfileMode", (byte)0x05);
        Set(turboVm, "_runningFanProfile", idleProfile.Copy());

        var cold = turboHw.GetCurrentTelemetry() with { CpuTemp = 45, GpuTemp = 42, CpuRpm = 1750, GpuRpm = 1750 };
        Run(turboVm, "ProcessFanCurves", cold);
        Assert.IsTrue(turboHw.QuietIdleBaseActive);
        Assert.AreEqual((byte)0x06, turboHw.LastQuietBaseMode);
        Assert.IsTrue(turboHw.ManualZeroFansActive);
        Assert.AreEqual(1, turboHw.SetManualZeroFansCallCount);
        CollectionAssert.AreEqual(new[] { "quietIdle:true/5", "manualZero" }, turboHw.Calls);

        // Repeated cold tick does not repeat manualZero or quietIdle WMI writes.
        nowMs += 1000;
        Run(turboVm, "ProcessFanCurves", cold);
        Assert.AreEqual(1, turboHw.SetManualZeroFansCallCount);
        Assert.HasCount(2, turboHw.Calls);

        // Exiting idle invalidates fan targets, disengages QuietIdleBase, and switches to SetFanSpeeds cleanly.
        int invalidatesBeforeExit = turboHw.InvalidateCount;
        nowMs += 1000;
        Run(turboVm, "ProcessFanCurves", cold with { CpuTemp = 70 });
        Assert.IsFalse(turboHw.QuietIdleBaseActive);
        Assert.IsFalse(turboHw.ManualZeroFansActive);
        Assert.IsGreaterThan(invalidatesBeforeExit, turboHw.InvalidateCount);
        Assert.AreEqual("quietIdle:false/5", turboHw.Calls[2]);
        Assert.StartsWith("curve:", turboHw.Calls[3]);
    }

    [TestMethod]
    public void SafeQuietIdleBase_DisengagesOnModeSwitch_OptionDisable_AndSave()
    {
        var hw = new RecordingFans { ActiveMode = 0x05 };
        var vm = Create(hw);
        var turboIdleProfile = new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            RampUpSeconds = 0,
            RampDownSeconds = 0,
            HysteresisCelsius = 0,
            MinRpmFloor = 1800,
            UseEcAutoBelowMin = true,
            SafeQuietIdleBase = true,
            CpuPoints = [new(59, 0), new(60, 2220)],
            GpuPoints = [new(59, 0), new(60, 2220)]
        };

        Set(vm, "_runningFanProfileMode", (byte)0x05);
        Set(vm, "_runningFanProfile", turboIdleProfile);

        var cold = hw.GetCurrentTelemetry() with { CpuTemp = 45, GpuTemp = 42, CpuRpm = 1800, GpuRpm = 1800 };
        Run(vm, "ProcessFanCurves", cold);
        Assert.IsTrue(hw.QuietIdleBaseActive);

        // 1. Disabling UseEcAutoBelowMin and saving/applying disengages QuietIdleBase cleanly.
        var noEcAuto = turboIdleProfile.Copy();
        noEcAuto.UseEcAutoBelowMin = false;
        vm.SaveFanProfile(0x05, noEcAuto);
        Assert.IsFalse(hw.QuietIdleBaseActive, "SaveFanProfile for active mode must disengage QuietIdleBase.");

        // Re-engage on Turbo, then verify switching Turbo (0x05) -> Balanced (0x01) disengages QuietIdleBase.
        Set(vm, "_runningFanProfileMode", (byte)0x05);
        Set(vm, "_runningFanProfile", turboIdleProfile.Copy());
        Run(vm, "ProcessFanCurves", cold);
        Assert.IsTrue(hw.QuietIdleBaseActive);

        hw.ActiveMode = 0x01;
        Run(vm, "RestoreFanMode");
        Assert.IsFalse(hw.QuietIdleBaseActive);

        // 2. Disabling SafeQuietIdleBase alone keeps 0% Auto without calling SetQuietIdleBase(true).
        var noSafeBase = turboIdleProfile.Copy();
        noSafeBase.SafeQuietIdleBase = false;
        Set(vm, "_runningFanProfileMode", (byte)0x01);
        Set(vm, "_runningFanProfile", noSafeBase);
        hw.Calls.Clear();
        Run(vm, "ProcessFanCurves", cold);
        Assert.IsFalse(hw.QuietIdleBaseActive);
        CollectionAssert.DoesNotContain(hw.Calls, "quietIdle:true/1");
    }

    [TestMethod]
    public async Task SafeQuietIdleBase_AndManualZero_ResetCleanlyOnCalibrationAndModeSwitchToBalanced()
    {
        var hw = new RecordingFans { ActiveMode = 0x05 };
        var vm = Create(hw);
        long nowMs = 1000;
        vm.FanTickMs = () => nowMs;
        var cal = new FanCalibrationData
        {
            RecommendedIdleEcMode = 0x01,
            BalancedAutoCpuRpm = 2200,
            BalancedAutoGpuRpm = 2200,
            ManualZeroCpuRpm = 1800,
            ManualZeroGpuRpm = 1800,
            PreferManualZeroIdle = true,
            Cpu = new SingleFanCalibration { Points = [new(0, 0), new(10, 2220), new(100, 6200)] },
            Gpu = new SingleFanCalibration { Points = [new(0, 0), new(10, 2220), new(100, 6200)] }
        };
        cal.Normalize();
        vm.FanCalibrationProvider = () => cal;

        var turboIdleProfile = new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            RampUpSeconds = 0,
            RampDownSeconds = 0,
            HysteresisCelsius = 0,
            MinRpmFloor = 1800,
            UseEcAutoBelowMin = true,
            SafeQuietIdleBase = true,
            CpuPoints = [new(59, 0), new(60, 2220)],
            GpuPoints = [new(59, 0), new(60, 2220)]
        };

        Set(vm, "_runningFanProfileMode", (byte)0x05);
        Set(vm, "_runningFanProfile", turboIdleProfile.Copy());

        var cold = hw.GetCurrentTelemetry() with { CpuTemp = 45, GpuTemp = 42, CpuRpm = 1800, GpuRpm = 1800 };
        Run(vm, "ProcessFanCurves", cold);
        Assert.IsTrue(hw.QuietIdleBaseActive);
        Assert.IsTrue(hw.ManualZeroFansActive);
        Assert.IsTrue((bool)typeof(MainViewModel).GetField("_quietIdleBaseEngaged", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!);
        Assert.IsTrue((bool)typeof(MainViewModel).GetField("_sentManualZero", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!);

        // 1. SaveFanProfile while _sentManualZero is active must invalidate targets and reset _sentManualZero cleanly before ReleaseCustomFans.
        int invalidatesBeforeSave = hw.InvalidateCount;
        vm.SaveFanProfile(0x05, turboIdleProfile.Copy());
        Assert.IsFalse(hw.QuietIdleBaseActive);
        Assert.IsFalse((bool)typeof(MainViewModel).GetField("_quietIdleBaseEngaged", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!);
        Assert.IsFalse((bool)typeof(MainViewModel).GetField("_sentManualZero", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!);
        Assert.IsGreaterThan(invalidatesBeforeSave, hw.InvalidateCount);

        Run(vm, "ReleaseCustomFans");
        Assert.AreEqual("mode:0", hw.Calls[^1]);

        // 2. Re-engage QuietIdleBase + ManualZero on Turbo (0x05), then switch to Balanced (0x01) -> _quietIdleBaseEngaged must reset cleanly.
        Set(vm, "_runningFanProfileMode", (byte)0x05);
        Set(vm, "_runningFanProfile", turboIdleProfile.Copy());
        Run(vm, "ProcessFanCurves", cold);
        Assert.IsTrue(hw.QuietIdleBaseActive);

        hw.SetPowerMode(0x01);
        hw.ActiveMode = 0x01;
        Run(vm, "RestoreFanMode");
        Assert.IsFalse(hw.QuietIdleBaseActive);
        Assert.IsFalse((bool)typeof(MainViewModel).GetField("_quietIdleBaseEngaged", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!);
        Assert.IsFalse((bool)typeof(MainViewModel).GetField("_sentManualZero", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!);

        // 3. Re-engage on Turbo (0x05), then start cancelled calibration -> DisengageQuietIdleBaseIfEngaged runs before calibration and RestoreFanMode runs in finally.
        hw.SetPowerMode(0x05);
        hw.ActiveMode = 0x05;
        Set(vm, "_runningFanProfileMode", (byte)0x05);
        Set(vm, "_runningFanProfile", turboIdleProfile.Copy());
        Run(vm, "ProcessFanCurves", cold);
        Assert.IsTrue(hw.QuietIdleBaseActive);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var calResult = await vm.RunFanCalibrationAsync(cancellationToken: cts.Token);
        Assert.AreEqual(FanCalibrationStatus.Cancelled, calResult.Status);
        Assert.IsFalse(hw.QuietIdleBaseActive);
        Assert.IsFalse((bool)typeof(MainViewModel).GetField("_quietIdleBaseEngaged", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!);
        Assert.IsFalse((bool)typeof(MainViewModel).GetField("_calibratingFans", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!);
    }

    private sealed class RecordingFans : MockHardwareService, IHardwareService
    {
        public List<string> Calls { get; } = new();
        public bool FailWrites { get; set; }
        public byte ActiveMode { get; set; } = 1;
        public int PowerModeReads { get; private set; }
        public int InvalidateCount { get; private set; }
        public bool TryGetPowerMode(out byte mode) { PowerModeReads++; mode = ActiveMode; return true; }
        public void InvalidateFanTargets() => InvalidateCount++;
        public new bool SetFanMode(byte cpuMode, byte gpuMode) { Calls.Add($"mode:{cpuMode}"); return !FailWrites && base.SetFanMode(cpuMode, gpuMode); }
        public new bool SetFanSpeeds(byte cpuPercentage, byte gpuPercentage) { Calls.Add($"curve:{cpuPercentage}/{gpuPercentage}"); return !FailWrites && base.SetFanSpeeds(cpuPercentage, gpuPercentage); }
        public new bool TrySetManualZeroFans() { Calls.Add("manualZero"); return !FailWrites && base.TrySetManualZeroFans(); }
        public new bool SetQuietIdleBase(bool enable, byte userPowerMode, byte quietBaseMode = 0x01)
        {
            Calls.Add($"quietIdle:{(enable ? "true" : "false")}/{userPowerMode}");
            return base.SetQuietIdleBase(enable, userPowerMode, quietBaseMode);
        }
    }
}
