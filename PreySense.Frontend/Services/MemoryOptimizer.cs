using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PreySense.Frontend.Services;

/// <summary>
/// Compacts managed heaps and releases unused working-set pages asynchronously in the background
/// without blocking startup or window restore animations.
/// </summary>
internal static class MemoryOptimizer
{
    private static int _trimInProgress;
    private static long _lastTrimTicks;
    private static long _lastEmptyWorkingSetTicks;

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

    public static void SchedulePostStartupOptimization()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(3500).ConfigureAwait(false);
            CompactAndTrimCore(aggressiveWorkingSetTrim: false);
            await Task.Delay(6500).ConfigureAwait(false);
            CompactAndTrimCore(aggressiveWorkingSetTrim: true);
        });
    }

    public static void ScheduleBackgroundTrim(int delayMs = 350, bool aggressiveWorkingSetTrim = true)
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastTrimTicks) < 1500)
            return;

        _ = Task.Run(async () =>
        {
            if (delayMs > 0)
                await Task.Delay(delayMs).ConfigureAwait(false);
            CompactAndTrimCore(aggressiveWorkingSetTrim);
        });
    }

    private static void CompactAndTrimCore(bool aggressiveWorkingSetTrim)
    {
        if (Interlocked.Exchange(ref _trimInProgress, 1) != 0)
            return;

        try
        {
            long now = Environment.TickCount64;
            Interlocked.Exchange(ref _lastTrimTicks, now);
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

            if (aggressiveWorkingSetTrim)
            {
                long lastEmpty = Interlocked.Read(ref _lastEmptyWorkingSetTicks);
                using var proc = Process.GetCurrentProcess();
                IntPtr handle = proc.Handle;
                _ = SetProcessWorkingSetSize(handle, (IntPtr)(-1), (IntPtr)(-1));
                // Throttle full working-set eviction so reopening windows quickly never suffers page-in stutter.
                if (lastEmpty == 0 || now - lastEmpty >= 300_000)
                {
                    Interlocked.Exchange(ref _lastEmptyWorkingSetTicks, now);
                    _ = EmptyWorkingSet(handle);
                }
            }
        }
        catch
        {
            // Ignore memory compaction errors on restricted handles.
        }
        finally
        {
            Volatile.Write(ref _trimInProgress, 0);
        }
    }
}
