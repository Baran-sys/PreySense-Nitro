using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using PreySense.Core.Configuration;
using PreySense.Core.Services;
using PreySense.Frontend.Controls;
using PreySense.Frontend.Services;
using PreySense.Frontend.ViewModels;
using PreySense.Mode;

namespace PreySense.Frontend.Views;

public partial class FanCurveWindow : Window
{
    private static readonly byte[] Modes = [6, 0, 1, 4, 5];
    private readonly Dictionary<byte, FanCurveProfile> _drafts = new();
    private readonly List<int> _visibleStageIndices = new();
    private int[] _dutyValues = Array.Empty<int>();
    private int[] _temperatureLabels = Array.Empty<int>();
    private int _leadInDuty;
    private byte _mode = 1;
    private bool _gpu, _loading, _applying;
    private FanCurveProfile _profile = new();
    private CancellationTokenSource? _calibrationCts;
    private MainViewModel? Vm => DataContext as MainViewModel;

    public FanCurveWindow()
    {
        InitializeComponent();
        RowDuty.IsValueEnabled = false;
        Curve.Edited += (_, _) => OnCurveEdited();
        Curve.SelectionChanged += (_, _) => SyncSelectedStage();
        Curve.PointAddRequested += (_, pt) => OnPointAddRequested(pt.Celsius, pt.Duty);
        UpdateEditor();
        UpdateCalibrationStatus();
        ConstrainWindowHeight();
        Loaded += (_, _) =>
        {
            ConstrainWindowHeight();
            if (Vm == null) return;
            _loading = true;
            _mode = Vm.ActivePowerMode;
            ProfileRow.SelectedItem = ProfileManager.ModeToProfileName(_mode);
            SyncAllModesRow.IsChecked = ConfigurationService.Instance.GetCurrent().General.SyncAllFanModes;
            _loading = false;
            LoadProfile();
            UpdateCalibrationStatus();
        };
        Closed += (_, _) => _calibrationCts?.Cancel();
        ProfileRow.AddHandler(ComboBox.SelectionChangedEvent, new SelectionChangedEventHandler(Profile_Changed));
        UseEcAutoBelowMinRow.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler(UseEcAutoBelowMin_Changed));
        UseEcAutoBelowMinRow.AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler(UseEcAutoBelowMin_Changed));
        SafeQuietIdleBaseRow.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler(SafeQuietIdleBase_Changed));
        SafeQuietIdleBaseRow.AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler(SafeQuietIdleBase_Changed));
    }

    private static FanCalibrationData? GetActiveCalibration()
    {
        try
        {
            return ConfigurationService.Instance.GetCurrent().Fans.Calibration;
        }
        catch
        {
            return null;
        }
    }

    private List<FanCurvePoint> ActivePoints => _gpu ? _profile.GpuPoints : _profile.CpuPoints;

    private static string FormatFanCalibrationSummary(string label, SingleFanCalibration fan)
    {
        int minRpm = fan.MinQuietRpm > 0 ? fan.MinQuietRpm : fan.MinStableRpm;
        string summary = $"{label} {minRpm}-{fan.MaxRpm} RPM";
        if (fan.EcAutoBaselineRpm <= 0 && fan.BlackoutMaxDuty <= 0)
            return summary;

        var details = new List<string>(2);
        if (fan.EcAutoBaselineRpm > 0)
            details.Add(LocalizationService.Format("Auto (~{0})", fan.EcAutoBaselineRpm));
        if (fan.BlackoutMaxDuty > 0)
            details.Add($"KickZone 1-{fan.BlackoutMaxDuty}%");
        return $"{summary} ({string.Join(", ", details)})";
    }

    private int GetActiveFanMinFloorRpm(FanCalibrationData? cal)
    {
        var fanCal = _gpu ? cal?.Gpu : cal?.Cpu;
        if (fanCal is { IsCalibrated: true })
        {
            int calFloor = fanCal.MinQuietRpm > 0 ? fanCal.MinQuietRpm : fanCal.MinStableRpm;
            if (calFloor > 0)
                return calFloor;
        }
        return 1800;
    }

    private static int GetEffectiveAutoBaselineRpm(FanCalibrationData? cal, bool gpu)
    {
        int recommendedAuto = cal != null ? cal.GetRecommendedIdleAutoRpm(gpu) : 0;
        if (recommendedAuto > 0)
            return recommendedAuto;
        var fanCal = gpu ? cal?.Gpu : cal?.Cpu;
        return fanCal is { EcAutoBaselineRpm: > 0 } ? fanCal.EcAutoBaselineRpm : 1800;
    }

    private string FormatZeroRpmRoutingText(FanCalibrationData? cal)
    {
        bool useEcAuto = UseEcAutoBelowMinRow?.IsChecked == true;
        if (useEcAuto)
        {
            int autoRpm = GetEffectiveAutoBaselineRpm(cal, _gpu);
            return LocalizationService.Format("0 RPM -> Auto (~{0} RPM)", autoRpm);
        }

        int activeFloor = GetActiveFanMinFloorRpm(cal);
        int configuredFloor = MinRpmFloorRow != null ? (int)Math.Round(MinRpmFloorRow.Value) : _profile.MinRpmFloor;
        int effectiveFloor = Math.Max(activeFloor, configuredFloor);
        return LocalizationService.Format("0 RPM -> Min Manual ({0} RPM)", effectiveFloor);
    }

    private bool IsSelectedPointZeroRpm(FanCalibrationData? cal)
    {
        if (Curve == null || SelectedStage is not FanStage stage)
            return false;

        int visible = Curve.SelectedIndex;
        var pts = ActivePoints;
        int rpm = visible >= 0 && visible < pts.Count
            ? Math.Max(0, pts[visible].Rpm)
            : FanCurveProfile.ConvertDutyToRpm(stage.DutyPercent(_gpu), _gpu, cal);
        return rpm == 0;
    }

    internal static string FormatCalibrationStatusSummary(FanCalibrationData? cal)
    {
        if (cal is not { IsCalibrated: true })
            return LocalizationService.Translate("Not calibrated - click Calibrate Fans");

        string stamp = cal.CalibratedAtUtc.HasValue
            ? cal.CalibratedAtUtc.Value.ToLocalTime().ToString("g")
            : LocalizationService.Translate("calibrated");
        string baseText = LocalizationService.Format(
            "Calibrated ({0}): {1}, {2}",
            stamp,
            FormatFanCalibrationSummary("CPU", cal.Cpu),
            FormatFanCalibrationSummary("GPU", cal.Gpu));

        if (cal.HasIdleProbeData)
        {
            if (cal.PreferManualZeroIdle)
            {
                baseText += " | " + LocalizationService.Format(
                    "Idle: Manual 0% (~{0}/{1} RPM)",
                    cal.ManualZeroCpuRpm,
                    cal.ManualZeroGpuRpm);
            }
            else
            {
                var (idleCpu, idleGpu) = cal.GetIdleAutoRpms(cal.RecommendedIdleEcMode);
                if (idleCpu > 0 || idleGpu > 0)
                {
                    string modeName = LocalizationService.Translate(
                        ProfileManager.ModeToProfileName(cal.RecommendedIdleEcMode));
                    baseText += " | " + LocalizationService.Format(
                        "Idle: {0} Auto (~{1}/{2} RPM)",
                        modeName,
                        idleCpu,
                        idleGpu);
                }
            }
        }

        return baseText;
    }

    private void UpdateCalibrationStatus(string? liveStatus = null)
    {
        if (CalibrationRow == null || CalibrationStatusText == null)
            return;

        bool calibrating = _calibrationCts != null;
        CalibrationRow.ActionText = LocalizationService.Translate(calibrating ? "Cancel" : "Calibrate Fans");
        CalibrationRow.IsEnabled = true;

        if (!string.IsNullOrEmpty(liveStatus))
        {
            CalibrationStatusText.Text = liveStatus;
            return;
        }

        var cal = GetActiveCalibration();
        string baseText = FormatCalibrationStatusSummary(cal);

        if (IsSelectedPointZeroRpm(cal))
            baseText = $"{baseText} | {FormatZeroRpmRoutingText(cal)}";

        CalibrationStatusText.Text = baseText;
    }

    private async void CalibrateFans_Click(object sender, RoutedEventArgs e)
    {
        if (_calibrationCts != null)
        {
            _calibrationCts.Cancel();
            return;
        }

        if (Vm == null || _applying)
            return;

        _calibrationCts = new CancellationTokenSource();
        SetApplying(true);
        bool keepResultMessage = false;
        UpdateCalibrationStatus(LocalizationService.Translate("Starting calibration..."));
        try
        {
            var progress = new Progress<FanCalibrationProgress>(p =>
            {
                string cpuTempText = p.CpuTempC > 0 ? $"CPU {p.CpuTempC}C" : "CPU N/A";
                string gpuTempText = p.GpuTempC > 0 ? $", GPU {p.GpuTempC}C" : string.Empty;
                UpdateCalibrationStatus(
                    LocalizationService.Format("Calibrating {0}% ({1}/{2}): CPU {3} RPM, GPU {4} RPM ({5}{6})",
                        p.DutyPercent, p.StepIndex, p.TotalSteps, p.CpuRpm, p.GpuRpm, cpuTempText, gpuTempText));
            });

            var result = await Vm.RunFanCalibrationAsync(progress, _calibrationCts.Token);
            if (!result.Success)
            {
                keepResultMessage = true;
                UpdateCalibrationStatus(result.Message);
                return;
            }

            var cal = GetActiveCalibration();
            _profile.SyncStagesFromPoints(cal);
            UpdateMinRpmFloorBounds(cal);
            SyncCurve();
            UpdateCalibrationStatus();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Fan calibration failed: {ex}");
            keepResultMessage = true;
            UpdateCalibrationStatus(LocalizationService.Format("Calibration failed: {0}", ex.Message));
        }
        finally
        {
            _calibrationCts?.Dispose();
            _calibrationCts = null;
            SetApplying(false);
            if (!keepResultMessage)
                UpdateCalibrationStatus();
            else if (CalibrationRow != null)
                CalibrationRow.ActionText = LocalizationService.Translate("Calibrate Fans");
        }
    }

    private void SaveDraft()
    {
        _profile.ControlMode = MaxOption.IsChecked == true ? FanControlMode.Max
            : CustomOption.IsChecked == true ? FanControlMode.Custom : FanControlMode.Auto;
        _profile.RampUpSeconds = Math.Clamp((int)Math.Round(RampUpRow.Value), 0, 10);
        _profile.RampDownSeconds = Math.Clamp((int)Math.Round(RampDownRow.Value), 0, 10);
        _profile.HysteresisCelsius = Math.Clamp((int)Math.Round(HysteresisRow.Value), 0, 5);
        int minAllowed = (int)Math.Round(MinRpmFloorRow.Minimum);
        int maxAllowed = (int)Math.Round(MinRpmFloorRow.Maximum);
        _profile.MinRpmFloor = Math.Clamp((int)Math.Round(MinRpmFloorRow.Value), minAllowed, maxAllowed);
        _profile.UseEcAutoBelowMin = UseEcAutoBelowMinRow.IsChecked == true;
        _profile.SafeQuietIdleBase = SafeQuietIdleBaseRow.IsChecked == true;
        _drafts[_mode] = _profile;
    }

    internal void LoadProfile()
    {
        _loading = true;
        _profile = _drafts.TryGetValue(_mode, out var draft) ? draft : Vm?.LoadFanProfile(_mode) ?? FanCurveProfile.CreateDefault(_mode);
        AutoOption.IsChecked = _profile.ControlMode == FanControlMode.Auto;
        MaxOption.IsChecked = _profile.ControlMode == FanControlMode.Max;
        CustomOption.IsChecked = _profile.ControlMode == FanControlMode.Custom;
        RampUpRow.Value = Math.Clamp(_profile.RampUpSeconds, 0, 10);
        RampDownRow.Value = Math.Clamp(_profile.RampDownSeconds, 0, 10);
        HysteresisRow.Value = Math.Clamp(_profile.HysteresisCelsius, 0, 5);
        UpdateMinRpmFloorBounds(GetActiveCalibration());
        UseEcAutoBelowMinRow.IsChecked = _profile.UseEcAutoBelowMin;
        SafeQuietIdleBaseRow.IsChecked = _profile.SafeQuietIdleBase;
        SyncSliderValueTexts();
        Curve.SelectedIndex = -1;
        _loading = false;
        UpdateEditor();
        SyncCurve();
    }

    private void UpdateMinRpmFloorBounds(FanCalibrationData? cal)
    {
        if (MinRpmFloorRow == null) return;
        int minStable = 1800;
        if (cal != null)
        {
            int cpuMin = cal.Cpu is { IsCalibrated: true }
                ? (cal.Cpu.MinQuietRpm > 0 ? cal.Cpu.MinQuietRpm : cal.Cpu.MinStableRpm)
                : 0;
            int gpuMin = cal.Gpu is { IsCalibrated: true }
                ? (cal.Gpu.MinQuietRpm > 0 ? cal.Gpu.MinQuietRpm : cal.Gpu.MinStableRpm)
                : 0;
            if (cpuMin > 0 && gpuMin > 0)
                minStable = Math.Min(cpuMin, gpuMin);
            else if (cpuMin > 0)
                minStable = cpuMin;
            else if (gpuMin > 0)
                minStable = gpuMin;
        }
        int maxAllowed = Math.Max(minStable, 3000);
        MinRpmFloorRow.Minimum = minStable;
        MinRpmFloorRow.Maximum = maxAllowed;
        MinRpmFloorRow.Value = Math.Clamp(_profile.MinRpmFloor, minStable, maxAllowed);
    }

    private void CurveSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        SyncSliderValueTexts();
        if (CustomOption.IsChecked == true)
            SaveDraft();
    }

    private void MinRpmFloor_Committed(object? sender, EventArgs e)
    {
        if (_loading) return;
        if (CustomOption.IsChecked == true)
            SaveDraft();
        SyncSelectedStage();
    }

    private void UseEcAutoBelowMin_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (CustomOption.IsChecked == true)
            SaveDraft();
        SyncSelectedStage();
    }

    private void SafeQuietIdleBase_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (CustomOption.IsChecked == true)
            SaveDraft();
    }

    private void SyncSliderValueTexts()
    {
        if (RampUpRow != null)
            RampUpRow.ValueText = $"{Math.Clamp((int)Math.Round(RampUpRow.Value), 0, 10)} s";
        if (RampDownRow != null)
            RampDownRow.ValueText = $"{Math.Clamp((int)Math.Round(RampDownRow.Value), 0, 10)} s";
        if (HysteresisRow != null)
            HysteresisRow.ValueText = $"{Math.Clamp((int)Math.Round(HysteresisRow.Value), 0, 5)} °C";
    }

    internal static (int[] StageIndices, int[] Temperatures, int[] Duties, int LeadInDuty)
        BuildGraph(FanStage[] stages, bool gpu)
    {
        // Reachable via this fan's rise. Disabled (127) rise stops the chain.
        var candidates = new List<(int Stage, int EntryC, int Duty)>();
        var reachable = new List<int>();
        for (int i = 0; i < stages.Length; i++)
        {
            if (i > 0 && !stages[i - 1].HasActiveRise(gpu))
                break;
            reachable.Add(i);
        }

        int leadInDuty = reachable.Count > 0 ? stages[0].DutyPercent(gpu) : 0;
        for (int v = 0; v < reachable.Count; v++)
        {
            int stageIndex = reachable[v];
            if (stageIndex == 0)
                continue;

            var stage = stages[stageIndex];
            int duty = stage.DutyPercent(gpu);
            if (!stages[stageIndex - 1].HasActiveRise(gpu))
                continue;
            int entryC = Math.Clamp(
                stages[stageIndex - 1].RiseCelsius(gpu),
                WpfFanCurveControl.MinCelsius,
                WpfFanCurveControl.MaxCelsius);

            // Unique X: bump past a colliding previous entry instead of overlaying.
            if (candidates.Count > 0 && entryC <= candidates[^1].EntryC)
                entryC = Math.Min(WpfFanCurveControl.MaxCelsius, candidates[^1].EntryC + 1);
            if (entryC > WpfFanCurveControl.MaxCelsius)
                continue;
            candidates.Add((stageIndex, entryC, duty));
        }

        return (candidates.Select(point => point.Stage).ToArray(),
            candidates.Select(point => point.EntryC).ToArray(),
            candidates.Select(point => point.Duty).ToArray(), leadInDuty);
    }

    private void RebuildVisibleStages()
    {
        var graph = BuildGraph(_profile.Stages, _gpu);
        _visibleStageIndices.Clear();
        _visibleStageIndices.AddRange(graph.StageIndices);
        _temperatureLabels = graph.Temperatures;
        _dutyValues = graph.Duties;
        _leadInDuty = graph.LeadInDuty;
    }

    private void SyncCurve()
    {
        int previousStage = SelectedStageIndex;
        RebuildVisibleStages();
        Curve.Temperatures = _temperatureLabels;
        Curve.Values = _dutyValues;
        Curve.LeadInDuty = _leadInDuty;
        int visible = previousStage < 0 ? -1 : _visibleStageIndices.IndexOf(previousStage);
        Curve.SelectedIndex = visible;
        Curve.InvalidateVisual();
        SyncSelectedStage();
    }

    private int SelectedStageIndex
    {
        get
        {
            int visible = Curve.SelectedIndex;
            return visible >= 0 && visible < _visibleStageIndices.Count
                ? _visibleStageIndices[visible]
                : -1;
        }
    }

    private void EnsureActivePointsInitialized(FanCalibrationData? cal)
    {
        var list = ActivePoints;
        if (list.Count > 0)
            return;

        for (int i = 0; i < _temperatureLabels.Length && i < _dutyValues.Length; i++)
        {
            int temp = Math.Clamp(_temperatureLabels[i], WpfFanCurveControl.MinCelsius, WpfFanCurveControl.MaxCelsius);
            int rpm = FanCurveProfile.ConvertDutyToRpm(_dutyValues[i], _gpu, cal);
            list.Add(new FanCurvePoint(temp, rpm));
        }
    }

    private void OnCurveEdited()
    {
        if (_loading || CustomOption.IsChecked != true || Curve.SelectedIndex < 0) return;
        int visible = Curve.SelectedIndex;
        if (visible < 0 || visible >= _temperatureLabels.Length || visible >= _dutyValues.Length) return;

        var cal = GetActiveCalibration();
        EnsureActivePointsInitialized(cal);
        if (visible >= ActivePoints.Count) return;

        WpfFanCurveControl.SetTemperature(_temperatureLabels, visible, _temperatureLabels[visible]);
        int duty = Math.Clamp(_dutyValues[visible], 0, 100);
        _profile.UpdatePointFromGraph(_gpu, visible, _temperatureLabels[visible], duty, cal);

        _temperatureLabels[visible] = ActivePoints[visible].TempC;
        _dutyValues[visible] = FanCurveProfile.ConvertRpmToDuty(ActivePoints[visible].Rpm, _gpu, cal);
        Curve.InvalidateVisual();
        SyncSelectedStage();
        SaveDraft();
    }

    private void OnPointAddRequested(int celsius, int duty)
    {
        if (_loading || _applying || CustomOption.IsChecked != true) return;

        var cal = GetActiveCalibration();
        EnsureActivePointsInitialized(cal);
        if (!_profile.TryInsertPoint(
            _gpu,
            celsius,
            duty,
            WpfFanCurveControl.MinCelsius,
            WpfFanCurveControl.MaxCelsius,
            cal,
            out int insertedVisible))
        {
            return;
        }

        SyncCurve();
        Curve.SelectedIndex = insertedVisible;
        SaveDraft();
    }

    private FanStage? SelectedStage
    {
        get
        {
            int index = SelectedStageIndex;
            return index >= 0 ? _profile.Stages[index] : null;
        }
    }

    private void SyncSelectedStage()
    {
        RowDuty.IsValueEnabled = false;
        if (SelectedStage is not FanStage stage)
        {
            RowTemp.IsEnabled = false;
            RowDuty.IsEnabled = false;
            RowRpm.IsEnabled = false;
            RowTemp.Title = LocalizationService.Translate("Temp");
            RowDuty.Title = LocalizationService.Translate("Duty");
            RowRpm.Title = LocalizationService.Translate("RPM");
            if (_calibrationCts == null)
                UpdateCalibrationStatus();
            return;
        }

        bool isCustom = CustomOption.IsChecked == true;
        _loading = true;
        RowTemp.IsEnabled = isCustom;
        RowDuty.IsEnabled = isCustom;
        RowRpm.IsEnabled = isCustom;
        string fan = _gpu ? "GPU" : "CPU";
        int visible = Curve.SelectedIndex;
        int celsius = visible >= 0 && visible < _temperatureLabels.Length
            ? _temperatureLabels[visible]
            : WpfFanCurveControl.MinCelsius;
        RowTemp.Title = LocalizationService.Translate($"{fan} Temp");
        RowDuty.Title = LocalizationService.Translate($"{fan} Duty");
        RowDuty.Minimum = 0;
        RowDuty.Maximum = 100;

        var cal = GetActiveCalibration();
        int maxRpm = FanCurveProfile.GetMaxRpm(_gpu, cal);

        RowRpm.Minimum = 0;
        RowRpm.Maximum = maxRpm;
        var (minTemp, maxTemp) = WpfFanCurveControl.TemperatureBounds(_temperatureLabels, visible);
        RowTemp.Minimum = minTemp;
        RowTemp.Maximum = maxTemp;
        RowTemp.Value = celsius;

        var pts = ActivePoints;
        int rpm = visible >= 0 && visible < pts.Count
            ? Math.Clamp(pts[visible].Rpm, 0, maxRpm)
            : FanCurveProfile.ConvertDutyToRpm(stage.DutyPercent(_gpu), _gpu, cal);
        int duty = FanCurveProfile.ConvertRpmToDuty(rpm, _gpu, cal);

        string rpmLabel = LocalizationService.Translate($"{fan} RPM");
        if (rpm == 0)
        {
            bool useEcAuto = UseEcAutoBelowMinRow?.IsChecked == true;
            int autoRpm = GetEffectiveAutoBaselineRpm(cal, _gpu);
            RowRpm.Title = useEcAuto
                ? LocalizationService.Format("{0} (Auto ~{1})", rpmLabel, autoRpm)
                : LocalizationService.Format("{0} (Min Manual)", rpmLabel);
        }
        else
        {
            RowRpm.Title = rpmLabel;
        }

        RowRpm.Value = rpm;
        RowDuty.Value = duty;
        _loading = false;
        if (_calibrationCts == null)
            UpdateCalibrationStatus();
        UpdateEditor();
    }

    private void StageField_Committed(object? sender, EventArgs e)
    {
        if (_loading || CustomOption.IsChecked != true || SelectedStage is not FanStage stage) return;
        int visible = Curve.SelectedIndex;
        int stageIndex = SelectedStageIndex;
        if (visible < 0 || stageIndex < 0) return;

        var cal = GetActiveCalibration();
        EnsureActivePointsInitialized(cal);

        if (ReferenceEquals(sender, RowTemp))
        {
            var (minTemp, maxTemp) = WpfFanCurveControl.TemperatureBounds(_temperatureLabels, visible);
            int clampedTemp = Math.Clamp((int)Math.Round(RowTemp.Value), minTemp, maxTemp);
            _profile.UpdatePointTemperature(_gpu, visible, clampedTemp, cal);
        }
        else if (ReferenceEquals(sender, RowRpm))
        {
            int committedRpm = (int)Math.Round(RowRpm.Value);
            if (committedRpm > 0)
            {
                int minFloor = GetActiveFanMinFloorRpm(cal);
                if (committedRpm < minFloor)
                    committedRpm = minFloor;
            }
            _profile.UpdatePointRpm(_gpu, visible, committedRpm, cal);
        }
        else if (ReferenceEquals(sender, RowDuty))
        {
            int duty = Math.Clamp((int)Math.Round(RowDuty.Value), 0, 100);
            int rpm = FanCurveProfile.ConvertDutyToRpm(duty, _gpu, cal);
            _profile.UpdatePointRpm(_gpu, visible, rpm, cal);
        }

        stage.Normalize();
        SyncCurve();
        SaveDraft();
    }

    private async void FanMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _applying) return;
        SaveDraft();
        UpdateEditor();
        SyncSelectedStage();
        if (Vm != null && !_applying)
        {
            SetApplying(true);
            try
            {
                await SaveProfileAsync(Vm, _mode, _profile);
                if (await EnsureModeActiveAsync(Vm))
                    await Vm.ApplyFanProfileAsync();
            }
            catch (Exception ex) { AppLogger.Error($"Fan operation failed: {ex}"); }
            finally { SetApplying(false); }
        }
    }

    private void ConstrainWindowHeight()
    {
        double workHeight = SystemParameters.WorkArea.Height;
        MaxHeight = Math.Max(360, workHeight - 40);
        if (ContentScroll != null)
        {
            bool footerVisible = FooterPanel != null && FooterPanel.Visibility == Visibility.Visible;
            double chromeHeight = footerVisible ? 102 : 56;
            ContentScroll.MaxHeight = Math.Max(240, MaxHeight - chromeHeight);
        }
    }

    private void UpdateEditor()
    {
        if (CurveEditor == null) return;
        bool isCustom = CustomOption.IsChecked == true;
        CurveEditor.IsEnabled = isCustom && !_applying;
        CurveEditor.Opacity = isCustom ? 1.0 : 0.45;
        if (FooterPanel != null)
            FooterPanel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        ConstrainWindowHeight();
    }

    private async void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _applying || !IsLoaded || Vm == null) return;
        int index = Array.IndexOf(Vm.FanCurveModeNames, ProfileRow.SelectedItem as string);
        if (index < 0 || Modes[index] == _mode) return;
        CommitActiveFields();
        SaveDraft();
        _mode = Modes[index];
        LoadProfile();
        await Vm.SetModeAsync(_mode);
    }

    private void FanTab_Changed(object sender, RoutedEventArgs e)
    {
        if (Curve == null) return;
        Curve.SelectedIndex = -1;
        _gpu = FanTabs.SelectedIndex == 1;
        SyncCurve();
    }

    private async System.Threading.Tasks.Task<bool> EnsureModeActiveAsync(MainViewModel vm)
    {
        if (await System.Threading.Tasks.Task.Run(() => vm.IsGpuHardwareModeActive(_mode))) return true;
        await vm.ApplyModeSwitchAsync(_mode);
        return await System.Threading.Tasks.Task.Run(() => vm.IsGpuHardwareModeActive(_mode));
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (Vm == null || _applying) return;
        SetApplying(true);
        try
        {
            var defaultProfile = FanCurveProfile.CreateDefault(_mode);
            defaultProfile.ControlMode = FanControlMode.Custom;
            _drafts[_mode] = defaultProfile;
            if (SyncAllModesRow.IsChecked == true)
            {
                foreach (byte m in Modes)
                {
                    var clone = defaultProfile.Copy();
                    _drafts[m] = clone;
                    await SaveProfileAsync(Vm, m, clone);
                }
            }
            else
            {
                await SaveProfileAsync(Vm, _mode, defaultProfile);
            }
            LoadProfile();
            if (!await EnsureModeActiveAsync(Vm)) return;
            await Vm.ApplyFanProfileAsync();
        }
        catch (Exception ex) { AppLogger.Error($"Fan operation failed: {ex}"); }
        finally { SetApplying(false); }
    }

    private void SetApplying(bool applying)
    {
        _applying = applying;
        ProfileRow.IsEnabled = AutoOption.IsEnabled = MaxOption.IsEnabled = CustomOption.IsEnabled = !applying;
        SyncAllModesRow.IsEnabled = !applying;
        if (CalibrationRow != null)
            CalibrationRow.IsEnabled = !applying || _calibrationCts != null;
        FooterPanel.IsEnabled = !applying;
        UpdateEditor();
    }

    private static System.Threading.Tasks.Task SaveProfileAsync(MainViewModel vm, byte mode, FanCurveProfile profile)
    {
        var snapshot = profile.Copy();
        return System.Threading.Tasks.Task.Run(() => vm.SaveFanProfile(mode, snapshot));
    }

    private void CommitActiveFields()
    {
        if (CustomOption.IsChecked != true) return;
        if (MinRpmFloorRow.IsKeyboardFocusWithin) MinRpmFloorRow.CommitText(notify: true);
        if (Curve.SelectedIndex < 0) return;
        if (RowTemp.IsKeyboardFocusWithin) RowTemp.CommitText(notify: true);
        if (RowRpm.IsKeyboardFocusWithin) RowRpm.CommitText(notify: true);
    }

    private async System.Threading.Tasks.Task SaveAllAsync()
    {
        if (Vm is not { } vm) return;
        CommitActiveFields();
        SaveDraft();
        bool syncAll = SyncAllModesRow.IsChecked == true;
        if (syncAll && _drafts.TryGetValue(_mode, out var activeProfile))
        {
            foreach (byte m in Modes)
            {
                _drafts[m] = activeProfile.Copy();
            }
        }
        var snapshots = _drafts.Select(entry => (entry.Key, Profile: entry.Value.Copy())).ToArray();
        await System.Threading.Tasks.Task.Run(() =>
        {
            ConfigurationService.Instance.Update(cfg => cfg.General.SyncAllFanModes = syncAll);
            foreach (var (mode, profile) in snapshots) vm.SaveFanProfile(mode, profile);
        });
        _drafts.Clear();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_applying) return;
        SetApplying(true);
        try { await SaveAllAsync(); }
        catch (Exception ex) { AppLogger.Error($"Fan save failed: {ex}"); }
        finally { SetApplying(false); }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (Vm == null || _applying) return;
        SetApplying(true);
        try
        {
            await SaveAllAsync();
            if (!await EnsureModeActiveAsync(Vm)) return;
            await Vm.ApplyFanProfileAsync();
        }
        catch (Exception ex) { AppLogger.Error($"Fan operation failed: {ex}"); }
        finally { SetApplying(false); }
    }
}
