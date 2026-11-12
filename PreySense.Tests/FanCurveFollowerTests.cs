using PreySense.Mode;

namespace PreySense.Tests;

[TestClass]
public class FanCurveFollowerTests
{
    private static FanStage[] Balanced() => FanCurveProfile.DefaultStages(1);

    [TestMethod]
    public void StageZeroStaysStoppedUntilRiseTrip()
    {
        var follower = new FanCurveFollower();
        var stages = Balanced();
        var (cpu, gpu, stage) = follower.Evaluate(40, 40, stages, 0, 0);
        Assert.AreEqual(0, stage);
        Assert.AreEqual(0, cpu);
        Assert.AreEqual(0, gpu);
        (cpu, gpu, stage) = follower.Evaluate(56, 40, stages, 0, 1000);
        Assert.AreEqual(1, stage);
        int shared = Math.Max(
            FanStage.RpmToDuty(1800, FanStage.CpuMaxRpm),
            FanStage.RpmToDuty(1850, FanStage.GpuMaxRpm));
        Assert.AreEqual(shared, cpu);
        Assert.AreEqual(shared, gpu);
    }

    [TestMethod]
    public void GpuRiseAloneAdvancesStage()
    {
        var follower = new FanCurveFollower();
        var stages = Balanced();
        var (_, _, stage) = follower.Evaluate(40, 50, stages, 0, 0);
        Assert.AreEqual(1, stage);
    }

    [TestMethod]
    public void RetreatUsesPreviousRiseAndRequiresBothSensorsClear()
    {
        var follower = new FanCurveFollower();
        var stages = Balanced();
        follower.Evaluate(65, 56, stages, 0, 0);
        var (_, _, stage) = follower.Evaluate(65, 56, stages, 0, 1000);
        Assert.AreEqual(2, stage);
        // Stage 2 was entered via stage 1 rises (CPU 65 / GPU 56). GPU still at 56 must not retreat.
        (_, _, stage) = follower.Evaluate(50, 56, stages, 0, 2000);
        Assert.AreEqual(2, stage, "CPU below previous rise but GPU still at previous rise must not retreat.");
        // Both below stage-1 rises → stage 1; still above stage-0 rises (56/50) → stay at 1.
        (_, _, stage) = follower.Evaluate(60, 52, stages, 0, 3000);
        Assert.AreEqual(1, stage);
        // Both below stage-0 rises → cold floor.
        (_, _, stage) = follower.Evaluate(40, 40, stages, 0, 4000);
        Assert.AreEqual(0, stage);
    }

    [TestMethod]
    public void RetreatHysteresisPreventsImmediateOscillationNearRiseTrip()
    {
        var follower = new FanCurveFollower();
        var stages = Balanced();
        // Stage 0 -> Stage 1 at CPU 56°C
        var (_, _, stage) = follower.Evaluate(56, 40, stages, 0, 0);
        Assert.AreEqual(1, stage);
        // Dropping 1-2°C below 56°C (to 54°C) stays in stage 1 due to 3°C retreat hysteresis
        (_, _, stage) = follower.Evaluate(54, 40, stages, 0, 1000);
        Assert.AreEqual(1, stage);
        // Dropping below 56 - 3 = 53°C (to 52°C) retreats back to stage 0 (0% Quiet Auto)
        (_, _, stage) = follower.Evaluate(52, 40, stages, 0, 2000);
        Assert.AreEqual(0, stage);
    }

    [TestMethod]
    public void StageChangesImmediatelyWithoutDelay()
    {
        var follower = new FanCurveFollower();
        var stages = Balanced();
        var (_, _, stage) = follower.Evaluate(40, 40, stages);
        Assert.AreEqual(0, stage);
        (_, _, stage) = follower.Evaluate(56, 40, stages);
        Assert.AreEqual(1, stage);
    }

    [TestMethod]
    public void SharedDelayHoldsStageChangeUntilElapsed()
    {
        var follower = new FanCurveFollower();
        var stages = Balanced();
        follower.Evaluate(40, 40, stages, delaySeconds: 2, nowMs: 0);
        var (_, _, stage) = follower.Evaluate(56, 40, stages, delaySeconds: 2, nowMs: 500);
        Assert.AreEqual(0, stage, "Stage must wait for the shared delay.");
        (_, _, stage) = follower.Evaluate(56, 40, stages, delaySeconds: 2, nowMs: 2499);
        Assert.AreEqual(0, stage);
        (_, _, stage) = follower.Evaluate(56, 40, stages, delaySeconds: 2, nowMs: 2500);
        Assert.AreEqual(1, stage);
    }

    [TestMethod]
    public void DisabledTripDoesNotAdvanceOnThatSensor()
    {
        var follower = new FanCurveFollower();
        var quiet = FanCurveProfile.DefaultStages(0);
        // Quiet stage 5: CPU rise 127, GPU rise 80 — only GPU can advance to stage 6.
        follower.Evaluate(90, 75, quiet, 0, 0);
        var (_, _, stage) = follower.Evaluate(100, 75, quiet, 0, 1000);
        Assert.IsLessThanOrEqualTo(5, stage);
        (_, _, stage) = follower.Evaluate(100, 80, quiet, 0, 2000);
        Assert.AreEqual(6, stage);
    }

    [TestMethod]
    public void FactoryPresetsMatchEcRpmTargets()
    {
        var balanced = FanCurveProfile.CreateDefault(1);
        Assert.HasCount(12, balanced.Stages);
        Assert.AreEqual(0, balanced.Stages[0].CpuTargetRpm);
        Assert.AreEqual(1800, balanced.Stages[1].CpuTargetRpm);
        Assert.AreEqual(2350, balanced.Stages[3].CpuTargetRpm);
        Assert.AreEqual(6400, balanced.Stages[11].CpuTargetRpm);
        CollectionAssert.AreEqual(
            FanCurveProfile.DefaultStages(0).Select(s => s.CpuTargetRpm).ToArray(),
            FanCurveProfile.DefaultStages(6).Select(s => s.CpuTargetRpm).ToArray());
    }

    [TestMethod]
    public void PerModeMaxChoiceDoesNotReplaceSavedStagesOrLeakToAnotherProfile()
    {
        var turbo = new FanCurveProfile { ControlMode = FanControlMode.Max, Stages = FanCurveProfile.DefaultStages(5) };
        var balanced = new FanCurveProfile();
        var draft = turbo.Copy();
        Assert.AreEqual(FanControlMode.Max, draft.ControlMode);
        Assert.AreEqual(FanControlMode.Auto, balanced.ControlMode);
        draft.ControlMode = FanControlMode.Custom;
        Assert.AreEqual(FanControlMode.Max, turbo.ControlMode);
        Assert.AreEqual(2150, turbo.Stages[1].CpuTargetRpm);
    }

    [TestMethod]
    public void DecodeControlModePrefersSavedValue()
    {
        Assert.AreEqual(FanControlMode.Max, FanCurveStorage.DecodeControlMode(1, false, false, 0));
        Assert.AreEqual(FanControlMode.Custom, FanCurveStorage.DecodeControlMode(2, false, true, 0));
        Assert.AreEqual(FanControlMode.Auto, FanCurveStorage.DecodeControlMode(null, false, true, 1));
        Assert.AreEqual(FanControlMode.Max, FanCurveStorage.DecodeControlMode(null, true, true, 1));
    }

    [TestMethod]
    public void EvaluateSmoothRampsGraduallyInsteadOfJumpingAbruptly()
    {
        var follower = new FanCurveFollower();
        var stages = Balanced();

        // Start cold in Stage 0 (0% Quiet Auto)
        var (cpu0, _, stage0) = follower.EvaluateSmooth(44, 40, stages, 0, 1000);
        Assert.AreEqual(0, stage0);
        Assert.AreEqual(0, cpu0);

        // Sudden jump in CPU temperature to 75°C:
        // Instead of jumping immediately to 37%+, EvaluateSmooth filters temperature and slew-limits duty.
        var (cpu1, _, _) = follower.EvaluateSmooth(75, 40, stages, 0, 2000);
        var (cpu2, _, _) = follower.EvaluateSmooth(75, 40, stages, 0, 3000);
        var (cpu3, _, _) = follower.EvaluateSmooth(75, 40, stages, 0, 4000);
        var (cpu4, _, _) = follower.EvaluateSmooth(75, 40, stages, 0, 5000);
        var (cpu5, _, _) = follower.EvaluateSmooth(75, 40, stages, 0, 6000);

        // Brief spike for 1-2s stays in 0% Quiet Auto due to EMA smoothing + delay,
        // then enters gently from the minimum floor and ramps smoothly by <= 1.5-2%/sec.
        Assert.AreEqual(0, cpu1);
        Assert.IsLessThanOrEqualTo(29, cpu4, $"Expected gentle initial ramp from quiet floor, got {cpu4}%");
        Assert.IsLessThanOrEqualTo(4, cpu5 - cpu4, $"Step per second after leaving floor should be bounded, got {cpu5 - cpu4}%");
    }

    [TestMethod]
    public void EvaluateContinuousDuty_InterpolatesBetweenPointsAndHitsExactStageValues()
    {
        var stages = FanCurveProfile.CreateDefault(1).Stages;
        stages[0].CpuRiseC = 50;
        stages[0].SetDuty(false, 0);
        stages[1].CpuRiseC = 70;
        stages[1].SetDuty(false, 20);
        stages[2].CpuRiseC = 127;
        stages[2].SetDuty(false, 60);

        Assert.AreEqual(0.0, FanCurveFollower.EvaluateContinuousDuty(40.0, stages, gpu: false, out _), 0.01);
        Assert.AreEqual(20.0, FanCurveFollower.EvaluateContinuousDuty(50.0, stages, gpu: false, out _), 0.01);
        Assert.AreEqual(40.0, FanCurveFollower.EvaluateContinuousDuty(60.0, stages, gpu: false, out _), 0.01);
        Assert.AreEqual(60.0, FanCurveFollower.EvaluateContinuousDuty(70.0, stages, gpu: false, out _), 0.01);
        Assert.AreEqual(60.0, FanCurveFollower.EvaluateContinuousDuty(85.0, stages, gpu: false, out _), 0.01);
    }

    [TestMethod]
    public void EvaluateSmooth_ConvergesToExactTargetAndTransitionsSmoothlyToZeroAuto()
    {
        var follower = new FanCurveFollower();
        var stages = FanCurveProfile.CreateDefault(1).Stages;
        stages[0].CpuRiseC = 50;
        stages[0].SetDuty(false, 0);
        stages[1].CpuRiseC = 70;
        stages[1].SetDuty(false, 20);
        stages[2].CpuRiseC = 127;
        stages[2].SetDuty(false, 60);

        // Steady at 60°C (midpoint between 50°C/20% and 70°C/60%) -> settles at exact 40%.
        int cpu = 0;
        long t = 0;
        for (int i = 0; i < 60; i++)
        {
            t += 1000;
            (cpu, _, _) = follower.EvaluateSmooth(60, 40, stages, 0, t);
        }
        Assert.AreEqual(40, cpu);

        // Cool back down to 40°C (below 50°C -> target 0% Auto): slews down gently without snapping at 23%, then settles at 0.
        int prev = cpu;
        for (int i = 0; i < 60; i++)
        {
            t += 1000;
            (cpu, _, _) = follower.EvaluateSmooth(40, 40, stages, 0, t);
            Assert.IsLessThanOrEqualTo(prev, cpu);
            if (prev > 6)
                Assert.IsLessThanOrEqualTo(2, prev - cpu, $"Sudden drop from {prev}% to {cpu}%");
            prev = cpu;
        }
        Assert.AreEqual(0, cpu);
    }

    private static FanCalibrationData CreateTestCalibration()
    {
        var cal = new FanCalibrationData
        {
            Cpu = new SingleFanCalibration
            {
                Points = [new(0, 0), new(10, 1800), new(50, 3800), new(100, 6200)]
            },
            Gpu = new SingleFanCalibration
            {
                Points = [new(0, 0), new(10, 1800), new(50, 3800), new(100, 6200)]
            }
        };
        cal.Normalize();
        return cal;
    }

    [TestMethod]
    public void EvaluateClosedLoop_ConvergesWithin100Rpm_UnderSimulatedMinus5PercentDrag()
    {
        var follower = new FanCurveFollower();
        var cal = CreateTestCalibration();
        var profile = new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            RampUpSeconds = 2,
            RampDownSeconds = 4,
            HysteresisCelsius = 2,
            MinRpmFloor = 1800,
            CpuPoints = [new(40, 1800), new(70, 4000), new(90, 5800)],
            GpuPoints = [new(40, 1800), new(70, 4000), new(90, 5800)]
        };

        int measuredCpu = 3750;
        int measuredGpu = 3750;
        long nowMs = 0;

        for (int i = 0; i < 20; i++)
        {
            var (cpuDuty, gpuDuty, _) = follower.EvaluateClosedLoop(
                cpuTemp: 70,
                gpuTemp: 70,
                measuredCpuRpm: measuredCpu,
                measuredGpuRpm: measuredGpu,
                profile: profile,
                calibration: cal,
                nowMs: nowMs);

            // Simulated mechanical drag: actual fan behaves as if duty is 5% lower.
            measuredCpu = cal.Cpu.DutyToRpm(Math.Max(0, cpuDuty - 5));
            measuredGpu = cal.Gpu.DutyToRpm(Math.Max(0, gpuDuty - 5));
            nowMs += 1000;
        }

        Assert.IsLessThanOrEqualTo(100, Math.Abs(4000 - measuredCpu), $"Expected CPU RPM within +-100 of 4000, got {measuredCpu}");
        Assert.IsLessThanOrEqualTo(100, Math.Abs(4000 - measuredGpu), $"Expected GPU RPM within +-100 of 4000, got {measuredGpu}");
        Assert.IsGreaterThan(0.0, follower.LastPiTrimCpuDuty);
    }

    [TestMethod]
    public void EvaluateClosedLoop_StepResponse_ConvergesUpWithin5sAndDownWithin8s_Without600RpmSpikes()
    {
        var follower = new FanCurveFollower();
        var cal = CreateTestCalibration();
        var profile = new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            RampUpSeconds = 2,
            RampDownSeconds = 4,
            HysteresisCelsius = 2,
            MinRpmFloor = 1800,
            CpuPoints = [new(45, 2000), new(75, 4200), new(90, 5800)],
            GpuPoints = [new(45, 2000), new(75, 4200), new(90, 5800)]
        };

        long nowMs = 0;
        follower.EvaluateClosedLoop(45, 45, 2000, 2000, profile, cal, nowMs);
        double prevTarget = follower.LastTargetCpuRpm;
        Assert.AreEqual(2000.0, prevTarget, 1.0);

        // Warm-up step: 45C -> 75C (2000 -> 4200 RPM) must converge within 5s without >600 RPM/s jump.
        for (int s = 1; s <= 5; s++)
        {
            nowMs += 1000;
            follower.EvaluateClosedLoop(75, 45, (int)prevTarget, 2000, profile, cal, nowMs);
            double step = follower.LastTargetCpuRpm - prevTarget;
            Assert.IsLessThanOrEqualTo(600.01, step, $"Tick {s}s jump {step:F1} RPM/s exceeded 600 RPM/s");
            prevTarget = follower.LastTargetCpuRpm;
        }
        Assert.IsLessThanOrEqualTo(100.0, Math.Abs(4200.0 - follower.LastTargetCpuRpm),
            $"Warm-up target did not converge within 5s (got {follower.LastTargetCpuRpm:F1} RPM)");

        // Settle completely at 75C
        for (int s = 0; s < 3; s++)
        {
            nowMs += 1000;
            follower.EvaluateClosedLoop(75, 45, 4200, 2000, profile, cal, nowMs);
            prevTarget = follower.LastTargetCpuRpm;
        }

        // Cool-down step: 75C -> 45C (4200 -> 2000 RPM) must converge within 8s without >600 RPM/s drop.
        for (int s = 1; s <= 8; s++)
        {
            nowMs += 1000;
            follower.EvaluateClosedLoop(45, 45, (int)prevTarget, 2000, profile, cal, nowMs);
            double drop = prevTarget - follower.LastTargetCpuRpm;
            Assert.IsLessThanOrEqualTo(600.01, drop, $"Cooldown tick {s}s drop {drop:F1} RPM/s exceeded 600 RPM/s");
            prevTarget = follower.LastTargetCpuRpm;
        }
        Assert.IsLessThanOrEqualTo(100.0, Math.Abs(2000.0 - follower.LastTargetCpuRpm),
            $"Cool-down target did not converge within 8s (got {follower.LastTargetCpuRpm:F1} RPM)");

        // Zero ramp time applies curve target immediately with zero RPM ramp lag.
        var instantFollower = new FanCurveFollower();
        var instantProfile = profile.Copy();
        instantProfile.RampUpSeconds = 0;
        instantProfile.RampDownSeconds = 0;
        instantFollower.EvaluateClosedLoop(45, 45, 2000, 2000, instantProfile, cal, 0);
        instantFollower.EvaluateClosedLoop(75, 45, 2000, 2000, instantProfile, cal, 5000);
        Assert.IsGreaterThan(3600.0, instantFollower.LastTargetCpuRpm);
    }

    [TestMethod]
    public void EvaluateClosedLoop_CooldownHysteresis_EliminatesJitterAndConvergesToExactCurve()
    {
        var follower = new FanCurveFollower();
        var cal = CreateTestCalibration();
        var profile = new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            RampUpSeconds = 0,
            RampDownSeconds = 0,
            HysteresisCelsius = 2,
            MinRpmFloor = 1800,
            CpuPoints = [new(40, 1800), new(70, 4000), new(90, 6000)],
            GpuPoints = [new(40, 1800), new(70, 4000), new(90, 6000)]
        };

        long nowMs = 0;
        follower.EvaluateClosedLoop(70, 40, 4000, 1800, profile, cal, nowMs);
        double steadyAt70 = follower.LastTargetCpuRpm;
        Assert.AreEqual(4000.0, steadyAt70, 0.5);

        // Oscillate within the 2C cooldown hysteresis deadband -> zero target RPM jitter.
        int[] jitterTemps = [69, 70, 68, 70, 69, 68, 70];
        foreach (int temp in jitterTemps)
        {
            nowMs += 1000;
            follower.EvaluateClosedLoop(temp, 40, 4000, 1800, profile, cal, nowMs);
            Assert.AreEqual(steadyAt70, follower.LastTargetCpuRpm, 0.5, $"Jitter at {temp}C caused target oscillation.");
        }

        // Steady cooldown to 65C exits hysteresis deadband and matches MonotoneCubicInterpolator identically.
        for (int i = 0; i < 8; i++)
        {
            nowMs += 1000;
            follower.EvaluateClosedLoop(65, 40, 3500, 1800, profile, cal, nowMs);
        }

        double expectedAt65 = MonotoneCubicInterpolator.Evaluate(65, profile.CpuPoints);
        Assert.AreEqual(expectedAt65, follower.LastTargetCpuRpm, 1.0);
    }

    [TestMethod]
    public void EvaluateClosedLoop_PiDeadband_TargetChangeFreeze_AndAntiWindup()
    {
        var cal = CreateTestCalibration();
        var profile = new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            RampUpSeconds = 0,
            RampDownSeconds = 0,
            HysteresisCelsius = 2,
            MinRpmFloor = 1800,
            CpuPoints = [new(40, 2000), new(60, 3000), new(70, 4000), new(90, 6200)],
            GpuPoints = [new(40, 2000), new(70, 4000), new(90, 6200)]
        };

        // 1. Deadband: |error| <= 75 RPM -> error = 0, integral stays 0.
        var deadbandFollower = new FanCurveFollower();
        for (long t = 0; t <= 10000; t += 1000)
            deadbandFollower.EvaluateClosedLoop(70, 40, 3930, 2000, profile, cal, t);
        Assert.AreEqual(0.0, deadbandFollower.LastCpuIntegral, 1e-6);
        Assert.AreEqual(0.0, deadbandFollower.LastPiTrimCpuDuty, 1e-6);

        // 2. Target-change freeze: >100 RPM target jump pauses integral accumulation for 2 seconds.
        var freezeFollower = new FanCurveFollower();
        freezeFollower.EvaluateClosedLoop(60, 40, 3000, 2000, profile, cal, 0);
        // Jump to 70C with RampUp=0 (3000 -> ~3600+ immediately); at t=1000..2500 target changed >100 within 2s.
        freezeFollower.EvaluateClosedLoop(70, 40, 3000, 2000, profile, cal, 1000);
        Assert.AreEqual(0.0, freezeFollower.LastCpuIntegral, 1e-6, "Integral must freeze immediately on >100 RPM step.");
        // Wait until temp settles and 2s freeze elapses
        long tMs = 2000;
        for (; tMs <= 8000; tMs += 1000)
            freezeFollower.EvaluateClosedLoop(70, 40, 3000, 2000, profile, cal, tMs);
        Assert.IsGreaterThan(0.0, freezeFollower.LastCpuIntegral, "Integral should accumulate after 2s target-change freeze expires.");

        // 3. Anti-windup: saturated at 100% duty does not accumulate positive integral windup.
        var windupFollower = new FanCurveFollower();
        for (long t = 0; t <= 15000; t += 1000)
            windupFollower.EvaluateClosedLoop(90, 40, 3000, 2000, profile, cal, t);
        Assert.AreEqual(0.0, windupFollower.LastCpuIntegral, 1e-6, "Saturated high output must prevent positive integral windup.");
        Assert.IsLessThanOrEqualTo(8.0, windupFollower.LastPiTrimCpuDuty);
        Assert.IsGreaterThanOrEqualTo(-8.0, windupFollower.LastPiTrimCpuDuty);
    }

    [TestMethod]
    public void EvaluateClosedLoop_MinRpmFloor_AndOptInEcAutoPolicy()
    {
        var cal = CreateTestCalibration(); // MinStableRpm = 1800, MinStableDuty = 10
        var profile = new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            RampUpSeconds = 0,
            RampDownSeconds = 0,
            HysteresisCelsius = 2,
            MinRpmFloor = 1800,
            UseEcAutoBelowMin = false,
            CpuPoints = [new(40, 0), new(70, 4000)],
            GpuPoints = [new(40, 0), new(70, 4000)]
        };

        // 1. Default UseEcAutoBelowMin=false never drops to 0% Auto; clamps to effective min floor.
        var manualFollower = new FanCurveFollower();
        var (cpuDuty, gpuDuty, _) = manualFollower.EvaluateClosedLoop(40, 40, 1800, 1800, profile, cal, 0);
        Assert.AreEqual(10, cpuDuty);
        Assert.AreEqual(10, gpuDuty);
        Assert.AreEqual(1800.0, manualFollower.LastTargetCpuRpm, 1.0);
        Assert.IsFalse(manualFollower.LastCpuEcAuto);

        // 2. If calibrated MinStableRpm > profile.MinRpmFloor (e.g. 2050 > 1800), hardware floor 2050 wins.
        var higherFloorCal = new FanCalibrationData
        {
            Cpu = new SingleFanCalibration { Points = [new(0, 0), new(12, 2050), new(100, 6200)] },
            Gpu = new SingleFanCalibration { Points = [new(0, 0), new(12, 2050), new(100, 6200)] }
        };
        higherFloorCal.Normalize();
        var (higherDuty, _, _) = new FanCurveFollower().EvaluateClosedLoop(40, 40, 2050, 2050, profile, higherFloorCal, 0);
        Assert.AreEqual(12, higherDuty);

        // 3. Uncalibrated fallback floor is FanStage.MinManualRpmFloor (2000).
        var uncalFollower = new FanCurveFollower();
        var (uncalDuty, _, _) = uncalFollower.EvaluateClosedLoop(40, 40, 2000, 2000, profile, calibration: null, nowMs: 0);
        Assert.AreEqual(2000.0, uncalFollower.LastTargetCpuRpm, 1.0);
        Assert.IsGreaterThanOrEqualTo(1, uncalDuty);

        // 4. Opt-in UseEcAutoBelowMin=true returns 0% duty when raw curve point <= 0.
        var ecAutoProfile = profile.Copy();
        ecAutoProfile.UseEcAutoBelowMin = true;
        var autoFollower = new FanCurveFollower();
        var (autoCpu, autoGpu, _) = autoFollower.EvaluateClosedLoop(40, 40, 2200, 2200, ecAutoProfile, cal, 0);
        Assert.AreEqual(0, autoCpu);
        Assert.AreEqual(0, autoGpu);
        Assert.IsTrue(autoFollower.LastCpuEcAuto);
        Assert.IsTrue(autoFollower.LastGpuEcAuto);
    }

    [TestMethod]
    public void EvaluateClosedLoop_ThermalEmergencyOverride_EngagesImmediatelyAndReleasesWithHysteresis()
    {
        var follower = new FanCurveFollower();
        var cal = CreateTestCalibration();
        var profile = new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            RampUpSeconds = 10,
            RampDownSeconds = 10,
            HysteresisCelsius = 2,
            MinRpmFloor = 1800,
            CpuPoints = [new(40, 1800), new(70, 3000), new(90, 4500)],
            GpuPoints = [new(40, 1800), new(70, 3000), new(90, 4500)]
        };

        follower.EvaluateClosedLoop(60, 55, 2500, 2500, profile, cal, 0);
        Assert.IsFalse(follower.EmergencyActive);

        // Trigger on CPU >= 95C: must bypass RampUp=10s and immediately output 100/100.
        var (cpu, gpu, _) = follower.EvaluateClosedLoop(95, 55, 2500, 2500, profile, cal, 1000);
        Assert.IsTrue(follower.EmergencyActive);
        Assert.AreEqual(100, cpu);
        Assert.AreEqual(100, gpu);

        // Hold above recovery hysteresis (Cpu <= 88 && Gpu <= 80).
        (cpu, gpu, _) = follower.EvaluateClosedLoop(89, 75, 6200, 6200, profile, cal, 2000);
        Assert.IsTrue(follower.EmergencyActive);
        Assert.AreEqual(100, cpu);
        Assert.AreEqual(100, gpu);

        // GPU trigger at 87C; if CPU recovers to 88C while GPU is 81C, emergency must hold.
        follower.EvaluateClosedLoop(88, 87, 6200, 6200, profile, cal, 3000);
        Assert.IsTrue(follower.EmergencyActive);
        follower.EvaluateClosedLoop(88, 81, 6200, 6200, profile, cal, 4000);
        Assert.IsTrue(follower.EmergencyActive);

        // Both <= 88C CPU and <= 80C GPU releases emergency.
        follower.EvaluateClosedLoop(88, 80, 6200, 6200, profile, cal, 5000);
        Assert.IsFalse(follower.EmergencyActive);
    }

    [TestMethod]
    public void EvaluateClosedLoop_1800To4500With1SecTachLagAndPlant_ConvergesWithinLatencyAndModerateStepLimit()
    {
        var cal = CreateTestCalibration(); // 10%->1800, 50%->3800, 100%->6200
        var profile = new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            RampUpSeconds = 2,
            RampDownSeconds = 4,
            HysteresisCelsius = 2,
            MinRpmFloor = 1800,
            CpuPoints = [new(45, 1800), new(75, 4500), new(90, 6200)],
            GpuPoints = [new(45, 1800), new(75, 4500), new(90, 6200)]
        };

        // 1) Step 45C (1800 RPM) -> 75C (4500 RPM) with 1-second tach lag plant.
        var follower = new FanCurveFollower();
        int measuredCpu = 1800;
        long nowMs = 0;
        follower.EvaluateClosedLoop(45, 45, measuredCpu, 1800, profile, cal, nowMs);
        double prevTarget = follower.LastTargetCpuRpm;
        int pendingPlantRpm = 1800;

        for (int s = 1; s <= 6; s++)
        {
            nowMs += 1000;
            measuredCpu = pendingPlantRpm; // 1-second tach lag from prior tick's EC command
            var (duty, _, _) = follower.EvaluateClosedLoop(75, 45, measuredCpu, 1800, profile, cal, nowMs);
            double deltaTarget = follower.LastTargetCpuRpm - prevTarget;
            Assert.IsLessThanOrEqualTo(600.01, deltaTarget, $"Tick {s}s jump {deltaTarget:F1} RPM/s exceeded 600 RPM/s.");
            prevTarget = follower.LastTargetCpuRpm;
            pendingPlantRpm = cal.Cpu.DutyToRpm(duty);
            if (s == 5)
            {
                Assert.IsLessThanOrEqualTo(100.0, Math.Abs(4500.0 - follower.LastTargetCpuRpm),
                    $"Target RPM at 5s was {follower.LastTargetCpuRpm:F1}, expected within +-100 of 4500.");
            }
        }
        Assert.IsLessThanOrEqualTo(100, Math.Abs(4500 - pendingPlantRpm),
            $"1s-lagged measured RPM at 6s was {pendingPlantRpm}, expected within +-100 of 4500.");

        // Settle at 75C with -3% mechanical drag and verify PI steady-state convergence within +-100 RPM.
        for (int s = 7; s <= 15; s++)
        {
            nowMs += 1000;
            measuredCpu = pendingPlantRpm;
            var (duty, _, _) = follower.EvaluateClosedLoop(75, 45, measuredCpu, 1800, profile, cal, nowMs);
            pendingPlantRpm = cal.Cpu.DutyToRpm(Math.Max(1, duty - 3));
        }
        Assert.IsLessThanOrEqualTo(100, Math.Abs(4500 - pendingPlantRpm),
            $"Steady-state 75C RPM under -3% drag was {pendingPlantRpm}, expected within +-100 of 4500.");

        // 2) Step 75C (4500 RPM) -> 45C (1800 RPM) with RampDownSeconds = 4 and 1-second tach lag.
        prevTarget = follower.LastTargetCpuRpm;
        for (int s = 1; s <= 8; s++)
        {
            nowMs += 1000;
            measuredCpu = pendingPlantRpm;
            var (duty, _, _) = follower.EvaluateClosedLoop(45, 45, measuredCpu, 1800, profile, cal, nowMs);
            double drop = prevTarget - follower.LastTargetCpuRpm;
            Assert.IsLessThanOrEqualTo(600.01, drop, $"Cooldown tick {s}s drop {drop:F1} RPM/s exceeded 600 RPM/s.");
            prevTarget = follower.LastTargetCpuRpm;
            pendingPlantRpm = cal.Cpu.DutyToRpm(duty);
        }
        Assert.IsLessThanOrEqualTo(100.0, Math.Abs(1800.0 - follower.LastTargetCpuRpm),
            $"Cooldown target RPM at 8s was {follower.LastTargetCpuRpm:F1}, expected within +-100 of 1800.");
        Assert.IsLessThanOrEqualTo(100, Math.Abs(1800 - pendingPlantRpm),
            $"Cooldown 1s-lagged measured RPM at 8s was {pendingPlantRpm}, expected within +-100 of 1800.");

        // Verify steady-state 45C (1800 RPM) under -3% mechanical drag also converges via PI integral trim.
        for (int s = 9; s <= 20; s++)
        {
            nowMs += 1000;
            measuredCpu = pendingPlantRpm;
            var (duty, _, _) = follower.EvaluateClosedLoop(45, 45, measuredCpu, 1800, profile, cal, nowMs);
            pendingPlantRpm = cal.Cpu.DutyToRpm(Math.Max(1, duty - 3));
        }
        Assert.IsLessThanOrEqualTo(100, Math.Abs(1800 - pendingPlantRpm),
            $"Steady-state 45C RPM under -3% drag was {pendingPlantRpm}, expected within +-100 of 1800.");

        // 3) Moderate step 50C (~2200 RPM) -> 65C (~3400 RPM): no >600 RPM/s single-tick spikes.
        var modFollower = new FanCurveFollower();
        modFollower.EvaluateClosedLoop(50, 45, 2200, 1800, profile, cal, 0);
        double modPrev = modFollower.LastTargetCpuRpm;
        for (int s = 1; s <= 6; s++)
        {
            modFollower.EvaluateClosedLoop(65, 45, (int)modPrev, 1800, profile, cal, s * 1000L);
            double jump = modFollower.LastTargetCpuRpm - modPrev;
            Assert.IsLessThanOrEqualTo(600.01, jump, $"Moderate step tick {s}s jump {jump:F1} RPM/s exceeded 600 RPM/s.");
            modPrev = modFollower.LastTargetCpuRpm;
        }
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void EvaluateClosedLoop_SleepingDgpu_SettlesAtMinRpmOrZeroEcAuto_UnlessCpuHotOrEmergency(int gpuTemp)
    {
        var cal = CreateTestCalibration(); // MinStableRpm = 1800, MinStableDuty = 10
        var profile = new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            RampUpSeconds = 0,
            RampDownSeconds = 0,
            HysteresisCelsius = 2,
            MinRpmFloor = 1800,
            UseEcAutoBelowMin = false,
            CpuPoints = [new(45, 1800), new(75, 4200), new(90, 6000)],
            GpuPoints = [new(50, 3232), new(75, 4500), new(90, 6000)]
        };

        // 1. UseEcAutoBelowMin = false -> sleeping dGPU settles cleanly at MinRPM (1800), NOT GpuPoints[0].Rpm (3232).
        var minFollower = new FanCurveFollower();
        var (_, gpuMinDuty, _) = minFollower.EvaluateClosedLoop(55, gpuTemp, 2500, 1800, profile, cal, 0);
        Assert.AreEqual(10, gpuMinDuty);
        Assert.AreEqual(1800.0, minFollower.LastTargetGpuRpm, 1.0);
        Assert.IsFalse(minFollower.LastGpuEcAuto);

        // 2. UseEcAutoBelowMin = true -> sleeping dGPU settles cleanly at 0% EC Auto.
        var autoProfile = profile.Copy();
        autoProfile.UseEcAutoBelowMin = true;
        var autoFollower = new FanCurveFollower();
        var (_, gpuAutoDuty, _) = autoFollower.EvaluateClosedLoop(55, gpuTemp, 2500, 2200, autoProfile, cal, 0);
        Assert.AreEqual(0, gpuAutoDuty);
        Assert.AreEqual(0.0, autoFollower.LastTargetGpuRpm, 0.1);
        Assert.IsTrue(autoFollower.LastGpuEcAuto);

        // 3. Shared heatsink protection when CPU >= 85C overrides sleeping GPU idle floor even with UseEcAutoBelowMin = true.
        for (int s = 1; s <= 5; s++)
            autoFollower.EvaluateClosedLoop(86, gpuTemp, 5400, 2200, autoProfile, cal, s * 1000L);
        Assert.IsFalse(autoFollower.LastGpuEcAuto);
        Assert.IsGreaterThan(4000.0, autoFollower.LastTargetGpuRpm);

        // 4. Thermal Emergency (CPU >= 95C) with sleeping dGPU forces 100/100 immediately.
        var (emCpu, emGpu, _) = autoFollower.EvaluateClosedLoop(95, gpuTemp, 5400, 2200, autoProfile, cal, 4000);
        Assert.IsTrue(autoFollower.EmergencyActive);
        Assert.AreEqual(100, emCpu);
        Assert.AreEqual(100, emGpu);
    }

    [TestMethod]
    public void EvaluateClosedLoop_KickZoneFloor_NeverEntersBlackout_AndTransitionsSmoothlyBetweenAutoAndManual()
    {
        var cal = new FanCalibrationData
        {
            CalibratedAtUtc = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc),
            Cpu = new SingleFanCalibration
            {
                BlackoutMaxDuty = 9,
                MinQuietDuty = 10,
                MinQuietRpm = 2220,
                EcAutoBaselineRpm = 1800,
                Points = [new(0, 0), new(10, 2220), new(50, 4000), new(100, 6200)]
            },
            Gpu = new SingleFanCalibration
            {
                BlackoutMaxDuty = 9,
                MinQuietDuty = 10,
                MinQuietRpm = 2220,
                EcAutoBaselineRpm = 1800,
                Points = [new(0, 0), new(10, 2220), new(50, 4000), new(100, 6200)]
            }
        };
        cal.Normalize();

        var profile = new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            RampUpSeconds = 2,
            RampDownSeconds = 4,
            HysteresisCelsius = 2,
            MinRpmFloor = 1800,
            UseEcAutoBelowMin = true,
            SafeQuietIdleBase = true,
            CpuPoints = [new(59, 0), new(60, 2220), new(80, 4500)],
            GpuPoints = [new(59, 0), new(60, 2220), new(80, 4500)]
        };

        var follower = new FanCurveFollower();
        long t = 0;

        // 1. Cold below 60C (0 RPM target) -> 0% EC Auto.
        var (cpu, gpu, _) = follower.EvaluateClosedLoop(48, 44, 1800, 1800, profile, cal, t);
        Assert.AreEqual(0, cpu);
        Assert.AreEqual(0, gpu);
        Assert.IsTrue(follower.LastCpuEcAuto);
        Assert.IsTrue(follower.LastGpuEcAuto);

        // 2. Exit EC Auto to >= 60C -> seeds at >= MinQuietRpm (2220) and jumps cleanly to >= 10% (never 1..9%).
        t += 1000;
        (cpu, gpu, _) = follower.EvaluateClosedLoop(68, 44, 1800, 1800, profile, cal, t);
        Assert.IsFalse(follower.LastCpuEcAuto);
        Assert.IsGreaterThanOrEqualTo(10, cpu);
        Assert.IsGreaterThanOrEqualTo(2220.0, follower.LastTargetCpuRpm);

        // 3. Even under strong negative PI drag (fan overspeeding at 2900 RPM vs 2220 target for 12s),
        // manual duty NEVER dips into BlackoutMaxDuty [1..9%].
        for (int i = 0; i < 12; i++)
        {
            t += 1000;
            (cpu, _, _) = follower.EvaluateClosedLoop(60, 44, 2900, 1800, profile, cal, t);
            Assert.IsGreaterThanOrEqualTo(10, cpu, $"Negative PI drag pushed duty to {cpu}% inside BlackoutMaxDuty 1..9%!");
            Assert.IsFalse(follower.LastCpuEcAuto);
        }

        // 4. Warm up to 75C (~3900 RPM), then cool back below 59C (past 2C hysteresis):
        // target slews smoothly down to MinQuietRpm and transitions cleanly to 0% EC Auto without getting stuck.
        for (int i = 0; i < 8; i++)
        {
            t += 1000;
            (cpu, _, _) = follower.EvaluateClosedLoop(75, 44, 3800, 1800, profile, cal, t);
        }
        Assert.IsGreaterThan(3000.0, follower.LastTargetCpuRpm);

        bool reachedAuto = false;
        int prevDuty = cpu;
        for (int i = 0; i < 15; i++)
        {
            t += 1000;
            (cpu, _, _) = follower.EvaluateClosedLoop(50, 44, 2220, 1800, profile, cal, t);
            if (cpu == 0)
            {
                reachedAuto = true;
                Assert.IsTrue(follower.LastCpuEcAuto);
                break;
            }
            Assert.IsGreaterThanOrEqualTo(10, cpu, $"Cooldown entered blackout zone {cpu}% before EC Auto!");
            Assert.IsLessThanOrEqualTo(prevDuty, cpu);
            prevDuty = cpu;
        }
        Assert.IsTrue(reachedAuto, "Cooldown below hysteresis failed to transition cleanly from MinQuietDuty to 0% EC Auto.");
    }

    [TestMethod]
    public void EvaluateClosedLoop_DynamicSlopeInversionGuard_LocksRuntimeMinDutyWithoutBlackoutCalibration()
    {
        // Deliberately NO BlackoutMaxDuty set (e.g. uncalibrated or optimistic low-end curve).
        // Hardware behavior: Duty >= 10% -> 2220 + (duty-10)*40 RPM, Duty 1..9% -> 3100 RPM anti-stall kick!
        var calWithoutBlackout = new FanCalibrationData
        {
            CalibratedAtUtc = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc),
            Cpu = new SingleFanCalibration
            {
                Points = [new(0, 0), new(2, 1600), new(10, 2220), new(50, 3820), new(100, 5820)]
            },
            Gpu = new SingleFanCalibration
            {
                Points = [new(0, 0), new(2, 1600), new(10, 2220), new(50, 3820), new(100, 5820)]
            }
        };
        calWithoutBlackout.Normalize();
        Assert.AreEqual(0, calWithoutBlackout.Cpu.BlackoutMaxDuty);

        var profile = new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            RampUpSeconds = 0,
            RampDownSeconds = 0,
            HysteresisCelsius = 0,
            MinRpmFloor = 2000,
            UseEcAutoBelowMin = false,
            CpuPoints = [new(40, 2000), new(55, 2220), new(80, 4500)],
            GpuPoints = [new(40, 2000), new(55, 2220), new(80, 4500)]
        };

        int HardwarePlantRpm(int duty) => duty switch
        {
            <= 0 => 1800,
            < 10 => 3100, // Nitro EC anti-stall kick!
            _ => 2220 + (duty - 10) * 40
        };

        var follower = new FanCurveFollower();
        long t = 0;

        // Establish 3 settled ticks at 55C -> target 2220 RPM -> 10% duty -> 2220 RPM.
        int cpuDuty = 0;
        for (int s = 0; s < 3; s++)
        {
            (cpuDuty, _, _) = follower.EvaluateClosedLoop(55, 45, 2220, 2220, profile, calWithoutBlackout, t);
            Assert.AreEqual(10, cpuDuty);
            Assert.IsFalse(follower.SlopeInversionTriggered);
            t += 1000;
        }

        // Cool to 40C -> target 2000 RPM -> feedforward/PI drops duty below 10% (triggering 3100 RPM kick on next tick).
        int measuredCpu = HardwarePlantRpm(cpuDuty);
        for (int s = 1; s <= 10; s++)
        {
            (cpuDuty, _, _) = follower.EvaluateClosedLoop(40, 45, measuredCpu, 2220, profile, calWithoutBlackout, t);
            measuredCpu = HardwarePlantRpm(cpuDuty);
            t += 1000;
        }

        Assert.IsTrue(follower.SlopeInversionTriggered, "Expected slope-inversion guard to trigger on +150 RPM spike.");
        Assert.AreEqual(10, follower.RuntimeMinCpuDuty);
        Assert.AreEqual(0.0, follower.LastCpuIntegral, 1e-6);
        Assert.AreEqual(10, cpuDuty, "Fan must hold 10% (~2220 RPM) instead of getting trapped at 2% (3100 RPM).");
        Assert.AreEqual(2220, measuredCpu);

        // Calibration update resets _runtimeMinDuty and SlopeInversionTriggered.
        var updatedCal = calWithoutBlackout.Copy();
        updatedCal.CalibratedAtUtc = calWithoutBlackout.CalibratedAtUtc!.Value.AddMinutes(5);
        t += 1000;
        follower.EvaluateClosedLoop(55, 45, 2220, 2220, profile, updatedCal, t);
        Assert.AreEqual(0, follower.RuntimeMinCpuDuty);
        Assert.IsFalse(follower.SlopeInversionTriggered);

        // Reset() also clears _runtimeMinDuty.
        follower.Reset();
        Assert.AreEqual(0, follower.RuntimeMinCpuDuty);
        Assert.IsFalse(follower.SlopeInversionTriggered);
    }

    [TestMethod]
    public void EvaluateClosedLoop_DiscreteGearPlateauDeadband_PreventsHunting_WhileAllowingOffGearTrim()
    {
        // Simulated discrete Acer Nitro EC plateaus: 10-18% -> 3100 RPM, 20-28% -> 3658 RPM.
        var cal = new FanCalibrationData
        {
            CalibratedAtUtc = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc),
            Cpu = new SingleFanCalibration
            {
                BlackoutMaxDuty = 9,
                MinQuietDuty = 10,
                MinQuietRpm = 3100,
                Points =
                [
                    new(0, 0),
                    new(10, 3100), new(12, 3100), new(14, 3100), new(16, 3100), new(18, 3100),
                    new(20, 3658), new(22, 3658), new(24, 3658), new(26, 3658), new(28, 3658),
                    new(30, 4150), new(100, 6200)
                ]
            },
            Gpu = new SingleFanCalibration
            {
                Points = [new(0, 0), new(10, 3100), new(100, 6200)]
            }
        };
        cal.Normalize();

        var profile = new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            RampUpSeconds = 0,
            RampDownSeconds = 0,
            HysteresisCelsius = 2,
            MinRpmFloor = 1800,
            CpuPoints = [new(50, 3100), new(65, 3350), new(85, 5500)],
            GpuPoints = [new(50, 3100), new(85, 5500)]
        };

        static int SteppedGearPlant(int duty) => duty switch
        {
            <= 19 => 3100,
            <= 28 => 3658,
            _ => 4150 + (duty - 30) * 30
        };

        // 1. Target 3350 RPM (halfway between 3100 and 3658): holds closer gear (3100 RPM) without hunting 19% <-> 20%.
        var follower = new FanCurveFollower();
        int measured = 3100;
        var duties = new List<int>();
        for (long t = 0; t <= 12000; t += 1000)
        {
            var (cpuDuty, _, _) = follower.EvaluateClosedLoop(65, 50, measured, 3100, profile, cal, t);
            duties.Add(cpuDuty);
            measured = SteppedGearPlant(cpuDuty);
        }

        // After settling onto closer gear (3100 RPM), duty remains steady (no continuous alternation) and integral doesn't saturate +8%.
        Assert.AreEqual(duties[^1], duties[^2], "Duty must hold steady on closest EC gear without hunting.");
        Assert.AreEqual(duties[^1], duties[^3], "Duty must hold steady on closest EC gear without hunting.");
        Assert.IsLessThan(4.0, Math.Abs(follower.LastCpuIntegral), $"Integral wound up to {follower.LastCpuIntegral:F2}% on discrete plateau.");

        // 2. When fan actually drifts off-gear (e.g. lagging at 2700 RPM and responding to duty increases, |error| > 150),
        // full PI trim resumes cleanly.
        var linearFollower = new FanCurveFollower();
        int laggingRpm = 2700;
        long nowMs = 0;
        for (int s = 0; s < 10; s++)
        {
            var (duty, _, _) = linearFollower.EvaluateClosedLoop(65, 50, laggingRpm, 3100, profile, cal, nowMs);
            // Smooth responsive plant below target (simulating -5% drag recovering slowly)
            laggingRpm = Math.Min(3200, laggingRpm + (duty >= 19 ? 45 : 20));
            nowMs += 1000;
        }
        Assert.IsGreaterThan(0.5, linearFollower.LastPiTrimCpuDuty, "PI trim must still engage when fan genuinely lags off-gear.");
    }

    [TestMethod]
    public void EvaluateClosedLoop_DynamicSlopeInversionGuard_DoesNotLockOnHighDutyTachGlitch()
    {
        var cal = new FanCalibrationData
        {
            CalibratedAtUtc = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc),
            Cpu = new SingleFanCalibration
            {
                Points = [new(0, 0), new(10, 2200), new(45, 3600), new(100, 5800)]
            },
            Gpu = new SingleFanCalibration
            {
                Points = [new(0, 0), new(10, 2200), new(45, 3600), new(100, 5800)]
            }
        };
        cal.Normalize();

        var profile = new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            RampUpSeconds = 0,
            RampDownSeconds = 0,
            HysteresisCelsius = 0,
            MinRpmFloor = 2200,
            UseEcAutoBelowMin = false,
            CpuPoints = [new(40, 2200), new(70, 3560), new(71, 3600), new(90, 5800)],
            GpuPoints = [new(40, 2200), new(90, 5800)]
        };

        var follower = new FanCurveFollower();
        long t = 0;

        // Establish steady 45% duty at 71C (3600 RPM).
        var (d1, _, _) = follower.EvaluateClosedLoop(71, 40, 3600, 2200, profile, cal, t);
        Assert.AreEqual(45, d1);

        // Drop 45% -> 44% at 70C (3560 RPM).
        t += 1000;
        var (d2, _, _) = follower.EvaluateClosedLoop(70, 40, 3600, 2200, profile, cal, t);
        Assert.AreEqual(44, d2);

        // Next sample has a +200 RPM transient tach glitch (3600 -> 3800 RPM).
        // Must NEVER lock RuntimeMinCpuDuty at 45%!
        t += 1000;
        var (d3, _, _) = follower.EvaluateClosedLoop(70, 40, 3800, 2200, profile, cal, t);
        Assert.IsFalse(follower.SlopeInversionTriggered, "High-duty 45% -> 44% tach glitch must not trigger slope-inversion lock.");
        Assert.AreEqual(0, follower.RuntimeMinCpuDuty);
        Assert.IsLessThanOrEqualTo(44, d3);

        // Cooling down to 40C (after 0.85s temp EMA settles) reaches 10% quiet duty smoothly without being pinned at 45%.
        int dCool = d3;
        for (int i = 0; i < 6; i++)
        {
            t += 1000;
            (dCool, _, _) = follower.EvaluateClosedLoop(40, 40, 2200, 2200, profile, cal, t);
        }
        Assert.AreEqual(10, dCool);
    }

    [TestMethod]
    public void EvaluateClosedLoop_DynamicSlopeInversionGuard_CancelsWhenDutyRampsBackUpInsideWindow()
    {
        var calWithoutBlackout = new FanCalibrationData
        {
            CalibratedAtUtc = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc),
            Cpu = new SingleFanCalibration
            {
                Points = [new(0, 0), new(2, 1600), new(10, 2220), new(50, 3820), new(100, 5820)]
            },
            Gpu = new SingleFanCalibration
            {
                Points = [new(0, 0), new(2, 1600), new(10, 2220), new(50, 3820), new(100, 5820)]
            }
        };
        calWithoutBlackout.Normalize();

        var profile = new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            RampUpSeconds = 0,
            RampDownSeconds = 0,
            HysteresisCelsius = 0,
            MinRpmFloor = 2000,
            UseEcAutoBelowMin = false,
            CpuPoints = [new(40, 2000), new(55, 2220), new(80, 4500)],
            GpuPoints = [new(40, 2000), new(80, 4500)]
        };

        var follower = new FanCurveFollower();
        // Start at 55C -> 10% duty.
        var (d0, _, _) = follower.EvaluateClosedLoop(55, 40, 2220, 2220, profile, calWithoutBlackout, 0);
        Assert.AreEqual(10, d0);

        // Drop 10% -> 7% at 40C (arms drop window at t=1000..4000).
        var (d1, _, _) = follower.EvaluateClosedLoop(40, 40, 2220, 2220, profile, calWithoutBlackout, 1000);
        Assert.IsLessThan(10, d1);

        // Temperature spikes at t=2000 -> duty climbs back up above 10%.
        var (d2, _, _) = follower.EvaluateClosedLoop(80, 40, 2220, 2220, profile, calWithoutBlackout, 2000);
        Assert.IsGreaterThanOrEqualTo(25, d2);

        // At t=3000 RPM responds by rising +1800 RPM from the spin-up: must NOT trigger slope inversion!
        follower.EvaluateClosedLoop(80, 40, 4000, 2220, profile, calWithoutBlackout, 3000);
        Assert.IsFalse(follower.SlopeInversionTriggered);
        Assert.AreEqual(0, follower.RuntimeMinCpuDuty);
    }

    [TestMethod]
    public void EvaluateClosedLoop_CooldownFrom4000RpmToEcAuto_RampsToMinQuietDutyWithoutIntegralDwell()
    {
        var cal = new FanCalibrationData
        {
            CalibratedAtUtc = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc),
            Cpu = new SingleFanCalibration
            {
                BlackoutMaxDuty = 9,
                MinQuietDuty = 10,
                MinQuietRpm = 2220,
                EcAutoBaselineRpm = 1800,
                Points = [new(0, 0), new(10, 2220), new(50, 4000), new(100, 6200)]
            },
            Gpu = new SingleFanCalibration
            {
                BlackoutMaxDuty = 9,
                MinQuietDuty = 10,
                MinQuietRpm = 2220,
                EcAutoBaselineRpm = 1800,
                Points = [new(0, 0), new(10, 2220), new(50, 4000), new(100, 6200)]
            }
        };
        cal.Normalize();

        var profile = new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            RampUpSeconds = 0,
            RampDownSeconds = 4,
            HysteresisCelsius = 2,
            MinRpmFloor = 1800,
            UseEcAutoBelowMin = true,
            SafeQuietIdleBase = true,
            CpuPoints = [new(59, 0), new(60, 2220), new(75, 4000), new(90, 6200)],
            GpuPoints = [new(59, 0), new(60, 2220), new(75, 4000), new(90, 6200)]
        };

        var follower = new FanCurveFollower();
        long t = 0;

        // Run hot at 75C (4000 RPM target) while lagging at 3500 RPM to accumulate positive integral.
        for (int i = 0; i < 8; i++)
        {
            follower.EvaluateClosedLoop(75, 45, 3500, 1800, profile, cal, t);
            t += 1000;
        }
        Assert.IsGreaterThan(1.0, follower.LastCpuIntegral);

        // Now cool to 50C (0 RPM / EC Auto target) with RampDown = 4s.
        var nonZeroDuties = new List<int>();
        bool reachedEcAuto = false;
        int simRpm = 4000;
        for (int i = 0; i < 15; i++)
        {
            var (cpu, _, _) = follower.EvaluateClosedLoop(50, 45, simRpm, 1800, profile, cal, t);
            if (cpu == 0)
            {
                reachedEcAuto = true;
                Assert.IsTrue(follower.LastCpuEcAuto);
                break;
            }
            Assert.IsGreaterThanOrEqualTo(10, cpu);
            if (nonZeroDuties.Count > 0)
                Assert.IsLessThanOrEqualTo(nonZeroDuties[^1], cpu);
            nonZeroDuties.Add(cpu);
            simRpm = 2220 + (cpu - 10) * 44;
            t += 1000;
        }

        Assert.IsTrue(reachedEcAuto);
        Assert.IsNotEmpty(nonZeroDuties);
        Assert.AreEqual(10, nonZeroDuties[^1], "Must reach MinQuietDuty (10%) cleanly before transitioning to 0% EC Auto.");
    }

    [TestMethod]
    public void EvaluateClosedLoop_FanLogTrace_140105To140110_NeverLocks11Percent()
    {
        var calWithoutBlackout = new FanCalibrationData
        {
            CalibratedAtUtc = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc),
            Cpu = new SingleFanCalibration
            {
                Points = [new(0, 0), new(2, 1600), new(10, 2220), new(11, 2885), new(50, 4000), new(100, 6200)]
            },
            Gpu = new SingleFanCalibration
            {
                Points = [new(0, 0), new(2, 1600), new(10, 2220), new(11, 2885), new(50, 4000), new(100, 6200)]
            }
        };
        calWithoutBlackout.Normalize();

        var profile = new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            RampUpSeconds = 2,
            RampDownSeconds = 4,
            HysteresisCelsius = 2,
            MinRpmFloor = 2200,
            UseEcAutoBelowMin = true,
            SafeQuietIdleBase = true,
            CpuPoints = [new(59, 0), new(60, 2500), new(75, 4200), new(90, 6000)],
            GpuPoints = [new(59, 0), new(60, 2500), new(75, 4200), new(90, 6000)]
        };

        // Exact 14:01:01 -> 14:01:10 trace from dist/fanlog.csv:
        // 14:01:05 spikes 68C (exits Auto to 11%), 14:01:06 drops to 10%, 14:01:07 tach reports 2777 RPM from spin-up lag.
        (int CpuTemp, int GpuTemp, int CpuRpm, int GpuRpm)[] trace =
        [
            (52, 43, 2479, 2564), // 14:01:01
            (59, 44, 2459, 2564), // 14:01:02
            (55, 44, 2459, 2586), // 14:01:03
            (56, 44, 2439, 2586), // 14:01:04
            (68, 44, 2459, 2542), // 14:01:05
            (51, 44, 2439, 2255), // 14:01:06
            (54, 44, 2777, 2173), // 14:01:07
            (49, 44, 2777, 2173), // 14:01:08
            (56, 44, 2777, 2173), // 14:01:09
            (54, 45, 2777, 2158), // 14:01:10
        ];

        var follower = new FanCurveFollower();
        long t = 0;
        foreach (var sample in trace)
        {
            follower.EvaluateClosedLoop(
                sample.CpuTemp,
                sample.GpuTemp,
                sample.CpuRpm,
                sample.GpuRpm,
                profile,
                calWithoutBlackout,
                t);
            Assert.AreEqual(0, follower.RuntimeMinCpuDuty, $"RuntimeMinCpuDuty must never lock at 11% (got {follower.RuntimeMinCpuDuty}%).");
            Assert.IsFalse(follower.SlopeInversionTriggered, "Single spin-up RPM lag after Auto exit must not trigger slope inversion.");
            t += 1000;
        }
    }

    [TestMethod]
    public void EvaluateClosedLoop_DynamicSlopeInversionGuard_RequiresThreeConsecutiveElevatedSamples()
    {
        var calWithoutBlackout = new FanCalibrationData
        {
            CalibratedAtUtc = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc),
            Cpu = new SingleFanCalibration
            {
                Points = [new(0, 0), new(2, 1600), new(10, 2220), new(50, 3820), new(100, 5820)]
            },
            Gpu = new SingleFanCalibration
            {
                Points = [new(0, 0), new(2, 1600), new(10, 2220), new(50, 3820), new(100, 5820)]
            }
        };
        calWithoutBlackout.Normalize();

        var profile = new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            RampUpSeconds = 0,
            RampDownSeconds = 0,
            HysteresisCelsius = 0,
            MinRpmFloor = 2000,
            UseEcAutoBelowMin = false,
            CpuPoints = [new(40, 2000), new(55, 2220), new(80, 4500)],
            GpuPoints = [new(40, 2000), new(80, 4500)]
        };

        var follower = new FanCurveFollower();
        long t = 0;

        // Establish 3 settled ticks at 10% (2220 RPM).
        for (int i = 0; i < 3; i++)
        {
            var (d, _, _) = follower.EvaluateClosedLoop(55, 40, 2220, 2220, profile, calWithoutBlackout, t);
            Assert.AreEqual(10, d);
            t += 1000;
        }

        // Drop 10% -> <= 9% at 40C (arms drop window).
        var (dropDuty, _, _) = follower.EvaluateClosedLoop(40, 40, 2220, 2220, profile, calWithoutBlackout, t);
        Assert.IsLessThan(10, dropDuty);
        Assert.IsFalse(follower.SlopeInversionTriggered);
        t += 1000;

        // 2 elevated samples (>= +150 RPM) followed by normal RPM resets DropRiseStreak and does NOT lock.
        follower.EvaluateClosedLoop(40, 40, 2450, 2220, profile, calWithoutBlackout, t);
        Assert.IsFalse(follower.SlopeInversionTriggered);
        t += 1000;
        follower.EvaluateClosedLoop(40, 40, 2450, 2220, profile, calWithoutBlackout, t);
        Assert.IsFalse(follower.SlopeInversionTriggered);
        t += 1000;
        follower.EvaluateClosedLoop(40, 40, 2220, 2220, profile, calWithoutBlackout, t);
        Assert.IsFalse(follower.SlopeInversionTriggered);
        Assert.AreEqual(0, follower.RuntimeMinCpuDuty);
        t += 1000;

        // Reset and settle at 55C (10%, 2220 RPM), then drop 10% -> <= 9% and sustain 3 consecutive samples >= +150 RPM.
        follower.Reset();
        for (int i = 0; i < 3; i++)
        {
            var (d, _, _) = follower.EvaluateClosedLoop(55, 40, 2220, 2220, profile, calWithoutBlackout, t);
            Assert.AreEqual(10, d);
            t += 1000;
        }
        var (secondDropDuty, _, _) = follower.EvaluateClosedLoop(40, 40, 2220, 2220, profile, calWithoutBlackout, t);
        Assert.IsLessThan(10, secondDropDuty);
        t += 1000;

        // Sample 1 and 2 stay unlocked; sample 3 locks RuntimeMinCpuDuty = 10 safely.
        follower.EvaluateClosedLoop(40, 40, 2450, 2220, profile, calWithoutBlackout, t);
        Assert.IsFalse(follower.SlopeInversionTriggered);
        t += 1000;
        follower.EvaluateClosedLoop(40, 40, 2450, 2220, profile, calWithoutBlackout, t);
        Assert.IsFalse(follower.SlopeInversionTriggered);
        t += 1000;
        var (lockedDuty, _, _) = follower.EvaluateClosedLoop(40, 40, 2450, 2220, profile, calWithoutBlackout, t);
        Assert.IsTrue(follower.SlopeInversionTriggered);
        Assert.AreEqual(10, follower.RuntimeMinCpuDuty);
        Assert.AreEqual(10, lockedDuty);
    }

    [TestMethod]
    public void EvaluateClosedLoop_SingleSecondRyzenSpike_StaysInEcAutoWithoutChatter()
    {
        var cal = CreateTestCalibration();
        var profile = new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            RampUpSeconds = 2,
            RampDownSeconds = 4,
            HysteresisCelsius = 2,
            MinRpmFloor = 1800,
            UseEcAutoBelowMin = true,
            SafeQuietIdleBase = true,
            CpuPoints = [new(59, 0), new(60, 2220), new(80, 4500)],
            GpuPoints = [new(59, 0), new(60, 2220), new(80, 4500)]
        };

        var follower = new FanCurveFollower();
        long t = 0;

        // 55 -> 66 -> 52 single-second spike (< 60 + 7 = 67C) must stay in 0% EC Auto with zero wake chatter.
        var (d0, _, _) = follower.EvaluateClosedLoop(55, 44, 1800, 1800, profile, cal, t);
        Assert.AreEqual(0, d0);
        Assert.IsTrue(follower.LastCpuEcAuto);

        t += 1000;
        var (d1, _, _) = follower.EvaluateClosedLoop(66, 44, 1800, 1800, profile, cal, t);
        Assert.AreEqual(0, d1);
        Assert.IsTrue(follower.LastCpuEcAuto);

        t += 1000;
        var (d2, _, _) = follower.EvaluateClosedLoop(52, 44, 1800, 1800, profile, cal, t);
        Assert.AreEqual(0, d2);
        Assert.IsTrue(follower.LastCpuEcAuto);

        // Two consecutive ticks at 66C (>= 60C) confirms sustained warmth and exits EC Auto cleanly.
        t += 1000;
        var (d3, _, _) = follower.EvaluateClosedLoop(66, 44, 1800, 1800, profile, cal, t);
        Assert.AreEqual(0, d3);
        Assert.IsTrue(follower.LastCpuEcAuto);

        t += 1000;
        var (d4, _, _) = follower.EvaluateClosedLoop(66, 44, 1800, 1800, profile, cal, t);
        Assert.IsGreaterThanOrEqualTo(10, d4);
        Assert.IsFalse(follower.LastCpuEcAuto);
    }

    [TestMethod]
    public void EvaluateClosedLoop_SleepingDgpu_WithHighGpuCurveThreshold_ExitsEcAutoWhenCpuHot_AndEmergencyFiresInOneTick()
    {
        var cal = CreateTestCalibration();
        var profile = new FanCurveProfile
        {
            ControlMode = FanControlMode.Custom,
            RampUpSeconds = 0,
            RampDownSeconds = 0,
            HysteresisCelsius = 2,
            MinRpmFloor = 1800,
            UseEcAutoBelowMin = true,
            SafeQuietIdleBase = true,
            CpuPoints = [new(59, 0), new(60, 2220), new(85, 5000)],
            GpuPoints = [new(89, 0), new(90, 2220)]
        };

        // 1. Sleeping dGPU (gpuTemp=0) enters EC Auto while CPU is cool, then CPU heats past _heldCpuTemp >= 85C:
        // GPU must exit EC Auto immediately for shared heatsink protection even though gpuPoints[86C] == 0.
        var follower = new FanCurveFollower();
        long t = 0;
        var (c0, g0, _) = follower.EvaluateClosedLoop(50, 0, 1800, 1800, profile, cal, t);
        Assert.AreEqual(0, c0);
        Assert.AreEqual(0, g0);
        Assert.IsTrue(follower.LastGpuEcAuto);

        for (int i = 1; i <= 4; i++)
        {
            t += 1000;
            (c0, g0, _) = follower.EvaluateClosedLoop(88, 0, 4500, 1800, profile, cal, t);
        }
        Assert.IsFalse(follower.LastCpuEcAuto);
        Assert.IsFalse(follower.LastGpuEcAuto, "Shared heatsink protection at _heldCpuTemp >= 85C must exit GPU EC Auto.");
        Assert.IsGreaterThanOrEqualTo(10, g0);
        Assert.IsGreaterThan(4000.0, follower.LastTargetGpuRpm);

        // 2. Thermal Emergency from cold EC Auto must fire (100, 100) in 1 single tick (AutoExitStreak never blocks it).
        var emFollower = new FanCurveFollower();
        emFollower.EvaluateClosedLoop(50, 0, 1800, 1800, profile, cal, 0);
        Assert.IsTrue(emFollower.LastCpuEcAuto);
        Assert.IsTrue(emFollower.LastGpuEcAuto);

        var (emCpu, emGpu, _) = emFollower.EvaluateClosedLoop(95, 0, 1800, 1800, profile, cal, 1000);
        Assert.IsTrue(emFollower.EmergencyActive);
        Assert.IsFalse(emFollower.LastCpuEcAuto);
        Assert.IsFalse(emFollower.LastGpuEcAuto);
        Assert.AreEqual(100, emCpu);
        Assert.AreEqual(100, emGpu);
    }
}
