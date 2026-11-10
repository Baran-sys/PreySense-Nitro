using PreySense.Core.Configuration;
using PreySense.Core.Driver;
using PreySense.Core.Hardware;
using PreySense.Core.Services;
using PreySense.Mode;

namespace PreySense.Helpers;

public partial class WmiController
{
    public bool SetFanBehavior(byte mode)
    {
        ulong value = mode switch
        {
            0 => 0x410009ul,
            1 => 0x820009ul,
            2 => 0xC30009ul,
            _ => 0x410009ul
        };
        return SendCommand(AcerWmi.GamingMethods.SetFanBehavior, value).success;
    }

    public bool SetFanBehaviorMask(ulong behaviorMask)
    {
        return SendCommand(AcerWmi.GamingMethods.SetFanBehavior, behaviorMask).success;
    }

    public static int ScalePercentToWmi(int percent) => EcFanPolicy.ClampDuty(percent);

    public void InvalidateFanTargets()
    {
        lock (_lock) { _lastSetFanMode = -1; }
    }

    public bool SetCpuFanSpeed(int percent) => SetFanDuty(1, percent);
    public bool SetGpuFanSpeed(int percent) => SetFanDuty(4, percent);

    private bool SetFanDuty(int fan, int percent)
    {
        int duty = EcFanPolicy.ClampDuty(percent);
        ulong payload = (ulong)(((duty * 25600) / 100) & 0xFF00) + (uint)fan;
        return SendCommand(AcerWmi.GamingMethods.SetFanSpeed, payload).success;
    }

    private static (int pl1, int pl2, int tau, int? staticPl1, int? staticPl2) GetEffectivePowerLimitsForMode(byte mode)
    {
        int? staticPl1 = null, staticPl2 = null;
        try
        {
            var cfg = ConfigurationService.Instance.GetCurrent();
            var t = cfg.Tuning;
            staticPl1 = t.FactoryStaticCpuPl1Watts;
            staticPl2 = t.FactoryStaticCpuPl2Watts;
            var p = mode switch
            {
                0 => t.Quiet,
                1 => t.Balanced,
                4 => t.Performance,
                5 => t.Turbo,
                6 => t.Eco,
                _ => t.Balanced
            };
            if (cfg.General.HardwareTuningEnabled && p.ApplyCpuLimits && p.CpuPl1Watts.HasValue && p.CpuPl2Watts.HasValue)
            {
                return (p.CpuPl1Watts.Value, p.CpuPl2Watts.Value, p.TauSeconds ?? CpuPowerLimitConfiguration.FactoryTauSeconds, staticPl1, staticPl2);
            }
            string modeName = mode switch
            {
                0 => "Quiet",
                1 => "Balanced",
                4 => "Performance",
                5 => "Turbo",
                6 => "Eco",
                _ => "Balanced"
            };
            if (t.FactoryDefaults != null && t.FactoryDefaults.TryGetValue(modeName, out var f) && f.CpuPl1Watts.HasValue && f.CpuPl2Watts.HasValue)
            {
                return (f.CpuPl1Watts.Value, f.CpuPl2Watts.Value, f.TauSeconds ?? CpuPowerLimitConfiguration.FactoryTauSeconds, staticPl1, staticPl2);
            }
        }
        catch { }

        return mode switch
        {
            0 => (CpuPowerLimitConfiguration.FactoryQuietPl1, CpuPowerLimitConfiguration.FactoryQuietPl2, CpuPowerLimitConfiguration.FactoryTauSeconds, staticPl1, staticPl2),
            6 => (CpuPowerLimitConfiguration.FactoryEcoPl1, CpuPowerLimitConfiguration.FactoryEcoPl2, CpuPowerLimitConfiguration.FactoryTauSeconds, staticPl1, staticPl2),
            4 => (CpuPowerLimitConfiguration.FactoryPerformancePl1, CpuPowerLimitConfiguration.FactoryPerformancePl2, CpuPowerLimitConfiguration.FactoryTauSeconds, staticPl1, staticPl2),
            5 => (CpuPowerLimitConfiguration.FactoryTurboPl1, CpuPowerLimitConfiguration.FactoryTurboPl2, CpuPowerLimitConfiguration.FactoryTauSeconds, staticPl1, staticPl2),
            _ => (CpuPowerLimitConfiguration.FactoryBalancedPl1, CpuPowerLimitConfiguration.FactoryBalancedPl2, CpuPowerLimitConfiguration.FactoryTauSeconds, staticPl1, staticPl2)
        };
    }

    private void ReapplyActiveModePowerLimits(byte activeMode)
    {
        try
        {
            SyncWindowsPowerMode(activeMode);
            var (pl1, pl2, tau, staticPl1, staticPl2) = GetEffectivePowerLimitsForMode(activeMode);
            PowerLimitController.SetCpuPowerLimits(pl1, pl2, true, tau, staticPl1, staticPl2);
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[QuietAutoFan] ReapplyActiveModePowerLimits error: {ex.Message}");
        }
    }

    private void EnsureQuietEcAutoBase(bool wantQuietAuto)
    {
        try
        {
            byte activeMode = _activeUserPowerMode;
            if (wantQuietAuto)
            {
                if (_quietEcOverrideActive || activeMode <= 1 || activeMode == 6)
                    return;

                ulong payload = (ulong)0x0B | ((ulong)1 << 8);
                if (SendCommand(AcerWmi.GamingMethods.SetMiscSetting, payload).success)
                {
                    _quietEcOverrideActive = true;
                    ReapplyActiveModePowerLimits(activeMode);
                    AppLogger.Log($"[QuietAutoFan] Switched EC base to Balanced (0x01) for silent ~2200 RPM 0% fan while preserving active user mode 0x{activeMode:X2}.");
                }
            }
            else if (_quietEcOverrideActive)
            {
                if (activeMode <= 1 || activeMode == 6)
                {
                    _quietEcOverrideActive = false;
                    return;
                }

                ulong payload = (ulong)0x0B | ((ulong)activeMode << 8);
                if (SendCommand(AcerWmi.GamingMethods.SetMiscSetting, payload).success)
                {
                    _quietEcOverrideActive = false;
                    ReapplyActiveModePowerLimits(activeMode);
                    AppLogger.Log($"[QuietAutoFan] Restored EC base to user mode 0x{activeMode:X2}.");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[QuietAutoFan] Error: {ex.Message}");
        }
    }

    public bool SetFanControlWmi(int mode, int cpuSpeed = 50, int gpuSpeed = 50)
    {
        lock (_lock)
        {
            cpuSpeed = EcFanPolicy.ClampDuty(cpuSpeed);
            gpuSpeed = EcFanPolicy.ClampDuty(gpuSpeed);
            bool force = mode != _lastSetFanMode;
            if (!force && (mode != 2 || (cpuSpeed == _lastSetCpuSpeed && gpuSpeed == _lastSetGpuSpeed)))
                return true;

            EnsureQuietEcAutoBase(mode == 2 && cpuSpeed <= 0 && gpuSpeed <= 0);

            if (mode == 2)
            {
                bool cpuAuto = cpuSpeed <= 0;
                bool gpuAuto = gpuSpeed <= 0;
                ulong targetMask = (cpuAuto && gpuAuto) ? 0x410009ul :
                                   (!cpuAuto && gpuAuto) ? 0x430009ul :
                                   (cpuAuto && !gpuAuto) ? 0xC10009ul : 0xC30009ul;

                bool prevCpuAuto = _lastSetCpuSpeed <= 0;
                bool prevGpuAuto = _lastSetGpuSpeed <= 0;
                ulong prevMask = (prevCpuAuto && prevGpuAuto) ? 0x410009ul :
                                 (!prevCpuAuto && prevGpuAuto) ? 0x430009ul :
                                 (prevCpuAuto && !prevGpuAuto) ? 0xC10009ul : 0xC30009ul;

                if (force || targetMask != prevMask)
                {
                    if (!SetFanBehaviorMask(targetMask)) return false;
                    force = true;
                }
                if (!cpuAuto && (force || cpuSpeed != _lastSetCpuSpeed) && !SetCpuFanSpeed(cpuSpeed))
                    return false;
                if (!gpuAuto && (force || gpuSpeed != _lastSetGpuSpeed) && !SetGpuFanSpeed(gpuSpeed))
                    return false;
            }
            else
            {
                if (force && !SetFanBehavior((byte)mode)) return false;
            }

            _lastSetFanMode = mode;
            _lastSetCpuSpeed = cpuSpeed;
            _lastSetGpuSpeed = gpuSpeed;
            return true;
        }
    }
}
