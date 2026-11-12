using System;
using System.Runtime.InteropServices;

namespace PreySense.Core.Hardware;

/// <summary>
/// Universal user-mode CPU hardware telemetry provider using the Windows Kernel Power Manager
/// (CallNtPowerInformation Level 11: ProcessorInformation).
/// Provides instantaneous per-core and aggregate operating frequencies across all AMD Ryzen
/// and Intel architectures without requiring ring-0 kernel drivers or elevated driver privileges.
/// </summary>
public static class NativeCpuInformation
{
    private const int ProcessorInformationLevel = 11;

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessorPowerInformation
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern uint CallNtPowerInformation(
        int informationLevel,
        IntPtr lpInputBuffer,
        uint nInputBufferSize,
        IntPtr lpOutputBuffer,
        uint nOutputBufferSize);

    public readonly record struct CpuClockSummary(
        int MaxCurrentMhz,
        int AverageCurrentMhz,
        int MaxNominalMhz,
        int CoreCount);

    /// <summary>
    /// Samples real-time CPU operating clocks across all logical processors.
    /// Returns null if the platform query fails or returns zero cores.
    /// </summary>
    public static CpuClockSummary? SampleClocks()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        int coreCount = Environment.ProcessorCount;
        if (coreCount <= 0)
            return null;

        int structSize = Marshal.SizeOf<ProcessorPowerInformation>();
        int totalBytes = structSize * coreCount;
        IntPtr buffer = Marshal.AllocHGlobal(totalBytes);

        try
        {
            uint status = CallNtPowerInformation(
                ProcessorInformationLevel,
                IntPtr.Zero,
                0,
                buffer,
                (uint)totalBytes);

            if (status != 0) // STATUS_SUCCESS = 0
                return null;

            int maxCurrent = 0;
            int maxNominal = 0;
            long sumCurrent = 0;
            int validCores = 0;

            for (int i = 0; i < coreCount; i++)
            {
                IntPtr ptr = IntPtr.Add(buffer, i * structSize);
                var info = Marshal.PtrToStructure<ProcessorPowerInformation>(ptr);

                int currentMhz = (int)info.CurrentMhz;
                int maxMhz = (int)info.MaxMhz;

                if (currentMhz > 0)
                {
                    if (currentMhz > maxCurrent) maxCurrent = currentMhz;
                    sumCurrent += currentMhz;
                    validCores++;
                }

                if (maxMhz > maxNominal)
                    maxNominal = maxMhz;
            }

            if (validCores == 0)
                return null;

            int averageCurrent = (int)(sumCurrent / validCores);
            return new CpuClockSummary(maxCurrent, averageCurrent, maxNominal, validCores);
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
