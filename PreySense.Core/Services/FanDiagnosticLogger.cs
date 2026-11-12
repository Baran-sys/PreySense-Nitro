using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Text;
using PreySense.Core.Configuration;

namespace PreySense.Core.Services
{
    public readonly record struct FanDiagnosticEntry(
        DateTime TimestampUtc,
        string PowerMode,
        string ControlMode,
        int CpuTempC,
        int GpuTempC,
        int CpuTargetRpm,
        int GpuTargetRpm,
        int SentCpuDuty,
        int SentGpuDuty,
        int MeasuredCpuRpm,
        int MeasuredGpuRpm,
        string EcState,
        bool EmergencyActive);

    /// <summary>
    /// Thread-safe CSV diagnostic logger for fan telemetry and control loop events.
    /// Rotates active log to fanlog.prev.csv when reaching the configured size threshold (~10 MB).
    /// </summary>
    public sealed class FanDiagnosticLogger
    {
        public const string CsvHeader =
            "TimestampUtc,PowerMode,ControlMode,CpuTempC,GpuTempC,CpuTargetRpm,GpuTargetRpm,SentCpuDuty,SentGpuDuty,MeasuredCpuRpm,MeasuredGpuRpm,EcState,EmergencyActive";

        public const long DefaultMaxFileSizeBytes = 10L * 1024L * 1024L;

        public static readonly FanDiagnosticLogger Shared = new();

        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
        private static readonly SearchValues<char> CsvSpecialChars = SearchValues.Create([',', '"', '\r', '\n']);
        private readonly object _gate = new();
        private readonly long _maxFileSizeBytes;
        private volatile bool _enabled;

        public string LogFilePath { get; }
        public string PreviousLogFilePath { get; }

        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        public FanDiagnosticLogger()
            : this(logFilePath: null, maxFileSizeBytes: DefaultMaxFileSizeBytes, enabled: false)
        {
        }

        public FanDiagnosticLogger(string? logFilePath, long maxFileSizeBytes = DefaultMaxFileSizeBytes, bool enabled = false)
        {
            _maxFileSizeBytes = maxFileSizeBytes > 0 ? maxFileSizeBytes : DefaultMaxFileSizeBytes;
            _enabled = enabled;
            LogFilePath = ResolveLogPath(logFilePath);
            PreviousLogFilePath = ResolvePreviousLogPath(LogFilePath);
        }

        private static string ResolveLogPath(string? customPath)
        {
            if (!string.IsNullOrWhiteSpace(customPath))
                return Path.GetFullPath(customPath);

            string configDir = Path.GetDirectoryName(ConfigurationService.Instance.ConfigPath)
                ?? AppContext.BaseDirectory;
            return Path.Combine(configDir, "fanlog.csv");
        }

        private static string ResolvePreviousLogPath(string logFilePath)
        {
            string dir = Path.GetDirectoryName(logFilePath) ?? AppContext.BaseDirectory;
            string name = Path.GetFileNameWithoutExtension(logFilePath);
            string ext = Path.GetExtension(logFilePath);
            if (string.IsNullOrEmpty(ext)) ext = ".csv";
            return Path.Combine(dir, $"{name}.prev{ext}");
        }

        public void Log(
            string powerMode,
            string controlMode,
            int cpuTempC,
            int gpuTempC,
            int cpuTargetRpm,
            int gpuTargetRpm,
            int sentCpuDuty,
            int sentGpuDuty,
            int measuredCpuRpm,
            int measuredGpuRpm,
            string ecState,
            bool emergencyActive,
            DateTime? timestampUtc = null)
        {
            if (!_enabled)
                return;

            var entry = new FanDiagnosticEntry(
                TimestampUtc: (timestampUtc ?? DateTime.UtcNow).ToUniversalTime(),
                PowerMode: powerMode ?? string.Empty,
                ControlMode: controlMode ?? string.Empty,
                CpuTempC: cpuTempC,
                GpuTempC: gpuTempC,
                CpuTargetRpm: cpuTargetRpm,
                GpuTargetRpm: gpuTargetRpm,
                SentCpuDuty: sentCpuDuty,
                SentGpuDuty: sentGpuDuty,
                MeasuredCpuRpm: measuredCpuRpm,
                MeasuredGpuRpm: measuredGpuRpm,
                EcState: ecState ?? string.Empty,
                EmergencyActive: emergencyActive);

            Log(in entry);
        }

        public void Log(in FanDiagnosticEntry entry)
        {
            if (!_enabled)
                return;

            string line = FormatCsvLine(in entry);

            lock (_gate)
            {
                if (!_enabled)
                    return;

                try
                {
                    string? dir = Path.GetDirectoryName(LogFilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    RotateIfNeeded();

                    bool writeHeader = !File.Exists(LogFilePath) || new FileInfo(LogFilePath).Length == 0;
                    using var stream = new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    using var writer = new StreamWriter(stream, Utf8NoBom);
                    if (writeHeader)
                        writer.WriteLine(CsvHeader);
                    writer.WriteLine(line);
                }
                catch
                {
                    // Diagnostic logging must never interrupt fan control or telemetry.
                }
            }
        }

        private void RotateIfNeeded()
        {
            if (!File.Exists(LogFilePath))
                return;

            var info = new FileInfo(LogFilePath);
            if (info.Length < _maxFileSizeBytes)
                return;

            if (File.Exists(PreviousLogFilePath))
                File.Delete(PreviousLogFilePath);

            File.Move(LogFilePath, PreviousLogFilePath);
        }

        public static string FormatCsvLine(in FanDiagnosticEntry entry)
        {
            DateTime utc = entry.TimestampUtc.Kind == DateTimeKind.Utc
                ? entry.TimestampUtc
                : entry.TimestampUtc.ToUniversalTime();

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{utc:O},{EscapeCsv(entry.PowerMode)},{EscapeCsv(entry.ControlMode)},{entry.CpuTempC},{entry.GpuTempC},{entry.CpuTargetRpm},{entry.GpuTargetRpm},{entry.SentCpuDuty},{entry.SentGpuDuty},{entry.MeasuredCpuRpm},{entry.MeasuredGpuRpm},{EscapeCsv(entry.EcState)},{(entry.EmergencyActive ? "true" : "false")}");
        }

        private static string EscapeCsv(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            if (!value.AsSpan().ContainsAny(CsvSpecialChars))
                return value;

            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }
    }
}
