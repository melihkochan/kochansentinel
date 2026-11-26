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

    // --- Driver Audit & Windows Update Drivers ---
    [ObservableProperty] private bool _isScanningDrivers;
    [ObservableProperty] private string _driverAuditSummary = "Eksik/hatalı aygıtlar, eski sürücüler ve resmi Windows Update sürücü güncellemeleri taranmaya hazır.";
    [ObservableProperty] private int _pendingDriverUpdateCount;
    public ObservableCollection<DriverAuditItem> DriverAuditItems { get; } = new();

    // --- Monitor & Color Calibration ---
    [ObservableProperty] private MonitorDisplayInfo? _selectedMonitor;
    [ObservableProperty] private string _monitorSummaryText = "Bağlı monitörlerin Hz, çözünürlük, ICC profili ve renk önerileri taranıyor...";
    [ObservableProperty] private int _monitorBrightnessValue = 100;
    [ObservableProperty] private int _monitorContrastValue = 50;
    [ObservableProperty] private string _brightnessControlModeText = "Ekran algılanıyor...";
    [ObservableProperty] private string _activeColorPresetName = "Varsayılan (Standart 6500K)";
    public ObservableCollection<MonitorDisplayInfo> Monitors { get; } = new();
    public ObservableCollection<ColorProfilePreset> ColorPresets { get; } = new();

    // --- Unused Services & Background Bloat/Risk Audit ---
    [ObservableProperty] private string _serviceAuditSummary = "Kullanılmayan telemetri/arka plan servisleri ve gereksiz süreçler denetlenmeye hazır.";
    [ObservableProperty] private string _bloatAuditSummary = "Arka planda çalışan gereksiz veya imzasız süreçler analiz ediliyor...";
    public ObservableCollection<ServiceOptimizationItem> UnusedServices { get; } = new();
    public ObservableCollection<BloatProcessAuditItem> BloatAndRiskyProcesses { get; } = new();

    partial void OnSelectedMonitorChanged(MonitorDisplayInfo? value)
    {
        if (value == null) return;
        if (value.SupportsDdcBrightness && value.CurrentBrightness >= 0)
        {
            MonitorBrightnessValue = value.CurrentBrightness;
            MonitorContrastValue = value.CurrentContrast >= 0 ? value.CurrentContrast : 50;
            BrightnessControlModeText = "✓ DDC/CI Donanım Kontrolü Aktif (gerçek monitör parlaklığı)";
        }
        else
        {
            MonitorBrightnessValue = 100;
            MonitorContrastValue = 50;
            BrightnessControlModeText = "⚡ Yazılımsal Gama Modu Aktif (Monitör OSD'sinde DDC/CI kapalıysa otomatik devreye girer)";
        }
    }

    private void InitializeHardwareStudio()
    {
        foreach (var preset in DisplayAndColorService.GetColorPresets())
            ColorPresets.Add(preset);
    }

    // --- Monitor & Color Commands ---
    [RelayCommand]
    private async Task ScanMonitorsAsync()
    {
        MonitorSummaryText = "Monitör Hz, çözünürlük, DDC/CI parlaklık ve ICC renk profili okunuyor...";
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
    private void ApplyColorPreset(ColorProfilePreset? preset)
    {
        if (preset == null) return;
        _activeColorPreset = preset;
        if (preset.Id == "default" && (SelectedMonitor == null || !SelectedMonitor.SupportsDdcBrightness))
        {
            MonitorBrightnessValue = 100;
            MonitorContrastValue = 50;
        }

        var (ok, msg) = _displayService.ApplyColorPreset(SelectedMonitor, preset);
        if (ok) ActiveColorPresetName = preset.Name;
        StatusBanner = msg;
        ShowToast(ok ? "Renk Profili Uygulandı" : "Renk Ayarı Uyarısı", msg, ok ? FindingSeverity.Info : FindingSeverity.Medium);
    }

    public void ApplyMonitorBrightnessLive(int percent)
    {
        var mon = SelectedMonitor;
        _ = Task.Run(() =>
        {
            bool hwOk = mon != null && mon.SupportsDdcBrightness && _displayService.SetHardwareBrightness(mon, percent);
            if (!hwOk)
            {
                var (swOk, swMsg) = _displayService.ApplySoftwareBrightnessContrast(mon, percent, MonitorContrastValue, _activeColorPreset);
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    BrightnessControlModeText = swOk
                        ? $"⚡ Yazılımsal Gama Modu (Parlaklık %{percent} / Kontrast %{MonitorContrastValue})"
                        : swMsg;
                    StatusBanner = swMsg;
                });
            }
            else
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    BrightnessControlModeText = $"✓ DDC/CI Donanım Parlaklığı: %{percent}";
                    StatusBanner = $"Monitör donanımsal parlaklığı %{percent} yapıldı.";
                });
            }
        });
    }

    public void ApplyMonitorContrastLive(int percent)
    {
        var mon = SelectedMonitor;
        _ = Task.Run(() =>
        {
            bool hwOk = mon != null && mon.SupportsDdcBrightness && mon.CurrentContrast >= 0 && _displayService.SetHardwareContrast(mon, percent);
            if (!hwOk)
            {
                var (swOk, swMsg) = _displayService.ApplySoftwareBrightnessContrast(mon, MonitorBrightnessValue, percent, _activeColorPreset);
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    BrightnessControlModeText = swOk
                        ? $"⚡ Yazılımsal Gama Modu (Parlaklık %{MonitorBrightnessValue} / Kontrast %{percent})"
                        : swMsg;
                    StatusBanner = swMsg;
                });
            }
            else
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    BrightnessControlModeText = $"✓ DDC/CI Donanım Kontrastı: %{percent}";
                    StatusBanner = $"Monitör donanımsal kontrastı %{percent} yapıldı.";
                });
            }
        });
    }

    [RelayCommand]
    private void LaunchDisplayTool(string? tool)
    {
        if (!string.IsNullOrWhiteSpace(tool))
            DisplayAndColorService.LaunchWindowsTool(tool);
    }

    // --- Driver Audit & Update Commands ---
    [RelayCommand]
    private async Task ScanHardwareDriversAsync()
    {
        if (IsScanningDrivers) return;
        IsScanningDrivers = true;
        _driverCts?.Cancel();
        _driverCts = new CancellationTokenSource();
        DriverAuditSummary = "Aygıt Yöneticisi hataları, kurulu sürücü tarihleri ve Windows Update WHQL sürücüleri taranıyor...";

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
    private async Task InstallPendingDriverUpdatesAsync()
    {
        if (IsScanningDrivers) return;
        IsScanningDrivers = true;
        _driverCts?.Cancel();
        _driverCts = new CancellationTokenSource();

        try
        {
            await EnsureAutoRestorePointAsync("Sürücü Güncellemesi Öncesi");
            var progress = new Progress<string>(msg =>
            {
                DriverAuditSummary = msg;
                StatusBanner = msg;
            });

            var (ok, msg) = await _driverService.InstallPendingDriverUpdatesAsync(progress, _driverCts.Token);
            DriverAuditSummary = msg;
            StatusBanner = msg;
            ShowToast("Sürücü Güncelleme", msg, ok ? FindingSeverity.Info : FindingSeverity.High);
            if (ok)
                await ScanHardwareDriversAsync();
        }
        finally
        {
            IsScanningDrivers = false;
        }
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
