using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PreySense.Core.Services;
using PreySense.Helpers;
using PreySense.Mode;

namespace PreySense.Tests
{
    [TestClass]
    public class PowerPolicyAndFanTests
    {
        [TestMethod]
        public void OverlayScheme_MapsEcoAndQuietToEfficiency()
        {
            Assert.AreEqual(WindowsPowerPolicy.OverlayEfficiency, WindowsPowerPolicy.OverlaySchemeForMode(0x00));
            Assert.AreEqual(WindowsPowerPolicy.OverlayEfficiency, WindowsPowerPolicy.OverlaySchemeForMode(0x06));
        }

        [TestMethod]
        public void OverlayScheme_MapsBalancedToBalanced()
        {
            Assert.AreEqual(WindowsPowerPolicy.OverlayBalanced, WindowsPowerPolicy.OverlaySchemeForMode(0x01));
        }

        [TestMethod]
        public void OverlayScheme_MapsPerformanceAndTurboToPerformance()
        {
            Assert.AreEqual(WindowsPowerPolicy.OverlayPerformance, WindowsPowerPolicy.OverlaySchemeForMode(0x04));
            Assert.AreEqual(WindowsPowerPolicy.OverlayPerformance, WindowsPowerPolicy.OverlaySchemeForMode(0x05));
        }

        [TestMethod]
        public void ProfileManager_UsesQuietNameAndDropsOverlayValues()
        {
            Assert.AreEqual("Quiet", ProfileManager.ModeToProfileName(0x00));
            string source = File.ReadAllText(Path.Combine(GetSolutionRoot(), "PreySense.Core", "Mode", "PerformanceProfile.cs"));
            Assert.DoesNotContain("WindowsPowerMode", source);
        }

        [TestMethod]
        public void WindowFooters_UseSharedBottomMargin()
        {
            string root = GetSolutionRoot();
            string spacing = File.ReadAllText(Path.Combine(root, "PreySense.Frontend", "Styles", "Spacing.xaml"));
            string controls = File.ReadAllText(Path.Combine(root, "PreySense.Frontend", "Styles", "Controls.xaml"));
            string mainWindow = File.ReadAllText(Path.Combine(root, "PreySense.Frontend", "MainWindow.xaml"));
            Assert.Contains("x:Key=\"WindowFooterMargin\"", spacing);
            Assert.Contains("11,6,11,10", spacing);
            Assert.Contains("{StaticResource WindowFooterMargin}", controls);
            Assert.DoesNotContain("Margin=\"0,10,0,0\"", mainWindow);
        }

        [TestMethod]
        public void FansFooterOpensPerModeEditor()
        {
            string root = GetSolutionRoot();
            string mainWindow = File.ReadAllText(Path.Combine(root, "PreySense.Frontend", "MainWindow.xaml"));
            Assert.Contains("FansButton_Click", mainWindow);
            Assert.DoesNotContain("CustomCpuFanPercent", mainWindow);
            Assert.IsTrue(File.Exists(Path.Combine(root, "PreySense.Frontend", "Views", "FanCurveWindow.xaml")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "PreySense.Frontend", "Views", "FanCurveWindow.xaml.cs")));
        }

        [TestMethod]
        public void ModeSettingsWindow_UsesCpuBoostDropdownWithoutLegacyExtras()
        {
            string root = GetSolutionRoot();
            string xaml = File.ReadAllText(Path.Combine(root, "PreySense.Frontend", "Views", "ModeSettingsWindow.xaml"));
            Assert.Contains("DropdownCpuBoost", xaml);
            Assert.DoesNotContain("SwitchCpuBoost", xaml);
            Assert.DoesNotContain("ComboCpuBoost", xaml);
            Assert.DoesNotContain("CPU Turbo Boost", xaml);
            Assert.DoesNotContain("Windows Power", xaml);
            Assert.DoesNotContain("ApplyOverlayScheme", xaml);
            Assert.DoesNotContain("GpuCoreOffset", xaml);
            Assert.DoesNotContain("GpuMemoryOffset", xaml);
        }

        [TestMethod]
        public void SettingsWindow_RemovesIgpuOnBatteryAndCpuBoost()
        {
            string root = GetSolutionRoot();
            string xaml = File.ReadAllText(Path.Combine(root, "PreySense.Frontend", "Views", "SettingsWindow.xaml"));
            Assert.DoesNotContain("iGPU on Battery", xaml);
            Assert.DoesNotContain("CPU Boost", xaml);
            Assert.DoesNotContain("IsGpuBatteryAutoEnabled", xaml);
            Assert.DoesNotContain("CpuBoostModes", xaml);
            Assert.DoesNotContain("ResetAdvancedSettingsCommand", xaml);
            Assert.DoesNotContain("ApplyAdvancedSettingsCommand", xaml);
            Assert.DoesNotContain("SaveAdvancedSettingsCommand", xaml);
            Assert.DoesNotContain("Factory Defaults", xaml);
            Assert.DoesNotContain("ViewFactoryDefaultsCommand", xaml);
        }

        [TestMethod]
        public void DisplayManager_NoLongerExposesColorProfiles()
        {
            string root = GetSolutionRoot();
            string source = File.ReadAllText(Path.Combine(root, "PreySense.Core", "Display", "DisplayManager.cs"));
            Assert.DoesNotContain("class DisplayColorProfile", source);
            Assert.DoesNotContain("LoadProfile(int hz)", source);
        }

        [TestMethod]
        public void EcFanPolicy_ClampsDutyToZeroThrough100()
        {
            Assert.AreEqual(0, EcFanPolicy.ClampDuty(-10));
            Assert.AreEqual(0, EcFanPolicy.ClampDuty(0));
            Assert.AreEqual(40, EcFanPolicy.ClampDuty(40));
            Assert.AreEqual(100, EcFanPolicy.ClampDuty(120));
        }

        [TestMethod]
        public void PowerEvents_NormalizeExternalPowerToAc()
        {
            Assert.IsTrue(WmiController.TryParseAcerPowerEvent(new byte[] { 8, 0 }, out var battery));
            Assert.AreEqual(PowerSourceKind.Battery, battery);
            Assert.IsTrue(WmiController.TryParseAcerPowerEvent(new byte[] { 8, 1 }, out var ac));
            Assert.AreEqual(PowerSourceKind.Ac, ac);
            Assert.IsTrue(WmiController.TryParseAcerPowerEvent(new byte[] { 8, 4 }, out var external));
            Assert.AreEqual(PowerSourceKind.Ac, external);
        }

        [TestMethod]
        public void FanCurveWindow_ShowsStageEditorAsDisabled_InAutoOrMaxMode()
        {
            string root = GetSolutionRoot();
            string fanWindowXaml = File.ReadAllText(Path.Combine(root, "PreySense.Frontend", "Views", "FanCurveWindow.xaml"));
            string fanWindowCs = File.ReadAllText(Path.Combine(root, "PreySense.Frontend", "Views", "FanCurveWindow.xaml.cs"));

            Assert.Contains("x:Name=\"FanTabs\"", fanWindowXaml);
            Assert.Contains("x:Name=\"Curve\"", fanWindowXaml);
            Assert.Contains("x:Name=\"CurveEditor\"", fanWindowXaml);
            Assert.Contains("WpfFanCurveControl", fanWindowXaml);

            Assert.Contains("CurveEditor.IsEnabled = isCustom && !_applying;", fanWindowCs);
            Assert.Contains("CurveEditor.Opacity = isCustom ? 1.0 : 0.45;", fanWindowCs);
            Assert.Contains("FooterPanel.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;", fanWindowCs);
        }

        [TestMethod]
        public void FanCurveWindow_Reset_SwitchesToAutoAndResetsToDefault()
        {
            string root = GetSolutionRoot();
            string fanWindowCs = File.ReadAllText(Path.Combine(root, "PreySense.Frontend", "Views", "FanCurveWindow.xaml.cs"));

            // Reset creates default EC stages, updates draft, persists and applies
            Assert.Contains("var defaultProfile = FanCurveProfile.CreateDefault(_mode);", fanWindowCs);
            Assert.Contains("await SaveProfileAsync(Vm, _mode, defaultProfile);", fanWindowCs);
            Assert.Contains("await Vm.ApplyFanProfileAsync();", fanWindowCs);

            var def = FanCurveProfile.CreateDefault(1);
            Assert.AreEqual(FanControlMode.Auto, def.ControlMode);
            Assert.AreEqual(2, def.RampUpSeconds);
            Assert.AreEqual(4, def.RampDownSeconds);
            Assert.AreEqual(2, def.HysteresisCelsius);
            Assert.AreEqual(1800, def.MinRpmFloor);
            Assert.IsTrue(def.UseEcAutoBelowMin);
            Assert.IsTrue(def.SafeQuietIdleBase);
            Assert.HasCount(FanCurveProfile.StageCount, def.Stages);
            Assert.AreEqual(1800, def.Stages[1].CpuTargetRpm);
        }

        [TestMethod]
        public void PowerAndFanPolicy_DebounceCancellation_And_DraftSynchronization()
        {
            string root = GetSolutionRoot();
            string powerModesCs = File.ReadAllText(Path.Combine(root, "PreySense.Frontend", "ViewModels", "MainViewModel.PowerModes.cs"));
            string fanWindowCs = File.ReadAllText(Path.Combine(root, "PreySense.Frontend", "Views", "FanCurveWindow.xaml.cs"));
            string modeSettingsCs = File.ReadAllText(Path.Combine(root, "PreySense.Frontend", "Views", "ModeSettingsWindow.xaml.cs"));

            // ApplyModeSwitchAsync cancels pending debounce timer immediately
            Assert.Contains("CancelPendingModeSwitch();\n            await _modeSwitchGate.WaitAsync();", powerModesCs.Replace("\r\n", "\n"));

            // Fan mode toggle synchronizes draft profile immediately
            Assert.Contains("SaveDraft();\n        UpdateEditor();", fanWindowCs.Replace("\r\n", "\n"));

            // Fan window Apply and Reset switch hardware mode immediately when not active
            Assert.Contains("await vm.ApplyModeSwitchAsync(_mode);", fanWindowCs);

            // Mode settings window applies tuning profile via driver transaction backend
            Assert.Contains("await vm.ApplyTuningProfileAsync", modeSettingsCs);
        }

        [TestMethod]
        public void FanCurve_Supports30CelsiusMinimum_AndGraphStartsAt30()
        {
            int minCelsius = PreySense.Frontend.Controls.WpfFanCurveControl.MinCelsius;
            Assert.AreEqual(30, minCelsius);

            string root = GetSolutionRoot();
            string xaml = File.ReadAllText(Path.Combine(root, "PreySense.Frontend", "Views", "FanCurveWindow.xaml"));
            Assert.Contains("x:Name=\"RowTemp\" Title=\"Temp\" Minimum=\"30\"", xaml);

            var bounds = PreySense.Frontend.Controls.WpfFanCurveControl.TemperatureBounds(new[] { 30, 45, 60 }, 0);
            Assert.AreEqual(30, bounds.Min);
            Assert.AreEqual(44, bounds.Max);

            var stages = FanCurveProfile.DefaultStages(1);
            stages[0].CpuRiseC = 30;
            var (indices, temperatures, duties, leadIn) = PreySense.Frontend.Views.FanCurveWindow.BuildGraph(stages, gpu: false);
            Assert.IsNotEmpty(temperatures);
            Assert.AreEqual(30, temperatures[0]);
        }

        [TestMethod]
        public void WmiController_ExitingMaxMode_DisengagesEcMaxLatchWithAuto()
        {
            string root = GetSolutionRoot();
            string fansCs = File.ReadAllText(Path.Combine(root, "PreySense.Core", "Hardware", "WmiController.Fans.cs"));
            Assert.Contains("if (_lastSetFanMode == 1 && mode != 1)", fansCs);
            Assert.Contains("SetFanBehavior(0);", fansCs);
        }

        [TestMethod]
        public void WindowSystemShadow_CalculatesStylesForNativeAnimation_WithoutMaximizeBox()
        {
            nint defaultStyle = PreySense.Frontend.Services.WindowSystemShadow.WsCaption |
                                PreySense.Frontend.Services.WindowSystemShadow.WsSysMenu |
                                PreySense.Frontend.Services.WindowSystemShadow.WsMaximizeBox;
            nint calculated = PreySense.Frontend.Services.WindowSystemShadow.CalculateWindowStyle(defaultStyle);

            // Must cleanly strip WS_CAPTION, WS_SYSMENU, and WS_MAXIMIZEBOX to prevent Windows from drawing system caption buttons
            Assert.AreEqual((nint)0, calculated & PreySense.Frontend.Services.WindowSystemShadow.WsCaption);
            Assert.AreEqual((nint)0, calculated & PreySense.Frontend.Services.WindowSystemShadow.WsSysMenu);
            Assert.AreEqual((nint)0, calculated & PreySense.Frontend.Services.WindowSystemShadow.WsMaximizeBox);

            // Source check: DwmwaTransitionsForcedDisabled must be set to 0 (transitions enabled)
            string root = GetSolutionRoot();
            string shadowCs = File.ReadAllText(Path.Combine(root, "PreySense.Frontend", "Services", "WindowSystemShadow.cs"));
            Assert.Contains("int disableTransitions = 0;", shadowCs);
            Assert.Contains("DwmSetWindowAttribute(hwnd, DwmwaTransitionsForcedDisabled, ref disableTransitions", shadowCs);
            Assert.Contains("UseAeroCaptionButtons = false", shadowCs);
            Assert.Contains("CaptionHeight = 0", shadowCs);
        }

        [TestMethod]
        public void MainViewModel_Startup_EnforcesAcerServicesDebloatWhenConfigured()
        {
            string root = GetSolutionRoot();
            string mainVmCs = File.ReadAllText(Path.Combine(root, "PreySense.Frontend", "ViewModels", "MainViewModel.cs"));
            Assert.Contains("if (cfg.General.AcerServicesDebloated)", mainVmCs);
            Assert.Contains("AcerServiceDebloater.StopAndDisableAll();", mainVmCs);
        }

        [TestMethod]
        public void PowerSource_TransitionToAc_ReappliesBatteryChargeLimitAndEnergySaver()
        {
            string root = GetSolutionRoot();
            string mainVmCs = File.ReadAllText(Path.Combine(root, "PreySense.Frontend", "ViewModels", "MainViewModel.cs"));
            string sessionCs = File.ReadAllText(Path.Combine(root, "PreySense.Frontend", "ViewModels", "MainViewModel.Session.cs"));

            // Verify AC connection re-applies battery limit
            Assert.Contains("if (onAc)", mainVmCs);
            Assert.Contains("if (IsBatteryLimitEnabled)", mainVmCs);
            Assert.Contains("_hw.SetBatteryChargeLimit(1);", mainVmCs);

            // Verify Energy Saver synchronization
            Assert.Contains("_energySaverWorker.Submit(false);", mainVmCs);
            Assert.Contains("_energySaverWorker.Submit(true);", mainVmCs);

            // Verify Windows native PowerModes.StatusChange triggers source transition
            Assert.Contains("if (e.Mode == PowerModes.StatusChange)", sessionCs);
            Assert.Contains("ApplyPowerSourceTransition(source);", sessionCs);
        }

        [TestMethod]
        public void FanStage_TryInsertVisibleStage_InsertsSortedPointWithoutCorruptingOtherSensor()
        {
            var stages = PreySense.Mode.FanCurveProfile.DefaultStages(1);
            var originalGpuDuties = new int[stages.Length];
            var originalGpuRises = new int[stages.Length];
            for (int i = 0; i < stages.Length; i++)
            {
                originalGpuDuties[i] = stages[i].GpuDutyPercent;
                originalGpuRises[i] = stages[i].GpuRiseC;
            }

            // Balanced CPU rises start around 56°C; inserting 45°C at 22% must insert at index 0.
            bool ok = PreySense.Mode.FanStage.TryInsertVisibleStage(
                stages, gpu: false, celsius: 45, duty: 22, minCelsius: 30, maxCelsius: 100, out int insertedIndex);

            Assert.IsTrue(ok);
            Assert.AreEqual(0, insertedIndex);
            Assert.AreEqual(45, stages[0].CpuRiseC);
            Assert.AreEqual(22, stages[1].CpuDutyPercent);

            // GPU stages must remain completely untouched
            for (int i = 0; i < stages.Length; i++)
            {
                Assert.AreEqual(originalGpuRises[i], stages[i].GpuRiseC, $"GPU rise changed at stage {i}");
                Assert.AreEqual(originalGpuDuties[i], stages[i].GpuDutyPercent, $"GPU duty changed at stage {i}");
            }
        }

        [TestMethod]
        public void FanStage_PhysicalRpmToDuty_RoundTripsLowManualPercentagesAccurately()
        {
            for (int duty = 1; duty <= 100; duty++)
            {
                int rpm = PreySense.Mode.FanStage.DutyToPhysicalRpm(duty, PreySense.Mode.FanStage.CpuMaxRpm);
                int parsedDuty = PreySense.Mode.FanStage.PhysicalRpmToDuty(rpm, PreySense.Mode.FanStage.CpuMaxRpm);
                Assert.AreEqual(duty, parsedDuty, $"Round-trip failed for duty={duty}% -> {rpm} RPM");
            }
            Assert.AreEqual(0, PreySense.Mode.FanStage.PhysicalRpmToDuty(0, PreySense.Mode.FanStage.CpuMaxRpm));
        }

        [TestMethod]
        public void WmiController_RemovedQuietEcAutoBase_NeverSwitchesPowerModeOnZeroDuty()
        {
            string root = GetSolutionRoot();
            string fansCs = File.ReadAllText(Path.Combine(root, "PreySense.Core", "Hardware", "WmiController.Fans.cs"));
            string wmiCs = File.ReadAllText(Path.Combine(root, "PreySense.Core", "Hardware", "WmiController.cs"));

            Assert.DoesNotContain("EnsureQuietEcAutoBase", fansCs);
            Assert.DoesNotContain("_quietEcOverrideActive", fansCs);
            Assert.DoesNotContain("ReapplyActiveModePowerLimits", fansCs);
            Assert.DoesNotContain("_quietEcOverrideActive", wmiCs);
            Assert.DoesNotContain("SetMiscSetting", fansCs);
        }

        [TestMethod]
        public void FanCurveWindow_ExposesDecoupledRampHysteresisFloorAndEcAutoControls()
        {
            string root = GetSolutionRoot();
            string xaml = File.ReadAllText(Path.Combine(root, "PreySense.Frontend", "Views", "FanCurveWindow.xaml"));
            string codeBehind = File.ReadAllText(Path.Combine(root, "PreySense.Frontend", "Views", "FanCurveWindow.xaml.cs"));
            Assert.Contains("x:Name=\"RampUpRow\"", xaml);
            Assert.Contains("x:Name=\"RampDownRow\"", xaml);
            Assert.Contains("x:Name=\"HysteresisRow\"", xaml);
            Assert.Contains("x:Name=\"MinRpmFloorRow\"", xaml);
            Assert.Contains("x:Name=\"UseEcAutoBelowMinRow\"", xaml);
            Assert.Contains("x:Name=\"SafeQuietIdleBaseRow\"", xaml);
            Assert.Contains("x:Name=\"ContentScroll\"", xaml);
            Assert.Contains("Grid.Row=\"2\"", xaml);
            Assert.Contains("SystemParameters.WorkArea.Height", codeBehind);
            Assert.Contains("CommitActiveFields();", codeBehind);
        }

        [TestMethod]
        public void FanCurveStorage_RoundTripsRampHysteresisMinFloorAndEcAutoFlags()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "PreySenseFanRoundTrip_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string configPath = Path.Combine(tempDir, "preysense.json");
                var service = new PreySense.Core.Configuration.ConfigurationService(configPath);
                service.Update(cfg =>
                {
                    cfg.Fans.Performance.ControlMode = (int)FanControlMode.Custom;
                    cfg.Fans.Performance.RampUpSeconds = 6;
                    cfg.Fans.Performance.RampDownSeconds = 9;
                    cfg.Fans.Performance.HysteresisCelsius = 4;
                    cfg.Fans.Performance.MinRpmFloor = 2350;
                    cfg.Fans.Performance.UseEcAutoBelowMin = true;
                    cfg.Fans.Performance.SafeQuietIdleBase = false;
                    cfg.Fans.Performance.Cpu = [new(45, 2350), new(75, 4500), new(90, 6200)];
                });

                var reloaded = new PreySense.Core.Configuration.ConfigurationService(configPath).GetCurrent();
                Assert.AreEqual(2, reloaded.Fans.Performance.ControlMode);
                Assert.AreEqual(6, reloaded.Fans.Performance.RampUpSeconds);
                Assert.AreEqual(9, reloaded.Fans.Performance.RampDownSeconds);
                Assert.AreEqual(4, reloaded.Fans.Performance.HysteresisCelsius);
                Assert.AreEqual(2350, reloaded.Fans.Performance.MinRpmFloor);
                Assert.IsTrue(reloaded.Fans.Performance.UseEcAutoBelowMin);
                Assert.IsFalse(reloaded.Fans.Performance.SafeQuietIdleBase);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        [TestMethod]
        public void UiPolish_SettingsRemovesSmartBoost_OverlayWidened_LiquidGlassConsistent()
        {
            string root = GetSolutionRoot();
            string settingsXaml = File.ReadAllText(Path.Combine(root, "PreySense.Frontend", "Views", "SettingsWindow.xaml"));
            string tuningXaml = File.ReadAllText(Path.Combine(root, "PreySense.Frontend", "Views", "ModeSettingsWindow.xaml"));
            string metricsXaml = File.ReadAllText(Path.Combine(root, "PreySense.Frontend", "Views", "MetricsWindow.xaml"));
            string appCs = File.ReadAllText(Path.Combine(root, "PreySense.Frontend", "App.xaml.cs"));
            string shadowCs = File.ReadAllText(Path.Combine(root, "PreySense.Frontend", "Services", "WindowSystemShadow.cs"));
            string dialogCs = File.ReadAllText(Path.Combine(root, "PreySense.Frontend", "Views", "CustomModalDialog.xaml.cs"));

            Assert.DoesNotContain("IsSmartBoostEnabled", settingsXaml, "Smart Boost must only appear in Tuning, not Settings.");
            Assert.Contains("IsSmartBoostEnabled", tuningXaml);
            Assert.Contains("Tag=\"tr-TR\"", settingsXaml);
            Assert.Contains("Width=\"576\"", metricsXaml);
            Assert.Contains("ThemeManager.LoadAndApply();", appCs);
            Assert.Contains("ApplyDwmAttributesOnly", shadowCs);
            Assert.Contains("IsVisibleChanged", shadowCs);
            Assert.Contains("Resources.MergedDictionaries.Clear();", dialogCs);
        }

        private static string GetSolutionRoot()
        {
            DirectoryInfo? dir = new(AppDomain.CurrentDomain.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PreySense.slnx")))
                dir = dir.Parent;
            return dir?.FullName ?? AppDomain.CurrentDomain.BaseDirectory;
        }
    }
}
