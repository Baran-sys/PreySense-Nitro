using System;
using System.Threading;
using System.Threading.Tasks;
using PreySense.Core.Configuration;
using PreySense.Core.Services;
using PreySense.Mode;

namespace PreySense.Frontend.ViewModels;

public partial class MainViewModel
{
    private static readonly int[] DefaultFanWriteBackoffMs = [200, 500, 1000];

    public string[] FanCurveModeNames { get; } = { "Eco", "Quiet", "Balanced", "Performance", "Turbo" };

    private readonly object _fanControlGate = new();
    private readonly FanCurveFollower _fanFollower = new();
    private FanCurveProfile? _runningFanProfile;
    private byte _runningFanProfileMode = 0xFF;
    private int? _sentCpuFan, _sentGpuFan;
    private bool _sentMaxFanMode, _fanOverrideActive, _autoFanModeApplied, _fanWriteInBackoff, _calibratingFans;
    private bool _fanSnapshotsSubscribed;
    private bool _quietIdleBaseEngaged;
    private byte _quietIdleBaseMode = 0x01;
    private bool _sentManualZero;

    internal int[]? FanWriteBackoffMs { get; set; }
    internal Action<int>? FanRetryWait { get; set; }
    internal Func<DateTime>? FanUtcNow { get; set; }
    internal Func<long>? FanTickMs { get; set; }
    internal Func<FanCalibrationData?>? FanCalibrationProvider { get; set; }
    private DateTime? _nextFanRetryUtc;

    private DateTime? _watchdogCpuDivergeStartUtc;
    private DateTime? _watchdogGpuDivergeStartUtc;
    private DateTime? _lastWatchdogRewriteUtc;
    private double _watchdogCpuRefTargetRpm = double.NaN;
    private double _watchdogGpuRefTargetRpm = double.NaN;

    public event EventHandler<string>? FanNoticeRaised;

    private bool RequiresFanTemperatures
    {
        get
        {
            lock (_fanControlGate)
            {
                return !_calibratingFans &&
                    (_runningFanProfile?.ControlMode == FanControlMode.Custom
                     || IsFanDiagnosticsLogEnabled
                     || _fanWriteInBackoff
                     || _fanFollower.EmergencyActive);
            }
        }
    }

    private void UpdateFanDemand()
    {
        lock (_fanControlGate)
        {
            bool required = !_disposed && RequiresFanTemperatures;
            if (required != _fanSnapshotsSubscribed)
            {
                if (required) TelemetryCoordinator.SnapshotAvailable += OnFanSnapshot;
                else TelemetryCoordinator.SnapshotAvailable -= OnFanSnapshot;
                _fanSnapshotsSubscribed = required;
            }
            TelemetryCoordinator.SetDemand(TelemetryDemand.FanController, required);
        }
    }

    private void OnFanSnapshot(TelemetrySnapshot snapshot)
    {
        if (!_disposed && _fanSnapshotsSubscribed)
            ProcessFanCurves(snapshot.ToHardwareTelemetry());
    }

    private void DisposeFanTelemetry()
    {
        lock (_fanControlGate)
        {
            TelemetryCoordinator.SnapshotAvailable -= OnFanSnapshot;
            _fanSnapshotsSubscribed = false;
            TelemetryCoordinator.SetDemand(TelemetryDemand.FanController, false);
        }
    }

    public int? AppliedCpuFanPercent
    {
        get { lock (_fanControlGate) return !_fanOverrideActive ? null : _sentMaxFanMode ? 100 : _sentCpuFan; }
    }
    public int? AppliedGpuFanPercent
    {
        get { lock (_fanControlGate) return !_fanOverrideActive ? null : _sentMaxFanMode ? 100 : _sentGpuFan; }
    }
    public FanCurveProfile LoadFanProfile(byte mode) => FanCurveStorage.Load(mode);
    public void SaveFanProfile(byte mode, FanCurveProfile profile)
    {
        FanCurveStorage.Save(mode, profile);
        lock (_fanControlGate)
        {
            if (_runningFanProfileMode == mode)
            {
                DisengageQuietIdleBaseIfEngaged(mode);
                ResetFanFollower();
            }
        }
        UpdateFanDemand();
        DiagnosticMessage = $"{ProfileManager.ModeToProfileName(mode)} fan settings saved.";
    }
    private void ResetFanFollower()
    {
        if (_sentManualZero)
            _hw?.InvalidateFanTargets();
        _fanFollower.Reset();
        _runningFanProfile = null;
        _runningFanProfileMode = 0xFF;
        _sentCpuFan = _sentGpuFan = null;
        _sentManualZero = false;
        _sentMaxFanMode = false;
        _fanWriteInBackoff = false;
        _nextFanRetryUtc = null;
        _autoFanModeApplied = false;
        ResetWatchdogState();
        // Keep override ownership until hardware successfully returns to Auto.
    }

    private void ResetWatchdogState()
    {
        _watchdogCpuDivergeStartUtc = null;
        _watchdogGpuDivergeStartUtc = null;
        _lastWatchdogRewriteUtc = null;
        _watchdogCpuRefTargetRpm = double.NaN;
        _watchdogGpuRefTargetRpm = double.NaN;
    }

    private bool ExecuteFanWriteWithRetry(Func<bool> writeAction)
    {
        if (writeAction())
            return true;

        int[] backoffs = FanWriteBackoffMs ?? DefaultFanWriteBackoffMs;
        Action<int> wait = FanRetryWait ?? Thread.Sleep;
        for (int i = 0; i < backoffs.Length; i++)
        {
            int delayMs = backoffs[i];
            if (delayMs > 0)
                wait(delayMs);
            if (_disposed)
                return false;
            _hw.InvalidateFanTargets();
            if (writeAction())
                return true;
        }
        return false;
    }

    private FanCalibrationData? ResolveFanCalibration()
    {
        if (FanCalibrationProvider != null)
            return FanCalibrationProvider();
        try
        {
            return ConfigurationService.Instance.GetCurrent().Fans.Calibration;
        }
        catch
        {
            return null;
        }
    }

    private void DisengageQuietIdleBaseIfEngaged(byte mode)
    {
        if (!_quietIdleBaseEngaged)
            return;

        byte restoreMode = mode is 0x00 or 0x01 or 0x04 or 0x05 or 0x06 ? mode : _runningFanProfileMode;
        if (restoreMode is not (0x00 or 0x01 or 0x04 or 0x05 or 0x06))
            restoreMode = 0x01;

        if (_hw.SetQuietIdleBase(false, restoreMode))
        {
            _quietIdleBaseEngaged = false;
            _quietIdleBaseMode = 0x01;
        }
        else
            AppLogger.Log($"[SafeQuietIdle] Failed to restore native power mode 0x{restoreMode:X2}; will retry.");
    }

    private void ProcessFanCurves(HardwareTelemetry telemetry)
    {
        if (_disposed) return;
        lock (_fanControlGate)
        {
            if (_disposed || _calibratingFans) return;
            DateTime nowUtc = (FanUtcNow ?? (() => DateTime.UtcNow))();
            long nowMs = (FanTickMs ?? (() => Environment.TickCount64))();

            // Mode changes already invalidate this profile through the mode event/apply paths.
            // Avoid another ACPI query on every telemetry sample.
            byte mode = _runningFanProfileMode;
            if (_runningFanProfile == null && !_hw.TryGetPowerMode(out mode)) return;
            if (_runningFanProfileMode != mode || _runningFanProfile == null)
            {
                byte prevMode = _runningFanProfileMode;
                if (_quietIdleBaseEngaged)
                {
                    if (prevMode != 0xFF && prevMode != mode)
                    {
                        if (_hw.SetQuietIdleBase(false, mode))
                        {
                            _quietIdleBaseEngaged = false;
                            _quietIdleBaseMode = 0x01;
                        }
                    }
                    else
                    {
                        DisengageQuietIdleBaseIfEngaged(mode);
                    }
                }
                ResetFanFollower();
                _runningFanProfile = FanCurveStorage.Load(mode);
                _runningFanProfileMode = mode;
                _hw.InvalidateFanTargets();
            }

            FanCalibrationData? cal = ResolveFanCalibration();

            if (_nextFanRetryUtc.HasValue)
            {
                if (nowUtc < _nextFanRetryUtc.Value)
                {
                    LogDiagnosticSample(mode, _runningFanProfile.ControlMode, telemetry, cal);
                    return;
                }
                _nextFanRetryUtc = null;
                _hw.InvalidateFanTargets();
            }

            int gpuTemp = telemetry.GpuTemp is > 0 and <= 125 ? telemetry.GpuTemp : 0;
            bool hasValidTemp = FanCurveProfile.ValidTemperatures(telemetry.CpuTemp, telemetry.GpuTemp);

            if (_runningFanProfile.ControlMode == FanControlMode.Auto)
            {
                DisengageQuietIdleBaseIfEngaged(mode);

                if (hasValidTemp)
                {
                    bool wasEmergency = _fanFollower.EmergencyActive;
                    _fanFollower.UpdateEmergencyState(telemetry.CpuTemp, gpuTemp);
                    if (wasEmergency != _fanFollower.EmergencyActive)
                        UpdateFanDemand();
                }

                if (_fanFollower.EmergencyActive)
                {
                    if (_sentCpuFan != 100 || _sentGpuFan != 100)
                    {
                        if (_sentManualZero)
                            _hw.InvalidateFanTargets();
                        _fanOverrideActive = true;
                        if (ExecuteFanWriteWithRetry(() => _hw.SetFanSpeeds(100, 100)))
                        {
                            if (_fanWriteInBackoff)
                                AppLogger.Log("[FanControl] Recovered Emergency fan override after backoff.");
                            _fanWriteInBackoff = false;
                            _sentCpuFan = 100;
                            _sentGpuFan = 100;
                            _sentManualZero = false;
                            _fanOverrideActive = true;
                            _autoFanModeApplied = _sentMaxFanMode = false;
                            UpdateFanDemand();
                        }
                        else
                        {
                            FanWriteFailed(nowUtc);
                        }
                    }
                    LogDiagnosticSample(mode, _runningFanProfile.ControlMode, telemetry, cal);
                    return;
                }

                if (!_autoFanModeApplied)
                {
                    if (_sentManualZero)
                        _hw.InvalidateFanTargets();
                    if (!ExecuteFanWriteWithRetry(() => _hw.SetFanMode(0, 0)))
                    {
                        FanWriteFailed(nowUtc);
                        LogDiagnosticSample(mode, _runningFanProfile.ControlMode, telemetry, cal);
                        return;
                    }
                    if (_fanWriteInBackoff)
                        AppLogger.Log("[FanControl] Recovered Auto fan mode after backoff.");
                    _fanWriteInBackoff = false;
                    _fanOverrideActive = false;
                    _autoFanModeApplied = true;
                    _sentCpuFan = _sentGpuFan = null;
                    _sentManualZero = false;
                    UpdateFanDemand();
                }
                else if (_fanWriteInBackoff)
                {
                    _fanWriteInBackoff = false;
                    UpdateFanDemand();
                }
                LogDiagnosticSample(mode, _runningFanProfile.ControlMode, telemetry, cal);
                return;
            }
            if (_runningFanProfile.ControlMode == FanControlMode.Max)
            {
                DisengageQuietIdleBaseIfEngaged(mode);

                if (hasValidTemp)
                    _fanFollower.UpdateEmergencyState(telemetry.CpuTemp, gpuTemp);

                if (!_sentMaxFanMode)
                {
                    if (_sentManualZero)
                        _hw.InvalidateFanTargets();
                    _fanOverrideActive = true;
                    if (ExecuteFanWriteWithRetry(() => _hw.SetFanMode(1, 1)))
                    {
                        if (_fanWriteInBackoff)
                            AppLogger.Log("[FanControl] Recovered Max fan mode after backoff.");
                        _fanWriteInBackoff = false;
                        _sentMaxFanMode = _fanOverrideActive = true;
                        _autoFanModeApplied = false;
                        _sentCpuFan = _sentGpuFan = null;
                        _sentManualZero = false;
                        UpdateFanDemand();
                    }
                    else
                    {
                        FanWriteFailed(nowUtc);
                    }
                }
                LogDiagnosticSample(mode, _runningFanProfile.ControlMode, telemetry, cal);
                return;
            }

            if (!hasValidTemp) return;
            int smartBoostCpuFloor = _smartBoost is { IsEnabled: true, CurrentTier: >= 2 } ? 4500 : 0;

            var (cpu, gpu, _) = _fanFollower.EvaluateClosedLoop(
                telemetry.CpuTemp,
                gpuTemp,
                telemetry.CpuRpm,
                telemetry.GpuRpm,
                _runningFanProfile,
                cal,
                nowMs,
                minCpuRpmFloorOverride: smartBoostCpuFloor);

            bool bothFansEcAuto = _runningFanProfile.SafeQuietIdleBase
                && _runningFanProfile.UseEcAutoBelowMin
                && !_fanFollower.EmergencyActive
                && _fanFollower.LastCpuEcAuto
                && _fanFollower.LastGpuEcAuto
                && cpu == 0
                && gpu == 0;

            byte idleEcMode = cal is { IsCalibrated: true, HasIdleProbeData: true }
                ? cal.RecommendedIdleEcMode
                : (byte)0x01;
            if (idleEcMode is not (0x00 or 0x01 or 0x06))
                idleEcMode = 0x01;

            bool shouldEngageQuietIdle = bothFansEcAuto
                && mode != idleEcMode
                && mode is 0x01 or 0x04 or 0x05
                && !(mode is 0x00 or 0x06 && idleEcMode != 0x06);
            bool quietIdleStateChanged = false;
            if (shouldEngageQuietIdle)
            {
                if ((!_quietIdleBaseEngaged || _quietIdleBaseMode != idleEcMode)
                    && _hw.SetQuietIdleBase(true, mode, idleEcMode))
                {
                    _quietIdleBaseEngaged = true;
                    _quietIdleBaseMode = idleEcMode;
                    quietIdleStateChanged = true;
                }
            }
            else if (_quietIdleBaseEngaged)
            {
                DisengageQuietIdleBaseIfEngaged(mode);
                quietIdleStateChanged = !_quietIdleBaseEngaged;
            }

            bool wantManualZero = bothFansEcAuto && cal is { PreferManualZeroIdle: true };

            bool forceWatchdogRewrite = EvaluateWatchdog(
                nowUtc,
                _fanFollower.EmergencyActive,
                cpu,
                gpu,
                gpuTemp,
                _fanFollower.LastTargetCpuRpm,
                _fanFollower.LastTargetGpuRpm,
                telemetry.CpuRpm,
                telemetry.GpuRpm);

            if (!forceWatchdogRewrite
                && !quietIdleStateChanged
                && _sentCpuFan == cpu
                && _sentGpuFan == gpu
                && _sentManualZero == wantManualZero)
            {
                LogDiagnosticSample(mode, _runningFanProfile.ControlMode, telemetry, cal);
                return;
            }

            if (forceWatchdogRewrite)
            {
                AppLogger.Log($"[FanWatchdog] Sustained RPM divergence detected (CPU {telemetry.CpuRpm}/{_fanFollower.LastTargetCpuRpm:F0}, GPU {telemetry.GpuRpm}/{_fanFollower.LastTargetGpuRpm:F0}); invalidating and rewriting EC fan speeds ({cpu}/{gpu}%).");
                _hw.InvalidateFanTargets();
                _lastWatchdogRewriteUtc = nowUtc;
                _watchdogCpuDivergeStartUtc = null;
                _watchdogGpuDivergeStartUtc = null;
            }
            else if (_sentManualZero && !wantManualZero)
            {
                _hw.InvalidateFanTargets();
            }

            _fanOverrideActive = true;
            if (ExecuteFanWriteWithRetry(() => wantManualZero
                ? _hw.TrySetManualZeroFans()
                : _hw.SetFanSpeeds((byte)cpu, (byte)gpu)))
            {
                if (_fanWriteInBackoff)
                    AppLogger.Log("[FanControl] Recovered Custom fan control after backoff.");
                _fanWriteInBackoff = false;
                _sentCpuFan = cpu;
                _sentGpuFan = gpu;
                _sentManualZero = wantManualZero;
                _fanOverrideActive = true;
                _autoFanModeApplied = _sentMaxFanMode = false;
            }
            else
            {
                FanWriteFailed(nowUtc);
            }

            LogDiagnosticSample(mode, _runningFanProfile.ControlMode, telemetry, cal);
        }
    }

    private bool EvaluateWatchdog(
        DateTime nowUtc,
        bool emergencyActive,
        int cpuDuty,
        int gpuDuty,
        int gpuTemp,
        double targetCpuRpm,
        double targetGpuRpm,
        int measuredCpuRpm,
        int measuredGpuRpm)
    {
        if (emergencyActive || !_sentCpuFan.HasValue || !_sentGpuFan.HasValue)
        {
            _watchdogCpuDivergeStartUtc = null;
            _watchdogGpuDivergeStartUtc = null;
            return false;
        }

        bool cpuDiverged = UpdateSensorWatchdog(
            nowUtc,
            active: cpuDuty > 0 && measuredCpuRpm >= 0,
            targetRpm: targetCpuRpm,
            measuredRpm: measuredCpuRpm,
            ref _watchdogCpuRefTargetRpm,
            ref _watchdogCpuDivergeStartUtc);

        bool gpuDiverged = UpdateSensorWatchdog(
            nowUtc,
            active: gpuTemp > 0 && gpuDuty > 0 && measuredGpuRpm >= 0,
            targetRpm: targetGpuRpm,
            measuredRpm: measuredGpuRpm,
            ref _watchdogGpuRefTargetRpm,
            ref _watchdogGpuDivergeStartUtc);

        if (!cpuDiverged && !gpuDiverged)
            return false;

        if (_lastWatchdogRewriteUtc.HasValue && (nowUtc - _lastWatchdogRewriteUtc.Value).TotalSeconds < 15.0)
            return false;

        return true;
    }

    private static bool UpdateSensorWatchdog(
        DateTime nowUtc,
        bool active,
        double targetRpm,
        int measuredRpm,
        ref double refTargetRpm,
        ref DateTime? divergeStartUtc)
    {
        if (!active || targetRpm <= 0.0)
        {
            divergeStartUtc = null;
            refTargetRpm = targetRpm;
            return false;
        }

        if (double.IsNaN(refTargetRpm))
        {
            refTargetRpm = targetRpm;
        }
        else if (Math.Abs(targetRpm - refTargetRpm) >= 100.0)
        {
            refTargetRpm = targetRpm;
            divergeStartUtc = null;
            return false;
        }

        if (Math.Abs(measuredRpm - targetRpm) > 600.0)
        {
            divergeStartUtc ??= nowUtc;
            return (nowUtc - divergeStartUtc.Value).TotalSeconds >= 15.0;
        }

        divergeStartUtc = null;
        return false;
    }

    private void LogDiagnosticSample(byte mode, FanControlMode controlMode, HardwareTelemetry telemetry, FanCalibrationData? cal)
    {
        if (!FanDiagnosticLogger.Shared.Enabled || telemetry.CpuTemp <= 0)
            return;

        int cpuTargetRpm = _fanFollower.EmergencyActive || controlMode == FanControlMode.Max
            ? FanCurveProfile.GetMaxRpm(gpu: false, cal)
            : controlMode == FanControlMode.Custom
                ? (int)Math.Round(_fanFollower.LastTargetCpuRpm, MidpointRounding.AwayFromZero)
                : 0;
        int gpuTargetRpm = _fanFollower.EmergencyActive || controlMode == FanControlMode.Max
            ? FanCurveProfile.GetMaxRpm(gpu: true, cal)
            : controlMode == FanControlMode.Custom
                ? (int)Math.Round(_fanFollower.LastTargetGpuRpm, MidpointRounding.AwayFromZero)
                : 0;
        int sentCpu = controlMode == FanControlMode.Max ? 100
            : _fanFollower.EmergencyActive ? (_sentCpuFan ?? 100)
            : controlMode == FanControlMode.Auto ? 0 : (_sentCpuFan ?? 0);
        int sentGpu = controlMode == FanControlMode.Max ? 100
            : _fanFollower.EmergencyActive ? (_sentGpuFan ?? 100)
            : controlMode == FanControlMode.Auto ? 0 : (_sentGpuFan ?? 0);
        string ecState = controlMode switch
        {
            FanControlMode.Max => "Max",
            FanControlMode.Auto => _fanFollower.EmergencyActive ? "Custom" : "Auto",
            _ => (_fanFollower.LastCpuEcAuto || _fanFollower.LastGpuEcAuto) ? "MixedAuto" : "Custom"
        };

        FanDiagnosticLogger.Shared.Log(
            powerMode: ProfileManager.ModeToProfileName(mode),
            controlMode: controlMode.ToString(),
            cpuTempC: telemetry.CpuTemp,
            gpuTempC: telemetry.GpuTemp,
            cpuTargetRpm: cpuTargetRpm,
            gpuTargetRpm: gpuTargetRpm,
            sentCpuDuty: sentCpu,
            sentGpuDuty: sentGpu,
            measuredCpuRpm: telemetry.CpuRpm,
            measuredGpuRpm: telemetry.GpuRpm,
            ecState: ecState,
            emergencyActive: _fanFollower.EmergencyActive);
    }

    private void FanWriteFailed(DateTime nowUtc)
    {
        _nextFanRetryUtc = nowUtc.AddSeconds(10);
        _fanWriteInBackoff = true;
        _sentCpuFan = _sentGpuFan = null;
        _sentManualZero = false;
        _sentMaxFanMode = false;
        _autoFanModeApplied = false;
        UpdateFanDemand();
        const string notice = "Fan control write failed. Retrying in 10s.";
        AppLogger.Log("[FanControl] All 3 WMI retries failed; backing off 10s before automatic recovery.");
        FanNoticeRaised?.Invoke(this, notice);
        Views.ModeToastWindow.ShowNotice(notice);
        _dispatcher?.InvokeAsync(() => DiagnosticMessage = notice);
    }

    private void RestoreFanMode()
    {
        if (_fanControlGate == null || _fanFollower == null)
            return;

        lock (_fanControlGate)
        {
            byte prevMode = _runningFanProfileMode;
            ResetFanFollower();
            if (!_hw.TryGetPowerMode(out byte mode))
            {
                DisengageQuietIdleBaseIfEngaged(prevMode);
                _fanWriteInBackoff = true;
                return;
            }
            if (_quietIdleBaseEngaged)
            {
                if (prevMode != 0xFF && prevMode != mode)
                {
                    if (_hw.SetQuietIdleBase(false, mode))
                    {
                        _quietIdleBaseEngaged = false;
                        _quietIdleBaseMode = 0x01;
                    }
                }
                else
                {
                    DisengageQuietIdleBaseIfEngaged(mode);
                }
            }
            _runningFanProfile = FanCurveStorage.Load(mode);
            _runningFanProfileMode = mode;
            _hw.InvalidateFanTargets();
            if (_runningFanProfile.ControlMode != FanControlMode.Custom)
            {
                UpdateFanDemand();
                ProcessFanCurves(default);
                return;
            }
            UpdateFanDemand();
            // Custom fan control waits for the next shared sample.
        }
    }

    private void ReleaseCustomFans()
    {
        lock (_fanControlGate)
        {
            DisengageQuietIdleBaseIfEngaged(_runningFanProfileMode);
            if (!_fanOverrideActive) return;
            try
            {
                if (_sentManualZero)
                    _hw.InvalidateFanTargets();
                _autoFanModeApplied = _hw.SetFanMode(0, 0);
                if (_autoFanModeApplied)
                {
                    _fanOverrideActive = _sentMaxFanMode = false;
                    _sentCpuFan = _sentGpuFan = null;
                    _sentManualZero = false;
                }
            }
            catch (Exception ex) { AppLogger.Log($"Could not return fans to Auto: {ex.Message}"); }
        }
    }

    public async Task ApplyFanProfileAsync()
    {
        await Task.Run(RestoreFanMode);
        lock (_fanControlGate)
        {
            if (_runningFanProfile == null || _fanWriteInBackoff)
                throw new InvalidOperationException("Fan settings could not be applied.");
        }
        DiagnosticMessage = "Fan settings saved; following the selected mode.";
    }

    public async Task<FanCalibrationResult> RunFanCalibrationAsync(
        IProgress<FanCalibrationProgress>? progress = null,
        System.Threading.CancellationToken cancellationToken = default)
    {
        lock (_fanControlGate)
        {
            if (_calibratingFans)
                return new FanCalibrationResult(false, FanCalibrationStatus.Cancelled, null, "Fan calibration is already running.");
            DisengageQuietIdleBaseIfEngaged(_runningFanProfileMode);
            _calibratingFans = true;
            UpdateFanDemand();
        }

        byte originalPowerMode = _hw.TryGetPowerMode(out byte currentMode) && ProfileManager.IsSupportedMode(currentMode)
            ? currentMode
            : (ProfileManager.IsSupportedMode(ActivePowerMode) ? ActivePowerMode : (byte)0x01);

        try
        {
            var service = new FanCalibrationService(
                _hw,
                ReadCalibrationTemperatures,
                () =>
                {
                    _hw.SetPowerMode(originalPowerMode);
                    _hw.InvalidateFanTargets();
                    _hw.SetFanMode(0, 0);
                });

            var result = await service.RunAsync(progress, cancellationToken);
            if (result.Success && result.Data != null)
            {
                await Task.Run(() => PreySense.Core.Configuration.ConfigurationService.Instance.Update(
                    cfg => cfg.Fans.Calibration = result.Data));
                DiagnosticMessage = "Fan calibration completed and saved.";
            }
            else
            {
                DiagnosticMessage = result.Message;
            }
            return result;
        }
        finally
        {
            lock (_fanControlGate)
            {
                _calibratingFans = false;
            }
            await Task.Run(RestoreFanMode);
        }
    }

    private (int CpuTempC, int GpuTempC) ReadCalibrationTemperatures()
    {
        if (_hw is ITelemetrySources sources)
        {
            var snap = TelemetryCollector.Read(TelemetryMetrics.Temperatures, sources, fanOnly: true);
            return (snap.CpuTemp ?? -1, snap.GpuTemp ?? -1);
        }
        if (_hw is MockHardwareService mock)
        {
            var t = mock.GetCurrentTelemetry();
            return (t.CpuTemp, t.GpuTemp);
        }
        return (-1, -1);
    }
}
