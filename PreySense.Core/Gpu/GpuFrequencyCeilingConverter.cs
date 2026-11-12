using System;

namespace PreySense.Gpu;

public static class GpuFrequencyCeilingConverter
{
    public const int MinWatts = 35;
    public const int MaxWatts = 175;
    public const int MinMhz = 800;
    public const int MaxMhz = 2700;

    private static readonly (int w, int mhz)[] Table =
    [
        (35, 900),
        (45, 1150),
        (60, 1400),
        (75, 1650),
        (95, 1850),
        (115, 2100),
        (140, 2400),
        (175, 2650)
    ];

    /// <summary>
    /// Converts a target GPU wattage to estimated maximum boost clock frequency ceiling (MHz).
    /// Scaled against typical NVIDIA mobile Ada Lovelace &amp; Ampere V/F curves.
    /// </summary>
    public static int WattsToFrequencyCeilingMhz(int watts)
    {
        int clampedWatts = Math.Clamp(watts, MinWatts, MaxWatts);
        for (int i = 0; i < Table.Length - 1; i++)
        {
            if (clampedWatts >= Table[i].w && clampedWatts <= Table[i + 1].w)
            {
                double ratio = (double)(clampedWatts - Table[i].w) / (Table[i + 1].w - Table[i].w);
                double mhz = Table[i].mhz + ratio * (Table[i + 1].mhz - Table[i].mhz);
                return (int)(Math.Round(mhz / 25.0) * 25.0); // snap to nearest 25 MHz
            }
        }
        return clampedWatts < Table[0].w ? Table[0].mhz : Table[^1].mhz;
    }

    /// <summary>
    /// Converts a frequency ceiling (MHz) to estimated target wattage.
    /// </summary>
    public static int FrequencyCeilingMhzToWatts(int mhz)
    {
        int clampedMhz = Math.Clamp(mhz, MinMhz, MaxMhz);
        for (int i = 0; i < Table.Length - 1; i++)
        {
            if (clampedMhz >= Table[i].mhz && clampedMhz <= Table[i + 1].mhz)
            {
                double ratio = (double)(clampedMhz - Table[i].mhz) / (Table[i + 1].mhz - Table[i].mhz);
                double w = Table[i].w + ratio * (Table[i + 1].w - Table[i].w);
                return (int)Math.Round(w);
            }
        }
        return clampedMhz < Table[0].mhz ? Table[0].w : Table[^1].w;
    }
}
