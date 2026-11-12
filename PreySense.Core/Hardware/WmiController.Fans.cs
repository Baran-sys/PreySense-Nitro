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
        lock (_lock) { _fanTargetsInvalidated = true; }
    }

    public bool SetCpuFanSpeed(int percent) => SetFanDuty(1, percent);
    public bool SetGpuFanSpeed(int percent) => SetFanDuty(4, percent);

    private bool SetFanDuty(int fan, int percent)
    {
        int duty = EcFanPolicy.ClampDuty(percent);
        ulong payload = (ulong)(((duty * 25600) / 100) & 0xFF00) + (uint)fan;
        return SendCommand(AcerWmi.GamingMethods.SetFanSpeed, payload).success;
    }

    public bool SetFanControlWmi(int mode, int cpuSpeed = 50, int gpuSpeed = 50)
    {
        lock (_lock)
        {
            cpuSpeed = EcFanPolicy.ClampDuty(cpuSpeed);
            gpuSpeed = EcFanPolicy.ClampDuty(gpuSpeed);
            bool force = _fanTargetsInvalidated || _lastSetManualZero || mode != _lastSetFanMode;
            if (!force && (mode != 2 || (cpuSpeed == _lastSetCpuSpeed && gpuSpeed == _lastSetGpuSpeed)))
                return true;

            if (_lastSetFanMode == 1 && mode != 1)
            {
                // Disengage Acer EC Max fan latch by resetting to Auto before applying Custom/other modes.
                SetFanBehavior(0);
            }

            if (mode == 2)
            {
                bool cpuAuto = cpuSpeed <= 0;
                bool gpuAuto = gpuSpeed <= 0;
                ulong targetMask = (cpuAuto && gpuAuto) ? 0x410009ul :
                                   (!cpuAuto && gpuAuto) ? 0x430009ul :
                                   (cpuAuto && !gpuAuto) ? 0xC10009ul : 0xC30009ul;

                bool prevCpuAuto = _lastSetCpuSpeed <= 0;
                bool prevGpuAuto = _lastSetGpuSpeed <= 0;
                ulong prevMask = _lastSetManualZero ? 0xC30009ul :
                                 (prevCpuAuto && prevGpuAuto) ? 0x410009ul :
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
            _lastSetManualZero = false;
            _fanTargetsInvalidated = false;
            return true;
        }
    }

    public bool SetManualZeroFansWmi()
    {
        lock (_lock)
        {
            if (_lastSetFanMode == 1)
            {
                // Disengage Acer EC Max fan latch by resetting to Auto before applying Manual 0%.
                SetFanBehavior(0);
            }

            if (!SetFanBehaviorMask(0xC30009ul))
                return false;
            _lastSetManualZero = true;
            _fanTargetsInvalidated = true;
            if (!SetCpuFanSpeed(0))
                return false;
            if (!SetGpuFanSpeed(0))
                return false;

            _lastSetFanMode = 2;
            _lastSetCpuSpeed = 0;
            _lastSetGpuSpeed = 0;
            _fanTargetsInvalidated = false;
            return true;
        }
    }
}
