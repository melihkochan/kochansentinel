using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KochanSentinel.App.ViewModels;
using KochanSentinel.Core.Interop;
using Wpf.Ui.Controls;

namespace KochanSentinel.App;

public partial class MainWindow : FluentWindow
{
    private const int HotkeyId = 9001;

    private readonly MainViewModel _vm;
    private readonly NotifyIcon _trayIcon;
    private readonly MiniMonitorWindow _miniHud;
    private HwndSource? _hwndSource;
    private bool _hwndHooksAttached;
    private bool _clipboardListenerAttached;
    private bool _isExplicitExit;
    private bool _uiInitialized;

    public bool IsSelfDefenseEnabled => _vm.Settings.EnableSelfDefenseWatchdog;

    public MainWindow(bool startMinimized = false, bool justUpdated = false)
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        _uiInitialized = true;

        if (DiskTreemap != null)
            DiskTreemap.NodeDoubleClicked += async node => await _vm.DrillIntoDiskNodeCommand.ExecuteAsync(node);
        SwitchTab(0);

        _trayIcon = CreateTrayIcon();
        _miniHud = new MiniMonitorWindow();
        _miniHud.ApplyPreferences(_vm.Settings);
        _miniHud.OnOpenMainRequested += RestoreWindow;
        _miniHud.OnBoostAsyncRequested += async () => await _vm.QuickBoostWithSummaryAsync();

        _vm.OnMetricsUpdated += m => _miniHud.UpdateMetrics(m);
        _vm.OnNavigateTabRequested += NavigateToTab;
        _vm.OnToggleMiniHudRequested += () => ToggleMiniHud_Click(this, new RoutedEventArgs());
        _vm.OnExportHtmlReportDialogRequested += () => ExportHtmlReport_Click(this, new RoutedEventArgs());
        _vm.OnBeforeSelfUpdateShutdownRequested += () =>
        {
            _isExplicitExit = true;
            (System.Windows.Application.Current as App)?.PrepareForSelfUpdate();
        };

        _vm.OnSettingsSynced += () =>
        {
            _miniHud.ApplyPreferences(_vm.Settings);
            ApplyGlobalHotkey(_vm.Settings.HudHotkeyPreset);
            ApplyWindowBackdrop(_vm.Settings.WindowBackdropMode);
            (System.Windows.Application.Current as App)?.SyncWatchdogState(_vm.Settings.EnableSelfDefenseWatchdog);
        };

        ApplyWindowBackdrop(_vm.Settings.WindowBackdropMode);

        if (_vm.Settings.ShowMiniHudOnStart)
            _miniHud.Show();

        _vm.OnTrayNotificationRequested += (title, msg, isWarning) =>
        {
            _trayIcon.ShowBalloonTip(4000, title, msg, isWarning ? ToolTipIcon.Warning : ToolTipIcon.Info);
        };

        // Ensure Win32 HWND exists even when started --minimized so Global Hotkey & Clipboard Guard work immediately!
        if (startMinimized)
        {
            EnsureHwndAndHooks();
            Hide();
        }

        if (justUpdated)
        {
            Dispatcher.BeginInvoke(() => _vm.NotifyJustUpdated());
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        EnsureHwndAndHooks();
    }

    private void EnsureHwndAndHooks()
    {
        if (_hwndHooksAttached) return;
        try
        {
            var helper = new WindowInteropHelper(this);
            IntPtr handle = helper.EnsureHandle();
            _hwndSource = HwndSource.FromHwnd(handle);
            _hwndSource?.AddHook(HwndHook);
            _hwndHooksAttached = true;

            ApplyGlobalHotkey(_vm.Settings.HudHotkeyPreset);
            _clipboardListenerAttached = NativeMethods.AddClipboardFormatListener(handle);
        }
        catch { }
    }

    private void ApplyGlobalHotkey(string? preset)
    {
        if (_hwndSource == null) return;
        try
        {
            NativeMethods.UnregisterHotKey(_hwndSource.Handle, HotkeyId);

            (uint mods, uint vk) = (preset ?? "Ctrl+Shift+H") switch
            {
                "Alt+Z" => (NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, NativeMethods.VK_Z),
                "Ctrl+Alt+H" => (NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT, NativeMethods.VK_H),
                "F9" => (NativeMethods.MOD_NOREPEAT, NativeMethods.VK_F9),
                _ => (NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT, NativeMethods.VK_H)
            };

            NativeMethods.RegisterHotKey(_hwndSource.Handle, HotkeyId, mods, vk);
        }
        catch { }
    }

    private void ApplyWindowBackdrop(string? mode)
    {
        try
        {
            WindowBackdropType = (mode ?? "None") switch
            {
                "Mica" => WindowBackdropType.Mica,
                "Acrylic" => WindowBackdropType.Acrylic,
                _ => WindowBackdropType.None
            };
        }
        catch { }
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            ToggleMiniHud_Click(this, new RoutedEventArgs());
            handled = true;
        }
        else if (msg == NativeMethods.WM_CLIPBOARDUPDATE && _vm.Settings.EnableClipboardClipperGuard)
        {
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    string clipText = System.Windows.Clipboard.GetText();
                    var (shouldRestore, safeText) = _vm.HandleClipboardTextChanged(clipText);
                    if (shouldRestore && !string.IsNullOrEmpty(safeText))
                    {
                        System.Windows.Clipboard.SetText(safeText);
                    }
                }
            }
            catch { }
        }
        return IntPtr.Zero;
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (_vm.IsCommandPaletteOpen)
                _vm.CloseCommandPaletteCommand.Execute(null);
            else
                _vm.OpenCommandPaletteCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _vm.IsCommandPaletteOpen)
        {
            _vm.CloseCommandPaletteCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void ToggleMiniHud_Click(object sender, RoutedEventArgs e)
    {
        if (_miniHud.IsVisible)
            _miniHud.Hide();
        else
            _miniHud.Show();
    }

    private void NavigateToTab(int idx)
    {
        switch (idx)
        {
            case 0: NavRadioDashboard.IsChecked = true; break;
            case 1: NavRadioMonitor.IsChecked = true; break;
            case 2: NavRadioCleaner.IsChecked = true; break;
            case 3: NavRadioGames.IsChecked = true; break;
            case 4: NavRadioSecurity.IsChecked = true; break;
            case 5: NavRadioSettings.IsChecked = true; break;
            case 6: NavRadioBenchmark.IsChecked = true; break;
            case 7: NavRadioDisk.IsChecked = true; break;
            case 8: NavRadioStudio.IsChecked = true; break;
        }
        _vm.SelectedNavIndex = idx;
        SwitchTab(idx);
    }

    private NotifyIcon CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("KOCHAN Sentinel'i Aç", null, (_, _) => RestoreWindow());
        menu.Items.Add("📌 Mini Monitör (HUD) Aç/Kapat", null, (_, _) => ToggleMiniHud_Click(this, new RoutedEventArgs()));
        menu.Items.Add("⚡ Hızlı Optimize Et", null, async (_, _) => await _vm.QuickBoostCommand.ExecuteAsync(null));
        menu.Items.Add("🎮 Manuel Oyun Modu", null, (_, _) => _vm.ToggleManualGameModeCommand.Execute(null));
        menu.Items.Add("📄 HTML Sağlık Raporu Oluştur", null, (_, _) => { RestoreWindow(); ExportHtmlReport_Click(this, new RoutedEventArgs()); });
        menu.Items.Add("🔄 Güncelleme Denetle", null, async (_, _) => await _vm.CheckUpdateCommand.ExecuteAsync(null));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (_, _) =>
        {
            _isExplicitExit = true;
            App.MarkCleanExit();
            System.Windows.Application.Current.Shutdown();
        });

        Icon trayBmpIcon = SystemIcons.Shield;
        try
        {
            var streamInfo = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/kochan.ico"));
            if (streamInfo != null)
                trayBmpIcon = new Icon(streamInfo.Stream);
        }
        catch { }

        var icon = new NotifyIcon
        {
            Icon = trayBmpIcon,
            Text = "KOCHAN Sentinel — Arka Plan Koruması Aktif",
            Visible = true,
            ContextMenuStrip = menu
        };
        icon.DoubleClick += (_, _) => RestoreWindow();
        return icon;
    }

    private void NavRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton rb &&
            int.TryParse(rb.Tag?.ToString(), out int idx) &&
            _vm != null)
        {
            _vm.SelectedNavIndex = idx;
            SwitchTab(idx);
        }
    }

    private void TelemetryCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string mode)
        {
            _vm.SetProcessSortModeCommand.Execute(mode);
            NavigateToTab(1);
        }
    }

    private void ProcessesSubTab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton rb &&
            int.TryParse(rb.Tag?.ToString(), out int subIdx) &&
            PanelProcTable != null && PanelNetShield != null && PanelFirewallRules != null && PanelDnsCache != null && PanelDnsSwitcher != null)
        {
            PanelProcTable.Visibility = subIdx == 0 ? Visibility.Visible : Visibility.Collapsed;
            PanelNetShield.Visibility = subIdx == 1 ? Visibility.Visible : Visibility.Collapsed;
            PanelFirewallRules.Visibility = subIdx == 2 ? Visibility.Visible : Visibility.Collapsed;
            PanelDnsCache.Visibility = subIdx == 3 ? Visibility.Visible : Visibility.Collapsed;
            PanelDnsSwitcher.Visibility = subIdx == 4 ? Visibility.Visible : Visibility.Collapsed;

            if (BtnRefreshNetShield != null)
                BtnRefreshNetShield.Visibility = subIdx is > 0 and < 4 ? Visibility.Visible : Visibility.Collapsed;
            if (NetShieldSummaryBar != null)
                NetShieldSummaryBar.Visibility = subIdx is > 0 and < 4 ? Visibility.Visible : Visibility.Collapsed;

            if (subIdx is > 0 and < 4 && _vm.ExternalConnections.Count == 0 && _vm.BlockedFirewallRules.Count == 0)
                _ = _vm.ScanNetworkShieldCommand.ExecuteAsync(null);
            else if (subIdx == 4)
                _vm.RefreshDnsAdaptersCommand.Execute(null);
        }
    }

    private void CleanerSubTab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton rb &&
            int.TryParse(rb.Tag?.ToString(), out int subIdx) &&
            PanelCleanerMain != null && PanelTweaksAndRepair != null && PanelStartupManager != null)
        {
            PanelCleanerMain.Visibility = subIdx == 0 ? Visibility.Visible : Visibility.Collapsed;
            PanelTweaksAndRepair.Visibility = subIdx == 1 ? Visibility.Visible : Visibility.Collapsed;
            PanelStartupManager.Visibility = subIdx == 2 ? Visibility.Visible : Visibility.Collapsed;

            if (subIdx == 2 && _vm.StartupApps.Count == 0)
                _ = _vm.ScanStartupAppsCommand.ExecuteAsync(null);
        }
    }

    private void SecuritySubTab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton rb &&
            int.TryParse(rb.Tag?.ToString(), out int subIdx) &&
            PanelFindings != null && PanelDrivers != null && PanelModulesAndExt != null && PanelHistory != null && PanelQuarantine != null)
        {
            PanelFindings.Visibility = subIdx == 0 ? Visibility.Visible : Visibility.Collapsed;
            PanelDrivers.Visibility = subIdx == 1 ? Visibility.Visible : Visibility.Collapsed;
            PanelModulesAndExt.Visibility = subIdx == 2 ? Visibility.Visible : Visibility.Collapsed;
            PanelHistory.Visibility = subIdx == 3 ? Visibility.Visible : Visibility.Collapsed;
            PanelQuarantine.Visibility = subIdx == 4 ? Visibility.Visible : Visibility.Collapsed;

            if (FindingInspectorCard != null)
                FindingInspectorCard.Visibility = subIdx == 0 && _vm.SelectedFinding != null ? Visibility.Visible : Visibility.Collapsed;
            if (BtnWhitelistBulk != null)
                BtnWhitelistBulk.Visibility = subIdx == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (BtnRemoveFindingsBulk != null)
                BtnRemoveFindingsBulk.Visibility = subIdx == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (BtnScanDrivers != null)
                BtnScanDrivers.Visibility = subIdx == 1 ? Visibility.Visible : Visibility.Collapsed;
            if (BtnScanModules != null)
                BtnScanModules.Visibility = subIdx == 2 ? Visibility.Visible : Visibility.Collapsed;
            if (BtnClearHistoryBulk != null)
                BtnClearHistoryBulk.Visibility = subIdx == 3 ? Visibility.Visible : Visibility.Collapsed;

            if (subIdx == 1 && _vm.KernelDrivers.Count == 0 && !_vm.IsBusy)
                _ = _vm.ScanKernelDriversCommand.ExecuteAsync(null);
            else if (subIdx == 2 && _vm.InjectedDlls.Count == 0 && _vm.BrowserExtIssues.Count == 0 && !_vm.IsBusy)
                _ = _vm.ScanInjectedAndExtensionsCommand.ExecuteAsync(null);
        }
    }

    private void SelectAllFindings_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox cb)
            _vm.SelectAllFindings = cb.IsChecked == true;
    }

    private void PanelHistory_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PanelHistory.SelectedItem is KochanSentinel.Core.Models.SecurityHistoryEntry entry)
        {
            if (entry.ActionType.Contains("Denetim", StringComparison.OrdinalIgnoreCase))
            {
                SecTabFindings.IsChecked = true;
                return;
            }

            System.Windows.MessageBox.Show(
                $"Tarih: {entry.DateTimeText}\nTür: {entry.ActionType}\nHedef: {entry.TargetName}\nSonuç: {entry.Verdict}\n\nDetay:\n{entry.Details}",
                "Güvenlik Kaydı Detayı",
                System.Windows.MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void SwitchTab(int index)
    {
        TabDashboard.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        TabProcesses.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        TabCleaner.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        TabGameMode.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        TabSecurity.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        TabSettings.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;
        if (TabBenchmark != null)
            TabBenchmark.Visibility = index == 6 ? Visibility.Visible : Visibility.Collapsed;
        if (TabDiskAnalyzer != null)
            TabDiskAnalyzer.Visibility = index == 7 ? Visibility.Visible : Visibility.Collapsed;
        if (TabHardwareStudio != null)
            TabHardwareStudio.Visibility = index == 8 ? Visibility.Visible : Visibility.Collapsed;

        FrameworkElement? active = index switch
        {
            0 => TabDashboard,
            1 => TabProcesses,
            2 => TabCleaner,
            3 => TabGameMode,
            4 => TabSecurity,
            5 => TabSettings,
            6 => TabBenchmark,
            7 => TabDiskAnalyzer,
            8 => TabHardwareStudio,
            _ => null
        };
        if (active != null && _uiInitialized)
            AnimateTabEntrance(active);
    }

    private static void AnimateTabEntrance(FrameworkElement el)
    {
        var trans = el.RenderTransform as TranslateTransform;
        if (trans == null || trans.IsFrozen)
        {
            trans = new TranslateTransform();
            el.RenderTransform = trans;
        }
        el.Opacity = 0;
        trans.Y = 8;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
        trans.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
    }

    private void DiskSubTab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton rb &&
            int.TryParse(rb.Tag?.ToString(), out int subIdx) &&
            PanelDiskTree != null && PanelDiskTopFiles != null && PanelDiskTypes != null && PanelDiskDuplicates != null)
        {
            PanelDiskTree.Visibility = subIdx == 0 ? Visibility.Visible : Visibility.Collapsed;
            PanelDiskTopFiles.Visibility = subIdx == 1 ? Visibility.Visible : Visibility.Collapsed;
            PanelDiskTypes.Visibility = subIdx == 2 ? Visibility.Visible : Visibility.Collapsed;
            PanelDiskDuplicates.Visibility = subIdx == 3 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void DiskTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_vm != null)
            _vm.SelectedDiskNode = e.NewValue as KochanSentinel.Core.Models.DiskNode;
    }

    private async void BrowseDiskFolder_Click(object sender, RoutedEventArgs e)
    {
        // Native modern .NET 8 WPF OpenFolderDialog (replaces legacy WinForms FolderBrowserDialog)
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Boyut analizi yapılacak klasörü seçin (TreeSize Görünümü)"
        };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            await _vm.RunDiskAnalysisCommand.ExecuteAsync(dlg.FolderName);
        }
    }

    private async void BrowseAndScanFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Taranacak Dosyayı Seçin",
            Filter = "Tüm Dosyalar (*.*)|*.*|Çalıştırılabilir (*.exe;*.dll;*.msi;*.bat;*.ps1)|*.exe;*.dll;*.msi;*.bat;*.ps1"
        };
        if (dlg.ShowDialog() == true)
            await _vm.ScanFileCommand.ExecuteAsync(dlg.FileName);
    }

    private async void FileScanner_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
            if (files is { Length: > 0 })
                await _vm.ScanFileCommand.ExecuteAsync(files[0]);
        }
    }

    private void SettingControl_AutoSave(object sender, RoutedEventArgs e)
    {
        if (_uiInitialized)
            _vm.SaveSettingsCommand.Execute(null);
    }

    private void SettingCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_uiInitialized && _vm != null)
            _vm.SaveSettingsCommand.Execute(null);
    }

    private void HudOpacitySlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_uiInitialized && _miniHud != null)
            _miniHud.ApplyPreferences(_vm.Settings);
    }

    private void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "KOCHAN Ayarlarını Dışa Aktar",
            FileName = $"kochan-settings-{DateTime.Now:yyyyMMdd}.json",
            Filter = "JSON Yapılandırması (*.json)|*.json"
        };
        if (dlg.ShowDialog() == true)
            _vm.ExportSettingsToPath(dlg.FileName);
    }

    private void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "KOCHAN Ayarlarını İçe Aktar",
            Filter = "JSON Yapılandırması (*.json)|*.json"
        };
        if (dlg.ShowDialog() == true)
            _vm.ImportSettingsFromPath(dlg.FileName);
    }

    private void ExportHtmlReport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "KOCHAN Sistem Sağlık & Güvenlik Raporunu Kaydet",
            FileName = $"KOCHAN-Rapor-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmm}.html",
            Filter = "HTML Raporu (*.html)|*.html"
        };
        if (dlg.ShowDialog() == true)
            _vm.ExportHtmlReportToPath(dlg.FileName);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isExplicitExit && _vm.Settings.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
            _trayIcon.ShowBalloonTip(2500, "KOCHAN Sentinel", "Uygulama arka planda sistemi korumaya devam ediyor.", ToolTipIcon.Info);
            return;
        }

        App.MarkCleanExit();

        if (_hwndSource != null)
        {
            NativeMethods.UnregisterHotKey(_hwndSource.Handle, HotkeyId);
            if (_clipboardListenerAttached)
                NativeMethods.RemoveClipboardFormatListener(_hwndSource.Handle);
            _hwndSource.RemoveHook(HwndHook);
        }
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _miniHud.Close();
        _vm.Dispose();
    }
}
