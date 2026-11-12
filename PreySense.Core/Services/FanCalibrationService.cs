using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PreySense.Mode;

namespace PreySense.Core.Services
{
    public enum FanCalibrationStatus
    {
        Succeeded,
        ThermalAbort,
        Cancelled,
        HardwareError
    }

    public readonly record struct FanCalibrationProgress(
        int StepIndex,
        int TotalSteps,
        int DutyPercent,
        int CpuRpm,
        int GpuRpm,
        int CpuTempC,
        int GpuTempC,
        string StatusMessage);

    public sealed record FanCalibrationResult(
        bool Success,
        FanCalibrationStatus Status,
        FanCalibrationData? Data,
        string Message);

    /// <summary>
    /// Sweeps fan duty steps from 0% to 100%, measures exact tachometer RPMs with multi-sample
    /// stability tracking, enforces thermal safety limits (CPU >= 85C or GPU >= 80C), and
    /// always restores the previous fan mode on completion, cancellation, or abort.
    /// </summary>
    public sealed class FanCalibrationService
    {
        public const int MaxSafeCpuTempC = 85;
        public const int MaxSafeGpuTempC = 80;
        public const int MinSpinningRpmThreshold = 200;
        public const int MaxStableSpreadRpm = 100;
        public const int KickZoneDeltaRpmThreshold = 120;

        public static readonly int[] DutySchedule =
        [
            0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30,
            35, 40, 45, 50, 55, 60, 65, 70, 75, 80, 85, 90, 95, 100
        ];

        public static readonly int[] LowEndDescendingSchedule =
        [
            14, 12, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1
        ];

        public static readonly byte[] IdleProbePowerModes = [0x01, 0x00, 0x06];

        private readonly record struct StepMeasurement(int Duty, int MeanRpm, int Spread);

        private sealed class IdleProbeOutcome
        {
            public byte RecommendedIdleEcMode { get; set; } = 0x01;
            public int QuietAutoCpuRpm { get; set; }
            public int QuietAutoGpuRpm { get; set; }
            public int BalancedAutoCpuRpm { get; set; }
            public int BalancedAutoGpuRpm { get; set; }
            public int EcoAutoCpuRpm { get; set; }
            public int EcoAutoGpuRpm { get; set; }
            public int ManualZeroCpuRpm { get; set; }
            public int ManualZeroGpuRpm { get; set; }
            public int BestAutoSum { get; set; } = int.MaxValue;
        }

        private readonly Func<byte, byte, bool> _setFanSpeeds;
        private readonly Func<(bool Ok, int CpuRpm, int GpuRpm)> _readExactRpms;
        private readonly Func<(int CpuTempC, int GpuTempC)> _readTemperatures;
        private readonly Action _restorePreviousMode;
        private readonly int _settleDelayMs;
        private readonly int _sampleIntervalMs;
        private readonly int _samplesPerStep;
        private readonly Func<int, CancellationToken, Task> _delayAsync;
        private readonly Func<byte, bool>? _setPowerModeForProbe;
        private readonly Func<bool>? _setManualZeroFans;
        private readonly Func<byte>? _getCurrentPowerMode;
        private readonly Action? _invalidateFanTargets;

        public FanCalibrationService(
            IHardwareService hardware,
            Func<(int CpuTempC, int GpuTempC)> readTemperatures,
            Action restorePreviousMode,
            int settleDelayMs = 6000,
            int sampleIntervalMs = 400,
            int samplesPerStep = 3,
            Func<int, CancellationToken, Task>? delayAsync = null)
            : this(
                (hardware ?? throw new ArgumentNullException(nameof(hardware))).SetFanSpeeds,
                () => hardware.TryReadExactFanRpms(out int cpuRpm, out int gpuRpm)
                    ? (true, cpuRpm, gpuRpm)
                    : (false, -1, -1),
                readTemperatures,
                restorePreviousMode,
                settleDelayMs,
                sampleIntervalMs,
                samplesPerStep,
                delayAsync,
                setPowerModeForProbe: hardware.SetPowerMode,
                setManualZeroFans: hardware.TrySetManualZeroFans,
                getCurrentPowerMode: () => hardware.TryGetPowerMode(out byte mode) ? mode : hardware.CurrentPowerMode,
                invalidateFanTargets: hardware.InvalidateFanTargets)
        {
        }

        public FanCalibrationService(
            Func<byte, byte, bool> setFanSpeeds,
            Func<(bool Ok, int CpuRpm, int GpuRpm)> readExactRpms,
            Func<(int CpuTempC, int GpuTempC)> readTemperatures,
            Action restorePreviousMode,
            int settleDelayMs = 6000,
            int sampleIntervalMs = 400,
            int samplesPerStep = 3,
            Func<int, CancellationToken, Task>? delayAsync = null,
            Func<byte, bool>? setPowerModeForProbe = null,
            Func<bool>? setManualZeroFans = null,
            Func<byte>? getCurrentPowerMode = null,
            Action? invalidateFanTargets = null)
        {
            _setFanSpeeds = setFanSpeeds ?? throw new ArgumentNullException(nameof(setFanSpeeds));
            _readExactRpms = readExactRpms ?? throw new ArgumentNullException(nameof(readExactRpms));
            _readTemperatures = readTemperatures ?? throw new ArgumentNullException(nameof(readTemperatures));
            _restorePreviousMode = restorePreviousMode ?? throw new ArgumentNullException(nameof(restorePreviousMode));
            _settleDelayMs = Math.Max(0, settleDelayMs);
            _sampleIntervalMs = Math.Max(0, sampleIntervalMs);
            _samplesPerStep = Math.Max(1, samplesPerStep);
            _delayAsync = delayAsync ?? DefaultDelayAsync;
            _setPowerModeForProbe = setPowerModeForProbe;
            _setManualZeroFans = setManualZeroFans;
            _getCurrentPowerMode = getCurrentPowerMode;
            _invalidateFanTargets = invalidateFanTargets;
        }

        private static Task DefaultDelayAsync(int milliseconds, CancellationToken ct) =>
            milliseconds <= 0 ? Task.CompletedTask : Task.Delay(milliseconds, ct);

        public async Task<FanCalibrationResult> RunAsync(
            IProgress<FanCalibrationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var cpuSweepSteps = new List<StepMeasurement>(DutySchedule.Length);
            var gpuSweepSteps = new List<StepMeasurement>(DutySchedule.Length);
            var cpuDescendingSteps = new List<StepMeasurement>(LowEndDescendingSchedule.Length);
            var gpuDescendingSteps = new List<StepMeasurement>(LowEndDescendingSchedule.Length);

            byte? originalPowerMode = null;
            if (_getCurrentPowerMode != null)
            {
                byte mode = _getCurrentPowerMode();
                if (ProfileManager.IsSupportedMode(mode))
                    originalPowerMode = mode;
            }
            bool powerModeModified = false;

            try
            {
                if (TryCheckThermalAbort(out int initCpuTemp, out int initGpuTemp))
                    return ThermalAbortResult(initCpuTemp, initGpuTemp);

                int idleProbeSteps = (_setPowerModeForProbe != null ? IdleProbePowerModes.Length : 0)
                    + (_setManualZeroFans != null ? 1 : 0);
                int totalSteps = idleProbeSteps + DutySchedule.Length + LowEndDescendingSchedule.Length;

                var idleProbe = new IdleProbeOutcome();
                if (idleProbeSteps > 0)
                {
                    var probeFailure = await RunIdleProbeAsync(
                        idleProbe,
                        totalSteps,
                        originalPowerMode,
                        () => powerModeModified = true,
                        () => powerModeModified = false,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                    if (probeFailure != null)
                        return probeFailure;
                }

                var sweepFailure = await RunSchedulePhaseAsync(
                    DutySchedule,
                    _settleDelayMs,
                    stepOffset: idleProbeSteps,
                    totalSteps,
                    cpuSweepSteps,
                    gpuSweepSteps,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                if (sweepFailure != null)
                    return sweepFailure;

                int descendingSettleMs = _settleDelayMs > 0 ? Math.Max(_settleDelayMs, 8000) : 0;
                var descendingFailure = await RunSchedulePhaseAsync(
                    LowEndDescendingSchedule,
                    descendingSettleMs,
                    stepOffset: idleProbeSteps + DutySchedule.Length,
                    totalSteps,
                    cpuDescendingSteps,
                    gpuDescendingSteps,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                if (descendingFailure != null)
                    return descendingFailure;

                int bestAutoSum = idleProbe.BestAutoSum;
                if (bestAutoSum == int.MaxValue)
                {
                    var cpuZero = cpuSweepSteps.FirstOrDefault(s => s.Duty == 0);
                    var gpuZero = gpuSweepSteps.FirstOrDefault(s => s.Duty == 0);
                    if (cpuZero.MeanRpm >= 0 && gpuZero.MeanRpm >= 0 && (cpuZero.MeanRpm > 0 || gpuZero.MeanRpm > 0))
                        bestAutoSum = cpuZero.MeanRpm + gpuZero.MeanRpm;
                }

                bool preferManualZeroIdle = bestAutoSum < int.MaxValue
                    && idleProbe.ManualZeroCpuRpm >= 1000
                    && idleProbe.ManualZeroGpuRpm >= 1000
                    && (idleProbe.ManualZeroCpuRpm + idleProbe.ManualZeroGpuRpm) < bestAutoSum - 150;

                var data = new FanCalibrationData
                {
                    Cpu = BuildSingleFanCalibration(cpuSweepSteps, cpuDescendingSteps),
                    Gpu = BuildSingleFanCalibration(gpuSweepSteps, gpuDescendingSteps),
                    CalibratedAtUtc = DateTime.UtcNow,
                    RecommendedIdleEcMode = idleProbe.RecommendedIdleEcMode,
                    QuietAutoCpuRpm = idleProbe.QuietAutoCpuRpm,
                    QuietAutoGpuRpm = idleProbe.QuietAutoGpuRpm,
                    BalancedAutoCpuRpm = idleProbe.BalancedAutoCpuRpm,
                    BalancedAutoGpuRpm = idleProbe.BalancedAutoGpuRpm,
                    EcoAutoCpuRpm = idleProbe.EcoAutoCpuRpm,
                    EcoAutoGpuRpm = idleProbe.EcoAutoGpuRpm,
                    ManualZeroCpuRpm = idleProbe.ManualZeroCpuRpm,
                    ManualZeroGpuRpm = idleProbe.ManualZeroGpuRpm,
                    PreferManualZeroIdle = preferManualZeroIdle
                };

                return new FanCalibrationResult(
                    Success: true,
                    Status: FanCalibrationStatus.Succeeded,
                    Data: data,
                    Message: "Fan calibration completed.");
            }
            catch (OperationCanceledException)
            {
                return new FanCalibrationResult(
                    Success: false,
                    Status: FanCalibrationStatus.Cancelled,
                    Data: null,
                    Message: "Fan calibration cancelled.");
            }
            finally
            {
                if (powerModeModified && originalPowerMode is byte restorePowerMode)
                {
                    try { _setPowerModeForProbe?.Invoke(restorePowerMode); } catch { }
                }
                try { _invalidateFanTargets?.Invoke(); } catch { }
                try { _restorePreviousMode(); } catch { }
            }
        }

        private async Task<FanCalibrationResult?> RunIdleProbeAsync(
            IdleProbeOutcome outcome,
            int totalSteps,
            byte? originalPowerMode,
            Action markPowerModeModified,
            Action markPowerModeRestored,
            IProgress<FanCalibrationProgress>? progress,
            CancellationToken cancellationToken)
        {
            int stepNumber = 0;

            if (_setPowerModeForProbe != null)
            {
                bool anyPowerSwitched = false;
                for (int i = 0; i < IdleProbePowerModes.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (TryCheckThermalAbort(out int cpuTemp, out int gpuTemp))
                        return ThermalAbortResult(cpuTemp, gpuTemp);

                    byte probeMode = IdleProbePowerModes[i];
                    stepNumber++;

                    if (!_setPowerModeForProbe(probeMode))
                        continue;

                    anyPowerSwitched = true;
                    markPowerModeModified();
                    _invalidateFanTargets?.Invoke();

                    if (!_setFanSpeeds(0, 0))
                    {
                        return new FanCalibrationResult(
                            Success: false,
                            Status: FanCalibrationStatus.HardwareError,
                            Data: null,
                            Message: $"Calibration failed: unable to set Auto fan mode during idle probe (0x{probeMode:X2}).");
                    }

                    var sample = await SampleSettledStepAsync(_settleDelayMs, cancellationToken).ConfigureAwait(false);
                    if (sample.Failure != null)
                        return sample.Failure;

                    if (sample.CpuMean >= 0 && sample.GpuMean >= 0)
                    {
                        switch (probeMode)
                        {
                            case 0x00:
                                outcome.QuietAutoCpuRpm = sample.CpuMean;
                                outcome.QuietAutoGpuRpm = sample.GpuMean;
                                break;
                            case 0x01:
                                outcome.BalancedAutoCpuRpm = sample.CpuMean;
                                outcome.BalancedAutoGpuRpm = sample.GpuMean;
                                break;
                            case 0x06:
                                outcome.EcoAutoCpuRpm = sample.CpuMean;
                                outcome.EcoAutoGpuRpm = sample.GpuMean;
                                break;
                        }

                        int sum = sample.CpuMean + sample.GpuMean;
                        if (sum < outcome.BestAutoSum)
                        {
                            outcome.BestAutoSum = sum;
                            outcome.RecommendedIdleEcMode = probeMode;
                        }
                    }

                    string modeName = ProfileManager.ModeToProfileName(probeMode);
                    progress?.Report(new FanCalibrationProgress(
                        StepIndex: stepNumber,
                        TotalSteps: totalSteps,
                        DutyPercent: 0,
                        CpuRpm: Math.Max(0, sample.CpuMean),
                        GpuRpm: Math.Max(0, sample.GpuMean),
                        CpuTempC: sample.CpuTempC,
                        GpuTempC: sample.GpuTempC,
                        StatusMessage: $"Idle probe {modeName} Auto: CPU {Math.Max(0, sample.CpuMean)} RPM, GPU {Math.Max(0, sample.GpuMean)} RPM"));
                }

                if (anyPowerSwitched && originalPowerMode is byte origMode)
                {
                    if (_setPowerModeForProbe(origMode))
                        markPowerModeRestored();
                    _invalidateFanTargets?.Invoke();
                }
            }

            if (_setManualZeroFans != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryCheckThermalAbort(out int cpuTemp, out int gpuTemp))
                    return ThermalAbortResult(cpuTemp, gpuTemp);

                stepNumber++;
                if (_setManualZeroFans())
                {
                    var sample = await SampleSettledStepAsync(_settleDelayMs, cancellationToken).ConfigureAwait(false);
                    if (sample.Failure != null)
                        return sample.Failure;

                    if (sample.CpuMean >= 0)
                        outcome.ManualZeroCpuRpm = sample.CpuMean;
                    if (sample.GpuMean >= 0)
                        outcome.ManualZeroGpuRpm = sample.GpuMean;

                    progress?.Report(new FanCalibrationProgress(
                        StepIndex: stepNumber,
                        TotalSteps: totalSteps,
                        DutyPercent: 0,
                        CpuRpm: Math.Max(0, sample.CpuMean),
                        GpuRpm: Math.Max(0, sample.GpuMean),
                        CpuTempC: sample.CpuTempC,
                        GpuTempC: sample.GpuTempC,
                        StatusMessage: $"Idle probe Manual 0%: CPU {Math.Max(0, sample.CpuMean)} RPM, GPU {Math.Max(0, sample.GpuMean)} RPM"));

                    // Force upcoming 0% Auto step in DutySchedule to reset 0xC30009 -> 0x410009.
                    _invalidateFanTargets?.Invoke();
                }
            }

            return null;
        }

        private async Task<(FanCalibrationResult? Failure, int CpuMean, int CpuSpread, int GpuMean, int GpuSpread, int CpuTempC, int GpuTempC)> SampleSettledStepAsync(
            int settleDelayMs,
            CancellationToken cancellationToken)
        {
            if (settleDelayMs > 0)
                await _delayAsync(settleDelayMs, cancellationToken).ConfigureAwait(false);

            var cpuSamples = new List<int>(_samplesPerStep);
            var gpuSamples = new List<int>(_samplesPerStep);
            int cpuTemp = -1;
            int gpuTemp = -1;

            for (int s = 0; s < _samplesPerStep; s++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (s > 0 && _sampleIntervalMs > 0)
                    await _delayAsync(_sampleIntervalMs, cancellationToken).ConfigureAwait(false);

                if (TryCheckThermalAbort(out cpuTemp, out gpuTemp))
                    return (ThermalAbortResult(cpuTemp, gpuTemp), -1, 0, -1, 0, cpuTemp, gpuTemp);

                var (ok, cRpm, gRpm) = _readExactRpms();
                if (ok)
                {
                    if (cRpm >= 0) cpuSamples.Add(cRpm);
                    if (gRpm >= 0) gpuSamples.Add(gRpm);
                }
            }

            var (cpuMean, cpuSpread) = cpuSamples.Count > 0 ? SummarizeSamples(cpuSamples) : (-1, 0);
            var (gpuMean, gpuSpread) = gpuSamples.Count > 0 ? SummarizeSamples(gpuSamples) : (-1, 0);
            return (null, cpuMean, cpuSpread, gpuMean, gpuSpread, cpuTemp, gpuTemp);
        }

        private async Task<FanCalibrationResult?> RunSchedulePhaseAsync(
            int[] schedule,
            int settleDelayMs,
            int stepOffset,
            int totalSteps,
            List<StepMeasurement> cpuSteps,
            List<StepMeasurement> gpuSteps,
            IProgress<FanCalibrationProgress>? progress,
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < schedule.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (TryCheckThermalAbort(out int abortCpuTemp, out int abortGpuTemp))
                    return ThermalAbortResult(abortCpuTemp, abortGpuTemp);

                int duty = schedule[i];
                if (!_setFanSpeeds((byte)duty, (byte)duty))
                {
                    return new FanCalibrationResult(
                        Success: false,
                        Status: FanCalibrationStatus.HardwareError,
                        Data: null,
                        Message: $"Calibration failed: unable to set fan duty to {duty}%.");
                }

                var sample = await SampleSettledStepAsync(settleDelayMs, cancellationToken).ConfigureAwait(false);
                if (sample.Failure != null)
                    return sample.Failure;

                if (sample.CpuMean < 0 && sample.GpuMean < 0)
                {
                    return new FanCalibrationResult(
                        Success: false,
                        Status: FanCalibrationStatus.HardwareError,
                        Data: null,
                        Message: $"Calibration failed: tachometer readback unavailable at {duty}%.");
                }

                if (sample.CpuMean >= 0)
                    cpuSteps.Add(new StepMeasurement(duty, sample.CpuMean, sample.CpuSpread));
                if (sample.GpuMean >= 0)
                    gpuSteps.Add(new StepMeasurement(duty, sample.GpuMean, sample.GpuSpread));

                int stepNumber = stepOffset + i + 1;
                progress?.Report(new FanCalibrationProgress(
                    StepIndex: stepNumber,
                    TotalSteps: totalSteps,
                    DutyPercent: duty,
                    CpuRpm: Math.Max(0, sample.CpuMean),
                    GpuRpm: Math.Max(0, sample.GpuMean),
                    CpuTempC: sample.CpuTempC,
                    GpuTempC: sample.GpuTempC,
                    StatusMessage: $"Step {stepNumber}/{totalSteps}: {duty}% -> CPU {Math.Max(0, sample.CpuMean)} RPM, GPU {Math.Max(0, sample.GpuMean)} RPM"));
            }

            return null;
        }

        private bool TryCheckThermalAbort(out int cpuTempC, out int gpuTempC)
        {
            (cpuTempC, gpuTempC) = _readTemperatures();
            return cpuTempC >= MaxSafeCpuTempC || gpuTempC >= MaxSafeGpuTempC;
        }

        private static FanCalibrationResult ThermalAbortResult(int cpuTempC, int gpuTempC) =>
            new(
                Success: false,
                Status: FanCalibrationStatus.ThermalAbort,
                Data: null,
                Message: $"Calibration aborted for thermal safety (CPU {cpuTempC}C, GPU {gpuTempC}C).");

        private static (int MeanRpm, int SpreadRpm) SummarizeSamples(List<int> samples)
        {
            if (samples.Count == 0)
                return (0, 0);

            int min = samples[0];
            int max = samples[0];
            long sum = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                int val = samples[i];
                if (val < min) min = val;
                if (val > max) max = val;
                sum += val;
            }

            int mean = (int)Math.Round(sum / (double)samples.Count, MidpointRounding.AwayFromZero);
            return (mean, max - min);
        }

        private static SingleFanCalibration BuildSingleFanCalibration(
            List<StepMeasurement> sweepSteps,
            List<StepMeasurement> descendingSteps)
        {
            var cal = new SingleFanCalibration();
            if (sweepSteps.Count == 0 && descendingSteps.Count == 0)
                return cal;

            var merged = new Dictionary<int, StepMeasurement>();
            foreach (var step in sweepSteps)
                merged[step.Duty] = step;

            // Prefer steady descending scan measurements for low duties (1..14%) to avoid
            // spin-up transient illusions and detect EC anti-stall kick zones reliably.
            foreach (var step in descendingSteps)
            {
                if (step.Duty is >= 1 and <= 14)
                    merged[step.Duty] = step;
            }

            if (merged.Count == 0)
                return cal;

            if (merged.TryGetValue(0, out var autoStep) && autoStep.MeanRpm >= 0)
                cal.EcAutoBaselineRpm = autoStep.MeanRpm;

            var orderedSteps = merged.Values.OrderBy(s => s.Duty).ToList();

            var lowEndCandidates = orderedSteps
                .Where(s => s.Duty is >= 1 and <= 14 && s.MeanRpm >= MinSpinningRpmThreshold)
                .ToList();
            var candidates = lowEndCandidates.Count > 0
                ? lowEndCandidates
                : orderedSteps.Where(s => s.Duty >= 1 && s.MeanRpm >= MinSpinningRpmThreshold).ToList();

            if (candidates.Count > 0)
            {
                var stableCandidates = candidates
                    .Where(s => s.Spread < MaxStableSpreadRpm)
                    .ToList();

                var best = stableCandidates.Count > 0
                    ? stableCandidates.OrderBy(s => s.MeanRpm).ThenBy(s => s.Duty).ThenBy(s => s.Spread).First()
                    : candidates.OrderBy(s => s.MeanRpm).ThenBy(s => s.Spread).ThenBy(s => s.Duty).First();

                cal.MinQuietDuty = best.Duty;
                cal.MinQuietRpm = best.MeanRpm;
                cal.MinStableDuty = best.Duty;
                cal.MinStableRpm = best.MeanRpm;

                bool hasKickZone = orderedSteps.Any(s =>
                    s.Duty >= 1 &&
                    s.Duty < best.Duty &&
                    s.MeanRpm > best.MeanRpm + KickZoneDeltaRpmThreshold);
                if (hasKickZone)
                    cal.BlackoutMaxDuty = best.Duty - 1;
            }

            foreach (var step in orderedSteps)
            {
                if (cal.IsDutyInKickZone(step.Duty))
                    continue;

                // On Acer EC firmware, duty=0 engages EC Auto; keep the manual table anchored at (0, 0)
                // while preserving the measured baseline in EcAutoBaselineRpm.
                int rpm = step.Duty == 0 ? 0 : step.MeanRpm;
                cal.Points.Add(new FanCalibrationPoint(step.Duty, rpm));
            }

            cal.NormalizeMonotonic();
            return cal;
        }
    }
}
