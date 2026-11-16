using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using PreySense.Core.Services;
using PreySense.Frontend.Services;
using PreySense.Frontend.ViewModels;

namespace PreySense.Frontend
{
    public partial class MainWindow : Window
    {
        private Services.KeyboardHook? _keyboardHook;
        private Services.TrayIconManager? _trayIconManager;
        private Services.HidDeviceArrivalWatcher? _hidArrivalWatcher;
        private bool _isExplicitExit = false;
        private readonly WindowVisibilitySession<Window> _visibilitySession = new();
        private readonly Dictionary<Type, Window> _toolWindows = new();
        private Window? _lastActiveWindow;
        private ForegroundWindowEvents? _windowActivityEvents;

        public MainWindow() => InitializeMainWindow(hardwareOverride: null);

        internal MainWindow(IHardwareService hardwareService) =>
            InitializeMainWindow(hardwareOverride: hardwareService ?? throw new ArgumentNullException(nameof(hardwareService)));

        private void InitializeMainWindow(IHardwareService? hardwareOverride)
        {
            InitializeComponent();
            Activated += (_, _) =>
            {
                _lastActiveWindow = this;
                _visibilitySession.RecordForeground(true);
            };
            IsVisibleChanged += (_, _) =>
            {
                if (DataContext is MainViewModel viewModel) viewModel.SetMainWindowTelemetryVisible(IsVisible);
                _windowActivityEvents?.Dispose();
                _windowActivityEvents = IsVisible
                    ? new ForegroundWindowEvents(window =>
                    {
                        _visibilitySession.RecordForeground(IsApplicationForeground(window));
                        QueueAutoHide();
                    })
                    : null;
                if (!IsVisible && !_isExplicitExit)
                    MemoryOptimizer.ScheduleBackgroundTrim(delayMs: 300, aggressiveWorkingSetTrim: true);
            };

            var helper = new System.Windows.Interop.WindowInteropHelper(this);
            IntPtr hWnd = helper.EnsureHandle();

            bool isTestHost = hardwareOverride is MockHardwareService;
            if (!isTestHost)
            {
                bool initialTrayVisible = true;
                try
                {
                    initialTrayVisible = !PreySense.Core.Configuration.ConfigurationService.Instance.GetCurrent().General.DisableTrayIcon;
                }
                catch { }

                _trayIconManager = new Services.TrayIconManager(hWnd, onOpen: ShowAndRestore, onQuit: QuitApplication, initiallyVisible: initialTrayVisible);
                var source = System.Windows.Interop.HwndSource.FromHwnd(hWnd);
                source?.AddHook(_trayIconManager.WndProc);
                try { _hidArrivalWatcher = new Services.HidDeviceArrivalWatcher(hWnd); } catch { }
            }

            IHardwareService hw;
            if (hardwareOverride is not null)
            {
                hw = hardwareOverride;
            }
            else
            {
                try
                {
                    hw = new ProductionHardwareService();
                }
                catch (Exception ex)
                {
                    System.IO.File.WriteAllText("hw_init.log", ex.ToString());
                    hw = new MockHardwareService();
                }
            }

            var vm = new MainViewModel(hw);
            if (!isTestHost)
                vm.TrayIconVisibilityHandler = visible => _trayIconManager?.SetVisible(visible);
            DataContext = vm;
            ApplyResponsivePercentageSize();

            // Setup Predator Sense Key & Combos
            if (!isTestHost)
            try
            {
                _keyboardHook = new Services.KeyboardHook(
                    onPredatorSensePressed: ToggleAppVisibility,
                    onPredatorNumberPressed: number =>
                    {
                        byte mode = number switch
                        {
                            1 => 0x06, // Eco
                            2 => 0x00, // Quiet
                            3 => 0x01, // Balanced
                            4 => 0x04, // Performance
                            5 => 0x05, // Turbo
                            _ => 0xFF
                        };
                        if (mode != 0xFF)
                        {
                            RunOnUi(() =>
                            {
                                if (DataContext is MainViewModel mvm)
                                {
                                    _ = mvm.SetModeAsync(mode);
                                }
                            });
                        }
                    },
                    onPredatorRPressed: () =>
                    {
                        RunOnUi(OverlayBridge.Toggle);
                    },
                    onModeKeyPressed: () =>
                    {
                        RunOnUi(() =>
                        {
                            if (DataContext is MainViewModel mvm)
                                mvm.HandleModeKeyPressed();
                        });
                    },
                    onBrightnessDelta: delta =>
                    {
                        RunOnUi(() =>
                        {
                            if (DataContext is MainViewModel mvm)
                                mvm.HandleFnBrightness(delta, "Keyboard");
                        });
                    });
            }
            catch { }


            StateChanged += (_, _) =>
            {
                if (WindowState == WindowState.Minimized)
                    HideApplicationWindowsForAutoHide();
            };

            Closed += (s, e) =>
            {
                _windowActivityEvents?.Dispose();
                _windowActivityEvents = null;
                _hidArrivalWatcher?.Dispose();
                _hidArrivalWatcher = null;
                _keyboardHook?.Dispose();
                _keyboardHook = null;
                _trayIconManager?.Dispose();
                _trayIconManager = null;
                OverlayBridge.Dispose();
                vm.Dispose();
            };
        }

        private void RunOnUi(Action action)
        {
            if (Dispatcher.CheckAccess())
                action();
            else
                _ = Dispatcher.BeginInvoke(action);
        }

        public void QuitApplication()
        {
            RunOnUi(async () =>
            {
                if (_isExplicitExit) return;
                _isExplicitExit = true;
                try
                {
                    if (DataContext is MainViewModel vm)
                    {
                        await vm.PrepareGameModeShutdownAsync();
                    }
                }
                catch { }
                Close();
                Application.Current.Shutdown();
            });
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_isExplicitExit)
            {
                e.Cancel = true;
                HideApplicationWindowsForAutoHide();
                return;
            }
            base.OnClosing(e);
        }

        private void ApplyResponsivePercentageSize()
        {
            var workArea = SystemParameters.WorkArea;
            Width = 455;
            double h = double.IsNaN(Height) || Height <= 0 ? 500 : Height;
            Left = workArea.Left + (workArea.Width - Width) / 2;
            Top = Math.Max(workArea.Top + 40, workArea.Top + (workArea.Height - h) / 2);
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            HideApplicationWindowsForAutoHide();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            ShowToolWindow<Views.SettingsWindow>();
        }

        private void ModeSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainViewModel vm)
                vm.EnsureGpuHardwareInitialized();

            ShowModeSettingsWindow();
        }

        private void GamesButton_Click(object sender, RoutedEventArgs e) =>
            ShowToolWindow<Views.GamesWindow>();

        private void FansButton_Click(object sender, RoutedEventArgs e) =>
            ShowToolWindow<Views.FanCurveWindow>();

        private void ShowModeSettingsWindow() => ShowToolWindow<Views.ModeSettingsWindow>();

        private void KeyboardButton_Click(object sender, RoutedEventArgs e)
        {
            ShowToolWindow<Views.KeyboardWindow>();
        }

        private void MetricsButton_Click(object sender, RoutedEventArgs e)
        {
            ShowToolWindow<Views.MetricsWindow>();
        }

        private void ShowToolWindow<TWindow>() where TWindow : Window, new()
        {
            if (_isExplicitExit) return;
            if (!_toolWindows.TryGetValue(typeof(TWindow), out Window? window))
            {
                window = new TWindow { Owner = this, DataContext = DataContext, ShowInTaskbar = false };
                _toolWindows.Add(typeof(TWindow), window);
                window.Activated += (_, _) => _lastActiveWindow = window;
                window.Closing += (_, _) => GuardToolWindowClose();
                window.Closed += (_, _) =>
                {
                    _toolWindows.Remove(typeof(TWindow));
                    MemoryOptimizer.ScheduleBackgroundTrim(delayMs: 250, aggressiveWorkingSetTrim: false);
                };
            }
            window.Topmost = true;
            if (!window.IsVisible) window.Show();
            ForceToForeground(window);
        }

        private void QuitButton_Click(object sender, RoutedEventArgs e)
        {
            QuitApplication();
        }
    }
}
