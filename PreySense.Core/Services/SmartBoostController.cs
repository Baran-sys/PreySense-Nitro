using System;
using PreySense.Mode;

namespace PreySense.Core.Services;

/// <summary>
/// Predictive multi-tier CPU thermal protection controller for Intel and AMD processors.
/// Combines thermal rise-rate lookahead (dT/dt) with staggered Windows PERFBOOSTMODE stepping
/// (Base -> Efficient Aggressive -> Efficient Enabled -> Disabled) and 1-tier-at-a-time hysteresis recovery
/// so systems avoid hitting TjMax thermal throttling while keeping smooth frame rates.
/// </summary>
public sealed class SmartBoostController
{
    private readonly object _gate = new();
    private readonly Func<long> _clockMs;

    private int _currentTier;
    private int _recoveryCount;
    private int? _lastAppliedMode;
    private bool _hasPrevSample;
    private int _prevTemp;
    private long _prevTimestampMs;
    private double _tempSlopePerSec;

    public bool IsEnabled { get; set; } = true;
    public int BasePerfBoostMode { get; set; } = 2; // 2=Aggressive default

    public int PredictiveArmCelsius { get; set; } = 78;
    public double LookaheadSeconds { get; set; } = 2.0;

    public int SoftThrottleCelsius { get; set; } = 84;
    public int ModerateThrottleCelsius { get; set; } = 88;
    public int EmergencyThrottleCelsius { get; set; } = 93;
    public int EmergencyPredictiveCelsius { get; set; } = 95;

    public int SoftRecoveryCelsius { get; set; } = 76;
    public int ModerateRecoveryCelsius { get; set; } = 81;
    public int EmergencyRecoveryCelsius { get; set; } = 85;

    public int SoftRecoverySamples { get; set; } = 5;
    public int ModerateRecoverySamples { get; set; } = 4;
    public int EmergencyRecoverySamples { get; set; } = 3;

    public int CurrentTier
    {
        get { lock (_gate) { return _currentTier; } }
    }

    public bool IsThrottlingMitigated
    {
        get { lock (_gate) { return _currentTier > 0; } }
    }

    public int CurrentAppliedBoostMode
    {
        get { lock (_gate) { return _lastAppliedMode ?? BasePerfBoostMode; } }
    }

    public Action<int> BoostApplier { get; set; }

    public SmartBoostController(Action<int>? boostApplier = null, Func<long>? clockMsProvider = null)
    {
        BoostApplier = boostApplier ?? (mode => PowerLimitController.SetCpuBoost(mode));
        _clockMs = clockMsProvider ?? (() => Environment.TickCount64);
    }

    /// <summary>Updates the user's configured base PERFBOOSTMODE and clears active mitigation state.</summary>
    public void SetBaseMode(int boostMode)
    {
        lock (_gate)
        {
            BasePerfBoostMode = Math.Clamp(boostMode, 0, 6);
            _currentTier = 0;
            _recoveryCount = 0;
            _lastAppliedMode = null;
            _hasPrevSample = false;
            _tempSlopePerSec = 0;
        }
    }

    public void ProcessTelemetry(int? cpuTemp, long? timestampMs = null)
    {
        int? modeToApply = null;
        string? logMessage = null;
        long nowMs = timestampMs ?? _clockMs();

        lock (_gate)
        {
            if (!IsEnabled || BasePerfBoostMode <= 0)
            {
                if (_currentTier > 0)
                {
                    int restore = Math.Clamp(BasePerfBoostMode, 0, 6);
                    _currentTier = 0;
                    _recoveryCount = 0;
                    _hasPrevSample = false;
                    _tempSlopePerSec = 0;
                    if (_lastAppliedMode != restore)
                    {
                        _lastAppliedMode = restore;
                        modeToApply = restore;
                        logMessage = $"[SmartBoost] Restored base boost mode {restore}.";
                    }
                }
            }
            else if (cpuTemp is int temp && temp > 0)
            {
                UpdateThermalTrend(temp, nowMs);

                double lookahead = temp >= PredictiveArmCelsius
                    ? Math.Clamp(_tempSlopePerSec, 0.0, 15.0) * LookaheadSeconds
                    : 0.0;
                double effectiveTemp = temp + lookahead;

                int desiredTier = ComputeDesiredTier(temp, effectiveTemp);
                if (desiredTier > _currentTier)
                {
                    int previousTier = _currentTier;
                    _currentTier = desiredTier;
                    _recoveryCount = 0;
                    int targetMode = ResolveTierBoostMode(_currentTier, BasePerfBoostMode);
                    if (targetMode != (_lastAppliedMode ?? BasePerfBoostMode))
                    {
                        _lastAppliedMode = targetMode;
                        modeToApply = targetMode;
                        logMessage = $"[SmartBoost] Escalated Tier {previousTier}->{_currentTier} (mode={targetMode}, T={temp}C, Teff={effectiveTemp:F1}C, dT/dt={_tempSlopePerSec:F1}C/s).";
                    }
                }
                else if (_currentTier > 0 && desiredTier < _currentTier)
                {
                    var (recoveryTemp, requiredSamples) = RecoveryPolicyForTier(_currentTier);
                    if (temp <= recoveryTemp && _tempSlopePerSec <= 0.5)
                    {
                        _recoveryCount++;
                        if (_recoveryCount >= requiredSamples)
                        {
                            int previousTier = _currentTier;
                            _currentTier--;
                            _recoveryCount = 0;
                            int targetMode = ResolveTierBoostMode(_currentTier, BasePerfBoostMode);
                            if (targetMode != (_lastAppliedMode ?? BasePerfBoostMode))
                            {
                                _lastAppliedMode = targetMode;
                                modeToApply = targetMode;
                                logMessage = $"[SmartBoost] Recovered Tier {previousTier}->{_currentTier} (mode={targetMode}, T={temp}C).";
                            }
                        }
                    }
                    else
                    {
                        _recoveryCount = 0;
                    }
                }
                else
                {
                    // Inside hysteresis hold band for the current tier.
                    _recoveryCount = 0;
                }
            }
        }

        if (modeToApply.HasValue)
        {
            try { BoostApplier(modeToApply.Value); } catch { }
            if (logMessage != null) AppLogger.Log(logMessage);
        }
    }

    public void Reset()
    {
        int? modeToApply = null;
        lock (_gate)
        {
            if (_currentTier > 0)
            {
                int restore = Math.Clamp(BasePerfBoostMode, 0, 6);
                _currentTier = 0;
                if (_lastAppliedMode != restore)
                {
                    _lastAppliedMode = restore;
                    modeToApply = restore;
                }
            }
            _recoveryCount = 0;
            _hasPrevSample = false;
            _tempSlopePerSec = 0;
        }

        if (modeToApply.HasValue)
        {
            try { BoostApplier(modeToApply.Value); } catch { }
        }
    }

    private void UpdateThermalTrend(int temp, long nowMs)
    {
        if (_hasPrevSample)
        {
            double dtSec = (nowMs - _prevTimestampMs) / 1000.0;
            if (dtSec is >= 0.2 and <= 5.0)
            {
                double rawSlope = (temp - _prevTemp) / dtSec;
                _tempSlopePerSec += 0.5 * (rawSlope - _tempSlopePerSec);
            }
            else if (dtSec > 5.0)
            {
                _tempSlopePerSec = 0;
            }
        }
        _prevTemp = temp;
        _prevTimestampMs = nowMs;
        _hasPrevSample = true;
    }

    private int ComputeDesiredTier(int temp, double effectiveTemp)
    {
        if (temp >= EmergencyThrottleCelsius || effectiveTemp >= EmergencyPredictiveCelsius)
            return 3;
        if (effectiveTemp >= ModerateThrottleCelsius)
            return 2;
        if (effectiveTemp >= SoftThrottleCelsius)
            return 1;
        return 0;
    }

    private (int RecoveryTemp, int RequiredSamples) RecoveryPolicyForTier(int tier) => tier switch
    {
        3 => (EmergencyRecoveryCelsius, Math.Max(1, EmergencyRecoverySamples)),
        2 => (ModerateRecoveryCelsius, Math.Max(1, ModerateRecoverySamples)),
        _ => (SoftRecoveryCelsius, Math.Max(1, SoftRecoverySamples))
    };

    /// <summary>
    /// Maps a mitigation tier onto Windows PERFBOOSTMODE while never exceeding the configured base mode's aggressiveness.
    /// Rank order: 0 (Disabled) &lt; 3 (Efficient Enabled) &lt; 1 (Enabled) &lt; 4 (Efficient Aggressive) &lt; 2 (Aggressive) &lt; 6 &lt; 5.
    /// </summary>
    public static int ResolveTierBoostMode(int tier, int baseMode)
    {
        baseMode = Math.Clamp(baseMode, 0, 6);
        if (baseMode == 0 || tier >= 3)
            return 0;
        if (tier <= 0)
            return baseMode;

        int desired = tier switch
        {
            1 => 4, // Efficient Aggressive
            2 => 3, // Efficient Enabled
            _ => 0
        };

        int baseRank = AggressivenessRank(baseMode);
        if (AggressivenessRank(desired) < baseRank)
            return desired;

        // When baseMode itself is already less aggressive (e.g., 1=Enabled or 3=Efficient Enabled), step one rank lower.
        return baseMode switch
        {
            1 => 3,
            3 => 0,
            _ => desired
        };
    }

    private static int AggressivenessRank(int perfBoostMode) => perfBoostMode switch
    {
        0 => 0,
        3 => 1,
        1 => 2,
        4 => 3,
        2 => 4,
        6 => 5,
        5 => 6,
        _ => 2
    };
}
