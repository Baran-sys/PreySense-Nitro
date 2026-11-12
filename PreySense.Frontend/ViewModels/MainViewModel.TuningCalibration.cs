using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PreySense.Core.Driver;
using PreySense.Core.Configuration;
using PreySense.Core.Hardware;
using PreySense.Core.Services;

namespace PreySense.Frontend.ViewModels;

public partial class MainViewModel
{
    private bool _tuningEnablePending;
    private bool _updatingTuningEnabled;
    private void SetTuningEnabledState(bool enabled)
    {
        void Apply()
        {
            _updatingTuningEnabled = true;
            try { IsHardwareTuningEnabled = enabled; }
            finally { _updatingTuningEnabled = false; }
        }

        if (_dispatcher == null || _dispatcher.CheckAccess())
            Apply();
        else
            _dispatcher.Invoke(Apply);
    }

    internal sealed record FactoryDefaultsCapture(
        Dictionary<string, ModeTuningConfig> Profiles,
        int? StaticCpuPl1Watts,
        int? StaticCpuPl2Watts,
        int? ProchotOffsetCelsius = null,
        bool? ProchotOffsetLocked = null,
        int? TjMaxCelsius = null,
        int? GpuBaselineWatts = null);

    private object? _factoryCaptureSync;
    private Task? _factoryCaptureTask;
    internal Task TuningEnableTask { get; private set; } = Task.CompletedTask;
    internal Func<FactoryDefaultsCapture>? CaptureTuningDefaults { get; set; }

    internal Task CaptureMissingFactoryDefaultsAsync()
    {
        lock (LazyInitializer.EnsureInitialized(ref _factoryCaptureSync))
        {
            // Startup and other callers share one capture, including its failure result.
            if (_factoryCaptureTask is { IsCompleted: false }) return _factoryCaptureTask;
            return _factoryCaptureTask = CaptureMissingFactoryDefaultsCoreAsync();
        }
    }

    private static bool HasCompleteFactoryDefaults(TuningConfig tuning)
    {
        if (CpuVendorHelper.IsAmd)
            return tuning.FactoryDefaults != null && tuning.FactoryDefaults.Count >= 5;

        return tuning.HasFactoryDefaults && tuning.HasFactoryStaticCpuLimits;
    }

    private async Task CaptureMissingFactoryDefaultsCoreAsync()
    {
        if (_disposed || HasCompleteFactoryDefaults(TuningStore.GetCurrent().Tuning)) return;
        bool gateHeld = false;
        bool busyChanged = false;
        bool wasBusy = IsBusy;
        try
        {
            await _modeSwitchGate.WaitAsync();
            gateHeld = true;
            if (_disposed || HasCompleteFactoryDefaults(TuningStore.GetCurrent().Tuning)) return;
            wasBusy = IsBusy;
            IsBusy = true;
            busyChanged = true;
            CancelPendingModeSwitch();
            var capture = await Task.Run(CaptureDefaultsWithDriver);
            if (_disposed) return;
            var capturedTuning = new TuningConfig
            {
                FactoryDefaults = capture.Profiles,
                FactoryStaticCpuPl1Watts = capture.StaticCpuPl1Watts,
                FactoryStaticCpuPl2Watts = capture.StaticCpuPl2Watts
            };
            if (!HasCompleteFactoryDefaults(capturedTuning))
                throw new InvalidOperationException("Factory defaults are incomplete.");
            // Commit the complete fresh capture together. Missing optional readings stay null,
            // rather than inheriting metadata from an earlier capture.
            bool autoEnabledProchot = false;
            TuningStore.Update(cfg =>
            {
                cfg.Tuning.FactoryDefaults = capture.Profiles;
                cfg.Tuning.FactoryStaticCpuPl1Watts = capture.StaticCpuPl1Watts;
                cfg.Tuning.FactoryStaticCpuPl2Watts = capture.StaticCpuPl2Watts;
                cfg.Tuning.FactoryProchotOffsetCelsius = capture.ProchotOffsetCelsius;
                cfg.Tuning.FactoryProchotOffsetLocked = capture.ProchotOffsetLocked;
                cfg.Tuning.FactoryTjMaxCelsius = capture.TjMaxCelsius;
                if (capture.ProchotOffsetCelsius is int capturedProchot &&
                    capture.ProchotOffsetLocked != true &&
                    cfg.Tuning.ProchotOffsetCelsius is null &&
                    !cfg.Tuning.ApplyProchotOffset)
                {
                    cfg.Tuning.ProchotOffsetCelsius = capturedProchot;
                    cfg.Tuning.ApplyProchotOffset = true;
                    autoEnabledProchot = true;
                }
            });
            if (autoEnabledProchot && capture.ProchotOffsetCelsius is int prochotVal)
            {
                ProchotOffsetCelsius = prochotVal;
                IsProchotOffsetEnabled = true;
            }
            if (capture.GpuBaselineWatts.HasValue) GpuBasePowerWatts = capture.GpuBaselineWatts;
            OnPropertyChanged(nameof(IsGpuProfileTuningAvailable));
            OnPropertyChanged(nameof(IsGpuClockTuningAvailable));
            OnPropertyChanged(nameof(ProchotActivationLabel));
            OnPropertyChanged(nameof(TjMaxLabel));
            var saved = TuningStore.GetCurrent();
            if (saved.General.HardwareTuningEnabled && HasCompleteFactoryDefaults(saved.Tuning))
                SetTuningEnabledState(true);
        }
        catch (Exception ex)
        {
            SetTuningEnabledState(false);
            DiagnosticMessage = $"Could not capture factory defaults: {ex.Message}";
        }
        finally
        {
            if (gateHeld)
            {
                if (busyChanged) IsBusy = wasBusy;
                _modeSwitchGate.Release();
            }
        }
    }

    private async Task InitializeTuningAndSessionAsync()
    {
        string? captureFailure = null;
        if (_hw is not MockHardwareService)
        {
            await RefreshProchotLockStateAsync();
            await CaptureMissingFactoryDefaultsAsync();
            if (!HasCompleteFactoryDefaults(TuningStore.GetCurrent().Tuning))
                captureFailure = DiagnosticMessage;
        }
        if (_disposed) return;
        await InitializeSessionAsync();
        if (!_disposed && !string.IsNullOrEmpty(captureFailure))
            DiagnosticMessage = $"{DiagnosticMessage} {captureFailure}".Trim();
    }

    private FactoryDefaultsCapture CaptureDefaultsWithDriver()
    {
        if (CaptureTuningDefaults is not null) return CaptureTuningDefaults();
        int? gpuBaselineWatts = GpuBasePowerWatts;
        if (!gpuBaselineWatts.HasValue)
        {
            int? detectedGpu = PreySense.Gpu.NvmlHelper.GetGpuDefaultPowerLimitWatts();
            if (detectedGpu.HasValue) gpuBaselineWatts = GpuPowerBaseline.SaveOnce(detectedGpu.Value);
            gpuBaselineWatts ??= 80;
        }

        if (CpuVendorHelper.IsAmd)
        {
            var fallback = new Dictionary<string, ModeTuningConfig>();
            foreach (var mode in new[] { "Eco", "Quiet", "Balanced", "Performance", "Turbo" })
            {
                fallback[mode] = new ModeTuningConfig
                {
                    GpuWatts = gpuBaselineWatts,
                    ApplyGpuLimits = false,
                    CpuBoost = true
                };
            }
            return new(fallback, null, null, null, null, null, gpuBaselineWatts);
        }

        var backend = CpuDriverRuntime.Backend ?? throw new InvalidOperationException(CpuDriverRuntime.Status);
        var calibration = new DriverFactoryCalibration(backend, backend);
        var profiles = calibration.Capture(gpuBaselineWatts, new TuningModeController(this));
        return new(profiles, calibration.StaticCpuPl1Watts, calibration.StaticCpuPl2Watts,
            calibration.ProchotOffsetCelsius, calibration.ProchotOffsetLocked, calibration.TjMaxCelsius, gpuBaselineWatts);
    }

    private async Task EnableTuningAsync()
    {
        if (_tuningEnablePending) return;
        _tuningEnablePending = true;
        bool gateHeld = false;
        try
        {
            await _modeSwitchGate.WaitAsync();
            gateHeld = true;
            if (_disposed)
            {
                SetTuningEnabledState(false);
                try { TuningStore.Update(cfg => cfg.General.HardwareTuningEnabled = false); } catch { }
                DiagnosticMessage = "Tuning stays off — PreySense is shutting down.";
                return;
            }

            if (CpuVendorHelper.IsAmd && CaptureTuningDefaults is null && !HasCompleteFactoryDefaults(TuningStore.GetCurrent().Tuning))
            {
                EnsureFallbackFactoryDefaults();
            }

            if (!HasCompleteFactoryDefaults(TuningStore.GetCurrent().Tuning))
            {
                SetTuningEnabledState(false);
                try { TuningStore.Update(cfg => cfg.General.HardwareTuningEnabled = false); } catch { }
                DiagnosticMessage = "Tuning stays off until factory defaults are ready.";
                return;
            }

            int? autoProchot = null;
            TuningStore.Update(cfg =>
            {
                cfg.General.HardwareTuningEnabled = true;
                if (!CpuVendorHelper.IsAmd &&
                    IsProchotOffsetAvailable &&
                    !IsProchotOffsetLocked &&
                    cfg.Tuning.ProchotOffsetCelsius is null &&
                    !cfg.Tuning.ApplyProchotOffset &&
                    cfg.Tuning.FactoryProchotOffsetCelsius is int factoryProchot)
                {
                    cfg.Tuning.ProchotOffsetCelsius = factoryProchot;
                    cfg.Tuning.ApplyProchotOffset = true;
                    autoProchot = factoryProchot;
                }
            });
            if (autoProchot is int prochotVal)
            {
                ProchotOffsetCelsius = prochotVal;
                IsProchotOffsetEnabled = true;
            }
            SetTuningEnabledState(true);
            DiagnosticMessage = "Tuning enabled.";
        }
        catch (Exception ex)
        {
            SetTuningEnabledState(false);
            try { TuningStore.Update(cfg => cfg.General.HardwareTuningEnabled = false); } catch { }
            DiagnosticMessage = $"Tuning stays off: {ex.Message}";
        }
        finally
        {
            if (gateHeld) _modeSwitchGate.Release();
            _tuningEnablePending = false;
        }
    }

    private void EnsureFallbackFactoryDefaults()
    {
        int? gpuBaselineWatts = GpuBasePowerWatts;
        if (!gpuBaselineWatts.HasValue)
        {
            int? detectedGpu = PreySense.Gpu.NvmlHelper.GetGpuDefaultPowerLimitWatts();
            if (detectedGpu.HasValue) gpuBaselineWatts = GpuPowerBaseline.SaveOnce(detectedGpu.Value);
            gpuBaselineWatts ??= 80;
        }

        TuningStore.Update(cfg =>
        {
            cfg.Tuning.FactoryDefaults ??= new Dictionary<string, ModeTuningConfig>();
            foreach (var mode in new[] { "Eco", "Quiet", "Balanced", "Performance", "Turbo" })
            {
                if (!cfg.Tuning.FactoryDefaults.ContainsKey(mode))
                {
                    cfg.Tuning.FactoryDefaults[mode] = new ModeTuningConfig
                    {
                        GpuWatts = gpuBaselineWatts,
                        ApplyGpuLimits = false,
                        CpuBoost = true
                    };
                }
            }
        });
    }

    private static string TuningModeName(int index) => index switch
    {
        0 => "Eco", 1 => "Quiet", 2 => "Balanced", 3 => "Performance", 4 => "Turbo",
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    internal ModeTuningConfig SavedTuningMode(int index)
    {
        var tuning = (TuningConfigurationReader?.Invoke() ?? TuningStore.GetCurrent()).Tuning;
        return index switch { 0 => tuning.Eco, 1 => tuning.Quiet, 2 => tuning.Balanced,
            3 => tuning.Performance, 4 => tuning.Turbo, _ => throw new ArgumentOutOfRangeException(nameof(index)) };
    }

    internal ModeTuningConfig TuningEditorMode(int index)
    {
        var saved = SavedTuningMode(index);
        var factory = FactoryTuningMode(index);
        return new ModeTuningConfig
        {
            CpuPl1Watts = saved.CpuPl1Watts ?? factory?.CpuPl1Watts,
            CpuPl2Watts = saved.CpuPl2Watts ?? factory?.CpuPl2Watts,
            TauSeconds = saved.TauSeconds ?? factory?.TauSeconds,
            CpuBoost = saved.CpuBoost ?? factory?.CpuBoost,
            CpuBoostMode = saved.EffectiveCpuBoostMode ?? factory?.EffectiveCpuBoostMode,
            GpuWatts = saved.GpuWatts ?? factory?.GpuWatts,
            GpuDeltaWatts = saved.GpuDeltaWatts ?? factory?.GpuDeltaWatts,
            GpuDynamicBoostWatts = saved.GpuDynamicBoostWatts ?? factory?.GpuDynamicBoostWatts,
            PlatformPowerOffsetWatts = saved.PlatformPowerOffsetWatts ?? factory?.PlatformPowerOffsetWatts,
            ApplyCpuLimits = saved.ApplyCpuLimits,
            ApplyGpuLimits = saved.ApplyGpuLimits,
            GpuCoreClockOffsetMhz = saved.GpuCoreClockOffsetMhz ?? 0,
            GpuMemoryClockOffsetMhz = saved.GpuMemoryClockOffsetMhz ?? 0,
            ApplyGpuClockOffsets = saved.ApplyGpuClockOffsets,
            ApplyGpuClockLock = saved.ApplyGpuClockLock,
            GpuClockLockWatts = saved.GpuClockLockWatts,
            GpuFrequencyCeilingMhz = saved.GpuFrequencyCeilingMhz
        };
    }

    internal int? TuningEditorProchotOffset =>
        ProchotOffsetCelsius ?? TuningStore.GetCurrent().Tuning.FactoryProchotOffsetCelsius;

    internal ModeTuningConfig? FactoryTuningMode(int index) =>
        TuningStore.GetCurrent().Tuning.FactoryDefaults.GetValueOrDefault(TuningModeName(index));

    /// <summary>ERM4 GPU offsets were captured; otherwise GPU apply stays unavailable (CPU/PROCHOT only).</summary>
    public bool IsGpuProfileTuningAvailable =>
        TuningStore.GetCurrent().Tuning.HasFactoryGpuProfiles;

    /// <summary>NVIDIA GPU is valid and P0 clock offset tuning is supported.</summary>
    public bool IsGpuClockTuningAvailable =>
        Gpu.NvidiaGpuControl.Shared.IsValid && Gpu.NvidiaGpuControl.Shared.IsClockTuningSupported;

    /// <summary>True if either Acer ERM4 GPU profile tuning or NVML frequency ceiling fallback is supported.</summary>
    public bool IsGpuPowerControlAvailable =>
        IsGpuProfileTuningAvailable || Gpu.NvidiaGpuControl.Shared.IsValid;

    /// <summary>True when normal ERM4 GPU profile tuning is NOT available, but NVML clock ceiling is supported.</summary>
    public bool IsGpuClockLockFallbackAvailable =>
        !IsGpuProfileTuningAvailable && Gpu.NvidiaGpuControl.Shared.IsValid;
}
