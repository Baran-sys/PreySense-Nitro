using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PreySense.Frontend.Services;

namespace PreySense.Frontend.ViewModels
{
    public partial class MainViewModel
    {
        // Acer OEM Services Debloater
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsAcerServicesEnabled))]
        private bool _isAcerServicesDebloated = false;
        [ObservableProperty] private string _debloaterStatusText = "Acer OEM Services: Active";
        public bool IsAcerServicesEnabled => !IsAcerServicesDebloated;

        // Battery Care Limit
        [ObservableProperty] private bool _isBatteryLimitEnabled = true;
        [ObservableProperty] private int _batteryLimitPercent = 80;

        // Platform Features
        private int _smartBoostEpoch;
        private LatestBackgroundWorker<(int Mode, int Epoch)>? _smartBoostWorker;
        private LatestBackgroundWorker<(int Mode, int Epoch)> SmartBoostWorker => _smartBoostWorker ??= new(
            req =>
            {
                if (req.Epoch == System.Threading.Volatile.Read(ref _smartBoostEpoch))
                    PreySense.Mode.PowerLimitController.SetCpuBoost(req.Mode);
            },
            (_, latest) => latest);

        private PreySense.Core.Services.SmartBoostController? _smartBoost;
        public PreySense.Core.Services.SmartBoostController SmartBoost =>
            _smartBoost ??= new(mode => SmartBoostWorker.Submit((mode, System.Threading.Volatile.Read(ref _smartBoostEpoch))));
        [ObservableProperty] private bool _isSmartBoostEnabled = true;

        internal void BumpSmartBoostEpoch() => System.Threading.Interlocked.Increment(ref _smartBoostEpoch);

        internal void UpdateSmartBoostDemand()
        {
            bool active = !_disposed && IsSmartBoostEnabled && SmartBoost.BasePerfBoostMode > 0;
            PreySense.Core.Services.TelemetryCoordinator.SetDemand(PreySense.Core.Services.TelemetryDemand.SmartBoost, active);
        }

        partial void OnIsSmartBoostEnabledChanged(bool value)
        {
            SmartBoost.IsEnabled = value;
            UpdateSmartBoostDemand();
            if (!value) SmartBoost.Reset();
            if (_disposed) return;
            try
            {
                PreySense.Core.Configuration.ConfigurationService.Instance.Update(cfg => cfg.General.SmartBoostEnabled = value);
            }
            catch { }
            DiagnosticMessage = value ? "Smart Boost enabled." : "Smart Boost disabled.";
        }

        [ObservableProperty] private bool _isRunOnStartupEnabled = false;
        [ObservableProperty] private bool _isBootSoundEnabled = false;
        [ObservableProperty] private bool _isTrayIconDisabled = false;
        [ObservableProperty] private bool _isFanDiagnosticsLogEnabled = false;
        internal Action<bool>? TrayIconVisibilityHandler { get; set; }

        partial void OnIsFanDiagnosticsLogEnabledChanged(bool value)
        {
            PreySense.Core.Services.FanDiagnosticLogger.Shared.Enabled = value;
            UpdateFanDemand();
            if (_disposed) return;
            try
            {
                PreySense.Core.Configuration.ConfigurationService.Instance.Update(cfg => cfg.General.FanDiagnosticsLogEnabled = value);
            }
            catch { }
            DiagnosticMessage = value ? "Fan diagnostic log enabled." : "Fan diagnostic log disabled.";
        }

        partial void OnIsTrayIconDisabledChanged(bool value)
        {
            if (_disposed) return;
            try
            {
                PreySense.Core.Configuration.ConfigurationService.Instance.Update(cfg => cfg.General.DisableTrayIcon = value);
            }
            catch { }
            TrayIconVisibilityHandler?.Invoke(!value);
            DiagnosticMessage = value ? "Tray icon disabled." : "Tray icon enabled.";
        }

        private int _startupPreferenceRevision;
        private int _startupPreferenceReads;
        private bool _startupPreferenceWriteInProgress;
        public bool CanChangeRunOnStartup => _startupPreferenceReads == 0 && !_startupPreferenceWriteInProgress;

        private Task? _startupPreferenceRefresh;
        internal Func<bool>? StartupPreferenceReader { get; set; }

        internal Task RefreshStartupPreferenceAsync()
        {
            if (_disposed) return Task.CompletedTask;
            // Opening Settings during initial discovery shares the active query.
            if (_startupPreferenceRefresh is { IsCompleted: false }) return _startupPreferenceRefresh;
            return _startupPreferenceRefresh = RefreshStartupPreferenceCoreAsync();
        }

        private async Task RefreshStartupPreferenceCoreAsync()
        {
            _startupPreferenceReads++;
            OnPropertyChanged(nameof(CanChangeRunOnStartup));
            try
            {
                int revision = _startupPreferenceRevision;
                var read = StartupPreferenceReader ?? StartupTaskService.ReconcileExistingTask;
                bool enabled = await Task.Run(read);
                if (!_disposed && revision == _startupPreferenceRevision) IsRunOnStartupEnabled = enabled;
            }
            catch (Exception ex) { AppLogger.Error($"Startup preference read failed: {ex}"); }
            finally
            {
                _startupPreferenceReads--;
                OnPropertyChanged(nameof(CanChangeRunOnStartup));
            }
        }

        [RelayCommand]
        public void ExportSettings()
        {
            if (_disposed) return;
            try
            {
                var sfd = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Export PreySense Settings",
                    Filter = "JSON Files (*.json)|*.json",
                    FileName = "preysense_backup.json",
                    DefaultExt = ".json"
                };

                if (sfd.ShowDialog() == true)
                {
                    PreySense.Core.Configuration.ConfigurationService.Instance.ExportToFile(sfd.FileName);
                    DiagnosticMessage = "Settings exported successfully.";
                }
            }
            catch (Exception ex)
            {
                DiagnosticMessage = $"Export failed: {ex.Message}";
            }
        }

        [RelayCommand]
        public async Task CheckForUpdatesAsync()
        {
            if (_disposed) return;
            await Services.UpdateCheckService.CheckForUpdatesAsync(isManualCheck: true);
        }

        [RelayCommand]
        public async Task ImportSettingsAsync()
        {
            if (_disposed) return;
            try
            {
                var ofd = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Import PreySense Settings",
                    Filter = "JSON Files (*.json)|*.json",
                    Multiselect = false
                };

                if (ofd.ShowDialog() == true)
                {
                    IsBusy = true;
                    DiagnosticMessage = "Importing settings...";
                    await Task.Run(() => PreySense.Core.Configuration.ConfigurationService.Instance.ImportFromFile(ofd.FileName));
                    LoadSessionPreferences();
                    await RestoreSessionSettingsAsync("import");
                    DiagnosticMessage = "Settings imported successfully.";
                }
            }
            catch (Exception ex)
            {
                DiagnosticMessage = $"Import failed: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        public async Task ToggleAcerServicesDebloaterAsync()
        {
            IsBusy = true;
            DiagnosticMessage = IsAcerServicesDebloated ? "Restoring Acer Services..." : "Debloating Acer OEM Services...";
            try
            {
                if (IsAcerServicesDebloated)
                {
                    await Task.Run(() => PreySense.Core.Services.AcerServiceDebloater.EnableAndStartAll());
                    IsAcerServicesDebloated = false;
                    DebloaterStatusText = "Acer OEM Services: Active";
                    DiagnosticMessage = "Acer Services Restored";
                }
                else
                {
                    await Task.Run(() => PreySense.Core.Services.AcerServiceDebloater.StopAndDisableAll());
                    IsAcerServicesDebloated = true;
                    DebloaterStatusText = "Acer OEM Services: Stopped & Disabled (~400MB Saved)";
                    DiagnosticMessage = "Acer Services Debloated";
                }
            }
            catch (Exception ex)
            {
                DiagnosticMessage = $"Error: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        public async Task ToggleRunOnStartupAsync()
        {
            if (_disposed) return;
            _startupPreferenceRevision++;
            _startupPreferenceWriteInProgress = true;
            OnPropertyChanged(nameof(CanChangeRunOnStartup));
            try
            {
                bool enable = IsRunOnStartupEnabled;
                var result = await Task.Run(() =>
                {
                    bool success = StartupTaskService.SetStartupEnabled(enable);
                    // Create already verifies XML readback; a successful delete is serialized with repair.
                    return (success, enabled: success ? enable : StartupTaskService.IsStartupEnabled());
                });
                if (_disposed) return;
                IsRunOnStartupEnabled = result.enabled;
                bool success = result.success && result.enabled == enable;
                if (success)
                {
                    DiagnosticMessage = enable ? "Run on Startup: Enabled (Elevated Task)" : "Run on Startup: Disabled";
                }
                else
                {
                    IsRunOnStartupEnabled = result.enabled;
                    DiagnosticMessage = "Startup config failed: check permissions";
                }
            }
            catch (Exception ex)
            {
                DiagnosticMessage = $"Startup config failed: {ex.Message}";
            }
            finally
            {
                _startupPreferenceWriteInProgress = false;
                OnPropertyChanged(nameof(CanChangeRunOnStartup));
            }
        }

        [RelayCommand]
        public async Task ToggleBootSoundAsync()
        {
            if (_disposed) return;
            bool enabled = !IsBootSoundEnabled;
            try
            {
                await Task.Run(() => _hw.SetBootSound(enabled));
                if (_disposed) return;
                IsBootSoundEnabled = enabled;
                DiagnosticMessage = enabled ? "Predator Boot Sound & Animation: Enabled" : "Predator Boot Sound: Muted";
            }
            catch (Exception ex) { DiagnosticMessage = $"Boot sound update failed: {ex.Message}"; }
        }

        [RelayCommand]
        public async Task ToggleBatteryLimitAsync()
        {
            if (_disposed) return;
            bool enabled = !IsBatteryLimitEnabled;
            try
            {
                await Task.Run(() => _hw.SetBatteryChargeLimit(enabled ? 1 : 0));
                if (_disposed) return;
                IsBatteryLimitEnabled = enabled;
                BatteryLimitPercent = enabled ? 80 : 100;
                DiagnosticMessage = enabled ? "Battery Care Limit Active (80%)" : "Battery Charge Limit Disabled (100%)";
            }
            catch (Exception ex) { DiagnosticMessage = $"Battery limit update failed: {ex.Message}"; }
        }

        [RelayCommand]
        public async Task SetBatteryLimitPercentAsync(object? param)
        {
            int percent = param switch
            {
                int i => i,
                double d => (int)Math.Round(d),
                string s when int.TryParse(s, out int parsed) => parsed,
                _ => 80
            };

            if (_disposed) return;
            bool enable = percent <= 80;
            try
            {
                await Task.Run(() => _hw.SetBatteryChargeLimit(enable ? 1 : 0));
                if (_disposed) return;
                BatteryLimitPercent = percent;
                IsBatteryLimitEnabled = enable;
                DiagnosticMessage = enable ? $"Battery Care Limit: {percent}% (Preserving Health)" : "Battery Charge Limit: 100% (Full Capacity)";
            }
            catch (Exception ex) { DiagnosticMessage = $"Battery limit update failed: {ex.Message}"; }
        }

    }
}
