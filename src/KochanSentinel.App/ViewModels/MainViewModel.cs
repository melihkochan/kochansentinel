using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KochanSentinel.Core.Models;
using KochanSentinel.Core.Services;

namespace KochanSentinel.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly TelemetryService _telemetry = new();
    private readonly CleanerService _cleaner = new();
    private readonly RamOptimizerService _ramOptimizer = new();
    private readonly GameModeService _gameMode = new();
    private readonly GameLibraryService _gameLibrary = new();
    private readonly SecurityShieldService _security = new();
    private readonly QuarantineService _quarantine = new();
    private readonly FirewallService _firewall = new();
    private readonly SettingsService _settingsService = new();
    private readonly BenchmarkService _benchmark = new();
    private readonly DiskAnalyzerService _diskAnalyzer = new();
    private readonly RansomwareCanaryService _canary = new();
    private readonly UsbAndModuleGuardService _usbGuard = new();
    private readonly KernelDriverAuditService _kernelAudit = new();
    private readonly NetworkShieldService _netShield = new();
    private readonly SystemTweaksAndRepairService _tweaksRepair = new();
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _benchmarkCts;
    private CancellationTokenSource? _diskScanCts;
    private CancellationTokenSource? _repairCts;
    private DateTime _lastAutoRamOpt = DateTime.MinValue;
    private DateTime _lastIslcCheck = DateTime.MinValue;
    private DateTime _lastIdleMaintenance = DateTime.MinValue;
    private DateTime _lastRatMonitorScan = DateTime.MinValue;
    private readonly HashSet<string> _alertedRatKeys = new(StringComparer.OrdinalIgnoreCase);
    private List<ProcessItem> _allProcesses = new();

    public event Action<string, string, bool>? OnTrayNotificationRequested;
    public event Action<SystemMetrics>? OnMetricsUpdated;
    public event Action? OnSettingsSynced;

    [ObservableProperty] private int _selectedNavIndex = 0;
    [ObservableProperty] private SystemMetrics _metrics = new();
    [ObservableProperty] private AppSettings _settings;
    [ObservableProperty] private string _statusBanner = "KOCHAN Sentinel hazır. Sistem izleniyor.";
    [ObservableProperty] private string _lastScanLabel = "Son Kontrol: Az önce";
    [ObservableProperty] private bool _isBusy;

    // Processes & Dashboard Top Consumers
    [ObservableProperty] private string _processSearchQuery = "";
    [ObservableProperty] private string _processSortMode = "RAM";
    [ObservableProperty] private string _processSortTitle = "Sıralama: En Çok RAM Kullananlar";
    [ObservableProperty] private string _processCategoryFilter = "Tümü";
    [ObservableProperty] private bool _showOnlyActiveConsumers;
    [ObservableProperty] private ProcessItem? _selectedProcess;
    public ObservableCollection<ProcessItem> Processes { get; } = new();
    public ObservableCollection<ProcessItem> TopConsumers { get; } = new();
    public ObservableCollection<double> CpuHistory { get; } = new();
    public ObservableCollection<double> RamHistory { get; } = new();
    public ObservableCollection<double> NetHistory { get; } = new();
    public ObservableCollection<ToastNotification> ActiveToasts { get; } = new();

    // Cleaner & Live Action Center
    public ObservableCollection<JunkCategory> JunkCategories { get; } = new();
    public ObservableCollection<string> CleanerActivityLog { get; } = new();
    [ObservableProperty] private string _cleanerSummary = "Kategoriler taranıyor veya tarama başlatmaya hazır.";
    [ObservableProperty] private string _cleanerLiveAction = "";
    [ObservableProperty] private bool _hasActionResult;
    [ObservableProperty] private string _lastActionResultTitle = "";
    [ObservableProperty] private string _lastActionResultDetail = "";
    private bool _selectAllJunkState = true;

    // Game Mode & Installed Library
    [ObservableProperty] private bool _isGameBoostActive;
    [ObservableProperty] private string _activeGameLabel = "Bekleniyor (Oyun açıldığında otomatik devreye girer)";
    [ObservableProperty] private string _gameModeButtonText = "▶  Oyun Modunu Başlat";
    [ObservableProperty] private string _gameModeBadgeText = "○ İZLEMEDE";
    [ObservableProperty] private string _newGameExeName = "";
    [ObservableProperty] private string _gameLibrarySummary = "Bilgisayardaki Steam, Epic Games ve Riot oyunları taranıyor...";
    public ObservableCollection<string> CustomGames { get; } = new();
    public ObservableCollection<InstalledGame> InstalledGames { get; } = new();

    // Security & Persistent History
    [ObservableProperty] private bool _isRealTimeShieldActive;
    [ObservableProperty] private FileScanResult? _lastFileScan;
    [ObservableProperty] private string _persistenceSummary = "Sistem koruması aktif. Derin başlangıç ve kalıntı taraması başlatılmaya hazır.";
    [ObservableProperty] private string _securityHeroTitle = "Sistem Koruması Aktif — Güncelsiniz";
    [ObservableProperty] private string _securityHeroSubtitle = "İndirilenler, Masaüstü ve Temp klasörleri gerçek zamanlı izleniyor (SHA-256 + Entropi + MalwareBazaar + Defender).";
    [ObservableProperty] private int _selectedSecuritySubTab = 0;
    [ObservableProperty] private SecurityFinding? _selectedFinding;
    [ObservableProperty] private bool _selectAllFindings;
    [ObservableProperty] private string _removeSelectedButtonText = "Seçilenleri Kaldır / Devre Dışı Bırak";
    [ObservableProperty] private QuarantineEntry? _selectedQuarantineItem;
    private bool _hasRunInitialAudit;
    private bool _suppressSelectAllSync;
    public ObservableCollection<SecurityFinding> PersistenceFindings { get; } = new();
    public ObservableCollection<SecurityEvent> SecurityEvents { get; } = new();
    public ObservableCollection<SecurityHistoryEntry> SecurityHistory { get; } = new();
    public ObservableCollection<QuarantineEntry> QuarantineItems { get; } = new();
    public ObservableCollection<string> TrustedWhitelist { get; } = new();

    // Hardware Benchmark (UserBenchmark-style)
    [ObservableProperty] private BenchmarkResult? _latestBenchmark;
    [ObservableProperty] private bool _isBenchmarkRunning;
    [ObservableProperty] private string _benchmarkStageText = "Hazır — CPU, RAM ve Disk Hızını Ölç";
    [ObservableProperty] private string _benchmarkDetailText = "Gerçek SHA-256 + Kayan Nokta CPU, 192MB/64MB RAM Bant Genişliği & Gecikme ve önbelleksiz (NO_BUFFERING) SSD IOPS testi.";
    [ObservableProperty] private int _benchmarkProgressPercent;
    [ObservableProperty] private string _selectedBenchmarkDrive = @"C:\";
    public ObservableCollection<string> AvailableBenchmarkDrives { get; } = new();
    public ObservableCollection<BenchmarkResult> BenchmarkHistory { get; } = new();

    // TreeSize Disk Analyzer
    [ObservableProperty] private string _selectedDiskTarget = @"C:\";
    [ObservableProperty] private bool _isDiskAnalyzing;
    [ObservableProperty] private string _diskScanStatusText = "Hazır — Sürücü veya klasör seçip Disk Analizini başlat.";
    [ObservableProperty] private string _diskScanLivePath = "";
    [ObservableProperty] private string _diskSummaryStats = "";
    [ObservableProperty] private DiskNode? _currentDiskRoot;
    [ObservableProperty] private DiskNode? _selectedDiskNode;
    public ObservableCollection<DiskNode> DiskTreeRoots { get; } = new();
    public ObservableCollection<DiskNode> TopLargestFiles { get; } = new();
    public ObservableCollection<DiskExtensionStat> DiskExtensionStats { get; } = new();

    // Advanced Security & System Repair Collections
    public ObservableCollection<KernelDriverItem> KernelDrivers { get; } = new();
    public ObservableCollection<NetworkConnectionEntry> ExternalConnections { get; } = new();
    public ObservableCollection<FirewallRuleItem> BlockedFirewallRules { get; } = new();
    public ObservableCollection<DnsCacheEntry> DnsCacheEntries { get; } = new();
    public ObservableCollection<LoadedDllModuleInfo> InjectedDlls { get; } = new();
    public ObservableCollection<BrowserExtensionIssue> BrowserExtIssues { get; } = new();
    public ObservableCollection<SystemTweakItem> SystemTweaks { get; } = new();
    public ObservableCollection<string> RepairLogLines { get; } = new();

    [ObservableProperty] private string _driverScanSummary = "Kernel (.sys) sürücüleri ve BYOVD zafiyet denetimi için tarama başlatın.";
    [ObservableProperty] private string _moduleScanSummary = "Çalışan süreçlerdeki imzasız DLL enjeksiyonları ve yüksek riskli tarayıcı eklentileri.";
    [ObservableProperty] private string _netShieldSummary = "Dış bağlantılar (GeoIP + Port), engellenen Firewall kuralları ve DNS önbelleği.";
    [ObservableProperty] private bool _isRepairRunning;
    [ObservableProperty] private string _repairStatusText = "Sistem tweaks, Geri Yükleme Noktası veya SFC/DISM onarımı hazır.";
    [ObservableProperty] private string _gitHubRepoSlug = "melihkochan/kochansentinel";
    [ObservableProperty] private string _updateStatusText = $"Mevcut Sürüm: v{SystemTweaksAndRepairService.CurrentAppVersion}";
    [ObservableProperty] private string _updateUrl = "";

    public MainViewModel()
    {
        _settings = _settingsService.Load();
        foreach (var g in _settings.CustomGameExecutables)
            CustomGames.Add(g);
        foreach (var w in _settings.TrustedWhitelist)
            TrustedWhitelist.Add(w);

        foreach (var h in _security.LoadHistory())
            SecurityHistory.Add(h);

        foreach (var q in _quarantine.LoadAll())
            QuarantineItems.Add(q);

        _gameMode.IsAutoModeEnabled = _settings.AutoGameMode;
        _gameMode.OnStatusChanged += (msg, sev) =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                UpdateGameModeUiState();
                AddSecurityEvent("Oyun Modu", msg, sev);
                StatusBanner = msg;
                OnTrayNotificationRequested?.Invoke("KOCHAN Oyun Modu", msg, false);
            });
        };

        _security.OnSecurityEvent += ev =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                AddSecurityEvent(ev.Source, ev.Message, ev.Severity);
                if (ev.Source.Contains("Gizli Süreç", StringComparison.OrdinalIgnoreCase) ||
                    ev.Source.Contains("Auto-Kill", StringComparison.OrdinalIgnoreCase))
                {
                    bool killed = ev.Source.Contains("Auto-Kill", StringComparison.OrdinalIgnoreCase);
                    AddHistoryEntry(
                        killed ? "Auto-Kill (EDR)" : "Gizli Süreç Uyarısı",
                        ev.Source,
                        ev.Message,
                        killed ? "Süreç Anında Durduruldu" : (ev.Severity >= FindingSeverity.High ? "Yüksek Riskli Komut İzlendi" : "İzlendi (Engellenmedi)"),
                        ev.Severity);
                    if (killed || ev.Severity >= FindingSeverity.High)
                        ShowToast(ev.Source, ev.Message, ev.Severity);
                }
                if (ev.Severity >= FindingSeverity.High)
                {
                    OnTrayNotificationRequested?.Invoke("KOCHAN Güvenlik Uyarısı!", ev.Message, true);
                    DispatchWebhookIfConfigured(ev.Source, ev.Message, ev.Severity);
                }
            });
        };

        _security.OnRealTimeFileScanned += res =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                LastFileScan = res;
                AddHistoryEntry(
                    "Anlık Kalkan",
                    res.FileName,
                    $"SHA-256: {res.Sha256[..Math.Min(16, res.Sha256.Length)]}... | Entropi: {res.ShannonEntropy:F2} | {res.CloudIntelResult}",
                    res.Verdict,
                    res.RiskLevel);

                bool isWhitelisted = Settings.TrustedWhitelist.Contains(res.FilePath, StringComparer.OrdinalIgnoreCase) ||
                                     Settings.TrustedWhitelist.Contains(res.FileName, StringComparer.OrdinalIgnoreCase);

                // Automatic Active Defense (EDR Auto-Quarantine for verified Critical malware drops)
                if (Settings.EnableAutoQuarantine &&
                    !isWhitelisted &&
                    res.RiskLevel == FindingSeverity.Critical &&
                    File.Exists(res.FilePath))
                {
                    TryTerminateProcessesByPath(res.FilePath);
                    var (ok, qEntry, qMsg) = _quarantine.QuarantineFile(res.FilePath, res.Sha256, $"Otomatik EDR Engeli: {res.Verdict}");
                    if (ok && qEntry != null)
                    {
                        QuarantineItems.Insert(0, qEntry);
                        AddHistoryEntry("Oto-Karantina (EDR)", qEntry.OriginalName, qEntry.OriginalPath, "Kritik Tehdit İzole Edildi", FindingSeverity.Critical);
                        ShowToast("Kritik Tehdit İzole Edildi", $"{qEntry.OriginalName} çalıştırılmadan AES-256 kasasına kilitlendi.", FindingSeverity.Critical);
                        OnTrayNotificationRequested?.Invoke("KOCHAN Aktif Koruma", $"{qEntry.OriginalName} otomatik karantinaya alındı!", true);
                        DispatchWebhookIfConfigured("Otomatik EDR Karantinası", $"{qEntry.OriginalName} ({qEntry.OriginalPath}) otomatik karantinaya alındı.", FindingSeverity.Critical);
                        StatusBanner = qMsg;
                    }
                }
            });
        };

        _security.AutoKillMaliciousScripts = _settings.AutoKillMaliciousScripts;
        _canary.OnRansomwareAlert += ev =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                AddSecurityEvent(ev.Source, ev.Message, ev.Severity);
                if (ev.Severity >= FindingSeverity.High)
                {
                    AddHistoryEntry("Fidye Kalkanı", "Honeypot Tuzak Dosyası", ev.Message, "Müdahale Uygulandı", ev.Severity);
                    ShowToast("Fidye Yazılımı Alarmı!", ev.Message, ev.Severity);
                    OnTrayNotificationRequested?.Invoke("KOCHAN Fidye Kalkanı!", ev.Message, true);
                    DispatchWebhookIfConfigured("Fidye Yazılımı Honeypot Alarmı!", ev.Message, ev.Severity);
                }
            });
        };
        _usbGuard.OnSecurityEvent += ev =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                AddSecurityEvent(ev.Source, ev.Message, ev.Severity);
                if (ev.Severity >= FindingSeverity.Medium)
                {
                    AddHistoryEntry("USB Kalkanı", ev.Source, ev.Message, ev.Severity >= FindingSeverity.High ? "Tehdit Yakalandı" : "Uyarı", ev.Severity);
                    ShowToast(ev.Source, ev.Message, ev.Severity);
                }
                if (ev.Severity >= FindingSeverity.High)
                    OnTrayNotificationRequested?.Invoke("KOCHAN USB Kalkanı!", ev.Message, true);
            });
        };

        SyncRealtimeProtections();
        ReloadSystemTweaks();
        InitializeModernFeatures();
        InitializeHardwareStudio();

        RefreshBenchmarkDrives();
        foreach (var b in _benchmark.LoadHistory())
            BenchmarkHistory.Add(b);
        LatestBenchmark = BenchmarkHistory.FirstOrDefault();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        for (int i = 0; i < 32; i++)
        {
            CpuHistory.Add(0);
            RamHistory.Add(0);
            NetHistory.Add(0);
        }
        _timer.Tick += async (_, _) => await RefreshTelemetryTickAsync();
        _timer.Start();

        _ = RefreshTelemetryTickAsync();
        _ = Task.Delay(2500).ContinueWith(_ =>
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(async () =>
            {
                if (!_hasRunInitialAudit && !IsBusy)
                    await RunPersistenceAuditAsync();
            });
        });
    }

    partial void OnProcessSearchQueryChanged(string value) => ApplyProcessFilter();
    partial void OnShowOnlyActiveConsumersChanged(bool value) => ApplyProcessFilter();

    partial void OnSelectedNavIndexChanged(int value)
    {
        if (value == 0 || value == 1)
            _ = RefreshTelemetryTickAsync();
        else if (value == 2)
        {
            ReloadSystemTweaks();
            if (JunkCategories.Count == 0 && !IsBusy)
                _ = ScanJunkAsync();
        }
        else if (value == 3 && InstalledGames.Count == 0 && !IsBusy)
            _ = ScanInstalledGamesAsync();
        else if (value == 4 && !_hasRunInitialAudit && !IsBusy)
            _ = RunPersistenceAuditAsync();
        else if (value == 6 || value == 7)
            RefreshBenchmarkDrives();
        else if (value == 8)
        {
            ReloadSystemTweaks();
            if (Monitors.Count == 0)
                _ = ScanMonitorsAsync();
        }
    }

    private bool _isRefreshing;

    private async Task RefreshTelemetryTickAsync()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            bool needFullProcesses = SelectedNavIndex == 1;
            bool needTopConsumers = SelectedNavIndex == 0;
            bool needRatSweep = Settings.EnableActiveRatMonitor && (DateTime.UtcNow - _lastRatMonitorScan).TotalSeconds >= 24;
            string sort = needFullProcesses ? ProcessSortMode : (needRatSweep && !needTopConsumers ? "Network" : "RAM");
            int highRisk = PersistenceFindings.Count(f => f.Severity >= FindingSeverity.High);
            int medRisk = PersistenceFindings.Count(f => f.Severity == FindingSeverity.Medium);
            var (m, procs) = await Task.Run(() =>
            {
                var metrics = _telemetry.CaptureMetrics(highRisk, medRisk);
                var topProcs = (needFullProcesses || needTopConsumers || needRatSweep)
                    ? _telemetry.GetTopProcesses((needFullProcesses || needRatSweep) ? 90 : 8, sort, _firewall.IsBlocked)
                    : null;
                return (metrics, topProcs);
            });

            if (needRatSweep && procs != null)
            {
                _lastRatMonitorScan = DateTime.UtcNow;
                var liveThreats = _security.ScanRunningProcessesForThreats(procs, Settings.TrustedWhitelist);
                foreach (var threat in liveThreats)
                {
                    string key = $"{threat.Name}|{threat.TargetPath}";
                    if (_alertedRatKeys.Add(key))
                    {
                        if (!PersistenceFindings.Any(f => f.TargetPath.Equals(threat.TargetPath, StringComparison.OrdinalIgnoreCase) && f.ProcessId == threat.ProcessId))
                        {
                            PersistenceFindings.Insert(0, threat);
                            UpdateSecurityHeroSummary();
                        }
                        AddSecurityEvent("Canlı RAT Dedektörü", $"{threat.Name} -> {threat.Reason}", threat.Severity);
                        AddHistoryEntry("Canlı RAT Dedektörü", threat.Name, $"{threat.Location} | {threat.TargetPath}", threat.Reason, threat.Severity);
                        ShowToast("Aktif RAT / Şüpheli Ağ Alarmı", $"{threat.Name}: {threat.Location}", threat.Severity);
                        OnTrayNotificationRequested?.Invoke("KOCHAN Aktif RAT Uyarısı!", $"{threat.Name}: {threat.Reason}", true);
                        DispatchWebhookIfConfigured("Canlı RAT / Şüpheli Ağ Alarmı", $"{threat.Name} -> {threat.Reason} ({threat.Location})", threat.Severity);
                    }
                }
            }

            Metrics = m;
            OnMetricsUpdated?.Invoke(m);
            CpuHistory.Add(m.CpuUsagePercent);
            if (CpuHistory.Count > 32) CpuHistory.RemoveAt(0);
            RamHistory.Add(m.RamUsagePercent);
            if (RamHistory.Count > 32) RamHistory.RemoveAt(0);
            NetHistory.Add(Math.Round(m.NetworkDownloadMbps + m.NetworkUploadMbps, 2));
            if (NetHistory.Count > 32) NetHistory.RemoveAt(0);
            if (procs != null)
            {
                if (needFullProcesses)
                {
                    _allProcesses = procs;
                    ApplyProcessFilter();
                }
                if (needTopConsumers)
                {
                    TopConsumers.Clear();
                    foreach (var p in procs.Take(6))
                        TopConsumers.Add(p);
                }
            }

            _gameMode.IsAutoModeEnabled = Settings.AutoGameMode;
            _gameMode.Tick(Settings, _telemetry.LastProcessNamesSnapshot);
            UpdateGameModeUiState();

            if (Settings.AutoRamOptimizeOnHighUsage &&
                m.RamUsagePercent >= Settings.RamThresholdPercent &&
                (DateTime.UtcNow - _lastAutoRamOpt).TotalMinutes >= 5)
            {
                _lastAutoRamOpt = DateTime.UtcNow;
                var res = await _ramOptimizer.OptimizeAsync(true);
                AddSecurityEvent("Oto-RAM", res.Message, FindingSeverity.Info);
            }

            if (Settings.EnableAutoIslc && (DateTime.UtcNow - _lastIslcCheck).TotalSeconds >= 60)
            {
                _lastIslcCheck = DateTime.UtcNow;
                var (triggered, freedMb) = await Task.Run(() => RamOptimizerService.CheckAndRunAutoIslc(Settings));
                if (triggered)
                {
                    AddSecurityEvent("ISLC Motoru", $"Boş RAM azaldığı için {freedMb} MB Standby List otomatik temizlendi.", FindingSeverity.Info);
                }
            }

            // Automatic Daily 21:00 OR 15-minute Idle Maintenance Scheduler
            if (Settings.EnableAutoMaintenanceSchedule && !IsBusy && !_gameMode.IsBoostActive)
            {
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                bool isDailyTime = DateTime.Now.Hour == 21 && Settings.LastAutoMaintenanceDate != today;
                bool isIdle15Min = KochanSentinel.Core.Interop.NativeMethods.GetSystemIdleTime().TotalMinutes >= 15
                                   && (DateTime.UtcNow - _lastIdleMaintenance).TotalHours >= 6;

                if (isDailyTime || isIdle15Min)
                {
                    _lastIdleMaintenance = DateTime.UtcNow;
                    if (isDailyTime)
                    {
                        Settings.LastAutoMaintenanceDate = today;
                        _settingsService.Save(Settings);
                    }
                    _ = RunScheduledMaintenanceAsync(isDailyTime ? "Günlük 21:00 Bakımı" : "15 Dk Boşta Kalma Bakımı");
                }
            }
        }
        catch { }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void ApplyProcessFilter()
    {
        var q = ProcessSearchQuery?.Trim() ?? "";
        IEnumerable<ProcessItem> query = _allProcesses;

        query = ProcessCategoryFilter switch
        {
            "İmzasızlar" => query.Where(p => !p.IsSigned),
            "Engelli" => query.Where(p => p.IsFirewallBlocked),
            "Tarayıcı" => query.Where(p => p.Category == "Tarayıcı"),
            "Oyun" => query.Where(p => p.Category == "Oyun / Launcher"),
            _ => query
        };

        if (ShowOnlyActiveConsumers)
        {
            query = ProcessSortMode switch
            {
                "Network" => query.Where(p => p.ActiveConnections > 0),
                "Disk" => query.Where(p => p.DiskKbPerSec > 0.1),
                "CPU" => query.Where(p => p.CpuPercent > 0.1),
                _ => query
            };
        }

        if (!string.IsNullOrEmpty(q))
        {
            query = query.Where(p =>
                p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.Category.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        // Preserve user's selected process by PID across live 1.8s ticks!
        int? selectedPid = SelectedProcess?.Pid;
        var filtered = query.ToList();

        Processes.Clear();
        ProcessItem? restoredSelection = null;
        foreach (var p in filtered)
        {
            Processes.Add(p);
            if (selectedPid.HasValue && p.Pid == selectedPid.Value)
                restoredSelection = p;
        }
        if (restoredSelection != null)
            SelectedProcess = restoredSelection;
    }

    [RelayCommand]
    private async Task QuickBoostAsync()
    {
        await QuickBoostWithSummaryAsync();
    }

    public async Task<string> QuickBoostWithSummaryAsync()
    {
        if (IsBusy) return "Meşgul";
        IsBusy = true;
        StatusBanner = "Hızlı optimizasyon çalıştırılıyor (RAM + EcoQoS + Temp + Standby List)...";
        try
        {
            var ramRes = await _ramOptimizer.OptimizeAsync(true);
            var junk = await _cleaner.ScanJunkAsync();
            var tempCats = junk.Where(j => j.Id == "user_temp").ToList();
            foreach (var c in tempCats) c.IsSelected = true;
            var cleanRes = await _cleaner.CleanAsync(tempCats);

            var msg = $"Hızlı Optimizasyon Tamamlandı! {ramRes.FreedMb:F0} MB RAM açıldı ({ramRes.EcoModeAppliedCount} Eco), {JunkCategory.FormatBytes(cleanRes.FreedBytes)} çöp silindi.";
            StatusBanner = msg;
            ShowToast("Hızlı Boost Tamamlandı", msg, FindingSeverity.Info);
            AddSecurityEvent("Hızlı Boost", msg, FindingSeverity.Info);
            _ = RefreshTelemetryTickAsync();
            return $"✓ {ramRes.FreedMb:F0} MB RAM + {JunkCategory.FormatBytes(cleanRes.FreedBytes)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OptimizeRamAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        HasActionResult = false;
        CleanerLiveAction = "Arka plan işlemleri optimize ediliyor ve Standby List temizleniyor...";
        AddCleanerLog("RAM & Standby List optimizasyonu başlatıldı...");
        try
        {
            var res = await _ramOptimizer.OptimizeAsync(true);
            StatusBanner = res.Message;
            HasActionResult = true;
            LastActionResultTitle = $"RAM Optimize Edildi: {res.FreedMb:F0} MB Rahatlatıldı";
            LastActionResultDetail = $"Önce: {res.BeforeUsedGb:F2} GB  →  Sonra: {res.AfterUsedGb:F2} GB  |  {res.TrimmedProcesses} işlem düzenlendi  |  Standby List: {(res.StandbyListPurged ? "Temizlendi" : "Yönetici izni gerekir")}";
            AddCleanerLog(LastActionResultTitle + " — " + LastActionResultDetail);
            AddSecurityEvent("RAM Motoru", res.Message, FindingSeverity.Info);
            ShowToast("RAM Optimizasyonu", $"{res.FreedMb:F0} MB RAM açıldı ({res.TrimmedProcesses} işlem)", FindingSeverity.Info);
        }
        finally
        {
            CleanerLiveAction = "";
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ScanJunkAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        HasActionResult = false;
        CleanerSummary = "Gereksiz dosyalar ve önbellekler taranıyor...";
        CleanerLiveAction = "Klasörler analiz ediliyor...";
        AddCleanerLog("Çöp dosya ve önbellek taraması başlatıldı...");
        try
        {
            var progress = new Progress<string>(msg =>
            {
                CleanerLiveAction = msg;
            });
            var cats = await _cleaner.ScanJunkAsync(progress);
            JunkCategories.Clear();
            foreach (var c in cats) JunkCategories.Add(c);

            long total = cats.Sum(c => c.SizeBytes);
            int files = cats.Sum(c => c.FileCount);
            CleanerSummary = $"Tarama bitti: Toplam {JunkCategory.FormatBytes(total)} ({files:N0} dosya) tespit edildi.";
            HasActionResult = true;
            LastActionResultTitle = $"Tarama Tamamlandı: {JunkCategory.FormatBytes(total)} Temizlenebilir Alan";
            LastActionResultDetail = $"{files:N0} geçici/önbellek dosyası bulundu. İstemediğiniz kategorilerin tikini kaldırıp temizleyebilirsiniz.";
            AddCleanerLog(CleanerSummary);
            StatusBanner = CleanerSummary;
        }
        finally
        {
            CleanerLiveAction = "";
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CleanJunkAsync()
    {
        if (IsBusy || JunkCategories.Count == 0) return;
        IsBusy = true;
        HasActionResult = false;
        var selectedNames = string.Join(", ", JunkCategories.Where(c => c.IsSelected).Select(c => c.Name));
        CleanerSummary = "Seçili kalıntılar temizleniyor...";
        CleanerLiveAction = "Dosyalar güvenli şekilde siliniyor...";
        AddCleanerLog($"Temizlik başlatıldı: {selectedNames}");
        try
        {
            await EnsureAutoRestorePointAsync("Çöp Temizliği Öncesi");
            var progress = new Progress<string>(msg =>
            {
                CleanerLiveAction = msg;
                AddCleanerLog(msg);
            });
            var res = await _cleaner.CleanAsync(JunkCategories, progress);
            var msg = $"{JunkCategory.FormatBytes(res.FreedBytes)} alan açıldı ({res.DeletedFiles:N0} dosya silindi, {res.SkippedLockedFiles} kullanımda atlandı).";
            CleanerSummary = "Temizlik tamamlandı! " + msg;
            StatusBanner = CleanerSummary;
            HasActionResult = true;
            LastActionResultTitle = $"Temizlik Tamamlandı: {JunkCategory.FormatBytes(res.FreedBytes)} Alan Açıldı!";
            LastActionResultDetail = $"{res.DeletedFiles:N0} dosya silindi, {res.SkippedLockedFiles} dosya şu an başka program tarafından kullanıldığı için güvenle atlandı.";
            AddCleanerLog(LastActionResultTitle + " — " + LastActionResultDetail);
            AddSecurityEvent("Temizleyici", CleanerSummary, FindingSeverity.Info);
            ShowToast("Disk Temizliği Tamamlandı", $"{JunkCategory.FormatBytes(res.FreedBytes)} boşaltıldı ({res.DeletedFiles:N0} dosya)", FindingSeverity.Info);

            // Re-scan silently to update remaining sizes
            var cats = await _cleaner.ScanJunkAsync();
            JunkCategories.Clear();
            foreach (var c in cats) JunkCategories.Add(c);
        }
        finally
        {
            CleanerLiveAction = "";
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ToggleSelectAllJunk()
    {
        _selectAllJunkState = !_selectAllJunkState;
        foreach (var cat in JunkCategories)
            cat.IsSelected = _selectAllJunkState;

        // Refresh list bindings
        var snapshot = JunkCategories.ToList();
        JunkCategories.Clear();
        foreach (var c in snapshot) JunkCategories.Add(c);
    }

    [RelayCommand]
    private void OpenJunkFolder(JunkCategory? cat)
    {
        if (cat == null) return;
        var existing = cat.TargetPaths.FirstOrDefault(Directory.Exists);
        if (!string.IsNullOrEmpty(existing))
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", existing) { UseShellExecute = true }); }
            catch { }
        }
    }

    private void AddCleanerLog(string text)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {text}";
        CleanerActivityLog.Insert(0, line);
        while (CleanerActivityLog.Count > 40)
            CleanerActivityLog.RemoveAt(CleanerActivityLog.Count - 1);
    }

    [RelayCommand]
    private void ToggleManualGameMode()
    {
        if (_gameMode.IsBoostActive)
            _gameMode.DeactivateBoost();
        else
            _gameMode.ActivateManualBoost(Settings);

        UpdateGameModeUiState();
    }

    private void UpdateGameModeUiState()
    {
        IsGameBoostActive = _gameMode.IsBoostActive;
        if (_gameMode.IsBoostActive)
        {
            ActiveGameLabel = $"OYUN MODU AKTİF: {_gameMode.ActiveGameName} (Yüksek Performans + Arka Plan Susturuldu)";
            GameModeButtonText = "■  Oyun Modunu Durdur (AKTİF)";
            GameModeBadgeText = "● BOOST AKTİF";
        }
        else
        {
            ActiveGameLabel = Settings.AutoGameMode
                ? "Otomatik İzleme Aktif — Oyun açıldığında devreye girer"
                : "Otomatik Oyun Modu Kapalı";
            GameModeButtonText = "▶  Oyun Modunu Başlat";
            GameModeBadgeText = "○ İZLEMEDE";
        }
    }

    [RelayCommand]
    private void AddCustomGame()
    {
        var name = NewGameExeName.Trim();
        if (string.IsNullOrEmpty(name)) return;
        if (!CustomGames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            CustomGames.Add(name);
            Settings.CustomGameExecutables.Add(name);
            _settingsService.Save(Settings);
        }
        NewGameExeName = "";
    }

    [RelayCommand]
    private void RemoveCustomGame(string? game)
    {
        if (string.IsNullOrEmpty(game)) return;
        CustomGames.Remove(game);
        Settings.CustomGameExecutables.Remove(game);
        _settingsService.Save(Settings);
    }

    [RelayCommand]
    private async Task ScanInstalledGamesAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        GameLibrarySummary = "Steam, Epic Games ve Riot oyunları taranıyor...";
        try
        {
            var games = await _gameLibrary.DiscoverInstalledGamesAsync();
            InstalledGames.Clear();
            foreach (var g in games) InstalledGames.Add(g);
            GameLibrarySummary = games.Count == 0
                ? "Kurulu Steam, Epic veya Riot oyunu bulunamadı."
                : $"{games.Count} kurulu oyun tespit edildi. Tek tıkla doğrulayıp optimize ederek başlatabilirsin.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task VerifyGameAsync(InstalledGame? game)
    {
        if (game == null || IsBusy) return;
        IsBusy = true;
        StatusBanner = $"{game.Name} klasörü ve DLL bütünlüğü doğrulanıyor...";
        try
        {
            var (ok, summary) = await _gameLibrary.VerifyGameFolderAsync(game);
            game.IsVerifiedOk = ok;
            game.VerifyStatus = summary;

            // Refresh UI item
            int idx = InstalledGames.IndexOf(game);
            if (idx >= 0)
            {
                InstalledGames.RemoveAt(idx);
                InstalledGames.Insert(idx, game);
            }

            StatusBanner = $"{game.Name}: {summary}";
            AddSecurityEvent("Oyun Doğrulama", $"{game.Name} -> {summary}", ok ? FindingSeverity.Info : FindingSeverity.High);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OptimizeAndLaunchGameAsync(InstalledGame? game)
    {
        if (game == null) return;
        await _ramOptimizer.OptimizeAsync(true);
        if (!_gameMode.IsBoostActive)
            _gameMode.ActivateManualBoost(Settings);
        UpdateGameModeUiState();

        try
        {
            if (!string.IsNullOrWhiteSpace(game.LaunchUriOrExe))
            {
                Process.Start(new ProcessStartInfo(game.LaunchUriOrExe) { UseShellExecute = true });
                StatusBanner = $"{game.Name} optimize edilip başlatılıyor!";
            }
        }
        catch
        {
            StatusBanner = $"{game.Name} başlatılamadı; klasörünü kontrol edin.";
        }
    }

    [RelayCommand]
    private void OpenGameFolder(InstalledGame? game)
    {
        if (game != null && Directory.Exists(game.InstallPath))
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", game.InstallPath) { UseShellExecute = true }); }
            catch { }
        }
    }

    [RelayCommand]
    private async Task ScanFileAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        IsBusy = true;
        StatusBanner = $"Dosya derinlemesine taranıyor: {Path.GetFileName(path)}...";
        try
        {
            var res = await _security.ScanFileAsync(path, Settings.VirusTotalApiKey, Settings.MalwareBazaarAuthKey);
            LastFileScan = res;
            StatusBanner = $"{res.FileName}: {res.Verdict}";
            AddSecurityEvent("Dosya Tarama", $"{res.FileName} -> {res.Verdict}", res.RiskLevel);
            AddHistoryEntry(
                "Manuel Tarama",
                res.FileName,
                $"Entropi: {res.ShannonEntropy:F2} | İmza: {res.SignerName} | {res.DefenderResult}",
                res.Verdict,
                res.RiskLevel);
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectAllFindingsChanged(bool value)
    {
        if (_suppressSelectAllSync) return;
        foreach (var f in PersistenceFindings)
            f.IsSelected = value;
        UpdateFindingsSelectionText();
    }

    private void UpdateFindingsSelectionText()
    {
        int count = PersistenceFindings.Count(f => f.IsSelected);
        RemoveSelectedButtonText = count > 0
            ? $"Seçilenleri Kaldır ({count})"
            : "Seçilenleri Kaldır / Devre Dışı Bırak";
    }

    [RelayCommand]
    private async Task RunPersistenceAuditAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        _hasRunInitialAudit = true;
        _suppressSelectAllSync = true;
        SelectAllFindings = false;
        _suppressSelectAllSync = false;
        PersistenceSummary = "Registry Run, Zamanlanmış Görevler, 3. Parti Servisler, WMI, Canlı RAT/C2 Ağları ve kalıntılar taranıyor...";
        try
        {
            var snapshotProcs = await Task.Run(() => _telemetry.GetTopProcesses(95, "Network", _firewall.IsBlocked));
            var findings = await _security.RunPersistenceAuditAsync(Settings.TrustedWhitelist, snapshotProcs);
            PersistenceFindings.Clear();
            foreach (var f in findings)
            {
                f.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SecurityFinding.IsSelected))
                        UpdateFindingsSelectionText();
                };
                PersistenceFindings.Add(f);
            }
            UpdateFindingsSelectionText();
            UpdateSecurityHeroSummary();

            AddSecurityEvent("Kalıntı Denetimi", PersistenceSummary, PersistenceFindings.Any(f => f.Severity >= FindingSeverity.High) ? FindingSeverity.High : FindingSeverity.Info);
            int highCount = PersistenceFindings.Count(f => f.Severity >= FindingSeverity.High);
            AddHistoryEntry(
                "Derin Sistem Denetimi",
                "Başlangıç, Görevler, Servisler & Kalıntılar",
                $"{PersistenceFindings.Count} bulgu ({highCount} yüksek riskli)",
                highCount > 0 ? "İnceleme Önerilir" : "Temiz",
                highCount > 0 ? FindingSeverity.High : FindingSeverity.Info);
            _ = RefreshTelemetryTickAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateSecurityHeroSummary()
    {
        int highCount = PersistenceFindings.Count(f => f.Severity >= FindingSeverity.High);
        LastScanLabel = $"Son Tarama: {DateTime.Now:HH:mm}";
        if (PersistenceFindings.Count == 0)
        {
            SecurityHeroTitle = "Sistem Temiz & Güncel — Tehdit Bulunamadı";
            PersistenceSummary = "Harika! Şüpheli bir kalıntı veya imzasız başlangıç kaydı bulunamadı.";
        }
        else
        {
            SecurityHeroTitle = highCount > 0
                ? $"Dikkat: {highCount} Yüksek Riskli Bulgu ({PersistenceFindings.Count} Toplam)"
                : $"{PersistenceFindings.Count} İncelenecek Başlangıç / Kalıntı Kaydı";
            PersistenceSummary = $"{PersistenceFindings.Count} bulgu listelendi ({highCount} yüksek riskli). İnceleyebilir, güvenilir listeye ekleyebilir veya kaldırabilirsin.";
        }
        StatusBanner = PersistenceSummary;
    }

    private async Task RunScheduledMaintenanceAsync(string reason)
    {
        try
        {
            var ramRes = await _ramOptimizer.OptimizeAsync(true);
            var junk = await _cleaner.ScanJunkAsync();
            var tempCats = junk.Where(j => j.Id == "user_temp").ToList();
            foreach (var c in tempCats) c.IsSelected = true;
            var cleanRes = await _cleaner.CleanAsync(tempCats);

            string detail = $"{ramRes.FreedMb:F0} MB RAM + {JunkCategory.FormatBytes(cleanRes.FreedBytes)} çöp temizlendi.";
            AddSecurityEvent(reason, detail, FindingSeverity.Info);
            AddHistoryEntry("Otomatik Bakım", reason, detail, "Tamamlandı", FindingSeverity.Info);
            await RunPersistenceAuditAsync();
        }
        catch { }
    }

    [RelayCommand]
    private async Task RemoveFindingAsync(SecurityFinding? finding)
    {
        var targets = PersistenceFindings.Where(f => f.IsSelected).ToList();
        if (targets.Count == 0)
        {
            var single = finding ?? SelectedFinding;
            if (single != null) targets.Add(single);
        }
        if (targets.Count == 0)
        {
            StatusBanner = "Kaldırmak için en az bir bulguyu işaretleyin veya seçin.";
            return;
        }

        bool allowSystemChanges = false;
        var sensitiveTargets = targets.Where(t => t.RequiresConfirmation).ToList();
        if (sensitiveTargets.Count > 0)
        {
            var names = string.Join("\n• ", sensitiveTargets.Take(5).Select(t => $"{t.Category}: {t.Name}"));
            var confirm = System.Windows.MessageBox.Show(
                $"Seçilen öğeler sistem düzeyinde kalıcılık içeriyor:\n\n• {names}\n\n" +
                "Servisler devre dışı bırakılacak; Defender istisnaları ve WMI/AppInit kayıtları silinecek.\nOnaylıyor musunuz?",
                "KOCHAN Sentinel — Güvenli Sistem Müdahalesi",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            allowSystemChanges = confirm == System.Windows.MessageBoxResult.Yes;
            if (allowSystemChanges)
                await EnsureAutoRestorePointAsync("Yüksek Riskli Kalıcılık Temizliği");
        }

        int removed = 0;
        int failed = 0;
        foreach (var target in targets)
        {
            if (_security.RemoveOrDisableFinding(target, allowSystemChanges))
            {
                PersistenceFindings.Remove(target);
                removed++;
                AddHistoryEntry("Öğe Kaldırıldı", target.Name, $"{target.Category} — {target.TargetPath}", "Başarıyla Kaldırıldı", FindingSeverity.Info);
            }
            else
            {
                failed++;
            }
        }

        _suppressSelectAllSync = true;
        SelectAllFindings = false;
        _suppressSelectAllSync = false;
        UpdateFindingsSelectionText();
        UpdateSecurityHeroSummary();
        _ = RefreshTelemetryTickAsync();

        StatusBanner = failed == 0
            ? $"{removed} öğe başarıyla kaldırıldı / devre dışı bırakıldı."
            : $"{removed} öğe kaldırıldı, {failed} öğe kilitli/korumalı olduğu için atlandı.";
        ShowToast("Güvenlik Müdahalesi", StatusBanner, failed > 0 ? FindingSeverity.Medium : FindingSeverity.Info);
        AddSecurityEvent("Temizlik", StatusBanner, failed > 0 ? FindingSeverity.Medium : FindingSeverity.Info);
    }

    [RelayCommand]
    private async Task NeutralizeFindingAsync(SecurityFinding? finding)
    {
        var target = finding ?? SelectedFinding;
        if (target == null)
        {
            StatusBanner = "Etkisiz hale getirmek için bir bulgu seçin.";
            return;
        }

        if (target.RequiresConfirmation)
        {
            var confirm = System.Windows.MessageBox.Show(
                $"\"{target.Name}\" ({target.Category}) üzerine tam müdahale uygulanacak:\n• Çalışıyorsa sonlandırılacak\n• Firewall engeli konacak\n• Dosya DPAPI kasasına karantinaya alınacak\n\nOnaylıyor musunuz?",
                "KOCHAN Sentinel — Tam Tehdit Müdahalesi",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);
            if (confirm != System.Windows.MessageBoxResult.Yes) return;
        }

        if (target.Severity >= FindingSeverity.High)
            await EnsureAutoRestorePointAsync($"Tam Müdahale ({target.Name})");

        var actionsDone = new List<string>();

        // 1. Kill process by PID or matching executable path
        if (target.ProcessId > 4)
        {
            if (_telemetry.KillProcess(target.ProcessId))
                actionsDone.Add($"PID {target.ProcessId} kapatıldı");
        }
        if (!string.IsNullOrWhiteSpace(target.TargetPath) && File.Exists(target.TargetPath))
        {
            if (TryTerminateProcessesByPath(target.TargetPath))
                actionsDone.Add("Süreç ağacı durduruldu");

            // 2. Firewall outbound block (skip Windows system binaries)
            if (!target.TargetPath.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase))
            {
                var procName = Path.GetFileNameWithoutExtension(target.TargetPath);
                var (fwOk, _) = await _firewall.BlockOutboundAsync(target.TargetPath, procName);
                if (fwOk) actionsDone.Add("İnternet engellendi");

                // 3. DPAPI AES-256-GCM Quarantine
                var (qOk, qEntry, _) = _quarantine.QuarantineFile(target.TargetPath, "", $"Etkisiz Hale Getirildi: {target.Reason}");
                if (qOk && qEntry != null)
                {
                    QuarantineItems.Insert(0, qEntry);
                    actionsDone.Add("Karantinaya alındı");
                }
            }
        }

        // 4. Clean registry / scheduled task / service persistence
        if (_security.RemoveOrDisableFinding(target, allowConfirmedSystemChanges: true))
            actionsDone.Add("Kalıcılık temizlendi");

        PersistenceFindings.Remove(target);
        UpdateFindingsSelectionText();
        UpdateSecurityHeroSummary();
        _ = RefreshTelemetryTickAsync();

        string summary = actionsDone.Count > 0 ? string.Join(" + ", actionsDone) : "Müdahale uygulandı";
        StatusBanner = $"{target.Name}: {summary}";
        AddHistoryEntry("Tam Tehdit Müdahalesi", target.Name, $"{target.TargetPath}", summary, FindingSeverity.Critical);
        ShowToast("Tehdit Etkisiz Hale Getirildi", $"{target.Name}: {summary}", FindingSeverity.Info);
    }

    private static bool TryTerminateProcessesByPath(string exePath)
    {
        bool killedAny = false;
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (proc.Id <= 4 || proc.Id == Environment.ProcessId) continue;
                var path = proc.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path) && path.Equals(exePath, StringComparison.OrdinalIgnoreCase))
                {
                    proc.Kill(entireProcessTree: true);
                    killedAny = true;
                }
            }
            catch { }
            finally { proc.Dispose(); }
        }
        return killedAny;
    }

    [RelayCommand]
    private void WhitelistFinding(SecurityFinding? finding)
    {
        var targets = PersistenceFindings.Where(f => f.IsSelected).ToList();
        if (targets.Count == 0)
        {
            var single = finding ?? SelectedFinding;
            if (single != null) targets.Add(single);
        }
        if (targets.Count == 0)
        {
            StatusBanner = "Güvenilir listeye eklemek için bir öğe seçin veya işaretleyin.";
            return;
        }

        foreach (var t in targets)
        {
            var token = !string.IsNullOrWhiteSpace(t.TargetPath) ? t.TargetPath : t.Name;
            if (!Settings.TrustedWhitelist.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                Settings.TrustedWhitelist.Add(token);
                TrustedWhitelist.Add(token);
            }
            PersistenceFindings.Remove(t);
            AddHistoryEntry("Güvenilir Liste", t.Name, token, "Yoksayıldı (Güvenilir)", FindingSeverity.Info);
        }

        _settingsService.Save(Settings);
        _suppressSelectAllSync = true;
        SelectAllFindings = false;
        _suppressSelectAllSync = false;
        UpdateFindingsSelectionText();
        UpdateSecurityHeroSummary();
        _ = RefreshTelemetryTickAsync();
        StatusBanner = $"{targets.Count} öğe Güvenilir Listeye (Whitelist) eklendi.";
        ShowToast("Güvenilir Liste", StatusBanner, FindingSeverity.Info);
    }

    [RelayCommand]
    private void RemoveWhitelistEntry(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return;
        TrustedWhitelist.Remove(entry);
        Settings.TrustedWhitelist.Remove(entry);
        _settingsService.Save(Settings);
        StatusBanner = $"Güvenilir listeden çıkarıldı: {entry}";
    }

    [RelayCommand]
    private void OpenProcessLocation(ProcessItem? proc)
    {
        var target = proc ?? SelectedProcess;
        if (target == null || string.IsNullOrWhiteSpace(target.ExecutablePath) || !File.Exists(target.ExecutablePath))
        {
            StatusBanner = "Seçilen işlemin dosya yolu alınamadı (sistem işlemi olabilir).";
            return;
        }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target.ExecutablePath}\"") { UseShellExecute = true }); }
        catch { }
    }

    [RelayCommand]
    private void OpenFindingLocation(SecurityFinding? finding)
    {
        var target = finding ?? SelectedFinding;
        if (target == null || string.IsNullOrWhiteSpace(target.TargetPath)) return;
        try
        {
            if (File.Exists(target.TargetPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target.TargetPath}\"") { UseShellExecute = true });
            }
            else if (Directory.Exists(target.TargetPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target.TargetPath}\"") { UseShellExecute = true });
            }
            else
            {
                var dir = Path.GetDirectoryName(target.TargetPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
                else
                    StatusBanner = $"Konum bulunamadı: {target.TargetPath}";
            }
        }
        catch { }
    }

    [RelayCommand]
    private async Task ScanSelectedFindingFileAsync(SecurityFinding? finding)
    {
        var target = finding ?? SelectedFinding;
        if (target == null || string.IsNullOrWhiteSpace(target.TargetPath) || !File.Exists(target.TargetPath))
        {
            StatusBanner = "Dosya diskte bulunamadı (komut satırı veya silinmiş öğe olabilir).";
            return;
        }
        await ScanFileAsync(target.TargetPath);
    }

    [RelayCommand]
    private void ClearSecurityHistory()
    {
        SecurityHistory.Clear();
        _security.SaveHistory(SecurityHistory);
        StatusBanner = "Güvenlik geçmişi kalıcı olarak temizlendi.";
    }

    [RelayCommand]
    private void QuarantineSelectedFinding()
    {
        var target = SelectedFinding;
        if (target == null || string.IsNullOrWhiteSpace(target.TargetPath) || !File.Exists(target.TargetPath))
        {
            StatusBanner = "Karantinaya almak için diskte var olan bir dosya seçin.";
            return;
        }

        var (ok, entry, msg) = _quarantine.QuarantineFile(target.TargetPath, "", target.Reason);
        if (ok && entry != null)
        {
            QuarantineItems.Insert(0, entry);
            PersistenceFindings.Remove(target);
            AddHistoryEntry("Karantina", entry.OriginalName, entry.OriginalPath, "Karantinaya Alındı", FindingSeverity.High);
        }
        StatusBanner = msg;
    }

    [RelayCommand]
    private void QuarantineLastScannedFile()
    {
        if (LastFileScan == null || !File.Exists(LastFileScan.FilePath))
        {
            StatusBanner = "Taranmış dosya diskte bulunamadı.";
            return;
        }
        var (ok, entry, msg) = _quarantine.QuarantineFile(LastFileScan.FilePath, LastFileScan.Sha256, LastFileScan.Verdict);
        if (ok && entry != null)
        {
            QuarantineItems.Insert(0, entry);
            AddHistoryEntry("Karantina", entry.OriginalName, entry.OriginalPath, "Karantinaya Alındı", FindingSeverity.High);
            LastFileScan = null;
        }
        StatusBanner = msg;
    }

    [RelayCommand]
    private void RestoreQuarantineItem(QuarantineEntry? item)
    {
        var target = item ?? SelectedQuarantineItem;
        if (target == null) return;
        var (ok, msg) = _quarantine.RestoreFile(target);
        if (ok)
        {
            QuarantineItems.Remove(target);
            AddHistoryEntry("Karantina Geri Yükleme", target.OriginalName, target.OriginalPath, "Geri Yüklendi", FindingSeverity.Info);
        }
        StatusBanner = msg;
    }

    [RelayCommand]
    private void DeleteQuarantineItem(QuarantineEntry? item)
    {
        var target = item ?? SelectedQuarantineItem;
        if (target == null) return;
        if (_quarantine.DeletePermanently(target))
        {
            QuarantineItems.Remove(target);
            StatusBanner = $"{target.OriginalName} karantinadan kalıcı olarak silindi.";
        }
    }

    [RelayCommand]
    private async Task BlockProcessFirewallAsync(ProcessItem? proc)
    {
        var target = proc ?? SelectedProcess;
        if (target == null || target.IsSystemCritical) return;
        var (ok, msg) = await _firewall.BlockOutboundAsync(target.ExecutablePath, target.Name);
        StatusBanner = msg;
        if (ok)
        {
            target.IsFirewallBlocked = true;
            AddHistoryEntry("Firewall Engel", target.Name, target.ExecutablePath, "İnternet Kesildi", FindingSeverity.Medium);
            ShowToast("Firewall Kuralı", msg, FindingSeverity.Medium);
            _ = RefreshTelemetryTickAsync();
        }
    }

    [RelayCommand]
    private async Task UnblockProcessFirewallAsync(ProcessItem? proc)
    {
        var target = proc ?? SelectedProcess;
        if (target == null) return;
        var (ok, msg) = await _firewall.UnblockOutboundAsync(target.Name);
        StatusBanner = msg;
        if (ok)
        {
            target.IsFirewallBlocked = false;
            ShowToast("Firewall Kuralı", msg, FindingSeverity.Info);
            _ = RefreshTelemetryTickAsync();
        }
    }

    [RelayCommand]
    private void OpenAppDataFolder()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KochanSentinel");
        Directory.CreateDirectory(dir);
        try { Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true }); }
        catch { }
    }

    private void AddHistoryEntry(string actionType, string targetName, string details, string verdict, FindingSeverity sev)
    {
        SecurityHistory.Insert(0, new SecurityHistoryEntry
        {
            ActionType = actionType,
            TargetName = targetName,
            Details = details,
            Verdict = verdict,
            Severity = sev
        });
        while (SecurityHistory.Count > 200)
            SecurityHistory.RemoveAt(SecurityHistory.Count - 1);
        _security.SaveHistory(SecurityHistory);
    }

    [RelayCommand]
    private void EcoModeProcess(ProcessItem? proc)
    {
        var target = proc ?? SelectedProcess;
        if (target == null || target.IsSystemCritical) return;
        bool ok = _telemetry.SetEfficiencyMode(target.Pid, true);
        StatusBanner = ok
            ? $"{target.Name} Verimlilik Moduna (EcoQoS) alındı."
            : $"{target.Name} için Verimlilik Modu uygulanamadı (yetki gerekebilir).";
    }

    [RelayCommand]
    private void SuspendProcess(ProcessItem? proc)
    {
        var target = proc ?? SelectedProcess;
        if (target == null || target.IsSystemCritical) return;
        bool ok = _telemetry.SuspendProcess(target.Pid);
        StatusBanner = ok ? $"{target.Name} duraklatıldı (Askıya alındı)." : $"{target.Name} duraklatılamadı.";
    }

    [RelayCommand]
    private void ResumeProcess(ProcessItem? proc)
    {
        var target = proc ?? SelectedProcess;
        if (target == null) return;
        bool ok = _telemetry.ResumeProcess(target.Pid);
        StatusBanner = ok ? $"{target.Name} devam ettirildi." : $"{target.Name} devam ettirilemedi.";
    }

    [RelayCommand]
    private void SetProcessCategoryFilter(string category)
    {
        ProcessCategoryFilter = category;
        ApplyProcessFilter();
    }

    [RelayCommand]
    private void DismissToast(ToastNotification? toast)
    {
        if (toast != null) ActiveToasts.Remove(toast);
    }

    private void ShowToast(string title, string message, FindingSeverity severity = FindingSeverity.Info)
    {
        var dispatch = System.Windows.Application.Current?.Dispatcher;
        if (dispatch == null) return;
        dispatch.InvokeAsync(async () =>
        {
            var toast = new ToastNotification
            {
                Title = title,
                Message = message,
                Severity = severity,
                AccentColor = severity switch
                {
                    FindingSeverity.Critical or FindingSeverity.High => "#EF4444",
                    FindingSeverity.Medium => "#F59E0B",
                    _ => "#22C55E"
                }
            };
            ActiveToasts.Insert(0, toast);
            while (ActiveToasts.Count > 3)
                ActiveToasts.RemoveAt(ActiveToasts.Count - 1);

            await Task.Delay(3800);
            ActiveToasts.Remove(toast);
        });
    }

    [RelayCommand]
    private void SetProcessSortMode(string mode)
    {
        ProcessSortMode = mode;
        ShowOnlyActiveConsumers = mode is "Network" or "Disk" or "CPU";
        ProcessSortTitle = mode switch
        {
            "CPU" => "Sıralama: En Çok CPU Kullananlar",
            "Disk" => "Sıralama: En Çok Disk Kullananlar (Okuma/Yazma)",
            "Network" => "Sıralama: Aktif Ağ Bağlantısı Olanlar (Sadece İnternet Kullananlar)",
            _ => "Sıralama: En Çok RAM Kullananlar"
        };
        _ = RefreshTelemetryTickAsync();
    }

    [RelayCommand]
    private void KillProcess(ProcessItem? proc)
    {
        var target = proc ?? SelectedProcess;
        if (target == null || target.IsSystemCritical) return;
        if (_telemetry.KillProcess(target.Pid))
        {
            Processes.Remove(target);
            StatusBanner = $"{target.Name} sonlandırıldı.";
        }
        else
        {
            StatusBanner = $"{target.Name} sonlandırılamadı.";
        }
    }

    [RelayCommand]
    private void SaveSettings()
    {
        bool startupSynced = _settingsService.Save(Settings);
        SyncRealtimeProtections();
        OnSettingsSynced?.Invoke();

        StatusBanner = startupSynced
            ? $"Ayarlar kaydedildi ({(Settings.StartWithWindows ? "Otomatik başlangıç aktif" : "Otomatik başlangıç kapalı")})."
            : "Ayarlar kaydedildi ancak Windows Görev Zamanlayıcı kaydı güncellenemedi.";
    }

    private void SyncRealtimeProtections()
    {
        _telemetry.SetSafeDriverlessSensors(Settings.UseSafeDriverlessSensors);
        _clipboardGuard.IsEnabled = Settings.EnableClipboardClipperGuard;
        _canary.AutoKillMode = Settings.RansomwareAutoKillMode;
        _security.UpdateApiKeys(Settings.VirusTotalApiKey, Settings.MalwareBazaarAuthKey);

        if (Settings.RealTimeDownloadsShield && !_security.IsRealTimeEnabled)
            _security.StartRealTimeShield(Settings.VirusTotalApiKey, Settings.MalwareBazaarAuthKey);
        else if (!Settings.RealTimeDownloadsShield && _security.IsRealTimeEnabled)
            _security.StopRealTimeShield();
        IsRealTimeShieldActive = _security.IsRealTimeEnabled;

        _security.AutoKillMaliciousScripts = Settings.AutoKillMaliciousScripts;
        if (Settings.EnableHiddenProcessDetector)
            _security.StartHiddenProcessDetector();
        else
            _security.StopHiddenProcessDetector();

        if (Settings.EnableRansomwareHoneypot)
            _canary.Start();
        else
            _canary.Stop();

        if (Settings.EnableUsbShield)
            _usbGuard.StartUsbWatcher();
        else
            _usbGuard.StopUsbWatcher();
    }

    private void RefreshBenchmarkDrives()
    {
        try
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .Select(d => d.RootDirectory.FullName)
                .ToList();
            AvailableBenchmarkDrives.Clear();
            foreach (var d in drives) AvailableBenchmarkDrives.Add(d);
            if (!AvailableBenchmarkDrives.Contains(SelectedBenchmarkDrive) && AvailableBenchmarkDrives.Count > 0)
                SelectedBenchmarkDrive = AvailableBenchmarkDrives[0];
            if (string.IsNullOrWhiteSpace(SelectedDiskTarget) && AvailableBenchmarkDrives.Count > 0)
                SelectedDiskTarget = AvailableBenchmarkDrives[0];
        }
        catch { }
    }

    [RelayCommand]
    private async Task RunFullBenchmarkAsync()
    {
        if (IsBenchmarkRunning) return;
        IsBenchmarkRunning = true;
        BenchmarkProgressPercent = 4;
        _benchmarkCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<BenchmarkProgress>(p =>
            {
                BenchmarkStageText = p.StageName;
                BenchmarkDetailText = p.Detail;
                BenchmarkProgressPercent = p.PercentComplete;
            });

            var result = await _benchmark.RunFullBenchmarkAsync(
                SelectedBenchmarkDrive,
                Metrics.CpuName,
                Metrics.GpuName,
                Metrics.RamTotalGb,
                progress,
                _benchmarkCts.Token,
                () => _telemetry.ReadSensorsNow());

            LatestBenchmark = result;
            BenchmarkHistory.Insert(0, result);
            while (BenchmarkHistory.Count > 20)
                BenchmarkHistory.RemoveAt(BenchmarkHistory.Count - 1);

            StatusBanner = $"Sistem Benchmark tamamlandı: Genel %{result.OverallScore} ({result.TierBadge})";
            ShowToast("Sistem Benchmark", StatusBanner, FindingSeverity.Info);
            AddSecurityEvent("Benchmark", $"Test bitti -> Oyun: %{result.GamingPercent} | İş İstasyonu: %{result.WorkstationPercent}", FindingSeverity.Info);
        }
        catch (OperationCanceledException)
        {
            BenchmarkStageText = "Test İptal Edildi";
            BenchmarkDetailText = "Kullanıcı tarafından durduruldu.";
            BenchmarkProgressPercent = 0;
            StatusBanner = "Sistem benchmark testi iptal edildi.";
        }
        catch (Exception ex)
        {
            BenchmarkStageText = "Test Başarısız";
            BenchmarkDetailText = ex.Message;
            BenchmarkProgressPercent = 0;
            StatusBanner = $"Benchmark hatası: {ex.Message}";
        }
        finally
        {
            _benchmarkCts?.Dispose();
            _benchmarkCts = null;
            IsBenchmarkRunning = false;
        }
    }

    [RelayCommand]
    private void CancelBenchmark()
    {
        _benchmarkCts?.Cancel();
    }

    [RelayCommand]
    private void ClearBenchmarkHistory()
    {
        BenchmarkHistory.Clear();
        _benchmark.ClearHistory();
        StatusBanner = "Benchmark geçmişi temizlendi.";
    }

    [RelayCommand]
    private async Task RunDiskAnalysisAsync(string? overridePath)
    {
        if (IsDiskAnalyzing) return;
        string target = !string.IsNullOrWhiteSpace(overridePath) ? overridePath : SelectedDiskTarget;
        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
        {
            StatusBanner = "Seçilen klasör veya sürücü bulunamadı.";
            return;
        }

        SelectedDiskTarget = target;
        IsDiskAnalyzing = true;
        SelectedDiskNode = null;
        DiskTreeRoots.Clear();
        TopLargestFiles.Clear();
        DiskExtensionStats.Clear();
        DiskSummaryStats = "";
        DiskScanStatusText = $"Taranıyor: {target} ...";
        DiskScanLivePath = target;
        _diskScanCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<DiskScanProgress>(p =>
            {
                DiskScanStatusText = $"Taranıyor: {p.ScannedFiles:N0} dosya ({p.ScannedFolders:N0} klasör) • {p.ScannedSizeFormatted}";
                DiskScanLivePath = p.CurrentPath;
            });

            var summary = await _diskAnalyzer.AnalyzeAsync(target, progress, _diskScanCts.Token);
            CurrentDiskRoot = summary.RootNode;
            DiskTreeRoots.Add(summary.RootNode);
            SelectedDiskNode = summary.RootNode;
            foreach (var f in summary.TopLargestFiles) TopLargestFiles.Add(f);
            foreach (var s in summary.ExtensionStats) DiskExtensionStats.Add(s);

            DiskScanStatusText = $"Tamamlandı ({summary.ElapsedSeconds:F1} sn): {summary.RootPath} — {summary.TotalSizeFormatted}";
            DiskScanLivePath = $"{summary.TotalFiles:N0} dosya ve {summary.TotalFolders:N0} klasör incelendi.";
            DiskSummaryStats = $"{summary.TotalSizeFormatted} • {summary.TotalFiles:N0} Dosya";
            StatusBanner = $"Disk analizi tamamlandı: {summary.RootPath} ({summary.TotalSizeFormatted})";
            ShowToast("Disk Analizi Tamamlandı", $"{summary.RootPath} tarandı — {summary.TotalSizeFormatted} ({summary.TotalFiles:N0} dosya)", FindingSeverity.Info);
        }
        catch (OperationCanceledException)
        {
            DiskScanStatusText = "Disk taraması kullanıcı tarafından iptal edildi.";
            DiskScanLivePath = "";
            StatusBanner = "Disk analizi iptal edildi.";
        }
        catch (Exception ex)
        {
            DiskScanStatusText = $"Tarama hatası: {ex.Message}";
            DiskScanLivePath = "";
        }
        finally
        {
            _diskScanCts?.Dispose();
            _diskScanCts = null;
            IsDiskAnalyzing = false;
        }
    }

    [RelayCommand]
    private void CancelDiskAnalysis()
    {
        _diskScanCts?.Cancel();
    }

    [RelayCommand]
    private void OpenDiskNodeLocation(DiskNode? node)
    {
        var target = node ?? SelectedDiskNode;
        if (target == null || string.IsNullOrWhiteSpace(target.FullPath)) return;
        try
        {
            if (File.Exists(target.FullPath))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target.FullPath}\"") { UseShellExecute = true });
            else if (Directory.Exists(target.FullPath))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target.FullPath}\"") { UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private async Task DrillIntoDiskNodeAsync(DiskNode? node)
    {
        var target = node ?? SelectedDiskNode;
        if (target == null || target.IsSummaryPlaceholder) return;
        string dir = target.IsDirectory ? target.FullPath : (Path.GetDirectoryName(target.FullPath) ?? "");
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            await RunDiskAnalysisAsync(dir);
    }

    [RelayCommand]
    private void NavigateParentDiskFolder()
    {
        if (string.IsNullOrWhiteSpace(SelectedDiskTarget)) return;
        try
        {
            var parent = Directory.GetParent(SelectedDiskTarget.TrimEnd('\\'));
            if (parent != null && Directory.Exists(parent.FullName))
                _ = RunDiskAnalysisAsync(parent.FullName);
        }
        catch { }
    }

    [RelayCommand]
    private void RecycleDiskNode(DiskNode? node)
    {
        var target = node ?? SelectedDiskNode;
        if (target == null)
        {
            StatusBanner = "Geri dönüşüme göndermek için ağaçtan veya listeden bir öğe seçin.";
            return;
        }

        if (target.IsSummaryPlaceholder)
        {
            StatusBanner = "Özet satırı silinemez; klasörü açıp dosya seçin.";
            return;
        }

        if (DiskAnalyzerService.IsSystemProtectedPath(target.FullPath))
        {
            StatusBanner = "Güvenlik kilidi: Windows sistem klasörleri buradan silinemez.";
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"\"{target.Name}\" ({target.SizeFormatted})\nKonum: {target.FullPath}\n\nBu öğe Geri Dönüşüm Kutusuna taşınacak. Devam edilsin mi?",
            "KOCHAN Disk Analizi — Geri Dönüşüme Taşı",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        var (ok, msg) = DiskAnalyzerService.SendItemToRecycleBin(target);
        StatusBanner = msg;
        if (ok)
        {
            ShowToast("Geri Dönüşüme Taşındı", msg, FindingSeverity.Info);
            TopLargestFiles.Remove(target);
            RemoveDiskNodeFromTree(DiskTreeRoots, target.FullPath);
            if (SelectedDiskNode == target) SelectedDiskNode = null;
        }
        else
        {
            ShowToast("Taşıma Başarısız", msg, FindingSeverity.Medium);
        }
    }

    private static bool RemoveDiskNodeFromTree(ObservableCollection<DiskNode> nodes, string fullPath)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase) && !nodes[i].IsSummaryPlaceholder)
            {
                nodes.RemoveAt(i);
                return true;
            }
            if (RemoveDiskNodeFromTree(nodes[i].Children, fullPath))
                return true;
        }
        return false;
    }

    [RelayCommand]
    private void RestartAsAdmin()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            System.Windows.Application.Current.Shutdown();
        }
        catch
        {
            StatusBanner = "Yönetici izni verilmedi.";
        }
    }

    private void AddSecurityEvent(string source, string message, FindingSeverity severity)
    {
        SecurityEvents.Insert(0, new SecurityEvent
        {
            Source = source,
            Message = message,
            Severity = severity
        });
        while (SecurityEvents.Count > 50)
            SecurityEvents.RemoveAt(SecurityEvents.Count - 1);
    }

    private async Task EnsureAutoRestorePointAsync(string reason)
    {
        if (!Settings.EnableAutoRestorePoint) return;
        if ((DateTime.UtcNow - Settings.LastAutoRestorePointUtc).TotalHours < 4) return;
        try
        {
            CleanerLiveAction = "Güvenlik sigortası: Sistem Geri Yükleme Noktası oluşturuluyor...";
            var (ok, msg) = await _tweaksRepair.CreateRestorePointAsync($"KOCHAN Sentinel - {reason}");
            if (ok)
            {
                Settings.LastAutoRestorePointUtc = DateTime.UtcNow;
                _settingsService.Save(Settings);
                ShowToast("Otomatik Yedek Sigortası", msg, FindingSeverity.Info);
                AddHistoryEntry("Geri Yükleme Noktası", "Otomatik Sigorta", msg, "Oluşturuldu", FindingSeverity.Info);
            }
        }
        catch { }
    }

    private void ReloadSystemTweaks()
    {
        SystemTweaks.Clear();
        foreach (var t in _tweaksRepair.LoadTweaksState())
            SystemTweaks.Add(t);
    }

    [RelayCommand]
    private async Task ScanKernelDriversAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        DriverScanSummary = "Çekirdek (.sys) sürücüleri ve BYOVD açıkları taranıyor...";
        try
        {
            var items = await _kernelAudit.ScanLoadedDriversAsync();
            KernelDrivers.Clear();
            foreach (var item in items) KernelDrivers.Add(item);

            int risky = items.Count(i => i.Severity >= FindingSeverity.Medium);
            int byovd = items.Count(i => i.IsKnownVulnerableByovd);
            DriverScanSummary = byovd > 0
                ? $"DİKKAT: {items.Count} sürücü tarandı — {byovd} bilinen BYOVD açığı, {risky} şüpheli sürücü tespit edildi!"
                : $"{items.Count} çekirdek sürücüsü tarandı ({risky} imzasız/dış konum bulgusu).";
            StatusBanner = DriverScanSummary;
            if (byovd > 0)
            {
                ShowToast("BYOVD Sürücü Uyarısı!", DriverScanSummary, FindingSeverity.Critical);
                AddHistoryEntry("BYOVD Sürücü Denetimi", $"{byovd} Açıklı Sürücü", DriverScanSummary, "Kritik Tespit", FindingSeverity.Critical);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ScanNetworkShieldAsync()
    {
        NetShieldSummary = "Canlı dış bağlantılar (IPv4/IPv6 + GeoIP), Firewall kuralları ve DNS önbelleği okunuyor...";
        try
        {
            var connsTask = _netShield.ScanExternalConnectionsAsync();
            var fwRules = await Task.Run(() => _firewall.GetBlockedRulesSnapshot());
            var dns = await _netShield.ReadDnsCacheAsync();
            var conns = await connsTask;

            ExternalConnections.Clear();
            foreach (var c in conns) ExternalConnections.Add(c);

            BlockedFirewallRules.Clear();
            foreach (var r in fwRules) BlockedFirewallRules.Add(r);

            DnsCacheEntries.Clear();
            foreach (var d in dns) DnsCacheEntries.Add(d);

            int suspNet = conns.Count(c => c.IsSuspicious);
            int suspDns = dns.Count(d => d.IsSuspicious);
            NetShieldSummary = $"{conns.Count} aktif dış bağlantı ({suspNet} şüpheli)  •  {fwRules.Count} Firewall engeli  •  {dns.Count} DNS kaydı ({suspDns} şüpheli)";
            StatusBanner = NetShieldSummary;
        }
        catch (Exception ex)
        {
            NetShieldSummary = $"Ağ kalkanı okunamadı: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task BlockNetEntryFirewallAsync(NetworkConnectionEntry? entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.ExecutablePath) || !File.Exists(entry.ExecutablePath))
        {
            StatusBanner = "Bu işlemin çalıştırılabilir dosya yolu alınamadığı için Firewall engeli konulamadı.";
            return;
        }
        var (ok, msg) = await _firewall.BlockOutboundAsync(entry.ExecutablePath, entry.ProcessName);
        StatusBanner = msg;
        if (ok)
        {
            ShowToast("İnternet Erişimi Kesildi", $"{entry.ProcessName} -> {entry.RemoteIp}", FindingSeverity.Medium);
            await ScanNetworkShieldAsync();
        }
    }

    [RelayCommand]
    private async Task UnblockFirewallRuleAsync(FirewallRuleItem? rule)
    {
        if (rule == null) return;
        var (ok, msg) = await _firewall.UnblockOutboundAsync(rule.ProcessName);
        StatusBanner = msg;
        if (ok)
        {
            BlockedFirewallRules.Remove(rule);
            ShowToast("Firewall Engeli Kaldırıldı", rule.ProcessName, FindingSeverity.Info);
            _ = RefreshTelemetryTickAsync();
        }
    }

    [RelayCommand]
    private async Task ScanInjectedAndExtensionsAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ModuleScanSummary = "Çalışan süreçlerde imzasız DLL ve tarayıcı eklenti/kısayolları denetleniyor...";
        try
        {
            var dlls = await _usbGuard.ScanInjectedDllsAsync();
            var exts = await _usbGuard.ScanBrowserExtensionsAndShortcutsAsync();

            InjectedDlls.Clear();
            foreach (var d in dlls) InjectedDlls.Add(d);

            BrowserExtIssues.Clear();
            foreach (var x in exts) BrowserExtIssues.Add(x);

            ModuleScanSummary = $"Tarama bitti: {dlls.Count} imzasız DLL modülü, {exts.Count} yüksek riskli eklenti/kısayol bulgusu.";
            StatusBanner = ModuleScanSummary;
            if (dlls.Count > 0 || exts.Count > 0)
                ShowToast("Bellek & Tarayıcı Denetimi", ModuleScanSummary, exts.Any(e => e.Severity >= FindingSeverity.High) ? FindingSeverity.High : FindingSeverity.Medium);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void KillDllProcess(LoadedDllModuleInfo? mod)
    {
        if (mod == null || mod.Pid <= 4) return;
        if (_telemetry.KillProcess(mod.Pid))
        {
            var toRemove = InjectedDlls.Where(m => m.Pid == mod.Pid).ToList();
            foreach (var item in toRemove) InjectedDlls.Remove(item);
            ShowToast("Süreç Kapatıldı", $"{mod.ProcessName} (PID {mod.Pid}) sonlandırıldı.", FindingSeverity.Info);
        }
    }

    [RelayCommand]
    private void OpenPathInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private void ToggleTweak(SystemTweakItem? item)
    {
        if (item == null) return;
        var (ok, msg) = _tweaksRepair.ToggleTweak(item.Id);
        ReloadSystemTweaks();
        StatusBanner = msg;
        ShowToast(ok ? "Sistem Optimizasyonu" : "Tweak Hatası", msg, ok ? FindingSeverity.Info : FindingSeverity.High);
    }

    [RelayCommand]
    private async Task CreateRestorePointNowAsync()
    {
        if (IsRepairRunning) return;
        IsRepairRunning = true;
        RepairStatusText = "Windows Sistem Geri Yükleme Noktası oluşturuluyor...";
        try
        {
            var (ok, msg) = await _tweaksRepair.CreateRestorePointAsync($"KOCHAN Sentinel Manuel ({DateTime.Now:dd.MM HH:mm})");
            if (ok)
            {
                Settings.LastAutoRestorePointUtc = DateTime.UtcNow;
                _settingsService.Save(Settings);
            }
            RepairLogLines.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {msg}");
            RepairStatusText = msg;
            StatusBanner = msg;
            ShowToast("Geri Yükleme Noktası", msg, ok ? FindingSeverity.Info : FindingSeverity.Medium);
        }
        finally
        {
            IsRepairRunning = false;
        }
    }

    [RelayCommand]
    private async Task RunSfcAsync() => await RunRepairToolAsync(useDism: false, "SFC /scannow Sistem Dosyası Denetimi");

    [RelayCommand]
    private async Task RunDismAsync() => await RunRepairToolAsync(useDism: true, "DISM /RestoreHealth İmaj Onarımı");

    private async Task RunRepairToolAsync(bool useDism, string label)
    {
        if (IsRepairRunning) return;
        IsRepairRunning = true;
        _repairCts?.Cancel();
        _repairCts = new CancellationTokenSource();
        RepairLogLines.Clear();
        RepairLogLines.Add($"[{DateTime.Now:HH:mm:ss}] {label} başlatıldı (yönetici izni ile birkaç dakika sürebilir)...");
        RepairStatusText = $"{label} çalışıyor...";
        try
        {
            var progress = new Progress<string>(line =>
            {
                RepairLogLines.Add(line);
                while (RepairLogLines.Count > 250) RepairLogLines.RemoveAt(0);
            });
            int code = await _tweaksRepair.RunSystemRepairAsync(useDism, progress, _repairCts.Token);
            RepairStatusText = code == 0 ? $"{label} başarıyla tamamlandı." : $"{label} tamamlandı (Çıkış kodu: {code}).";
            RepairLogLines.Add($"[{DateTime.Now:HH:mm:ss}] {RepairStatusText}");
            ShowToast("Sistem Onarımı", RepairStatusText, code == 0 ? FindingSeverity.Info : FindingSeverity.Medium);
        }
        catch (OperationCanceledException)
        {
            RepairStatusText = $"{label} kullanıcı tarafından durduruldu.";
        }
        finally
        {
            IsRepairRunning = false;
        }
    }

    [RelayCommand]
    private void CancelRepair()
    {
        _repairCts?.Cancel();
    }

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        UpdateStatusText = "GitHub sürümleri kontrol ediliyor...";
        var res = await _updater.CheckForUpdateAsync(GitHubRepoSlug);
        AvailableUpdate = res;
        HasAvailableUpdate = res.HasUpdate;
        UpdateStatusText = res.Message;
        UpdateUrl = res.ReleaseUrl;
        if (res.HasUpdate)
            UpdateBannerText = $"Yeni sürüm hazır: {res.LatestVersion} (Mevcut: v{res.CurrentVersion}) — Tek tıkla otomatik güncelleyebilirsin!";
        ShowToast("Sürüm Kontrolü", res.Message, res.HasUpdate ? FindingSeverity.Medium : FindingSeverity.Info);
    }

    [RelayCommand]
    private void OpenUpdateUrl()
    {
        if (string.IsNullOrWhiteSpace(UpdateUrl)) return;
        try { Process.Start(new ProcessStartInfo(UpdateUrl) { UseShellExecute = true }); } catch { }
    }

    public void ExportSettingsToPath(string path)
    {
        try
        {
            // Redact secret API keys / webhooks when exporting settings to share safely
            var redacted = SettingsService.CreateRedactedExportCopy(Settings);
            var json = System.Text.Json.JsonSerializer.Serialize(redacted, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            StatusBanner = $"Ayarlar dışa aktarıldı (API anahtarları güvenlik için gizlendi): {Path.GetFileName(path)}";
            ShowToast("Ayar Yedeği Oluşturuldu", "API anahtarları ve Webhook güvenliğiniz için dışa aktarımdan çıkarıldı.", FindingSeverity.Info);
        }
        catch (Exception ex)
        {
            ShowToast("Dışa Aktarma Hatası", ex.Message, FindingSeverity.High);
        }
    }

    public void ImportSettingsFromPath(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var imported = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);
            if (imported == null) throw new InvalidDataException("Geçersiz JSON dosyası.");
            Settings = imported;
            CustomGames.Clear();
            foreach (var g in Settings.CustomGameExecutables) CustomGames.Add(g);
            TrustedWhitelist.Clear();
            foreach (var w in Settings.TrustedWhitelist) TrustedWhitelist.Add(w);
            SaveSettings();
            ShowToast("Ayarlar İçe Aktarıldı", Path.GetFileName(path), FindingSeverity.Info);
        }
        catch (Exception ex)
        {
            ShowToast("İçe Aktarma Hatası", ex.Message, FindingSeverity.High);
        }
    }

    public void Dispose()
    {
        _driverCts?.Cancel();
        _updateCts?.Cancel();
        _dupCts?.Cancel();
        _repairCts?.Cancel();
        _diskScanCts?.Cancel();
        _benchmarkCts?.Cancel();
        _timer.Stop();
        _gameMode.DeactivateBoost();
        _canary.Dispose();
        _usbGuard.Dispose();
        _security.Dispose();
        _telemetry.Dispose();
    }
}

public sealed class ToastNotification
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public FindingSeverity Severity { get; set; } = FindingSeverity.Info;
    public string AccentColor { get; set; } = "#22C55E";
}
