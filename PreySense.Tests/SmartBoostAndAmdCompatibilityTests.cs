using System.Collections.Generic;
using PreySense.Core.Hardware;
using PreySense.Core.Services;
using PreySense.Helpers;
using PreySense.Mode;

namespace PreySense.Tests;

[TestClass]
public class SmartBoostAndAmdCompatibilityTests
{
    [TestMethod]
    public void SmartBoost_EngagesMultiTierMitigationOnRisingTemperature()
    {
        var appliedModes = new List<int>();
        var controller = new SmartBoostController(mode => appliedModes.Add(mode))
        {
            BasePerfBoostMode = 2 // Aggressive
        };

        // Normal steady warmup (slow ~0.5°C/s rise, no fast spike)
        controller.ProcessTelemetry(83, timestampMs: 1000);
        Assert.IsFalse(controller.IsThrottlingMitigated);
        Assert.AreEqual(0, controller.CurrentTier);
        Assert.IsEmpty(appliedModes);

        // Tier 1 Soft (84°C gentle): steps down to Efficient Aggressive (4) instead of killing boost
        controller.ProcessTelemetry(84, timestampMs: 3000);
        Assert.IsTrue(controller.IsThrottlingMitigated);
        Assert.AreEqual(1, controller.CurrentTier);
        Assert.HasCount(1, appliedModes);
        Assert.AreEqual(4, appliedModes[0]);

        // Tier 2 Moderate (88°C gentle): steps down to Efficient Enabled (3)
        controller.ProcessTelemetry(88, timestampMs: 8000);
        Assert.AreEqual(2, controller.CurrentTier);
        Assert.HasCount(2, appliedModes);
        Assert.AreEqual(3, appliedModes[1]);

        // Tier 3 Emergency (93°C): disables boost (0) to stop TjMax cooking
        controller.ProcessTelemetry(93, timestampMs: 14000);
        Assert.AreEqual(3, controller.CurrentTier);
        Assert.HasCount(3, appliedModes);
        Assert.AreEqual(0, appliedModes[2]);

        // Subsequent emergency sample: no redundant applier call
        controller.ProcessTelemetry(94, timestampMs: 15000);
        Assert.HasCount(3, appliedModes);
    }

    [TestMethod]
    public void SmartBoost_PredictiveTrendPreventsSpikeOvershoot()
    {
        var appliedModes = new List<int>();
        var controller = new SmartBoostController(mode => appliedModes.Add(mode))
        {
            BasePerfBoostMode = 2
        };

        controller.ProcessTelemetry(76, timestampMs: 1000);
        Assert.AreEqual(0, controller.CurrentTier);

        // Rapid +9°C/s jump (76 -> 85 in 1s): EMA slope ~4.5°C/s -> Teff = 85 + 9 = 94°C (>= 88°C Moderate / near 95°C)
        controller.ProcessTelemetry(85, timestampMs: 2000);
        Assert.IsGreaterThanOrEqualTo(2, controller.CurrentTier, "Predictive lookahead should escalate past Tier 1 on fast heat spikes.");
        Assert.IsNotEmpty(appliedModes);
    }

    [TestMethod]
    public void SmartBoost_RecoversStepByStepWithHysteresis()
    {
        var appliedModes = new List<int>();
        var controller = new SmartBoostController(mode => appliedModes.Add(mode))
        {
            BasePerfBoostMode = 2,
            SoftRecoverySamples = 2,
            ModerateRecoverySamples = 2,
            EmergencyRecoverySamples = 2
        };

        // Enter Tier 3
        controller.ProcessTelemetry(94, timestampMs: 1000);
        Assert.AreEqual(3, controller.CurrentTier);

        // Stabilize <= 85°C for 2 samples -> steps down 1 tier to Tier 2 (mode 3)
        controller.ProcessTelemetry(84, timestampMs: 2000);
        Assert.AreEqual(3, controller.CurrentTier);
        controller.ProcessTelemetry(84, timestampMs: 3000);
        Assert.AreEqual(2, controller.CurrentTier);
        Assert.AreEqual(3, appliedModes[^1]);

        // Stabilize <= 81°C for 2 samples -> steps down to Tier 1 (mode 4)
        controller.ProcessTelemetry(80, timestampMs: 4000);
        controller.ProcessTelemetry(80, timestampMs: 5000);
        Assert.AreEqual(1, controller.CurrentTier);
        Assert.AreEqual(4, appliedModes[^1]);

        // Cool below 76°C -> first sample only, then temp bumps up resetting counter
        controller.ProcessTelemetry(75, timestampMs: 6000);
        controller.ProcessTelemetry(79, timestampMs: 7000);
        Assert.AreEqual(1, controller.CurrentTier);

        // Two consecutive <= 76°C samples -> returns to BasePerfBoostMode (2)
        controller.ProcessTelemetry(75, timestampMs: 8000);
        controller.ProcessTelemetry(74, timestampMs: 9000);
        Assert.AreEqual(0, controller.CurrentTier);
        Assert.IsFalse(controller.IsThrottlingMitigated);
        Assert.AreEqual(2, appliedModes[^1]);
    }

    [TestMethod]
    public void SmartBoost_RespectsBaseModeCeilingAndResetRestoresBase()
    {
        // When user's base mode is Enabled (1), Tier 1 must step down to Efficient Enabled (3), never up to 4.
        Assert.AreEqual(3, SmartBoostController.ResolveTierBoostMode(tier: 1, baseMode: 1));
        Assert.AreEqual(0, SmartBoostController.ResolveTierBoostMode(tier: 1, baseMode: 0));

        var appliedModes = new List<int>();
        var controller = new SmartBoostController(mode => appliedModes.Add(mode))
        {
            BasePerfBoostMode = 2
        };

        controller.ProcessTelemetry(95, timestampMs: 1000);
        Assert.IsTrue(controller.IsThrottlingMitigated);
        Assert.HasCount(1, appliedModes);

        // Immediate Reset restores BasePerfBoostMode without waiting for telemetry ticks
        controller.Reset();
        Assert.IsFalse(controller.IsThrottlingMitigated);
        Assert.HasCount(2, appliedModes);
        Assert.AreEqual(2, appliedModes[1]);
    }

    [TestMethod]
    public void SmartBoost_DisablingRestoresBaseBoostMode()
    {
        var appliedModes = new List<int>();
        var controller = new SmartBoostController(mode => appliedModes.Add(mode))
        {
            BasePerfBoostMode = 2
        };

        controller.ProcessTelemetry(95, timestampMs: 1000);
        Assert.IsTrue(controller.IsThrottlingMitigated);
        Assert.HasCount(1, appliedModes);

        // Disabling should immediately unthrottle and restore base mode
        controller.IsEnabled = false;
        controller.ProcessTelemetry(95, timestampMs: 2000);
        Assert.IsFalse(controller.IsThrottlingMitigated);
        Assert.HasCount(2, appliedModes);
        Assert.AreEqual(2, appliedModes[1]);
    }

    [TestMethod]
    public void SmartBoost_IgnoresInvalidTemperatures()
    {
        var appliedModes = new List<int>();
        var controller = new SmartBoostController(mode => appliedModes.Add(mode));

        controller.ProcessTelemetry(null);
        controller.ProcessTelemetry(-1);
        controller.ProcessTelemetry(0);

        Assert.IsFalse(controller.IsThrottlingMitigated);
        Assert.IsEmpty(appliedModes);
    }

    [TestMethod]
    public void CpuVendorHelper_ReturnsValidVendorProperties()
    {
        string vendor = CpuVendorHelper.Vendor;
        Assert.IsFalse(string.IsNullOrWhiteSpace(vendor));
        bool isAmd = CpuVendorHelper.IsAmd;
        bool isIntel = CpuVendorHelper.IsIntel;
        Assert.IsTrue(isAmd || isIntel || vendor == "Unknown");
    }

    [TestMethod]
    public void DeviceHelper_TriggerPnpReenumeration_ExecutesSafely()
    {
        // Must never throw an unhandled exception even in test sandbox
        DeviceHelper.TriggerPnpReenumeration();
    }

    [TestMethod]
    public void NativeCpuInformation_SampleClocks_ReturnsSensibleValuesOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var sample = NativeCpuInformation.SampleClocks();
        Assert.IsNotNull(sample);
        Assert.IsGreaterThan(0, sample.Value.CoreCount);
        Assert.IsGreaterThan(0, sample.Value.MaxCurrentMhz);
        Assert.IsGreaterThan(0, sample.Value.AverageCurrentMhz);
        Assert.IsGreaterThan(0, sample.Value.MaxNominalMhz);
    }
}
