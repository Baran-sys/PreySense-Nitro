using System.Text.Json;
using PreySense.Core.Configuration;
using PreySense.Gpu;

namespace PreySense.Tests;

[TestClass]
public class GpuClockLockFallbackTests
{
    [TestMethod]
    public void GpuFrequencyCeilingConverter_ScalesWattsToFrequencyCorrectly()
    {
        Assert.AreEqual(1400, GpuFrequencyCeilingConverter.WattsToFrequencyCeilingMhz(60));
        Assert.AreEqual(1650, GpuFrequencyCeilingConverter.WattsToFrequencyCeilingMhz(75));
        Assert.AreEqual(1850, GpuFrequencyCeilingConverter.WattsToFrequencyCeilingMhz(95));
        Assert.AreEqual(2100, GpuFrequencyCeilingConverter.WattsToFrequencyCeilingMhz(115));
        Assert.AreEqual(2400, GpuFrequencyCeilingConverter.WattsToFrequencyCeilingMhz(140));
    }

    [TestMethod]
    public void GpuFrequencyCeilingConverter_ScalesFrequencyToWattsCorrectly()
    {
        Assert.AreEqual(60, GpuFrequencyCeilingConverter.FrequencyCeilingMhzToWatts(1400));
        Assert.AreEqual(75, GpuFrequencyCeilingConverter.FrequencyCeilingMhzToWatts(1650));
        Assert.AreEqual(95, GpuFrequencyCeilingConverter.FrequencyCeilingMhzToWatts(1850));
        Assert.AreEqual(115, GpuFrequencyCeilingConverter.FrequencyCeilingMhzToWatts(2100));
        Assert.AreEqual(140, GpuFrequencyCeilingConverter.FrequencyCeilingMhzToWatts(2400));
    }

    [TestMethod]
    public void GpuFrequencyCeilingConverter_ClampsBoundaryValues()
    {
        Assert.AreEqual(900, GpuFrequencyCeilingConverter.WattsToFrequencyCeilingMhz(10));
        Assert.AreEqual(2650, GpuFrequencyCeilingConverter.WattsToFrequencyCeilingMhz(300));

        Assert.AreEqual(35, GpuFrequencyCeilingConverter.FrequencyCeilingMhzToWatts(400));
        Assert.AreEqual(175, GpuFrequencyCeilingConverter.FrequencyCeilingMhzToWatts(3200));
    }

    [TestMethod]
    public void ModeTuningConfig_GpuClockLock_PropertiesAndValidation()
    {
        var config = new ModeTuningConfig
        {
            GpuClockLockWatts = 75,
            GpuFrequencyCeilingMhz = 1650,
            ApplyGpuClockLock = true
        };

        Assert.IsTrue(config.HasGpuClockLockValues);
        Assert.IsTrue(config.HasValues);

        string json = JsonSerializer.Serialize(config);
        var deserialized = JsonSerializer.Deserialize<ModeTuningConfig>(json);

        Assert.IsNotNull(deserialized);
        Assert.IsTrue(deserialized.ApplyGpuClockLock);
        Assert.AreEqual(75, deserialized.GpuClockLockWatts);
        Assert.AreEqual(1650, deserialized.GpuFrequencyCeilingMhz);
    }

    [TestMethod]
    public void ModeTuningConfig_HasValues_RequiresClockLockValuesWhenApplyGpuClockLockIsTrue()
    {
        var config = new ModeTuningConfig
        {
            ApplyGpuClockLock = true,
            GpuClockLockWatts = null,
            GpuFrequencyCeilingMhz = null
        };

        Assert.IsFalse(config.HasGpuClockLockValues);
        Assert.IsFalse(config.HasValues);

        config.GpuClockLockWatts = 95;
        Assert.IsTrue(config.HasGpuClockLockValues);
        Assert.IsTrue(config.HasValues);
    }
}
