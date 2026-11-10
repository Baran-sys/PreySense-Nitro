using System;

namespace PreySense.Gpu
{
    public interface IGpuControl : IDisposable
    {
        bool IsNvidia { get; }
        bool IsValid { get; }
        string FullName { get; }
        int? GetCurrentTemperature();
        int? GetGpuUse();
        (long usedMb, long totalMb)? GetVramInfo();
        float? GetGpuPower();
        void KillGPUApps();
        int? GetGpuMemoryClock();
        float? GetGpuVoltage();
        bool IsClockTuningSupported { get; }
        int SetClocks(int coreMhz, int memoryMhz);
        bool GetClocks(out int coreMhz, out int memoryMhz, bool forceWake = false);
        int ResetClocks();
    }
}
