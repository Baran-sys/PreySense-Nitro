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
    /// Evaluates the fan curve using continuous EMA temperature smoothing, continuous piecewise-linear
    /// curve interpolation between configured stage points, and imperceptible slew-rate limiting.
    /// Zero percent routes strictly to EC Auto minimum RPM.
    /// </summary>
    public (int CpuPercent, int GpuPercent, int Stage) EvaluateSmooth(
        int cpuTemp, int gpuTemp, FanStage[] stages, int delaySeconds = 0, long nowMs = 0)
    {
        if (stages == null || stages.Length == 0)
            return (0, 0, 0);

        bool isFirstSample = double.IsNaN(_currentSmoothCpuDuty);
        double dtSec = !isFirstSample && _lastSmoothMs > 0 && nowMs > _lastSmoothMs
            ? Math.Clamp((nowMs - _lastSmoothMs) / 1000.0, 0.25, 3.0)
            : 1.0;

        if (isFirstSample)
        {
            _smoothedCpuTemp = cpuTemp;
            _smoothedGpuTemp = gpuTemp > 0 ? gpuTemp : 0;
        }
        else
        {
            // dt-normalized exponential moving average filters brief spikes smoothly without integer quantization jumps.
            double tauTempSec = 2.8 + Math.Clamp(delaySeconds, 0, 5) * 0.8;
            double alpha = 1.0 - Math.Exp(-dtSec / tauTempSec);
            _smoothedCpuTemp += alpha * (cpuTemp - _smoothedCpuTemp);
            if (Math.Abs(cpuTemp - _smoothedCpuTemp) < 0.08)
                _smoothedCpuTemp = cpuTemp;

            if (gpuTemp > 0)
            {
                _smoothedGpuTemp = _smoothedGpuTemp <= 0
                    ? gpuTemp
                    : _smoothedGpuTemp + alpha * (gpuTemp - _smoothedGpuTemp);
                if (Math.Abs(gpuTemp - _smoothedGpuTemp) < 0.08)
                    _smoothedGpuTemp = gpuTemp;
            }
            else
            {
                _smoothedGpuTemp = 0;
            }
        }

        double targetCpuDuty, targetGpuDuty;
        int appliedStage;

        bool fixedDutyStages = stages[0].CpuRiseC >= FanStage.DisabledTripCelsius && stages[0].GpuRiseC >= FanStage.DisabledTripCelsius;
        if (fixedDutyStages)
        {
            double shared = Math.Max(stages[0].CpuDutyPercent, stages[0].GpuDutyPercent);
            targetCpuDuty = shared;
            targetGpuDuty = shared;
            appliedStage = 0;
        }
        else
        {
            targetCpuDuty = EvaluateContinuousDuty(_smoothedCpuTemp, stages, gpu: false, out int cpuStage);
            if (gpuTemp > 0)
            {
                targetGpuDuty = EvaluateContinuousDuty(_smoothedGpuTemp, stages, gpu: true, out int gpuStage);
                appliedStage = Math.Max(cpuStage, gpuStage);
            }
            else
            {
                targetGpuDuty = _smoothedCpuTemp >= 85.0 ? stages[Math.Clamp(cpuStage, 0, stages.Length - 1)].GpuDutyPercent : stages[0].GpuDutyPercent;
                appliedStage = cpuStage;
            }
        }

        _lastSmoothMs = nowMs;

        if (isFirstSample)
        {
            _currentSmoothCpuDuty = targetCpuDuty;
            _currentSmoothGpuDuty = targetGpuDuty;
            return (ToFinalDuty(targetCpuDuty), ToFinalDuty(targetGpuDuty), appliedStage);
        }

        _currentSmoothCpuDuty = StepSmoothDuty(_currentSmoothCpuDuty, targetCpuDuty, dtSec);
        _currentSmoothGpuDuty = StepSmoothDuty(_currentSmoothGpuDuty, targetGpuDuty, dtSec);

        return (ToFinalDuty(_currentSmoothCpuDuty), ToFinalDuty(_currentSmoothGpuDuty), appliedStage);
    }

    private static int ToFinalDuty(double duty) =>
        duty <= 0.5 ? 0 : Math.Clamp((int)Math.Round(duty, MidpointRounding.AwayFromZero), 1, 100);

    /// <summary>
    /// Evaluates continuous piecewise-linear target duty along active curve points, hitting each configured
    /// stage point cleanly with no step jumps or asymmetric retreat lag.
    /// </summary>
    public static double EvaluateContinuousDuty(double temp, FanStage[] stages, bool gpu, out int activeStage)
    {
        activeStage = 0;
        if (stages == null || stages.Length == 0)
            return 0.0;

        double leadIn = stages[0].DutyPercent(gpu);
        int count = 0;
        Span<(int EntryC, double Duty, int StageIndex)> points = stackalloc (int, double, int)[stages.Length];

        for (int i = 1; i < stages.Length; i++)
        {
            if (!stages[i - 1].HasActiveRise(gpu))
                break;

            int entryC = stages[i - 1].RiseCelsius(gpu);
            if (count > 0 && entryC <= points[count - 1].EntryC)
                entryC = Math.Min(FanStage.DisabledTripCelsius - 1, points[count - 1].EntryC + 1);

            points[count++] = (entryC, stages[i].DutyPercent(gpu), i);
        }

        if (count == 0 || temp < points[0].EntryC)
            return leadIn;

        if (temp >= points[count - 1].EntryC)
        {
            activeStage = points[count - 1].StageIndex;
            return points[count - 1].Duty;
        }

        for (int i = 0; i < count - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            if (temp >= a.EntryC && temp <= b.EntryC)
            {
                activeStage = a.StageIndex;
                if (a.Duty <= 0.0 && b.Duty <= 0.0)
                    return 0.0;

                double span = Math.Max(1.0, b.EntryC - a.EntryC);
                double t = (temp - a.EntryC) / span;
                return a.Duty + t * (b.Duty - a.Duty);
            }
        }

        activeStage = points[count - 1].StageIndex;
        return points[count - 1].Duty;
    }

    private static double StepSmoothDuty(double currentDuty, double targetDuty, double dtSec)
    {
        const double autoEquivalentDuty = 5.0; // ~2200 RPM hardware Auto floor on 1..100% EC scale
        const double maxRampUpPerSec = 1.5;
        const double maxRampDownPerSec = 1.0;

        if (targetDuty <= 0.5)
        {
            if (currentDuty <= autoEquivalentDuty + 0.5)
                return 0.0;
            return Math.Max(autoEquivalentDuty, currentDuty - maxRampDownPerSec * dtSec);
        }

        if (currentDuty <= 0.5)
            currentDuty = Math.Min(targetDuty, autoEquivalentDuty);

        double diff = targetDuty - currentDuty;
        if (Math.Abs(diff) < 0.05)
            return targetDuty;
        if (diff > 0)
            return Math.Min(targetDuty, currentDuty + maxRampUpPerSec * dtSec);
        return Math.Max(targetDuty, currentDuty - maxRampDownPerSec * dtSec);
    }

    private sealed class ClosedLoopFanState
    {
        public double SmoothedTargetRpm = double.NaN;
        public double Integral;
        public double RefTargetRpm = double.NaN;
        public long LastTargetChangeMs;
        public bool WasEcAuto;
        public int TicksSinceAutoExit;
        public int AutoExitStreak;

        // Slope-inversion guard state
        public int PrevManualDuty;
        public int PrevMeasuredRpm = -1;
        public int PrevPrevMeasuredRpm = -1;
        public int RuntimeMinDuty;
        public bool DropWindowActive;
        public int DropRiseStreak;
        public int PreDropSafeDuty;
        public int PreDropBaselineRpm = -1;
        public long DropWindowStartMs;
        public long DropWindowEndMs;
        public bool SlopeInversionTriggered;

        // EC discrete gear plateau anti-hunt tracker
        public int PlateauHoldDir;
        public long PlateauHoldStartMs;
        public int PlateauStartDuty;
        public int PlateauStartRpm = -1;

        public void Reset(bool resetRuntimeMin = true)
        {
            SmoothedTargetRpm = double.NaN;
            Integral = 0.0;
            RefTargetRpm = double.NaN;
            LastTargetChangeMs = 0;
            WasEcAuto = false;
            TicksSinceAutoExit = 0;
            AutoExitStreak = 0;

            PrevManualDuty = 0;
            PrevMeasuredRpm = -1;
            PrevPrevMeasuredRpm = -1;
            DropWindowActive = false;
            DropRiseStreak = 0;
            PreDropSafeDuty = 0;
            PreDropBaselineRpm = -1;
            DropWindowStartMs = 0;
            DropWindowEndMs = 0;

            PlateauHoldDir = 0;
            PlateauHoldStartMs = 0;
            PlateauStartDuty = 0;
            PlateauStartRpm = -1;

            if (resetRuntimeMin)
            {
                RuntimeMinDuty = 0;
                SlopeInversionTriggered = false;
            }
        }
    }

    private bool _hasClosedLoopSample;
    private long _lastClosedLoopMs;
    private double _filteredCpuTemp = double.NaN;
    private double _filteredGpuTemp = double.NaN;
    private double _heldCpuTemp = double.NaN;
    private double _heldGpuTemp = double.NaN;
    private bool _cpuCoolingActive;
    private bool _gpuCoolingActive;
    private readonly ClosedLoopFanState _cpuLoop = new();
    private readonly ClosedLoopFanState _gpuLoop = new();
    private string? _lastCalibrationSignature;

    public double LastTargetCpuRpm { get; private set; }
    public double LastTargetGpuRpm { get; private set; }
    public double LastFeedforwardCpuDuty { get; private set; }
    public double LastFeedforwardGpuDuty { get; private set; }
    public double LastPiTrimCpuDuty { get; private set; }
    public double LastPiTrimGpuDuty { get; private set; }
    public double LastCpuIntegral => _cpuLoop.Integral;
    public double LastGpuIntegral => _gpuLoop.Integral;
    public bool EmergencyActive { get; private set; }
    public bool LastCpuEcAuto { get; private set; }
    public bool LastGpuEcAuto { get; private set; }
    public bool SlopeInversionTriggered => _cpuLoop.SlopeInversionTriggered || _gpuLoop.SlopeInversionTriggered;
    public int RuntimeMinCpuDuty => _cpuLoop.RuntimeMinDuty;
    public int RuntimeMinGpuDuty => _gpuLoop.RuntimeMinDuty;

    private static string BuildCalibrationSignature(FanCalibrationData? cal)
    {
        if (cal == null)
            return "none";

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{cal.CalibratedAtUtc?.Ticks ?? 0}|{SingleSignature(cal.Cpu)}|{SingleSignature(cal.Gpu)}");

        static string SingleSignature(SingleFanCalibration? s)
        {
            if (s == null) return "null";
            return $"{s.MinQuietDuty}:{s.MinQuietRpm}:{s.MinStableDuty}:{s.MinStableRpm}:{s.BlackoutMaxDuty}:{s.MaxRpm}:{s.Points?.Count ?? 0}";
        }
    }

    public static int ResolveEffectiveMinRpm(int profileMinRpmFloor, SingleFanCalibration? fanCal)
    {
        if (fanCal is { IsCalibrated: true, MinStableRpm: > 0 })
        {
            int upper = Math.Max(fanCal.MinStableRpm, 3000);
            return Math.Clamp(profileMinRpmFloor, fanCal.MinStableRpm, upper);
        }
        return Math.Max(profileMinRpmFloor, FanStage.MinManualRpmFloor);
    }

    /// <summary>
    /// Closed-loop fan controller combining dt-normalized EMA temperature smoothing,
    /// cooldown hysteresis, decoupled ramp-up/ramp-down target RPM shaping,
    /// calibration feedforward, and anti-windup PI RPM trim.
    /// </summary>
    public (int CpuPercent, int GpuPercent, int Stage) EvaluateClosedLoop(
        int cpuTemp,
        int gpuTemp,
        int measuredCpuRpm,
        int measuredGpuRpm,
        FanCurveProfile profile,
        FanCalibrationData? calibration = null,
        long nowMs = 0,
        int minCpuRpmFloorOverride = 0,
        int minGpuRpmFloorOverride = 0)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // Preserve legacy fixed-duty stage tables when no RPM curve points are present.
        bool hasCpuPoints = profile.CpuPoints is { Count: > 0 };
        bool hasGpuPoints = profile.GpuPoints is { Count: > 0 };
        bool legacyFixedDuty = !hasCpuPoints && !hasGpuPoints &&
            profile.Stages is { Length: > 0 } &&
            profile.Stages[0].CpuRiseC >= FanStage.DisabledTripCelsius &&
            profile.Stages[0].GpuRiseC >= FanStage.DisabledTripCelsius;

        string calSignature = BuildCalibrationSignature(calibration);
        if (_lastCalibrationSignature != null && _lastCalibrationSignature != calSignature)
        {
            _cpuLoop.RuntimeMinDuty = 0;
            _cpuLoop.SlopeInversionTriggered = false;
            _cpuLoop.DropWindowActive = false;
            _cpuLoop.DropRiseStreak = 0;
            _gpuLoop.RuntimeMinDuty = 0;
            _gpuLoop.SlopeInversionTriggered = false;
            _gpuLoop.DropWindowActive = false;
            _gpuLoop.DropRiseStreak = 0;
        }
        _lastCalibrationSignature = calSignature;

        UpdateEmergencyState(cpuTemp, gpuTemp);

        bool isFirstSample = !_hasClosedLoopSample;
        double dtSec = !isFirstSample && nowMs > _lastClosedLoopMs
            ? Math.Clamp((nowMs - _lastClosedLoopMs) / 1000.0, 0.05, 5.0)
            : (isFirstSample ? 1.0 : 0.0);
        _hasClosedLoopSample = true;
        _lastClosedLoopMs = nowMs;

        int hystC = Math.Clamp(profile.HysteresisCelsius, 0, 5);
        UpdateFilteredAndHeldTemps(cpuTemp, gpuTemp, isFirstSample, dtSec, hystC);

        int maxCpuRpm = FanCurveProfile.GetMaxRpm(gpu: false, calibration);
        int maxGpuRpm = FanCurveProfile.GetMaxRpm(gpu: true, calibration);

        if (EmergencyActive)
        {
            _cpuLoop.Reset(resetRuntimeMin: false);
            _gpuLoop.Reset(resetRuntimeMin: false);
            _cpuLoop.SmoothedTargetRpm = maxCpuRpm;
            _gpuLoop.SmoothedTargetRpm = maxGpuRpm;
            _cpuLoop.RefTargetRpm = maxCpuRpm;
            _gpuLoop.RefTargetRpm = maxGpuRpm;
            _cpuLoop.LastTargetChangeMs = nowMs;
            _gpuLoop.LastTargetChangeMs = nowMs;

            LastTargetCpuRpm = maxCpuRpm;
            LastTargetGpuRpm = maxGpuRpm;
            LastFeedforwardCpuDuty = 100.0;
            LastFeedforwardGpuDuty = 100.0;
            LastPiTrimCpuDuty = 0.0;
            LastPiTrimGpuDuty = 0.0;
            LastCpuEcAuto = false;
            LastGpuEcAuto = false;
            return (100, 100, 0);
        }

        if (legacyFixedDuty)
        {
            int sharedDuty = Math.Clamp(Math.Max(profile.Stages[0].CpuDutyPercent, profile.Stages[0].GpuDutyPercent), 0, 100);
            LastTargetCpuRpm = FanStage.DutyToPhysicalRpm(sharedDuty, maxCpuRpm);
            LastTargetGpuRpm = FanStage.DutyToPhysicalRpm(sharedDuty, maxGpuRpm);
            LastFeedforwardCpuDuty = sharedDuty;
            LastFeedforwardGpuDuty = sharedDuty;
            LastPiTrimCpuDuty = 0.0;
            LastPiTrimGpuDuty = 0.0;
            LastCpuEcAuto = sharedDuty <= 0;
            LastGpuEcAuto = sharedDuty <= 0;
            return (sharedDuty, sharedDuty, 0);
        }

        IReadOnlyList<FanCurvePoint> cpuPoints;
        IReadOnlyList<FanCurvePoint> gpuPoints;
        if (hasCpuPoints || hasGpuPoints)
        {
            (IReadOnlyList<FanCurvePoint>? fallbackCpu, IReadOnlyList<FanCurvePoint>? fallbackGpu) =
                !hasCpuPoints || !hasGpuPoints
                    ? FanCurveProfile.MigrateFromLegacyStages(profile.Stages, calibration)
                    : (null, null);
            cpuPoints = hasCpuPoints ? profile.CpuPoints! : fallbackCpu!;
            gpuPoints = hasGpuPoints ? profile.GpuPoints! : fallbackGpu!;
        }
        else
        {
            (cpuPoints, gpuPoints) = FanCurveProfile.MigrateFromLegacyStages(profile.Stages, calibration);
        }

        int effectiveMinCpuRpm = ResolveEffectiveMinRpm(profile.MinRpmFloor, calibration?.Cpu);
        int effectiveMinGpuRpm = ResolveEffectiveMinRpm(profile.MinRpmFloor, calibration?.Gpu);

        int? cpuFirstNonZeroTemp = FirstNonZeroPointTemp(cpuPoints);
        bool cpuImmediateAutoExit = _cpuLoop.WasEcAuto
            && profile.UseEcAutoBelowMin
            && ((cpuFirstNonZeroTemp.HasValue && cpuTemp >= cpuFirstNonZeroTemp.Value + 7)
                || minCpuRpmFloorOverride > 0);
        if (cpuImmediateAutoExit && cpuFirstNonZeroTemp.HasValue && _filteredCpuTemp < cpuFirstNonZeroTemp.Value)
        {
            _filteredCpuTemp = cpuFirstNonZeroTemp.Value;
            _heldCpuTemp = Math.Max(_heldCpuTemp, cpuFirstNonZeroTemp.Value);
            _cpuCoolingActive = false;
        }

        double rawCpuRpm = EvaluateCurveRpm(_heldCpuTemp, cpuPoints);
        if (cpuImmediateAutoExit && rawCpuRpm <= 0.0)
            rawCpuRpm = EvaluateCurveRpm(cpuTemp, cpuPoints);
        if (minCpuRpmFloorOverride > 0)
            rawCpuRpm = Math.Max(rawCpuRpm, minCpuRpmFloorOverride);
        bool cpuRawCurveZero = EvaluateCurveRpm(cpuTemp, cpuPoints) <= 0.0;

        bool gpuSharedHeatsinkHot = gpuTemp <= 0 && _heldCpuTemp >= 85.0;
        int? gpuFirstNonZeroTemp = FirstNonZeroPointTemp(gpuPoints);
        int effectiveRawGpuTemp = gpuTemp > 0 ? gpuTemp : (gpuSharedHeatsinkHot ? cpuTemp : 0);
        bool gpuImmediateAutoExit = _gpuLoop.WasEcAuto
            && profile.UseEcAutoBelowMin
            && effectiveRawGpuTemp > 0
            && ((gpuSharedHeatsinkHot && rawCpuRpm > 0.0)
                || (gpuFirstNonZeroTemp.HasValue && effectiveRawGpuTemp >= gpuFirstNonZeroTemp.Value + 7)
                || minGpuRpmFloorOverride > 0);
        if (gpuImmediateAutoExit && gpuTemp > 0 && gpuFirstNonZeroTemp.HasValue && _filteredGpuTemp < gpuFirstNonZeroTemp.Value)
        {
            _filteredGpuTemp = gpuFirstNonZeroTemp.Value;
            _heldGpuTemp = Math.Max(_heldGpuTemp, gpuFirstNonZeroTemp.Value);
            _gpuCoolingActive = false;
        }

        double rawGpuRpm = gpuTemp > 0
            ? EvaluateCurveRpm(_heldGpuTemp, gpuPoints)
            : (gpuSharedHeatsinkHot
                ? Math.Max(rawCpuRpm, EvaluateCurveRpm(_heldCpuTemp, gpuPoints))
                : 0.0);
        if (gpuImmediateAutoExit && rawGpuRpm <= 0.0 && effectiveRawGpuTemp > 0)
            rawGpuRpm = EvaluateCurveRpm(effectiveRawGpuTemp, gpuPoints);
        if (minGpuRpmFloorOverride > 0)
            rawGpuRpm = Math.Max(rawGpuRpm, minGpuRpmFloorOverride);
        bool gpuRawCurveZero = gpuTemp > 0
            ? EvaluateCurveRpm(gpuTemp, gpuPoints) <= 0.0
            : (!gpuSharedHeatsinkHot || (cpuRawCurveZero && EvaluateCurveRpm(cpuTemp, gpuPoints) <= 0.0));

        int cpuDuty = EvaluateSingleClosedLoopFan(
            _cpuLoop,
            "CPU",
            cpuTemp,
            cpuFirstNonZeroTemp,
            rawCpuRpm,
            cpuRawCurveZero,
            profile.UseEcAutoBelowMin,
            effectiveMinCpuRpm,
            maxCpuRpm,
            measuredCpuRpm,
            calibration?.Cpu,
            isFirstSample,
            dtSec,
            profile.RampUpSeconds,
            profile.RampDownSeconds,
            nowMs,
            minCpuRpmFloorOverride,
            out double activeTargetCpuRpm,
            out double ffCpu,
            out double trimCpu,
            out bool cpuAuto);

        int gpuDuty = EvaluateSingleClosedLoopFan(
            _gpuLoop,
            "GPU",
            effectiveRawGpuTemp,
            gpuFirstNonZeroTemp,
            rawGpuRpm,
            gpuRawCurveZero,
            profile.UseEcAutoBelowMin,
            effectiveMinGpuRpm,
            maxGpuRpm,
            measuredGpuRpm,
            calibration?.Gpu,
            isFirstSample,
            dtSec,
            profile.RampUpSeconds,
            profile.RampDownSeconds,
            nowMs,
            minGpuRpmFloorOverride,
            out double activeTargetGpuRpm,
            out double ffGpu,
            out double trimGpu,
            out bool gpuAuto,
            forceImmediateAutoExit: gpuSharedHeatsinkHot && rawGpuRpm > 0.0);

        LastTargetCpuRpm = activeTargetCpuRpm;
        LastTargetGpuRpm = activeTargetGpuRpm;
        LastFeedforwardCpuDuty = ffCpu;
        LastFeedforwardGpuDuty = ffGpu;
        LastPiTrimCpuDuty = trimCpu;
        LastPiTrimGpuDuty = trimGpu;
        LastCpuEcAuto = cpuAuto;
        LastGpuEcAuto = gpuAuto;

        return (cpuDuty, gpuDuty, 0);
    }

    public void UpdateEmergencyState(int cpuTemp, int gpuTemp)
    {
        if (cpuTemp >= 95 || (gpuTemp > 0 && gpuTemp >= 87))
        {
            EmergencyActive = true;
            return;
        }

        if (EmergencyActive && cpuTemp <= 88 && (gpuTemp <= 0 || gpuTemp <= 80))
        {
            EmergencyActive = false;
        }
    }

    private void UpdateFilteredAndHeldTemps(int cpuTemp, int gpuTemp, bool isFirstSample, double dtSec, int hystC)
    {
        const double tauTempSec = 1.35;

        if (isFirstSample || double.IsNaN(_filteredCpuTemp))
        {
            _filteredCpuTemp = cpuTemp;
            _heldCpuTemp = cpuTemp;
            _cpuCoolingActive = false;
        }
        else if (dtSec > 0.0)
        {
            double alpha = 1.0 - Math.Exp(-dtSec / tauTempSec);
            _filteredCpuTemp += alpha * (cpuTemp - _filteredCpuTemp);
            if (Math.Abs(cpuTemp - _filteredCpuTemp) <= 0.1)
                _filteredCpuTemp = cpuTemp;

            UpdateSensorHeldTemp(_filteredCpuTemp, hystC, ref _heldCpuTemp, ref _cpuCoolingActive);
        }

        if (gpuTemp > 0)
        {
            if (isFirstSample || double.IsNaN(_filteredGpuTemp) || _filteredGpuTemp <= 0.0)
            {
                _filteredGpuTemp = gpuTemp;
                _heldGpuTemp = gpuTemp;
                _gpuCoolingActive = false;
            }
            else if (dtSec > 0.0)
            {
                double alpha = 1.0 - Math.Exp(-dtSec / tauTempSec);
                _filteredGpuTemp += alpha * (gpuTemp - _filteredGpuTemp);
                if (Math.Abs(gpuTemp - _filteredGpuTemp) <= 0.1)
                    _filteredGpuTemp = gpuTemp;

                UpdateSensorHeldTemp(_filteredGpuTemp, hystC, ref _heldGpuTemp, ref _gpuCoolingActive);
            }
        }
        else
        {
            _filteredGpuTemp = 0.0;
            _heldGpuTemp = 0.0;
            _gpuCoolingActive = false;
        }
    }

    private static void UpdateSensorHeldTemp(double filteredTemp, int hystC, ref double heldTemp, ref bool coolingActive)
    {
        if (hystC <= 0 || filteredTemp >= heldTemp)
        {
            heldTemp = filteredTemp;
            coolingActive = false;
            return;
        }

        if (coolingActive)
        {
            heldTemp = filteredTemp;
            return;
        }

        if (filteredTemp <= heldTemp - hystC)
        {
            heldTemp = filteredTemp;
            coolingActive = true;
        }
    }

    private static int? FirstNonZeroPointTemp(IReadOnlyList<FanCurvePoint> points)
    {
        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].Rpm > 0)
                return points[i].TempC;
        }
        return null;
    }

    private static double EvaluateCurveRpm(double temp, IReadOnlyList<FanCurvePoint> points)
    {
        if (points == null || points.Count == 0)
            return 0.0;
        if (points.Count == 1)
            return Math.Max(0.0, points[0].Rpm);

        var xs = new double[points.Count];
        var ys = new double[points.Count];
        for (int i = 0; i < points.Count; i++)
        {
            xs[i] = points[i].TempC;
            ys[i] = points[i].Rpm;
        }
        return Math.Max(0.0, MonotoneCubicInterpolator.Evaluate(temp, xs, ys));
    }

    private static double StepTargetRpm(
        double currentRpm,
        double desiredRpm,
        double dtSec,
        int rampUpSeconds,
        int rampDownSeconds)
    {
        if (dtSec <= 0.0)
            return currentRpm;

        double diff = desiredRpm - currentRpm;
        if (Math.Abs(diff) <= 15.0)
            return desiredRpm;

        bool rampingUp = diff > 0.0;
        int rampSec = rampingUp ? Math.Clamp(rampUpSeconds, 0, 10) : Math.Clamp(rampDownSeconds, 0, 10);
        if (rampSec <= 0)
            return desiredRpm;

        double tauRpm = rampingUp
            ? Math.Max(0.22, rampSec * 0.18)
            : Math.Max(0.25, rampSec * 0.30);

        double alpha = 1.0 - Math.Exp(-dtSec / tauRpm);
        double step = diff * alpha;

        double maxRatePerSec = rampingUp
            ? Math.Min(600.0, 1200.0 / rampSec)
            : Math.Min(600.0, 2400.0 / rampSec);
        double maxStep = maxRatePerSec * dtSec;
        if (Math.Abs(step) > maxStep)
            step = Math.Sign(step) * maxStep;

        return currentRpm + step;
    }

    private static double PhysicalRpmToDutyDouble(double rpm, int maxRpm)
    {
        if (rpm <= 0.0) return 0.0;
        double span = Math.Max(1.0, maxRpm - FanStage.MinManualRpmFloor);
        double clamped = Math.Clamp(rpm, FanStage.MinManualRpmFloor, maxRpm);
        return Math.Clamp((clamped - FanStage.MinManualRpmFloor) * 100.0 / span, 1.0, 100.0);
    }

    private static int EvaluateSingleClosedLoopFan(
        ClosedLoopFanState state,
        string fanLabel,
        int rawTemp,
        int? firstNonZeroTempC,
        double rawRpm,
        bool rawCurveIsZeroAtRawTemp,
        bool useEcAutoBelowMin,
        int effectiveMinRpm,
        int maxRpm,
        int measuredRpm,
        SingleFanCalibration? fanCal,
        bool isFirstSample,
        double dtSec,
        int rampUpSeconds,
        int rampDownSeconds,
        long nowMs,
        int minRpmFloorOverride,
        out double activeTargetRpmOut,
        out double feedforwardDuty,
        out double piTrimDuty,
        out bool isEcAuto,
        bool forceImmediateAutoExit = false)
    {
        bool wantEcAuto = useEcAutoBelowMin && rawRpm <= 0.0;
        if (state.WasEcAuto && useEcAutoBelowMin)
        {
            if (wantEcAuto || (rawCurveIsZeroAtRawTemp && minRpmFloorOverride <= 0))
            {
                state.AutoExitStreak = 0;
                wantEcAuto = true;
            }
            else
            {
                bool immediateExit = forceImmediateAutoExit
                    || minRpmFloorOverride > 0
                    || !firstNonZeroTempC.HasValue
                    || rawTemp >= firstNonZeroTempC.Value + 7;
                if (immediateExit)
                {
                    state.AutoExitStreak = 2;
                    wantEcAuto = false;
                }
                else
                {
                    state.AutoExitStreak++;
                    wantEcAuto = state.AutoExitStreak < 2;
                }
            }
        }
        else if (wantEcAuto)
        {
            state.AutoExitStreak = 0;
        }

        double clampedActiveRpm = Math.Clamp(rawRpm, effectiveMinRpm, maxRpm);

        if (isFirstSample || double.IsNaN(state.SmoothedTargetRpm))
        {
            state.SmoothedTargetRpm = wantEcAuto ? 0.0 : clampedActiveRpm;
        }
        else if (wantEcAuto)
        {
            if (state.WasEcAuto)
            {
                state.SmoothedTargetRpm = 0.0;
            }
            else
            {
                state.SmoothedTargetRpm = StepTargetRpm(
                    state.SmoothedTargetRpm,
                    effectiveMinRpm,
                    dtSec,
                    rampUpSeconds,
                    rampDownSeconds);
            }
        }
        else
        {
            if (state.WasEcAuto)
            {
                double seedBase = measuredRpm > 0 ? measuredRpm : effectiveMinRpm;
                state.SmoothedTargetRpm = Math.Clamp(Math.Max(seedBase, effectiveMinRpm), effectiveMinRpm, maxRpm);
            }

            state.SmoothedTargetRpm = StepTargetRpm(
                state.SmoothedTargetRpm,
                clampedActiveRpm,
                dtSec,
                rampUpSeconds,
                rampDownSeconds);
        }

        double desiredForRampCheck = wantEcAuto ? effectiveMinRpm : clampedActiveRpm;
        return ComputeClosedLoopDuty(
            state,
            fanLabel,
            desiredForRampCheck,
            wantEcAuto,
            effectiveMinRpm,
            maxRpm,
            measuredRpm,
            fanCal,
            dtSec,
            nowMs,
            out activeTargetRpmOut,
            out feedforwardDuty,
            out piTrimDuty,
            out isEcAuto);
    }

    private static bool IsHoldingCalibratedGearPlateau(
        SingleFanCalibration? fanCal,
        double activeTargetRpm,
        int measuredRpm)
    {
        if (fanCal is not { IsCalibrated: true } || fanCal.Points is not { Count: >= 3 } pts)
            return false;

        for (int i = 0; i < pts.Count - 1; i++)
        {
            var a = pts[i];
            var b = pts[i + 1];
            if (a.Duty <= 0 || b.Rpm - a.Rpm < 250 || b.Duty - a.Duty > 4)
                continue;

            bool aPlateau = i > 0 && pts[i - 1].Duty > 0 && a.Duty - pts[i - 1].Duty >= 2 && a.Rpm - pts[i - 1].Rpm <= 50;
            bool bPlateau = i + 2 < pts.Count && pts[i + 2].Duty - b.Duty >= 2 && pts[i + 2].Rpm - b.Rpm <= 50;
            if (!aPlateau && !bPlateau)
                continue;

            if (activeTargetRpm > a.Rpm + 40 && activeTargetRpm < b.Rpm - 40)
            {
                int closerGearRpm = Math.Abs(activeTargetRpm - a.Rpm) <= Math.Abs(b.Rpm - activeTargetRpm)
                    ? a.Rpm
                    : b.Rpm;
                if (Math.Abs(measuredRpm - closerGearRpm) <= 60)
                    return true;
            }
        }

        int runStart = 0;
        for (int i = 1; i <= pts.Count; i++)
        {
            bool continues = i < pts.Count && pts[i].Duty > 0 && pts[runStart].Duty > 0 &&
                Math.Abs(pts[i].Rpm - pts[runStart].Rpm) <= 40;
            if (!continues)
            {
                int runEnd = i - 1;
                if (pts[runStart].Duty > 0 && pts[runEnd].Duty - pts[runStart].Duty >= 2)
                {
                    int plateauRpm = pts[runStart].Rpm;
                    if (Math.Abs(measuredRpm - plateauRpm) <= 40 && Math.Abs(activeTargetRpm - plateauRpm) <= 150)
                        return true;
                }
                runStart = i;
            }
        }

        return false;
    }

    private static int ComputeClosedLoopDuty(
        ClosedLoopFanState state,
        string fanLabel,
        double desiredRpm,
        bool wantEcAuto,
        int effectiveMinRpm,
        int maxRpm,
        int measuredRpm,
        SingleFanCalibration? fanCal,
        double dtSec,
        long nowMs,
        out double activeTargetRpmOut,
        out double feedforwardDuty,
        out double piTrimDuty,
        out bool isEcAuto)
    {
        bool wasEcAuto = state.WasEcAuto;
        if (wantEcAuto && (state.WasEcAuto || state.SmoothedTargetRpm <= effectiveMinRpm + 15.0))
        {
            isEcAuto = true;
            activeTargetRpmOut = 0.0;
            feedforwardDuty = 0.0;
            piTrimDuty = 0.0;
            state.SmoothedTargetRpm = 0.0;
            state.Integral = 0.0;
            state.RefTargetRpm = 0.0;
            state.WasEcAuto = true;
            state.TicksSinceAutoExit = 0;
            state.PrevManualDuty = 0;
            state.PrevPrevMeasuredRpm = state.PrevMeasuredRpm;
            state.PrevMeasuredRpm = measuredRpm;
            state.DropWindowActive = false;
            state.DropRiseStreak = 0;
            state.PlateauHoldDir = 0;
            return 0;
        }

        if (wantEcAuto && state.Integral > 0.0)
            state.Integral = 0.0;

        isEcAuto = false;
        state.WasEcAuto = false;
        state.TicksSinceAutoExit = wasEcAuto ? 1 : state.TicksSinceAutoExit + 1;
        state.AutoExitStreak = 0;
        double activeTargetRpm = Math.Clamp(state.SmoothedTargetRpm, effectiveMinRpm, maxRpm);
        activeTargetRpmOut = activeTargetRpm;

        int baseMinDuty;
        if (fanCal is { IsCalibrated: true })
        {
            int calMinDuty = fanCal.MinQuietDuty > 0 ? fanCal.MinQuietDuty : fanCal.MinStableDuty;
            int blackoutMinDuty = fanCal.BlackoutMaxDuty > 0 ? fanCal.BlackoutMaxDuty + 1 : 1;
            baseMinDuty = Math.Clamp(Math.Max(calMinDuty, blackoutMinDuty), 1, 100);
            feedforwardDuty = fanCal.RpmToDutyDouble(activeTargetRpm, maxRpm);
        }
        else
        {
            baseMinDuty = 1;
            feedforwardDuty = PhysicalRpmToDutyDouble(activeTargetRpm, maxRpm);
        }

        int minManualDuty = Math.Clamp(Math.Max(baseMinDuty, state.RuntimeMinDuty), 1, 100);
        feedforwardDuty = Math.Clamp(feedforwardDuty, minManualDuty, 100.0);

        if (double.IsNaN(state.RefTargetRpm))
        {
            state.RefTargetRpm = activeTargetRpm;
            state.LastTargetChangeMs = nowMs;
        }
        else if (Math.Abs(activeTargetRpm - state.RefTargetRpm) > 100.0)
        {
            state.RefTargetRpm = activeTargetRpm;
            state.LastTargetChangeMs = nowMs;
        }

        bool targetRamping = Math.Abs(desiredRpm - state.SmoothedTargetRpm) > 25.0;
        bool freezeIntegral = targetRamping || (nowMs - state.LastTargetChangeMs) < 2000L;

        const double kp = 0.004;
        const double ki = 0.0025;
        const double maxTrim = 8.0;
        const double deadbandRpm = 75.0;

        double error = 0.0;
        if (measuredRpm >= 0)
        {
            double rawError = activeTargetRpm - measuredRpm;
            error = Math.Abs(rawError) <= deadbandRpm ? 0.0 : rawError;
        }

        double pTerm = kp * error;
        bool holdCloserGear = false;
        bool plateauFreezeIntegral = false;

        if (measuredRpm >= 0 && error != 0.0 && !targetRamping)
        {
            if (IsHoldingCalibratedGearPlateau(fanCal, activeTargetRpm, measuredRpm))
            {
                holdCloserGear = true;
                plateauFreezeIntegral = true;
            }
            else if (!freezeIntegral)
            {
                int errDir = error > 0.0 ? 1 : -1;
                int previewDuty = Math.Clamp(
                    (int)Math.Round(feedforwardDuty + Math.Clamp(pTerm + state.Integral, -maxTrim, maxTrim), MidpointRounding.AwayFromZero),
                    minManualDuty,
                    100);

                if (state.PlateauHoldDir != errDir || state.PlateauStartRpm < 0 || Math.Abs(measuredRpm - state.PlateauStartRpm) >= 40)
                {
                    state.PlateauHoldDir = errDir;
                    state.PlateauHoldStartMs = nowMs;
                    state.PlateauStartDuty = state.PrevManualDuty > 0 ? state.PrevManualDuty : previewDuty;
                    state.PlateauStartRpm = measuredRpm;
                }
                else if (Math.Abs(previewDuty - state.PlateauStartDuty) >= 2 && (nowMs - state.PlateauHoldStartMs) >= 4000L)
                {
                    plateauFreezeIntegral = true;
                }
            }
            else
            {
                state.PlateauHoldDir = 0;
            }
        }
        else
        {
            state.PlateauHoldDir = 0;
        }

        if (!freezeIntegral && !plateauFreezeIntegral && dtSec > 0.0 && error != 0.0)
        {
            double candidateIntegral = Math.Clamp(state.Integral + ki * error * dtSec, -maxTrim, maxTrim);
            double candidateTrim = pTerm + candidateIntegral;
            double candidateTotal = feedforwardDuty + candidateTrim;
            bool saturatedHigh = (candidateTrim > maxTrim || candidateTotal > 100.0) && error > 0.0;
            bool saturatedLow = (candidateTrim < -maxTrim || candidateTotal < minManualDuty) && error < 0.0;
            if (!saturatedHigh && !saturatedLow)
                state.Integral = candidateIntegral;
        }

        piTrimDuty = holdCloserGear
            ? Math.Clamp(state.Integral, -maxTrim, maxTrim)
            : Math.Clamp(pTerm + state.Integral, -maxTrim, maxTrim);

        double totalDuty = Math.Clamp(feedforwardDuty + piTrimDuty, minManualDuty, 100.0);
        int finalDuty = Math.Clamp((int)Math.Round(totalDuty, MidpointRounding.AwayFromZero), minManualDuty, 100);

        int maxDropDestinationDuty = fanCal is { BlackoutMaxDuty: > 0 }
            ? Math.Clamp(fanCal.BlackoutMaxDuty + 1, 1, 15)
            : 9;
        int maxSlopeGuardDuty = fanCal is { BlackoutMaxDuty: > 0 }
            ? Math.Clamp(fanCal.BlackoutMaxDuty + 1, 1, 15)
            : 10;

        if (state.DropWindowActive)
        {
            if (nowMs < state.DropWindowStartMs
                || nowMs > state.DropWindowEndMs
                || state.PreDropSafeDuty > maxSlopeGuardDuty
                || finalDuty >= state.PreDropSafeDuty
                || finalDuty > maxDropDestinationDuty)
            {
                state.DropWindowActive = false;
                state.DropRiseStreak = 0;
            }
            else if (measuredRpm >= 0 && state.PreDropBaselineRpm >= 0)
            {
                if (measuredRpm >= state.PreDropBaselineRpm + 150)
                {
                    if (++state.DropRiseStreak >= 3)
                    {
                        int safeFloor = Math.Clamp(state.PreDropSafeDuty, 1, maxSlopeGuardDuty);
                        state.RuntimeMinDuty = Math.Max(state.RuntimeMinDuty, safeFloor);
                        state.Integral = 0.0;
                        state.DropWindowActive = false;
                        state.DropRiseStreak = 0;
                        state.SlopeInversionTriggered = true;
                        state.PlateauHoldDir = 0;
                        piTrimDuty = 0.0;
                        feedforwardDuty = Math.Clamp(feedforwardDuty, state.RuntimeMinDuty, 100.0);
                        finalDuty = Math.Clamp(
                            (int)Math.Round(feedforwardDuty, MidpointRounding.AwayFromZero),
                            state.RuntimeMinDuty,
                            100);
                        AppLogger.Log(
                            $"[FanControl] Slope inversion detected on {fanLabel}: duty drop from {safeFloor}% sustained RPM {state.PreDropBaselineRpm} -> {measuredRpm} for 3 samples; locking runtime min duty at {state.RuntimeMinDuty}%.");
                    }
                }
                else
                {
                    state.DropRiseStreak = 0;
                    if (measuredRpm <= state.PreDropBaselineRpm - 30
                        && state.PrevManualDuty >= 1
                        && state.PrevManualDuty <= maxSlopeGuardDuty)
                    {
                        state.PreDropSafeDuty = state.PrevManualDuty;
                        state.PreDropBaselineRpm = measuredRpm;
                    }
                }
            }
        }

        if (!state.DropWindowActive
            && !wasEcAuto
            && state.TicksSinceAutoExit >= 3
            && state.PrevMeasuredRpm >= 0
            && state.PrevPrevMeasuredRpm >= 0
            && Math.Abs(state.PrevMeasuredRpm - state.PrevPrevMeasuredRpm) <= 120
            && state.PrevManualDuty >= 1
            && state.PrevManualDuty <= maxSlopeGuardDuty
            && finalDuty <= maxDropDestinationDuty
            && finalDuty < state.PrevManualDuty)
        {
            int baseline = measuredRpm >= 0 ? measuredRpm : state.PrevMeasuredRpm;
            if (baseline >= 0)
            {
                state.DropWindowActive = true;
                state.DropRiseStreak = 0;
                state.PreDropSafeDuty = state.PrevManualDuty;
                state.PreDropBaselineRpm = baseline;
                state.DropWindowStartMs = nowMs;
                state.DropWindowEndMs = nowMs + 5000L;
            }
        }

        state.PrevManualDuty = finalDuty;
        state.PrevPrevMeasuredRpm = state.PrevMeasuredRpm;
        state.PrevMeasuredRpm = measuredRpm;
        return finalDuty;
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

        _hasClosedLoopSample = false;
        _lastClosedLoopMs = 0;
        _filteredCpuTemp = double.NaN;
        _filteredGpuTemp = double.NaN;
        _heldCpuTemp = double.NaN;
        _heldGpuTemp = double.NaN;
        _cpuCoolingActive = false;
        _gpuCoolingActive = false;
        _cpuLoop.Reset(resetRuntimeMin: true);
        _gpuLoop.Reset(resetRuntimeMin: true);
        _lastCalibrationSignature = null;

        LastTargetCpuRpm = 0.0;
        LastTargetGpuRpm = 0.0;
        LastFeedforwardCpuDuty = 0.0;
        LastFeedforwardGpuDuty = 0.0;
        LastPiTrimCpuDuty = 0.0;
        LastPiTrimGpuDuty = 0.0;
        EmergencyActive = false;
        LastCpuEcAuto = false;
        LastGpuEcAuto = false;
    }
}
