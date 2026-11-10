namespace PreySense.Mode;

/// <summary>
/// EC-style stage machine: rise on CPU or GPU trip; retreat when both sensors drop
/// below the previous stage's rise. Optional shared delay holds stage changes.
/// </summary>
public sealed class FanCurveFollower
{
    private int? _stage;
    private int? _pendingStage;
    private long _pendingSince;

    public (int CpuPercent, int GpuPercent, int Stage) Evaluate(
        int cpuTemp, int gpuTemp, FanStage[] stages, int delaySeconds = 0, long nowMs = 0)
    {
        if (stages == null || stages.Length == 0)
            return (0, 0, 0);

        int stage = Math.Clamp(_stage ?? 0, 0, stages.Length - 1);
        int desired = stage;
        while (desired < stages.Length - 1 && ShouldAdvance(cpuTemp, gpuTemp, stages[desired]))
            desired++;
        while (desired > 0 && ShouldRetreat(cpuTemp, gpuTemp, stages, desired))
            desired--;

        int delay = Math.Clamp(delaySeconds, 0, 5);
        if (delay <= 0 || desired == stage)
        {
            _pendingStage = null;
            _stage = desired;
        }
        else
        {
            if (_pendingStage != desired)
            {
                _pendingStage = desired;
                _pendingSince = nowMs;
            }
            if (nowMs - _pendingSince >= delay * 1000L)
            {
                _stage = desired;
                _pendingStage = null;
            }
        }

        int applied = Math.Clamp(_stage ?? desired, 0, stages.Length - 1);
        var current = stages[applied];
        // Curves keep separate CPU/GPU duties; both fans run at the higher of the two.
        int duty = Math.Max(current.CpuDutyPercent, current.GpuDutyPercent);
        return (duty, duty, applied);
    }

    public (int CpuPercent, int GpuPercent, int Stage) EvaluateIndependent(
        int cpuTemp, int gpuTemp, FanStage[] stages, int delaySeconds = 0, long nowMs = 0)
    {
        if (stages == null || stages.Length == 0)
            return (0, 0, 0);

        int cpuStage = EvaluateSingleSensorStage(ref _cpuStage, ref _pendingCpuStage, ref _pendingCpuSince,
            cpuTemp, stages, gpu: false, delaySeconds, nowMs);
        int gpuStage = gpuTemp > 0
            ? EvaluateSingleSensorStage(ref _gpuStage, ref _pendingGpuStage, ref _pendingGpuSince,
                gpuTemp, stages, gpu: true, delaySeconds, nowMs)
            : 0;

        int cpuDuty = stages[cpuStage].CpuDutyPercent;
        bool fixedDutyStages = stages[0].CpuRiseC >= FanStage.DisabledTripCelsius && stages[0].GpuRiseC >= FanStage.DisabledTripCelsius;
        int gpuDuty = gpuTemp > 0
            ? stages[gpuStage].GpuDutyPercent
            : fixedDutyStages
                ? Math.Max(stages[cpuStage].CpuDutyPercent, stages[cpuStage].GpuDutyPercent)
                : (cpuTemp >= 85 ? stages[cpuStage].GpuDutyPercent : stages[0].GpuDutyPercent);
        return (cpuDuty, gpuDuty, Math.Max(cpuStage, gpuStage));
    }

    private int? _cpuStage, _gpuStage;
    private int? _pendingCpuStage, _pendingGpuStage;
    private long _pendingCpuSince, _pendingGpuSince;

    private int EvaluateSingleSensorStage(
        ref int? currentStageField, ref int? pendingStageField, ref long pendingSinceField,
        int temp, FanStage[] stages, bool gpu, int delaySeconds, long nowMs)
    {
        int stage = Math.Clamp(currentStageField ?? 0, 0, stages.Length - 1);
        int desired = stage;
        while (desired < stages.Length - 1)
        {
            int rise = stages[desired].RiseCelsius(gpu);
            if (rise < FanStage.DisabledTripCelsius && temp >= rise)
                desired++;
            else
                break;
        }
        int hyst = Math.Max(0, RetreatHysteresisCelsius);
        while (desired > 0)
        {
            int prevRise = stages[desired - 1].RiseCelsius(gpu);
            if (prevRise >= FanStage.DisabledTripCelsius || temp < (prevRise - hyst))
                desired--;
            else
                break;
        }

        int delay = Math.Clamp(delaySeconds, 0, 5);
        if (delay <= 0 || desired == stage)
        {
            pendingStageField = null;
            currentStageField = desired;
        }
        else
        {
            if (pendingStageField != desired)
            {
                pendingStageField = desired;
                pendingSinceField = nowMs;
            }
            if (nowMs - pendingSinceField >= delay * 1000L)
            {
                currentStageField = desired;
                pendingStageField = null;
            }
        }

        return Math.Clamp(currentStageField ?? desired, 0, stages.Length - 1);
    }

    private static bool ShouldAdvance(int cpu, int gpu, FanStage stage)
    {
        bool cpuTrip = stage.CpuRiseC < FanStage.DisabledTripCelsius && cpu >= stage.CpuRiseC;
        bool gpuTrip = stage.GpuRiseC < FanStage.DisabledTripCelsius && gpu >= stage.GpuRiseC;
        return cpuTrip || gpuTrip;
    }

    /// <summary>
    /// Hysteresis deadband in Celsius below the previous stage's rise before retreating,
    /// preventing rapid fan speed oscillation when temperature hovers near a trip point.
    /// </summary>
    public int RetreatHysteresisCelsius { get; set; } = 3;

    /// <summary>
    /// Leave the current stage once both sensors are below the rise that entered it
    /// minus the retreat hysteresis deadband.
    /// </summary>
    private bool ShouldRetreat(int cpu, int gpu, FanStage[] stages, int stageIndex)
    {
        if (stageIndex <= 0) return false;
        var previous = stages[stageIndex - 1];
        int hyst = Math.Max(0, RetreatHysteresisCelsius);
        bool cpuClear = previous.CpuRiseC >= FanStage.DisabledTripCelsius || cpu < (previous.CpuRiseC - hyst);
        bool gpuClear = previous.GpuRiseC >= FanStage.DisabledTripCelsius || gpu < (previous.GpuRiseC - hyst);
        return cpuClear && gpuClear;
    }

    private double _smoothedCpuTemp = double.NaN;
    private double _smoothedGpuTemp = double.NaN;
    private double _currentSmoothCpuDuty = double.NaN;
    private double _currentSmoothGpuDuty = double.NaN;
    private long _lastSmoothMs;

    /// <summary>
    /// Evaluates the fan curve with EMA temperature smoothing and slew-rate limiting
    /// so fan transitions are gradual and each fan hits its exact configured stage duty/RPM.
    /// </summary>
    public (int CpuPercent, int GpuPercent, int Stage) EvaluateSmooth(
        int cpuTemp, int gpuTemp, FanStage[] stages, int delaySeconds = 0, long nowMs = 0)
    {
        if (stages == null || stages.Length == 0)
            return (0, 0, 0);

        bool isFirstSample = double.IsNaN(_currentSmoothCpuDuty);
        if (isFirstSample)
        {
            _smoothedCpuTemp = cpuTemp;
            _smoothedGpuTemp = gpuTemp;
        }
        else
        {
            // Exponential moving average filters out brief 1-2s temperature spikes.
            const double alpha = 0.35;
            _smoothedCpuTemp += alpha * (cpuTemp - _smoothedCpuTemp);
            if (gpuTemp > 0)
                _smoothedGpuTemp = double.IsNaN(_smoothedGpuTemp) || _smoothedGpuTemp <= 0
                    ? gpuTemp
                    : _smoothedGpuTemp + alpha * (gpuTemp - _smoothedGpuTemp);
            else
                _smoothedGpuTemp = 0;
        }

        int effCpu = (int)Math.Round(_smoothedCpuTemp, MidpointRounding.AwayFromZero);
        int effGpu = (int)Math.Round(_smoothedGpuTemp, MidpointRounding.AwayFromZero);

        int effectiveDelay = delaySeconds > 0 ? delaySeconds : (isFirstSample ? 0 : 1);
        var (targetCpuDuty, targetGpuDuty, appliedStage) = EvaluateIndependent(effCpu, effGpu, stages, effectiveDelay, nowMs);

        // If all stages have DisabledTripCelsius (e.g. FixedDutyStages in tests), preserve shared max duty compatibility.
        if (stages[0].CpuRiseC >= FanStage.DisabledTripCelsius && stages[0].GpuRiseC >= FanStage.DisabledTripCelsius)
        {
            int shared = Math.Max(targetCpuDuty, targetGpuDuty);
            targetCpuDuty = shared;
            targetGpuDuty = shared;
        }

        if (isFirstSample)
        {
            _currentSmoothCpuDuty = targetCpuDuty;
            _currentSmoothGpuDuty = targetGpuDuty;
            _lastSmoothMs = nowMs;
            return (Math.Clamp(targetCpuDuty, 0, 100), Math.Clamp(targetGpuDuty, 0, 100), appliedStage);
        }

        double dtSec = _lastSmoothMs > 0 && nowMs > _lastSmoothMs
            ? Math.Clamp((nowMs - _lastSmoothMs) / 1000.0, 0.25, 3.0)
            : 1.0;
        _lastSmoothMs = nowMs;

        _currentSmoothCpuDuty = StepSmoothDuty(_currentSmoothCpuDuty, targetCpuDuty, dtSec);
        _currentSmoothGpuDuty = StepSmoothDuty(_currentSmoothGpuDuty, targetGpuDuty, dtSec);

        int finalCpu = _currentSmoothCpuDuty <= 0.5
            ? 0
            : Math.Clamp((int)Math.Round(_currentSmoothCpuDuty, MidpointRounding.AwayFromZero), 1, 100);
        int finalGpu = _currentSmoothGpuDuty <= 0.5
            ? 0
            : Math.Clamp((int)Math.Round(_currentSmoothGpuDuty, MidpointRounding.AwayFromZero), 1, 100);
        return (finalCpu, finalGpu, appliedStage);
    }

    private static double StepSmoothDuty(double currentDuty, double targetDuty, double dtSec)
    {
        const double quietAutoEquivalentDuty = 23.0;
        const double maxRampUpPerSec = 3.0;
        const double maxRampDownPerSec = 2.0;

        if (targetDuty <= 0.5)
        {
            if (currentDuty <= quietAutoEquivalentDuty + 1.5)
                return 0.0;
            return Math.Max(quietAutoEquivalentDuty, currentDuty - maxRampDownPerSec * dtSec);
        }

        if (targetDuty >= quietAutoEquivalentDuty && currentDuty < quietAutoEquivalentDuty)
            currentDuty = quietAutoEquivalentDuty;

        double diff = targetDuty - currentDuty;
        if (diff > 0)
            return Math.Min(targetDuty, currentDuty + maxRampUpPerSec * dtSec);
        if (diff < 0)
            return Math.Max(targetDuty, currentDuty - maxRampDownPerSec * dtSec);
        return currentDuty;
    }

    public void Reset()
    {
        _stage = null;
        _pendingStage = null;
        _pendingSince = 0;
        _cpuStage = null;
        _gpuStage = null;
        _pendingCpuStage = null;
        _pendingGpuStage = null;
        _pendingCpuSince = 0;
        _pendingGpuSince = 0;
        _smoothedCpuTemp = double.NaN;
        _smoothedGpuTemp = double.NaN;
        _currentSmoothCpuDuty = double.NaN;
        _currentSmoothGpuDuty = double.NaN;
        _lastSmoothMs = 0;
    }
}
