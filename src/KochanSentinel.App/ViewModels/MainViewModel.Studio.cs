using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KochanSentinel.Core.Models;
using KochanSentinel.Core.Services;

namespace KochanSentinel.App.ViewModels;

public partial class MainViewModel
{
    private readonly DriverUpdateService _driverService = new();
    private readonly DisplayAndColorService _displayService = new();
    private readonly ServiceAndBloatAuditService _serviceAudit = new();
    private CancellationTokenSource? _driverCts;
    private ColorProfilePreset _activeColorPreset = DisplayAndColorService.GetColorPresets()[0];
    private bool _suppressSliderLiveApply;

    // --- Driver Audit & Live Windows Update Driver Installer ---
    [ObservableProperty] private bool _isScanningDrivers;
    [ObservableProperty] private bool _isInstallingDrivers;
    [ObservableProperty] private string _driverAuditSummary = "Eksik/hatalı aygıtlar, eski sürücüler ve resmi Windows Update sürücü güncellemeleri taranmaya hazır.";
    [ObservableProperty] private int _pendingDriverUpdateCount;

    [ObservableProperty] private string _driverInstallPhase = "HAZIRLANIYOR";
    [ObservableProperty] private double _driverInstallPercent;
    [ObservableProperty] private string _driverInstallCurrentItem = "";
    [ObservableProperty] private string _driverInstallSpeedText = "—";
    [ObservableProperty] private string _driverInstallDetailText = "";
    public ObservableCollection<DriverAuditItem> DriverAuditItems { get; } = new();

    // --- Monitor & Live DWM GPU / DDC Color Calibration ---
    [ObservableProperty] private MonitorDisplayInfo? _selectedMonitor;
    [ObservableProperty] private string _monitorSummaryText = "Bağlı monitörlerin Hz, çözünürlük, ICC profili ve renk önerileri taranıyor...";
    [ObservableProperty] private int _monitorBrightnessValue = 100;
    [ObservableProperty] private int _monitorContrastValue = 50;
    [ObservableProperty] private bool _useHardwareDdcMode;
    [ObservableProperty] private string _brightnessControlModeText = "⚡ DWM GPU Motoru Aktif — Her monitörde anında canlı parlaklık, kontrast & renk";
    [ObservableProperty] private string _activeColorPresetName = "Varsayılan (Standart 6500K)";
    public ObservableCollection<MonitorDisplayInfo> Monitors { get; } = new();
    public ObservableCollection<ColorProfilePreset> ColorPresets { get; } = new();

    // --- Unused Services & Background Bloat/Risk Audit ---
    [ObservableProperty] private string _serviceAuditSummary = "Kullanılmayan telemetri/arka plan servisleri ve gereksiz süreçler denetlenmeye hazır.";
    [ObservableProperty] private string _bloatAuditSummary = "Arka planda çalışan gereksiz veya imzasız süreçler analiz ediliyor...";
    public ObservableCollection<ServiceOptimizationItem> UnusedServices { get; } = new();
    public ObservableCollection<BloatProcessAuditItem> BloatAndRiskyProcesses { get; } = new();

    public bool IsDriverOperationBusy => IsScanningDrivers || IsInstallingDrivers;
    public bool IsDwmSoftwareMode => !UseHardwareDdcMode;

    partial void OnIsScanningDriversChanged(bool value) => OnPropertyChanged(nameof(IsDriverOperationBusy));
    partial void OnIsInstallingDriversChanged(bool value) => OnPropertyChanged(nameof(IsDriverOperationBusy));
    partial void OnUseHardwareDdcModeChanged(bool value) => OnPropertyChanged(nameof(IsDwmSoftwareMode));

    partial void OnSelectedMonitorChanged(MonitorDisplayInfo? value)
    {
        if (value == null) return;
        _suppressSliderLiveApply = true;
        try
        {
            // Laptops with native WMI backlight default to Hardware; external desktop monitors default to instant DWM GPU
            // because many desktop monitor firmwares/Windows 11 24H2 ignore DDC/CI or GDI gamma ramps.
            if (value.IsLaptopInternalPanel && value.CurrentBrightness >= 0)
            {
                UseHardwareDdcMode = true;
                MonitorBrightnessValue = Math.Clamp(value.CurrentBrightness, 25, 100);
                MonitorContrastValue = 50;
                BrightnessControlModeText = "✓ Laptop Dahili Arka Işık (Donanımsal WMI) Aktif";
            }
            else
            {
                UseHardwareDdcMode = false;
                MonitorBrightnessValue = 100;
                MonitorContrastValue = 50;
                BrightnessControlModeText = value.SupportsDdcBrightness
                    ? "⚡ DWM GPU Motoru Aktif (Anında Etki) • İstersen yukarıdan Donanım DDC/CI seçebilirsin"
                    : "⚡ DWM GPU Motoru Aktif (Windows 11/10 tüm monitörlerde anında canlı etki)";
            }
        }
        finally
        {
            _suppressSliderLiveApply = false;
        }
    }

    // --- Startup Task Scheduler Live Status (Settings Tab) ---
    [ObservableProperty] private string _startupTaskStatusText = "Başlangıç görevi durumu kontrol ediliyor...";
    [ObservableProperty] private string _startupTaskStatusColor = "#A1A1AA";

    private void InitializeHardwareStudio()
    {
        foreach (var preset in DisplayAndColorService.GetColorPresets())
            ColorPresets.Add(preset);
        _activeColorPreset = ColorPresets.FirstOrDefault() ?? _activeColorPreset;
        RefreshStartupTaskStatus();
    }

    public void RefreshStartupTaskStatus()
    {
        _ = Task.Run(() =>
        {
            var status = _settingsService.GetStartupRegistrationStatus(Settings.StartWithWindows);
            string color = !Settings.StartWithWindows
                ? "#A1A1AA"
                : (status.Registered && status.TargetExists ? "#86EFAC" : "#FBBF24");
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                StartupTaskStatusText = status.StatusSummary;
                StartupTaskStatusColor = color;
            });
        });
    }

    [RelayCommand]
    private void RepairWindowsStartupTask()
    {
        Settings.StartWithWindows = true;
        bool ok = _settingsService.Save(Settings);
        RefreshStartupTaskStatus();
        StatusBanner = ok
            ? "Windows otomatik başlangıç görevi (KochanSentinelAutoStart) mevcut EXE yoluna başarıyla yenilendi!"
            : "Başlangıç görevi oluşturulamadı (Yönetici izni gerekiyor).";
        ShowToast(ok ? "Otomatik Başlangıç Hazır" : "Başlangıç Görevi Hatası", StatusBanner, ok ? FindingSeverity.Info : FindingSeverity.High);
    }

    [RelayCommand]
    private void OpenTaskScheduler()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskschd.msc") { UseShellExecute = true });
        }
        catch { }
    }

    public void CleanupHardwareStudioOnExit()
    {
        _driverCts?.Cancel();
        _displayService.CleanupColorEngine();
    }

    // --- Monitor & Color Commands ---
    [RelayCommand]
    private async Task ScanMonitorsAsync()
    {
        MonitorSummaryText = "Monitör Hz, çözünürlük, parlaklık motoru ve ICC renk profili okunuyor...";
        try
        {
            string gpu = Metrics.GpuName;
            var list = await Task.Run(() => _displayService.InspectMonitors(gpu));
            Monitors.Clear();
            foreach (var m in list) Monitors.Add(m);
            SelectedMonitor = Monitors.FirstOrDefault(m => m.IsPrimary) ?? Monitors.FirstOrDefault();

            int hzIssues = list.Count(m => m.NeedsRefreshRateBoost);
            MonitorSummaryText = hzIssues > 0
                ? $"DİKKAT: {list.Count} monitör bulundu — {hzIssues} monitör desteklediği maksimum Hz değerinin altında çalışıyor!"
                : $"{list.Count} monitör analiz edildi — yenileme hızları optimum. Parlaklık, kontrast ve renk profillerini aşağıdan anlık ayarlayabilirsin.";
            StatusBanner = MonitorSummaryText;
        }
        catch (Exception ex)
        {
            MonitorSummaryText = $"Monitör analizi hatası: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task BoostMonitorMaxRefreshRateAsync(MonitorDisplayInfo? monitor)
    {
        var target = monitor ?? SelectedMonitor;
        if (target == null) return;
        var (ok, msg) = _displayService.SetMaxRefreshRate(target);
        StatusBanner = msg;
        ShowToast(ok ? "Ekran Yenileme Hızı" : "Hz Değiştirilemedi", msg, ok ? FindingSeverity.Info : FindingSeverity.High);
        if (ok) await ScanMonitorsAsync();
    }

    [RelayCommand]
    private void SetBrightnessControlMode(string? mode)
    {
        bool wantHardware = string.Equals(mode, "ddc", StringComparison.OrdinalIgnoreCase);
        if (wantHardware && SelectedMonitor != null && !SelectedMonitor.SupportsDdcBrightness)
        {
            UseHardwareDdcMode = false;
            BrightnessControlModeText = "⚠ Bu monitörde DDC/CI kapalı — DWM GPU Motoru ile devam ediliyor (anında etki)";
            ShowToast("DDC/CI Bulunamadı", "Monitör OSD menüsünde DDC/CI kapalı görünüyor. DWM GPU motoru zaten anında çalışır.", FindingSeverity.Medium);
            return;
        }

        UseHardwareDdcMode = wantHardware;
        if (wantHardware)
        {
            // Keep color preset active via DWM, reset DWM brightness/contrast to neutral so hardware takes over
            _displayService.ApplyLiveDwmColorAndBrightness(SelectedMonitor, 100, 50, _activeColorPreset);
            if (SelectedMonitor != null && SelectedMonitor.CurrentBrightness >= 0)
            {
                _suppressSliderLiveApply = true;
                MonitorBrightnessValue = Math.Clamp(SelectedMonitor.CurrentBrightness, 25, 100);
                if (SelectedMonitor.CurrentContrast >= 0)
                    MonitorContrastValue = Math.Clamp(SelectedMonitor.CurrentContrast, 10, 100);
                _suppressSliderLiveApply = false;
            }
            BrightnessControlModeText = "✓ Donanım DDC/CI Modu — Monitörün fiziksel arka ışığını kontrol eder";
        }
        else
        {
            BrightnessControlModeText = "⚡ DWM GPU Motoru Aktif — Her monitörde anında canlı parlaklık & kontrast";
            ApplyCurrentDwmOnUiThread();
        }
    }

    [RelayCommand]
    private void ApplyColorPreset(ColorProfilePreset? preset)
    {
        if (preset == null) return;
        _activeColorPreset = preset;
        foreach (var p in ColorPresets)
            p.IsActive = string.Equals(p.Id, preset.Id, StringComparison.OrdinalIgnoreCase);

        if (preset.Id == "default" && !UseHardwareDdcMode)
        {
            _suppressSliderLiveApply = true;
            MonitorBrightnessValue = 100;
            MonitorContrastValue = 50;
            _suppressSliderLiveApply = false;
        }

        int dwmBright = UseHardwareDdcMode ? 100 : MonitorBrightnessValue;
        int dwmContrast = UseHardwareDdcMode ? 50 : MonitorContrastValue;

        var (ok, engine, msg) = _displayService.ApplyLiveDwmColorAndBrightness(SelectedMonitor, dwmBright, dwmContrast, preset);
        if (ok)
        {
            ActiveColorPresetName = preset.Name;
            BrightnessControlModeText = UseHardwareDdcMode
                ? $"✓ Donanım DDC/CI + {engine} Renk: {preset.BadgeText}"
                : $"⚡ {engine} Aktif • Profil: {preset.BadgeText} | Parlaklık %{dwmBright} | Kontrast %{dwmContrast}";
        }
        StatusBanner = msg;
        ShowToast(ok ? $"Renk Profili: {preset.BadgeText}" : "Renk Ayarı Uyarısı", msg, ok ? FindingSeverity.Info : FindingSeverity.Medium);
    }

    [RelayCommand]
    private void ResetDisplayAndColor()
    {
        var def = ColorPresets.FirstOrDefault(p => p.Id == "default") ?? DisplayAndColorService.GetColorPresets()[0];
        _suppressSliderLiveApply = true;
        MonitorBrightnessValue = 100;
        MonitorContrastValue = 50;
        _suppressSliderLiveApply = false;

        ApplyColorPreset(def);
    }

    public void ApplyMonitorBrightnessLive(int percent)
    {
        if (_suppressSliderLiveApply) return;

        if (!UseHardwareDdcMode)
        {
            ApplyCurrentDwmOnUiThread();
            return;
        }

        var mon = SelectedMonitor;
        _ = Task.Run(() =>
        {
            bool hwOk = mon != null && mon.SupportsDdcBrightness && _displayService.SetHardwareBrightness(mon, percent);
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (hwOk)
                {
                    BrightnessControlModeText = $"✓ DDC/CI Donanım Parlaklığı: %{percent}";
                    StatusBanner = $"Monitör donanımsal parlaklığı %{percent} yapıldı.";
                }
                else
                {
                    // Auto-fallback to instant DWM GPU mode if hardware DDC/CI rejected the write
                    UseHardwareDdcMode = false;
                    ApplyCurrentDwmOnUiThread();
                }
            });
        });
    }

    public void ApplyMonitorContrastLive(int percent)
    {
        if (_suppressSliderLiveApply) return;

        if (!UseHardwareDdcMode)
        {
            ApplyCurrentDwmOnUiThread();
            return;
        }

        var mon = SelectedMonitor;
        _ = Task.Run(() =>
        {
            bool hwOk = mon != null && mon.SupportsDdcBrightness && mon.CurrentContrast >= 0 && _displayService.SetHardwareContrast(mon, percent);
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (hwOk)
                {
                    BrightnessControlModeText = $"✓ DDC/CI Donanım Kontrastı: %{percent}";
                    StatusBanner = $"Monitör donanımsal kontrastı %{percent} yapıldı.";
                }
                else
                {
                    UseHardwareDdcMode = false;
                    ApplyCurrentDwmOnUiThread();
                }
            });
        });
    }

    private void ApplyCurrentDwmOnUiThread()
    {
        var (ok, engine, msg) = _displayService.ApplyLiveDwmColorAndBrightness(
            SelectedMonitor, MonitorBrightnessValue, MonitorContrastValue, _activeColorPreset);
        BrightnessControlModeText = ok
            ? $"⚡ {engine} Aktif • Parlaklık %{MonitorBrightnessValue} | Kontrast %{MonitorContrastValue} | {_activeColorPreset.BadgeText}"
            : msg;
        StatusBanner = msg;
    }

    [RelayCommand]
    private void LaunchDisplayTool(string? tool)
    {
        if (!string.IsNullOrWhiteSpace(tool))
            DisplayAndColorService.LaunchWindowsTool(tool);
    }

    // --- Driver Audit & Live Progress Installer Commands ---
    [RelayCommand]
    private async Task ScanHardwareDriversAsync()
    {
        if (IsScanningDrivers || IsInstallingDrivers) return;
        IsScanningDrivers = true;
        _driverCts?.Cancel();
        _driverCts = new CancellationTokenSource();
        DriverAuditSummary = "Aygıt Yöneticisi hataları, PCI donanım kimlikleri ve Windows Update WHQL sürücüleri taranıyor...";

        try
        {
            var (items, pending, errors, summary) = await _driverService.ScanDriversAndUpdatesAsync(queryWindowsUpdate: true, _driverCts.Token);
            DriverAuditItems.Clear();
            foreach (var item in items) DriverAuditItems.Add(item);
            PendingDriverUpdateCount = pending;
            DriverAuditSummary = summary;
            StatusBanner = summary;
            ShowToast("Sürücü Denetimi", summary, (pending > 0 || errors > 0) ? FindingSeverity.Medium : FindingSeverity.Info);
        }
        catch (OperationCanceledException)
        {
            DriverAuditSummary = "Sürücü taraması iptal edildi.";
        }
        catch (Exception ex)
        {
            DriverAuditSummary = $"Sürücü tarama hatası: {ex.Message}";
        }
        finally
        {
            IsScanningDrivers = false;
        }
    }

    [RelayCommand]
    private Task InstallPendingDriverUpdatesAsync() => RunDriverInstallCoreAsync(null);

    [RelayCommand]
    private async Task HandleDriverItemActionAsync(DriverAuditItem? item)
    {
        if (item == null) return;
        if (item.IsPendingWindowsUpdate && !string.IsNullOrWhiteSpace(item.UpdateId))
        {
            await RunDriverInstallCoreAsync(item.UpdateId);
            return;
        }

        DriverUpdateService.OpenDriverCatalogLookup(item);
        StatusBanner = $"Microsoft Update Catalog açıldı: {(string.IsNullOrWhiteSpace(item.HardwareId) ? item.DeviceName : item.HardwareId)}";
    }

    [RelayCommand]
    private void CancelDriverOperation()
    {
        _driverCts?.Cancel();
        DriverInstallPhase = "İPTAL EDİLİYOR";
        DriverInstallDetailText = "Windows Update işlemi güvenli şekilde durduruluyor...";
    }

    private async Task RunDriverInstallCoreAsync(string? singleUpdateId)
    {
        if (IsScanningDrivers || IsInstallingDrivers) return;
        IsInstallingDrivers = true;
        _driverCts?.Cancel();
        _driverCts = new CancellationTokenSource();

        DriverInstallPhase = "HAZIRLANIYOR";
        DriverInstallPercent = 2;
        DriverInstallCurrentItem = "Geri yükleme noktası kontrol ediliyor ve Windows Update başlatılıyor...";
        DriverInstallSpeedText = "—";
        DriverInstallDetailText = "Başlatılıyor...";

        bool shouldRescan = false;
        try
        {
            await EnsureAutoRestorePointAsync("Sürücü Güncellemesi Öncesi");

            var progress = new Progress<DriverInstallProgress>(p =>
            {
                DriverInstallPhase = p.Phase.ToUpperInvariant();
                DriverInstallPercent = p.Percent;
                DriverInstallCurrentItem = p.TotalCount > 0
                    ? $"({p.CurrentIndex}/{p.TotalCount})  {p.CurrentDriverName}"
                    : p.CurrentDriverName;
                DriverInstallSpeedText = p.SpeedMbps >= 1.0
                    ? $"{p.SpeedMbps:F1} MB/s"
                    : p.SpeedMbps >= 0.02
                        ? $"{p.SpeedMbps * 1024.0:F0} KB/s"
                        : p.Phase == "Kuruluyor" ? "Kuruluyor..." : "Bekleniyor...";
                DriverInstallDetailText = $"{p.DetailText}  •  Geçen süre: {p.ElapsedText}";
                DriverAuditSummary = $"{p.Phase}: {DriverInstallCurrentItem}";
                StatusBanner = $"{p.Phase} (%{p.Percent:F0}) — {p.CurrentDriverName}";
            });

            var (ok, installed, failed, _, msg) = await _driverService.InstallDriverUpdatesAsync(singleUpdateId, progress, _driverCts.Token);
            DriverAuditSummary = msg;
            StatusBanner = msg;
            ShowToast(ok ? "Sürücü Kurulumu Tamamlandı" : "Sürücü Kurulum Uyarısı", msg,
                (ok && failed == 0) ? FindingSeverity.Info : FindingSeverity.High);
            shouldRescan = installed > 0 || ok;
        }
        finally
        {
            // Release install lock BEFORE triggering re-scan so ScanHardwareDriversAsync actually runs!
            IsInstallingDrivers = false;
        }

        if (shouldRescan)
            await ScanHardwareDriversAsync();
    }

    [RelayCommand]
    private void OpenWindowsOptionalUpdates() => DriverUpdateService.OpenWindowsOptionalUpdates();

    [RelayCommand]
    private void OpenVendorDriverPortal() => DriverUpdateService.OpenVendorDriverPortal(Metrics.GpuName);

    // --- Unused Services & Background Bloat/Risk Audit Commands ---
    [RelayCommand]
    private async Task ScanServicesAndBloatAsync()
    {
        ServiceAuditSummary = "Kullanılmayan Windows servisleri ve arka plan bloatware/riskli süreçler taranıyor...";
        try
        {
            var srvTask = _serviceAudit.ScanUnusedServicesAsync();
            var procTask = Task.Run(() => _telemetry.GetTopProcesses(110, "RAM", _firewall.IsBlocked));
            await Task.WhenAll(srvTask, procTask);

            UnusedServices.Clear();
            foreach (var s in srvTask.Result) UnusedServices.Add(s);

            var bloat = _serviceAudit.AuditRunningBloatAndRisks(procTask.Result);
            BloatAndRiskyProcesses.Clear();
            foreach (var b in bloat) BloatAndRiskyProcesses.Add(b);

            int unoptSrv = UnusedServices.Count(s => !s.IsOptimized);
            int riskyCount = bloat.Count(b => b.IsRisky);
            int bloatCount = bloat.Count(b => !b.IsRisky);
            double bloatRam = bloat.Where(b => !b.IsRisky).Sum(b => b.RamMb);

            ServiceAuditSummary = $"{UnusedServices.Count} denetlenen servis ({unoptSrv} tanesi optimize edilebilir — kritik sistem servisleri korunur).";
            BloatAuditSummary = $"{bloatCount} gereksiz arka plan süreci ({bloatRam:F0} MB RAM)  •  {riskyCount} imzasız/incelenmesi önerilen süreç.";
            StatusBanner = $"{ServiceAuditSummary} {BloatAuditSummary}";
        }
        catch (Exception ex)
        {
            ServiceAuditSummary = $"Servis tarama hatası: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ToggleWindowsServiceAsync(ServiceOptimizationItem? item)
    {
        if (item == null) return;
        await EnsureAutoRestorePointAsync($"Servis Ayarı ({item.ServiceName})");
        var (ok, msg) = _serviceAudit.ToggleService(item);
        StatusBanner = msg;
        ShowToast(ok ? "Windows Servisi" : "Servis Hatası", msg, ok ? FindingSeverity.Info : FindingSeverity.High);
        await ScanServicesAndBloatAsync();
    }

    [RelayCommand]
    private async Task OptimizeAllSafeServicesAsync()
    {
        await EnsureAutoRestorePointAsync("Güvenli Servis Optimizasyonu");
        var (_, msg) = await _serviceAudit.OptimizeAllSafeServicesAsync();
        StatusBanner = msg;
        ShowToast("Servis Optimizasyonu", msg, FindingSeverity.Info);
        await ScanServicesAndBloatAsync();
    }

    [RelayCommand]
    private void EcoModeAllBloatProcesses()
    {
        int applied = 0;
        foreach (var item in BloatAndRiskyProcesses.Where(b => !b.IsRisky))
        {
            if (_telemetry.SetEfficiencyMode(item.Pid, true))
                applied++;
        }
        StatusBanner = applied > 0
            ? $"{applied} gereksiz arka plan süreci Verimlilik Moduna (EcoQoS) alındı."
            : "Eco moduna alınabilecek yeni arka plan süreci bulunamadı.";
        ShowToast("Arka Plan Optimizasyonu", StatusBanner, FindingSeverity.Info);
    }

    [RelayCommand]
    private void EcoModeAuditedProcess(BloatProcessAuditItem? item)
    {
        if (item == null || item.Pid <= 4) return;
        bool ok = _telemetry.SetEfficiencyMode(item.Pid, true);
        StatusBanner = ok ? $"{item.ProcessName} Verimlilik Moduna (EcoQoS) alındı." : $"{item.ProcessName} Eco moduna alınamadı.";
    }

    [RelayCommand]
    private void KillAuditedProcess(BloatProcessAuditItem? item)
    {
        if (item == null || item.Pid <= 4) return;
        if (_telemetry.KillProcess(item.Pid))
        {
            BloatAndRiskyProcesses.Remove(item);
            StatusBanner = $"{item.ProcessName} (PID {item.Pid}) sonlandırıldı.";
            ShowToast("Süreç Kapatıldı", StatusBanner, FindingSeverity.Info);
        }
    }
}
