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
        // then enters gently near the ~26% Quiet Auto floor and ramps up by at most 3%/sec.
        Assert.AreEqual(0, cpu1);
        Assert.IsLessThanOrEqualTo(29, cpu4, $"Expected gentle initial ramp from quiet floor, got {cpu4}%");
        Assert.IsLessThanOrEqualTo(4, cpu5 - cpu4, $"Step per second after leaving floor should be bounded, got {cpu5 - cpu4}%");
    }
}
