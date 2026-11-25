using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KochanSentinel.App.ViewModels;
using KochanSentinel.Core.Services;
using Wpf.Ui.Controls;

namespace KochanSentinel.App;

public partial class MainWindow : FluentWindow
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int HotkeyId = 9001;
    private const int WmHotkey = 0x0312;

    private readonly MainViewModel _vm;
    private readonly NotifyIcon _trayIcon;
    private readonly MiniMonitorWindow _miniHud;
    private HwndSource? _hwndSource;
    private bool _isExplicitExit;
    private bool _uiInitialized;

    public bool IsSelfDefenseEnabled => _vm.Settings.EnableSelfDefenseWatchdog;

    public MainWindow(bool startMinimized = false)
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
        _vm.OnSettingsSynced += () =>
        {
            _miniHud.ApplyPreferences(_vm.Settings);
            (System.Windows.Application.Current as App)?.SyncWatchdogState(_vm.Settings.EnableSelfDefenseWatchdog);
        };

        if (_vm.Settings.ShowMiniHudOnStart)
            _miniHud.Show();

        _vm.OnTrayNotificationRequested += (title, msg, isWarning) =>
        {
            _trayIcon.ShowBalloonTip(4000, title, msg, isWarning ? ToolTipIcon.Warning : ToolTipIcon.Info);
        };

        if (startMinimized)
        {
            Hide();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var helper = new WindowInteropHelper(this);
        _hwndSource = HwndSource.FromHwnd(helper.Handle);
        _hwndSource?.AddHook(HwndHook);
        // Alt + Z global hotkey (MOD_ALT=0x0001 | MOD_NOREPEAT=0x4000, VK_Z=0x5A)
        RegisterHotKey(helper.Handle, HotkeyId, 0x0001 | 0x4000, 0x5A);
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            ToggleMiniHud_Click(this, new RoutedEventArgs());
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void ToggleMiniHud_Click(object sender, RoutedEventArgs e)
    {
        if (_miniHud.IsVisible)
            _miniHud.Hide();
        else
            _miniHud.Show();
    }

    private NotifyIcon CreateTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("KOCHAN Sentinel'i Aç", null, (_, _) => RestoreWindow());
        menu.Items.Add("📌 Mini Monitör (HUD) Aç/Kapat", null, (_, _) => ToggleMiniHud_Click(this, new RoutedEventArgs()));
        menu.Items.Add("⚡ Hızlı Optimize Et", null, async (_, _) => await _vm.QuickBoostCommand.ExecuteAsync(null));
        menu.Items.Add("🎮 Manuel Oyun Modu", null, (_, _) => _vm.ToggleManualGameModeCommand.Execute(null));
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

    private void TelemetryCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string mode)
        {
            _vm.SetProcessSortModeCommand.Execute(mode);
            NavRadioMonitor.IsChecked = true;
            _vm.SelectedNavIndex = 1;
            SwitchTab(1);
        }
    }

    private void ProcessesSubTab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton rb &&
            int.TryParse(rb.Tag?.ToString(), out int subIdx) &&
            PanelProcTable != null && PanelNetShield != null && PanelFirewallRules != null && PanelDnsCache != null)
        {
            PanelProcTable.Visibility = subIdx == 0 ? Visibility.Visible : Visibility.Collapsed;
            PanelNetShield.Visibility = subIdx == 1 ? Visibility.Visible : Visibility.Collapsed;
            PanelFirewallRules.Visibility = subIdx == 2 ? Visibility.Visible : Visibility.Collapsed;
            PanelDnsCache.Visibility = subIdx == 3 ? Visibility.Visible : Visibility.Collapsed;
            if (BtnRefreshNetShield != null)
                BtnRefreshNetShield.Visibility = subIdx > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (NetShieldSummaryBar != null)
                NetShieldSummaryBar.Visibility = subIdx > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (subIdx > 0 && _vm.ExternalConnections.Count == 0 && _vm.BlockedFirewallRules.Count == 0)
                _ = _vm.ScanNetworkShieldCommand.ExecuteAsync(null);
        }
    }

    private void CleanerSubTab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton rb &&
            int.TryParse(rb.Tag?.ToString(), out int subIdx) &&
            PanelCleanerMain != null && PanelTweaksAndRepair != null)
        {
            PanelCleanerMain.Visibility = subIdx == 0 ? Visibility.Visible : Visibility.Collapsed;
            PanelTweaksAndRepair.Visibility = subIdx == 1 ? Visibility.Visible : Visibility.Collapsed;
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
        {
            _vm.SelectAllFindings = cb.IsChecked == true;
        }
    }

    private void PanelHistory_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
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
            PanelDiskTree != null && PanelDiskTopFiles != null && PanelDiskTypes != null)
        {
            PanelDiskTree.Visibility = subIdx == 0 ? Visibility.Visible : Visibility.Collapsed;
            PanelDiskTopFiles.Visibility = subIdx == 1 ? Visibility.Visible : Visibility.Collapsed;
            PanelDiskTypes.Visibility = subIdx == 2 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void DiskTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_vm != null)
            _vm.SelectedDiskNode = e.NewValue as KochanSentinel.Core.Models.DiskNode;
    }

    private async void BrowseDiskFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Boyut analizi yapılacak klasörü seçin (TreeSize Görünümü)",
            UseDescriptionForTitle = true
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
        {
            await _vm.RunDiskAnalysisCommand.ExecuteAsync(dlg.SelectedPath);
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
        {
            await _vm.ScanFileCommand.ExecuteAsync(dlg.FileName);
        }
    }

    private async void FileScanner_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
            if (files is { Length: > 0 })
            {
                await _vm.ScanFileCommand.ExecuteAsync(files[0]);
            }
        }
    }

    private void SettingControl_AutoSave(object sender, RoutedEventArgs e)
    {
        if (_uiInitialized)
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
            UnregisterHotKey(_hwndSource.Handle, HotkeyId);
            _hwndSource.RemoveHook(HwndHook);
        }
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _miniHud.Close();
        _vm.Dispose();
    }
}

internal static class GraphicsExtensions
{
    public static void DrawRoundedRectangleCompat(this Graphics g, System.Drawing.Pen pen, int x, int y, int w, int h, int r)
    {
        using var path = new GraphicsPath();
        int d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        g.DrawPath(pen, path);
    }
}
