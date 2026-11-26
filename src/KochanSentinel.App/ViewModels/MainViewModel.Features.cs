using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KochanSentinel.Core.Models;
using KochanSentinel.Core.Services;

namespace KochanSentinel.App.ViewModels;

public sealed class CommandPaletteAction
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Category { get; init; } = "";
    public string Shortcut { get; init; } = "";
    public Action ExecuteAction { get; init; } = () => { };
}

public partial class MainViewModel
{
    private readonly UpdateService _updater = new();
    private readonly StartupManagerService _startupMgr = new();
    private readonly DnsSwitcherService _dnsSwitcher = new();
    private readonly ClipboardGuardService _clipboardGuard = new();
    private readonly WebhookAlertService _webhook = new();
    private readonly ReportExportService _reportExport = new();
    private CancellationTokenSource? _updateCts;
    private CancellationTokenSource? _dupCts;
    private List<CommandPaletteAction> _allPaletteActions = new();

    public event Action<int>? OnNavigateTabRequested;
    public event Action? OnToggleMiniHudRequested;
    public event Action? OnExportHtmlReportDialogRequested;
    public event Action? OnBeforeSelfUpdateShutdownRequested;

    public string AppVersionText => $"v{UpdateService.GetCurrentAppVersion()}";

    public string[] HotkeyPresets { get; } = { "Ctrl+Shift+H", "Alt+Z", "Ctrl+Alt+H", "F9" };
    public string[] BackdropPresets { get; } = { "None", "Mica", "Acrylic" };

    // Auto-Updater State
    [ObservableProperty] private bool _hasAvailableUpdate;
    [ObservableProperty] private UpdateCheckResult? _availableUpdate;
    [ObservableProperty] private bool _isDownloadingUpdate;
    [ObservableProperty] private double _updateProgressPercent;
    [ObservableProperty] private string _updateBannerText = "";

    // Command Palette (Ctrl + K)
    [ObservableProperty] private bool _isCommandPaletteOpen;
    [ObservableProperty] private string _commandPaletteQuery = "";
    [ObservableProperty] private CommandPaletteAction? _selectedPaletteAction;
    public ObservableCollection<CommandPaletteAction> FilteredPaletteCommands { get; } = new();

    // Boot Time & Startup Manager
    [ObservableProperty] private BootPerformanceInfo _bootPerformance = new();
    [ObservableProperty] private string _startupManagerSummary = "Başlangıç uygulamaları ve Windows açılış süresi taranmaya hazır.";
    public ObservableCollection<StartupAppItem> StartupApps { get; } = new();

    // One-Click Secure DNS Switcher
    [ObservableProperty] private string _selectedDnsAdapter = "";
    [ObservableProperty] private string _currentDnsText = "Okunuyor...";
    public ObservableCollection<string> ActiveNetworkAdapters { get; } = new();
    public ObservableCollection<DnsProfileItem> DnsProfiles { get; } = new();

    // Duplicate File Finder
    [ObservableProperty] private bool _isFindingDuplicates;
    [ObservableProperty] private string _duplicateSummaryText = "Seçili disk/klasörde birebir aynı (SHA-256 doğrulamalı) yinelenen dosyaları bul.";
    public ObservableCollection<DuplicateFileGroup> DuplicateGroups { get; } = new();

    partial void OnCommandPaletteQueryChanged(string value) => RefreshPaletteFilter();

    partial void OnSelectedDnsAdapterChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            CurrentDnsText = _dnsSwitcher.GetCurrentDnsSummary(value);
    }

    private void InitializeModernFeatures()
    {
        foreach (var p in DnsSwitcherService.GetProfiles())
            DnsProfiles.Add(p);

        RefreshDnsAdapters();
        BuildCommandPaletteCatalog();

        // Load boot time asynchronously in background
        _ = Task.Run(async () =>
        {
            var boot = await _startupMgr.ReadLastBootPerformanceAsync();
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                BootPerformance = boot;
            });
        });

        // Silent GitHub Update Check ~6 seconds after startup
        if (Settings.AutoCheckUpdatesOnStart)
        {
            _ = Task.Delay(6000).ContinueWith(async _ =>
            {
                var res = await _updater.CheckForUpdateAsync(GitHubRepoSlug);
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (res.HasUpdate)
                    {
                        AvailableUpdate = res;
                        HasAvailableUpdate = true;
                        UpdateUrl = res.ReleaseUrl;
                        UpdateStatusText = res.Message;
                        UpdateBannerText = $"Yeni sürüm hazır: {res.LatestVersion} (Mevcut: v{res.CurrentVersion}) — Tek tıkla otomatik güncelleyebilirsin!";
                        ShowToast("Yeni Güncelleme Mevcut!", $"{res.LatestVersion} yayınlandı. Üst çubuktan tek tıkla güncelleyebilirsin.", FindingSeverity.Info);
                    }
                });
            });
        }
    }

    private void BuildCommandPaletteCatalog()
    {
        _allPaletteActions = new List<CommandPaletteAction>
        {
            new() { Title = "Tek Tıkla Hızlandır (Quick Boost)", Subtitle = "RAM + EcoQoS + Temp + Standby List temizliği", Category = "Aksiyon", Shortcut = "Boost", ExecuteAction = () => _ = QuickBoostAsync() },
            new() { Title = "Mini HUD Aç / Kapat", Subtitle = $"Yüzer performans göstergesi ({Settings.HudHotkeyPreset})", Category = "Görünüm", Shortcut = Settings.HudHotkeyPreset, ExecuteAction = () => OnToggleMiniHudRequested?.Invoke() },
            new() { Title = "HTML Sistem Sağlık Raporu Dışa Aktar", Subtitle = "Donanım, sıcaklık, benchmark ve güvenlik bulgularını tek rapor yap", Category = "Rapor", Shortcut = "HTML", ExecuteAction = () => OnExportHtmlReportDialogRequested?.Invoke() },
            new() { Title = "Güncelleme Denetle (GitHub Releases)", Subtitle = "Yeni sürümü kontrol et ve otomatik güncelle", Category = "Sistem", Shortcut = AppVersionText, ExecuteAction = () => _ = CheckUpdateAsync() },
            new() { Title = "Derin Güvenlik & Kalıntı Taraması", Subtitle = "Registry, Görevler, Servisler, WMI, RAT & C2 denetimi", Category = "Güvenlik", ExecuteAction = () => { OnNavigateTabRequested?.Invoke(4); _ = RunPersistenceAuditAsync(); } },
            new() { Title = "RAM & Standby List Optimize Et", Subtitle = "Working Set ve Standby önbelleğini boşalt", Category = "Optimizasyon", ExecuteAction = () => _ = OptimizeRamAsync() },
            new() { Title = "Manuel Oyun Modunu Aç / Kapat", Subtitle = "Yüksek Performans + 0.5ms Timer + Arka Plan Susturma", Category = "Oyun", ExecuteAction = () => ToggleManualGameMode() },
            new() { Title = "Sistem Benchmark Başlat", Subtitle = "CPU, RAM, 3D SIMD, NVMe 4K IOPS + Termal Throttling testi", Category = "Benchmark", ExecuteAction = () => { OnNavigateTabRequested?.Invoke(6); _ = RunFullBenchmarkAsync(); } },
            new() { Title = "Disk Analizi (TreeSize & Treemap)", Subtitle = "Paralel hızlı klasör ve yinelenen dosya analizi", Category = "Depolama", ExecuteAction = () => OnNavigateTabRequested?.Invoke(7) },
            new() { Title = "Canlı Monitör & Ağ Kalkanı", Subtitle = "Süreçler, canlı ağ hızı (KB/s), GeoIP ve DNS değiştirici", Category = "İzleme", ExecuteAction = () => OnNavigateTabRequested?.Invoke(1) },
            new() { Title = "Başlangıç Yöneticisi & Boot Süresi", Subtitle = "PC açılış süresini gör ve başlangıç uygulamalarını yönet", Category = "Optimizasyon", ExecuteAction = () => { OnNavigateTabRequested?.Invoke(2); _ = ScanStartupAppsAsync(); } },
            new() { Title = "SFC /scannow Sistem Onarımı", Subtitle = "Bozuk Windows sistem dosyalarını tara ve onar", Category = "Onarım", ExecuteAction = () => { OnNavigateTabRequested?.Invoke(2); _ = RunSfcAsync(); } },
            new() { Title = "Geri Yükleme Noktası Oluştur", Subtitle = "Windows Sistem Geri Yükleme sigortası al", Category = "Yedek", ExecuteAction = () => _ = CreateRestorePointNowAsync() },
            new() { Title = "Genel Bakış (Dashboard)", Subtitle = "Canlı telemetri, sağlık puanı ve sürücüler", Category = "Sayfa", ExecuteAction = () => OnNavigateTabRequested?.Invoke(0) },
            new() { Title = "Oyun Merkezi & Kütüphane", Subtitle = "Steam, Epic ve Riot oyunlarını doğrula & başlat", Category = "Sayfa", ExecuteAction = () => OnNavigateTabRequested?.Invoke(3) },
            new() { Title = "Ayarlar & Kalkan Yapılandırması", Subtitle = "Tema, kısayollar, API anahtarları, Discord Webhook", Category = "Sayfa", ExecuteAction = () => OnNavigateTabRequested?.Invoke(5) }
        };
        RefreshPaletteFilter();
    }

    private void RefreshPaletteFilter()
    {
        var q = CommandPaletteQuery?.Trim() ?? "";
        IEnumerable<CommandPaletteAction> matches = _allPaletteActions;
        if (!string.IsNullOrEmpty(q))
        {
            matches = matches.Where(a =>
                a.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                a.Subtitle.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                a.Category.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        FilteredPaletteCommands.Clear();
        foreach (var m in matches) FilteredPaletteCommands.Add(m);
        SelectedPaletteAction = FilteredPaletteCommands.FirstOrDefault();
    }

    [RelayCommand]
    private void OpenCommandPalette()
    {
        BuildCommandPaletteCatalog();
        CommandPaletteQuery = "";
        IsCommandPaletteOpen = true;
    }

    [RelayCommand]
    private void CloseCommandPalette()
    {
        IsCommandPaletteOpen = false;
    }

    [RelayCommand]
    private void ExecutePaletteAction(CommandPaletteAction? action)
    {
        var target = action ?? SelectedPaletteAction;
        IsCommandPaletteOpen = false;
        target?.ExecuteAction();
    }

    // --- Auto-Updater Commands ---
    [RelayCommand]
    private async Task InstallAvailableUpdateAsync()
    {
        if (IsDownloadingUpdate) return;
        if (AvailableUpdate == null || !AvailableUpdate.HasUpdate)
        {
            await CheckUpdateAsync();
            if (AvailableUpdate == null || !AvailableUpdate.HasUpdate) return;
        }

        if (string.IsNullOrWhiteSpace(AvailableUpdate.ExeDownloadUrl))
        {
            OpenUpdateUrl();
            return;
        }

        IsDownloadingUpdate = true;
        UpdateProgressPercent = 2;
        _updateCts?.Cancel();
        _updateCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<UpdateDownloadProgress>(p =>
            {
                UpdateProgressPercent = p.Percent;
                UpdateBannerText = p.StatusText;
                UpdateStatusText = p.StatusText;
            });

            var (ok, stagedPath, msg) = await _updater.DownloadAndVerifyUpdateAsync(AvailableUpdate, progress, _updateCts.Token);
            if (!ok)
            {
                UpdateBannerText = msg;
                UpdateStatusText = msg;
                ShowToast("Güncelleme Hatası", msg, FindingSeverity.High);
                return;
            }

            var (applied, applyMsg) = UpdateService.ApplyStagedUpdateAndRelaunch(
                stagedPath,
                () => OnBeforeSelfUpdateShutdownRequested?.Invoke());

            if (applied)
            {
                StatusBanner = applyMsg;
                System.Windows.Application.Current?.Shutdown();
            }
            else
            {
                UpdateBannerText = applyMsg;
                ShowToast("Güncelleme Uygulanamadı", applyMsg, FindingSeverity.High);
            }
        }
        finally
        {
            IsDownloadingUpdate = false;
        }
    }

    [RelayCommand]
    private void DismissUpdateBanner()
    {
        HasAvailableUpdate = false;
    }

    [RelayCommand]
    private void ShowUpdateReleaseNotes()
    {
        if (AvailableUpdate == null) return;
        string notes = string.IsNullOrWhiteSpace(AvailableUpdate.ReleaseNotes)
            ? "Bu sürüm için ek sürüm notu girilmemiş."
            : AvailableUpdate.ReleaseNotes;
        System.Windows.MessageBox.Show(
            $"KOCHAN Sentinel {AvailableUpdate.LatestVersion}\n\n{notes}",
            $"Sürüm Notları — {AvailableUpdate.LatestVersion}",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    // --- Startup Manager & Boot Performance ---
    [RelayCommand]
    private async Task ScanStartupAppsAsync()
    {
        StartupManagerSummary = "Başlangıç uygulamaları ve Windows açılış performansı okunuyor...";
        try
        {
            var bootTask = _startupMgr.ReadLastBootPerformanceAsync();
            var appsTask = _startupMgr.ScanStartupAppsAsync();
            await Task.WhenAll(bootTask, appsTask);

            BootPerformance = bootTask.Result;
            StartupApps.Clear();
            foreach (var app in appsTask.Result) StartupApps.Add(app);

            int active = StartupApps.Count(a => a.IsEnabled);
            StartupManagerSummary = $"{StartupApps.Count} başlangıç kaydı ({active} aktif, {StartupApps.Count - active} devre dışı)  •  {BootPerformance.SummaryText}";
            StatusBanner = StartupManagerSummary;
        }
        catch (Exception ex)
        {
            StartupManagerSummary = $"Başlangıç tarama hatası: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ToggleStartupApp(StartupAppItem? item)
    {
        if (item == null) return;
        bool nextState = !item.IsEnabled;
        var (ok, msg) = _startupMgr.SetStartupEnabled(item, nextState);
        StatusBanner = msg;
        if (ok)
        {
            int idx = StartupApps.IndexOf(item);
            if (idx >= 0)
            {
                StartupApps.RemoveAt(idx);
                StartupApps.Insert(idx, item);
            }
            ShowToast("Başlangıç Yöneticisi", msg, FindingSeverity.Info);
        }
        else
        {
            ShowToast("Başlangıç Hatası", msg, FindingSeverity.High);
        }
    }

    // --- One-Click Secure DNS Switcher ---
    [RelayCommand]
    private void RefreshDnsAdapters()
    {
        var adapters = _dnsSwitcher.GetActiveAdapterNames();
        ActiveNetworkAdapters.Clear();
        foreach (var a in adapters) ActiveNetworkAdapters.Add(a);
        if (string.IsNullOrWhiteSpace(SelectedDnsAdapter) || !ActiveNetworkAdapters.Contains(SelectedDnsAdapter))
            SelectedDnsAdapter = ActiveNetworkAdapters.FirstOrDefault() ?? "";
        CurrentDnsText = string.IsNullOrWhiteSpace(SelectedDnsAdapter)
            ? "Aktif ağ bağdaştırıcısı bulunamadı"
            : _dnsSwitcher.GetCurrentDnsSummary(SelectedDnsAdapter);
    }

    [RelayCommand]
    private async Task ApplyDnsProfileAsync(DnsProfileItem? profile)
    {
        if (profile == null) return;
        if (string.IsNullOrWhiteSpace(SelectedDnsAdapter))
            RefreshDnsAdapters();

        StatusBanner = $"DNS uygulanıyor: {profile.Name}...";
        var (ok, msg) = await _dnsSwitcher.ApplyProfileAsync(SelectedDnsAdapter, profile, enableDoh: true);
        CurrentDnsText = _dnsSwitcher.GetCurrentDnsSummary(SelectedDnsAdapter);
        StatusBanner = msg;
        ShowToast(ok ? "DNS Güncellendi" : "DNS Hatası", msg, ok ? FindingSeverity.Info : FindingSeverity.High);
        if (ok)
            AddSecurityEvent("DNS Kalkanı", msg, FindingSeverity.Info);
    }

    // --- Duplicate File Finder ---
    [RelayCommand]
    private async Task FindDuplicatesAsync()
    {
        if (IsFindingDuplicates) return;
        string target = SelectedDiskTarget;
        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
        {
            StatusBanner = "Yinelenen dosya aramak için geçerli bir klasör/sürücü seçin.";
            return;
        }

        IsFindingDuplicates = true;
        DuplicateGroups.Clear();
        DuplicateSummaryText = $"Taranıyor: {target} (2 MB üzeri birebir aynı SHA-256 dosyalar)...";
        _dupCts?.Cancel();
        _dupCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<DiskScanProgress>(p =>
            {
                DuplicateSummaryText = $"{p.CurrentPath} ({p.ScannedFiles:N0} aday dosya)";
            });

            var groups = await _diskAnalyzer.FindDuplicatesAsync(target, 2 * 1024 * 1024, progress, _dupCts.Token);
            foreach (var g in groups) DuplicateGroups.Add(g);

            long wasted = groups.Sum(g => g.WastedBytes);
            DuplicateSummaryText = groups.Count == 0
                ? $"Harika! {target} içinde 2 MB üzeri yinelenen kopya dosya bulunamadı."
                : $"{groups.Count} grup yinelenen dosya bulundu — Toplam kazanılabilir alan: {JunkCategory.FormatBytes(wasted)}";
            StatusBanner = DuplicateSummaryText;
            ShowToast("Yinelenen Dosya Taraması", DuplicateSummaryText, FindingSeverity.Info);
        }
        catch (OperationCanceledException)
        {
            DuplicateSummaryText = "Yinelenen dosya taraması iptal edildi.";
        }
        catch (Exception ex)
        {
            DuplicateSummaryText = $"Tarama hatası: {ex.Message}";
        }
        finally
        {
            IsFindingDuplicates = false;
        }
    }

    [RelayCommand]
    private void CancelDuplicateScan() => _dupCts?.Cancel();

    [RelayCommand]
    private void RecycleDuplicateCopies(DuplicateFileGroup? group)
    {
        if (group == null) return;
        var copies = group.Files.Where(f => !f.IsOriginalKeeper).ToList();
        if (copies.Count == 0) return;

        var confirm = System.Windows.MessageBox.Show(
            $"\"{group.FileNameSample}\" dosyasının en eski asıl kopyası KORUNACAK,\ndiğer {copies.Count} kopyası ({group.WastedFormatted}) Geri Dönüşüm Kutusuna taşınacak.\n\nOnaylıyor musunuz?",
            "KOCHAN — Yinelenen Kopyaları Temizle",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        int moved = 0;
        foreach (var copy in copies)
        {
            if (KochanSentinel.Core.Interop.NativeMethods.SendToRecycleBin(copy.FullPath))
                moved++;
        }

        if (moved > 0)
        {
            DuplicateGroups.Remove(group);
            string msg = $"{moved} kopya Geri Dönüşüme taşındı ({group.WastedFormatted} kazanıldı).";
            StatusBanner = msg;
            ShowToast("Kopyalar Temizlendi", msg, FindingSeverity.Info);
        }
    }

    // --- Process Context Menu Actions ---
    [RelayCommand]
    private void SetProcessPriority(string? level)
    {
        var target = SelectedProcess;
        if (target == null || target.IsSystemCritical || string.IsNullOrWhiteSpace(level)) return;

        var prio = level switch
        {
            "High" => ProcessPriorityClass.High,
            "AboveNormal" => ProcessPriorityClass.AboveNormal,
            "BelowNormal" => ProcessPriorityClass.BelowNormal,
            "Idle" => ProcessPriorityClass.Idle,
            _ => ProcessPriorityClass.Normal
        };

        bool ok = _telemetry.SetProcessPriority(target.Pid, prio);
        StatusBanner = ok
            ? $"{target.Name} önceliği '{prio}' olarak ayarlandı."
            : $"{target.Name} önceliği değiştirilemedi.";
        if (ok) ShowToast("Süreç Önceliği", StatusBanner, FindingSeverity.Info);
    }

    [RelayCommand]
    private async Task ScanSelectedProcessExeAsync(ProcessItem? proc)
    {
        var target = proc ?? SelectedProcess;
        if (target == null || string.IsNullOrWhiteSpace(target.ExecutablePath) || !File.Exists(target.ExecutablePath))
        {
            StatusBanner = "Bu sürecin dosya yolu alınamadı.";
            return;
        }
        OnNavigateTabRequested?.Invoke(4);
        await ScanFileAsync(target.ExecutablePath);
    }

    // --- Clipboard Crypto Clipper Guard ---
    public (bool ShouldRestore, string SafeText) HandleClipboardTextChanged(string text)
    {
        _clipboardGuard.IsEnabled = Settings.EnableClipboardClipperGuard;
        var (hijack, restore, ev) = _clipboardGuard.InspectClipboardText(text);
        if (hijack && ev != null)
        {
            AddSecurityEvent(ev.Source, ev.Message, ev.Severity);
            AddHistoryEntry("Clipper Kalkanı", "Kripto Cüzdan Panosu", ev.Message, "Pano Kaçırma Engellendi", FindingSeverity.Critical);
            ShowToast("KRİPTO CLIPPER ENGELLENDİ!", ev.Message, FindingSeverity.Critical);
            OnTrayNotificationRequested?.Invoke("KOCHAN Kripto Clipper Alarmı!", ev.Message, true);
            DispatchWebhookIfConfigured("Kripto Clipper Engellendi!", ev.Message, FindingSeverity.Critical);
            return (true, restore);
        }
        return (false, "");
    }

    // --- Discord Webhook Alerts ---
    [RelayCommand]
    private async Task TestDiscordWebhookAsync()
    {
        var (ok, msg) = await _webhook.SendAlertAsync(
            Settings.DiscordWebhookUrl,
            "KOCHAN Sentinel Test Bildirimi",
            $"Discord Webhook bağlantısı başarıyla doğrulandı ({Environment.MachineName}). Yüksek/Kritik tehditler anında buraya bildirilecek.",
            FindingSeverity.Info,
            bypassRateLimit: true);
        StatusBanner = msg;
        ShowToast("Discord Webhook", msg, ok ? FindingSeverity.Info : FindingSeverity.High);
    }

    private void DispatchWebhookIfConfigured(string title, string message, FindingSeverity severity)
    {
        if (severity < FindingSeverity.High || string.IsNullOrWhiteSpace(Settings.DiscordWebhookUrl))
            return;
        _ = _webhook.SendAlertAsync(Settings.DiscordWebhookUrl, title, message, severity);
    }

    // --- One-Click HTML Health & Security Report ---
    public void ExportHtmlReportToPath(string path)
    {
        try
        {
            var fwRules = _firewall.GetBlockedRulesSnapshot();
            string saved = _reportExport.ExportHtmlReport(
                path,
                Metrics,
                LatestBenchmark,
                BootPerformance,
                PersistenceFindings,
                fwRules,
                StartupApps,
                SecurityHistory);

            StatusBanner = $"HTML Sağlık & Güvenlik Raporu oluşturuldu: {Path.GetFileName(saved)}";
            ShowToast("HTML Rapor Oluşturuldu", "Rapor varsayılan tarayıcıda açılıyor...", FindingSeverity.Info);
            try { Process.Start(new ProcessStartInfo(saved) { UseShellExecute = true }); } catch { }
        }
        catch (Exception ex)
        {
            ShowToast("Rapor Hatası", ex.Message, FindingSeverity.High);
        }
    }

    public void NotifyJustUpdated()
    {
        ShowToast($"Güncelleme Tamamlandı ({AppVersionText})", $"KOCHAN Sentinel {AppVersionText} sürümüne başarıyla güncellendi!", FindingSeverity.Info);
        StatusBanner = $"KOCHAN Sentinel {AppVersionText} sürümüne güncellendi.";
    }
}
