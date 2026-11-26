using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Threading.Tasks;
using KochanSentinel.Core.Models;
using Microsoft.Win32;

namespace KochanSentinel.Core.Services;

/// <summary>
/// Safe, reversible Windows Service optimizer (following ChrisTitus WinUtil &amp; Black Viper safe guidelines)
/// and live Background Bloatware / Risky Process Auditor.
/// Never touches Defender, Windows Update, Audio, GPU, or Network core services.
/// </summary>
public sealed class ServiceAndBloatAuditService
{
    private sealed record ServiceSpec(
        string Name, string Display, string Category, string Desc,
        string Recommended, string DefaultMode, bool SafeForBulk, FindingSeverity Impact);

    private static readonly ServiceSpec[] AuditedServices =
    {
        new("DiagTrack", "Bağlı Kullanıcı Deneyimleri ve Telemetri", "Telemetri", "Microsoft'a sürekli tanı ve kullanım telemetrisi gönderen ana servis.", "Disabled", "Automatic", true, FindingSeverity.High),
        new("dmwappushservice", "Cihaz Yönetimi WAP Push Yönlendirme", "Telemetri", "Telemetri verilerini yönlendiren yardımcı servis.", "Disabled", "Manual", true, FindingSeverity.Medium),
        new("WerSvc", "Windows Hata Bildirimi (WER)", "Telemetri", "Uygulama çökmelerinde Microsoft'a hata raporu hazırlayıp gönderir.", "Manual", "Manual", true, FindingSeverity.Low),
        new("RemoteRegistry", "Uzak Kayıt Defteri (Remote Registry)", "Güvenlik", "Ağ üzerindeki uzak kullanıcıların kayıt defterini düzenlemesine izin verir (ev PC'de kapalı olmalı).", "Disabled", "Disabled", true, FindingSeverity.High),
        new("MapsBroker", "İndirilen Haritalar Yöneticisi", "Gereksiz", "Çevrimdışı Windows Haritalar uygulamasının arka plan güncelleyicisi.", "Manual", "Automatic", true, FindingSeverity.Low),
        new("Fax", "Windows Faks Servisi", "Gereksiz", "Eski analog modem/faks gönderimi için servis.", "Disabled", "Manual", true, FindingSeverity.Low),
        new("RetailDemo", "Perakende Mağaza Demo Servisi", "Gereksiz", "Sadece teknoloji mağazalarındaki vitrin bilgisayarları içindir.", "Disabled", "Manual", true, FindingSeverity.Low),
        new("lfsvc", "Coğrafi Konum Servisi", "Gizlilik", "Konum izleme servisi (kullanmıyorsanız kapatılabilir).", "Manual", "Manual", true, FindingSeverity.Low),
        new("WpcMonSvc", "Ebeveyn Denetimleri", "Gereksiz", "Microsoft Aile/Ebeveyn denetimi kullanmıyorsanız gereksizdir.", "Manual", "Manual", true, FindingSeverity.Low),
        new("wisvc", "Windows Insider Servisi", "Gereksiz", "Beta/Insider programına kayıtlı değilseniz arka planda gereksizdir.", "Manual", "Manual", true, FindingSeverity.Low),
        new("XblAuthManager", "Xbox Live Kimlik Doğrulama", "Xbox", "Xbox uygulaması/Game Pass kullanmıyorsanız Manuel moda alınabilir.", "Manual", "Manual", false, FindingSeverity.Low),
        new("XblGameSave", "Xbox Live Oyun Kaydetme", "Xbox", "Xbox Cloud Save kullanmıyorsanız Manuel moda alınabilir.", "Manual", "Manual", false, FindingSeverity.Low),
        new("XboxNetApiSvc", "Xbox Live Ağ Hizmeti", "Xbox", "Xbox çok oyunculu ağı kullanmıyorsanız Manuel moda alınabilir.", "Manual", "Manual", false, FindingSeverity.Low),
        new("SysMain", "SysMain (Superfetch)", "Disk / SSD", "Uygulamaları önceden RAM'e yükler. Hızlı NVMe SSD kullanan oyun PC'lerinde gereksiz disk/RAM aktivitesi yapabilir.", "Disabled", "Automatic", false, FindingSeverity.Medium),
        new("Spooler", "Yazdırma Biriktiricisi (Print Spooler)", "Donanım", "Bilgisayarınıza bağlı yazıcı yoksa kapatmak hem RAM kazandırır hem PrintNightmare saldırı yüzeyini kapatır.", "Manual", "Automatic", false, FindingSeverity.Medium)
    };

    private static readonly Dictionary<string, string> KnownBloatProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Widgets"] = "Windows 11 Haber/Hava Durumu (WebView2 RAM tüketir)",
        ["WidgetService"] = "Windows Widget arka plan servisi",
        ["PhoneExperienceHost"] = "Telefon Bağlantısı arka plan uygulaması",
        ["YourPhone"] = "Telefon Bağlantısı eski uygulaması",
        ["OneDrive"] = "OneDrive sürekli dosya eşitleme istemcisi",
        ["SearchIndexer"] = "Windows Dosya İndeksleyici (disk/CPU yorabilir)",
        ["GameBar"] = "Xbox Game Bar arka plan overlay süreci",
        ["GameBarFTServer"] = "Xbox Game Bar arka plan yardımcı sunucusu",
        ["Copilot"] = "Windows Copilot arka plan WebView süreci",
        ["AdobeUpdateService"] = "Adobe arka plan güncelleyicisi",
        ["CCXProcess"] = "Adobe Creative Cloud arka plan yardımcısı",
        ["EpicWebHelper"] = "Epic Games Launcher arka plan tarayıcı modülü",
        ["OriginWebHelperService"] = "EA / Origin arka plan yardımcısı",
        ["msedgewebview2"] = "Arka plan yerleşik WebView2 motoru (Widget/Copilot/Teams)",
        ["Cortana"] = "Cortana eski sesli asistan kalıntısı"
    };

    public async Task<List<ServiceOptimizationItem>> ScanUnusedServicesAsync()
    {
        return await Task.Run(() =>
        {
            var result = new List<ServiceOptimizationItem>();
            var installedMap = new Dictionary<string, ServiceControllerStatus>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var sc in ServiceController.GetServices())
                {
                    try { installedMap[sc.ServiceName] = sc.Status; }
                    catch { }
                    finally { sc.Dispose(); }
                }
            }
            catch { }

            foreach (var spec in AuditedServices)
            {
                if (!installedMap.TryGetValue(spec.Name, out var status))
                    continue;

                string startMode = ReadStartMode(spec.Name);
                bool isRunning = status == ServiceControllerStatus.Running;
                bool isOpt = (!isRunning) && (
                    startMode.Equals(spec.Recommended, StringComparison.OrdinalIgnoreCase) ||
                    (spec.Recommended == "Manual" && startMode.Equals("Disabled", StringComparison.OrdinalIgnoreCase)));

                result.Add(new ServiceOptimizationItem
                {
                    ServiceName = spec.Name,
                    DisplayName = spec.Display,
                    Category = spec.Category,
                    Description = spec.Desc,
                    CurrentStatus = isRunning ? "Çalışıyor" : "Durduruldu",
                    CurrentStartMode = startMode,
                    RecommendedStartMode = spec.Recommended,
                    DefaultStartMode = spec.DefaultMode,
                    IsOptimized = isOpt,
                    ImpactSeverity = isOpt ? FindingSeverity.Info : (isRunning ? spec.Impact : FindingSeverity.Low)
                });
            }

            return result
                .OrderBy(s => s.IsOptimized)
                .ThenByDescending(s => s.ImpactSeverity)
                .ThenBy(s => s.Category)
                .ToList();
        });
    }

    public (bool Ok, string Message) ToggleService(ServiceOptimizationItem item)
    {
        string targetMode = item.IsOptimized ? item.DefaultStartMode : item.RecommendedStartMode;
        bool stopNow = !item.IsOptimized;
        return ApplyServiceMode(item.ServiceName, item.DisplayName, targetMode, stopNow);
    }

    public async Task<(int ChangedCount, string Summary)> OptimizeAllSafeServicesAsync()
    {
        return await Task.Run(() =>
        {
            int changed = 0;
            foreach (var spec in AuditedServices.Where(s => s.SafeForBulk))
            {
                string cur = ReadStartMode(spec.Name);
                if (!cur.Equals(spec.Recommended, StringComparison.OrdinalIgnoreCase))
                {
                    var (ok, _) = ApplyServiceMode(spec.Name, spec.Display, spec.Recommended, stopFirst: true);
                    if (ok) changed++;
                }
            }
            return (changed, changed > 0
                ? $"{changed} gereksiz telemetri/arka plan servisi güvenle optimize edildi (Xbox/Yazıcı/SysMain tercihine bırakıldı)."
                : "Tüm güvenli telemetri ve gereksiz servisler zaten optimize durumda.");
        });
    }

    public List<BloatProcessAuditItem> AuditRunningBloatAndRisks(IEnumerable<ProcessItem> runningProcesses)
    {
        var list = new List<BloatProcessAuditItem>();
        foreach (var p in runningProcesses)
        {
            if (p.Pid <= 4 || p.Pid == Environment.ProcessId) continue;

            if (!p.IsSigned && !p.IsSystemCritical && !string.IsNullOrWhiteSpace(p.ExecutablePath) &&
                !p.ExecutablePath.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase))
            {
                bool riskyLoc = p.ExecutablePath.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase) ||
                                p.ExecutablePath.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase);
                list.Add(new BloatProcessAuditItem
                {
                    Pid = p.Pid,
                    ProcessName = p.Name,
                    Category = riskyLoc ? "Riskli / İmzasız Süreç" : "İmzasız Arka Plan",
                    Reason = riskyLoc
                        ? $"Temp/AppData'dan çalışan imzasız süreç (Ağ bağlantısı: {p.ActiveConnections})."
                        : "Dijital imzası doğrulanmamış çalışan uygulama.",
                    RamMb = p.RamMb,
                    CpuPercent = p.CpuPercent,
                    IsRisky = true,
                    Severity = (riskyLoc && p.ActiveConnections > 0) ? FindingSeverity.High : FindingSeverity.Medium
                });
            }
            else if (KnownBloatProcesses.TryGetValue(p.Name, out var desc))
            {
                list.Add(new BloatProcessAuditItem
                {
                    Pid = p.Pid,
                    ProcessName = p.Name,
                    Category = "Gereksiz Arka Plan",
                    Reason = desc,
                    RamMb = p.RamMb,
                    CpuPercent = p.CpuPercent,
                    IsRisky = false,
                    Severity = p.RamMb >= 150 ? FindingSeverity.Medium : FindingSeverity.Low
                });
            }
        }

        return list
            .OrderByDescending(x => x.IsRisky)
            .ThenByDescending(x => x.RamMb)
            .ToList();
    }

    private static (bool Ok, string Message) ApplyServiceMode(string serviceName, string displayName, string mode, bool stopFirst)
    {
        try
        {
            if (stopFirst)
                RunSc($"stop \"{serviceName}\"");

            string scMode = mode switch
            {
                "Disabled" => "disabled",
                "Automatic" => "auto",
                _ => "demand"
            };

            bool ok = RunSc($"config \"{serviceName}\" start= {scMode}") == 0;
            return ok
                ? (true, $"{displayName} ({serviceName}) → '{mode}' olarak ayarlandı.")
                : (false, $"{serviceName} değiştirilemedi (korumalı servis veya yetki eksik).");
        }
        catch (Exception ex)
        {
            return (false, $"Servis hatası: {ex.Message}");
        }
    }

    private static string ReadStartMode(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}", false);
            if (key?.GetValue("Start") is int startVal)
            {
                return startVal switch
                {
                    2 => "Automatic",
                    3 => "Manual",
                    4 => "Disabled",
                    _ => "Other"
                };
            }
        }
        catch { }
        return "Manual";
    }

    private static int RunSc(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(4000);
            return p?.ExitCode ?? -1;
        }
        catch { return -1; }
    }
}
