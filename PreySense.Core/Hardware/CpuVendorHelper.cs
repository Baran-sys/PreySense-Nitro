using System;
using System.Buffers.Binary;
using System.Management;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace PreySense.Core.Hardware;

public static class CpuVendorHelper
{
    private static readonly Lazy<string> _cpuVendorString = new(DetectVendorString);
    private static readonly Lazy<bool> _isAmdCpu = new(DetectAmdCpu);
    private static readonly Lazy<bool> _isIntelCpu = new(DetectIntelCpu);

    public static string Vendor => _cpuVendorString.Value;
    public static bool IsAmd => _isAmdCpu.Value;
    public static bool IsIntel => _isIntelCpu.Value;

    private static string DetectVendorString()
    {
        if (X86Base.IsSupported)
        {
            try
            {
                var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
                Span<byte> bytes = stackalloc byte[12];
                BinaryPrimitives.WriteInt32LittleEndian(bytes[0..4], ebx);
                BinaryPrimitives.WriteInt32LittleEndian(bytes[4..8], edx);
                BinaryPrimitives.WriteInt32LittleEndian(bytes[8..12], ecx);
                string str = Encoding.ASCII.GetString(bytes);
                if (!string.IsNullOrWhiteSpace(str)) return str;
            }
            catch { }
        }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer FROM Win32_Processor");
            foreach (var obj in searcher.Get())
            {
                string? m = obj["Manufacturer"]?.ToString();
                if (!string.IsNullOrWhiteSpace(m)) return m;
            }
        }
        catch { }

        return "Unknown";
    }

    private static bool DetectAmdCpu() =>
        Vendor.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
        Vendor.Contains("AuthenticAMD", StringComparison.OrdinalIgnoreCase);

    private static bool DetectIntelCpu() =>
        Vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
        Vendor.Contains("GenuineIntel", StringComparison.OrdinalIgnoreCase);
}
