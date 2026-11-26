using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using KochanSentinel.Core.Models;

namespace KochanSentinel.Core.Services;

/// <summary>
/// Audits missing/errored Device Manager hardware, scans installed driver ages (GPU, Network, Audio, Chipset),
/// queries official Windows Update Agent (WUA COM) for pending driver updates, and installs them on demand.
/// </summary>
public sealed class DriverUpdateService
{
    private static readonly HashSet<string> KeyDeviceClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Display", "Net", "MEDIA", "Bluetooth", "SCSIAdapter", "DiskDrive", "USB", "System"
    };

    public async Task<(List<DriverAuditItem> Items, int PendingUpdateCount, int ErrorDeviceCount, string Summary)> ScanDriversAndUpdatesAsync(
        bool queryWindowsUpdate = true,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var items = new List<DriverAuditItem>();
            int pendingCount = 0;
            int errorCount = 0;

            // 1. Check Device Manager for Missing / Errored Devices (ConfigManagerErrorCode != 0)
            try
            {
                using var errSearcher = new ManagementObjectSearcher(
                    "SELECT Name, PNPClass, ConfigManagerErrorCode, Manufacturer FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
                foreach (ManagementObject obj in errSearcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    uint code = obj["ConfigManagerErrorCode"] != null ? Convert.ToUInt32(obj["ConfigManagerErrorCode"]) : 0;
                    if (code == 0 || code == 22) continue; // 22 = manually disabled by user

                    string name = obj["Name"]?.ToString()?.Trim() ?? "Bilinmeyen Aygıt";
                    string cls = obj["PNPClass"]?.ToString()?.Trim() ?? "Aygıt";
                    string mfg = obj["Manufacturer"]?.ToString()?.Trim() ?? "Bilinmiyor";
                    errorCount++;

                    items.Add(new DriverAuditItem
                    {
                        DeviceName = name,
                        DeviceClass = cls,
                        Provider = mfg,
                        CurrentVersion = $"Hata Kodu {code}",
                        DriverDateText = "Eksik / Hatalı",
                        StatusBadge = "EKSİK SÜRÜCÜ",
                        Recommendation = code == 28
                            ? "Bu donanımın sürücüsü hiç kurulmamış! Aşağıdan Windows Update ile kurabilirsin."
                            : $"Aygıt Yöneticisi hata kodu ({code}). Sürücüyü yeniden kurmak veya güncellemek önerilir.",
                        Severity = FindingSeverity.Critical
                    });
                }
            }
            catch { }

            // 2. Query official Windows Update Agent COM API for Pending Driver Updates
            if (queryWindowsUpdate)
            {
                try
                {
                    var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
                    if (sessionType != null)
                    {
                        dynamic? session = Activator.CreateInstance(sessionType);
                        if (session != null)
                        {
                            dynamic searcher = session.CreateUpdateSearcher();
                            dynamic searchResult = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
                            int count = (int)searchResult.Updates.Count;
                            for (int i = 0; i < count; i++)
                            {
                                ct.ThrowIfCancellationRequested();
                                dynamic upd = searchResult.Updates.Item(i);
                                string title = upd.Title?.ToString() ?? "Sürücü Güncellemesi";
                                string driverClass = "";
                                string provider = "Windows Update";
                                string verDate = "Yeni Sürüm";
                                string updateId = "";

                                try { driverClass = upd.DriverClass?.ToString() ?? "Sürücü"; } catch { }
                                try { provider = upd.DriverProvider?.ToString() ?? provider; } catch { }
                                try { updateId = upd.Identity?.UpdateID?.ToString() ?? ""; } catch { }
                                try
                                {
                                    DateTime dt = Convert.ToDateTime(upd.DriverVerDate);
                                    if (dt.Year > 2000) verDate = dt.ToString("dd.MM.yyyy");
                                }
                                catch { }

                                pendingCount++;
                                items.Add(new DriverAuditItem
                                {
                                    DeviceName = title,
                                    DeviceClass = string.IsNullOrWhiteSpace(driverClass) ? "Driver Update" : driverClass,
                                    Provider = provider,
                                    CurrentVersion = "Güncelleme Hazır",
                                    DriverDateText = verDate,
                                    StatusBadge = "GÜNCELLEME VAR",
                                    Recommendation = "Resmi Windows Update WHQL sürücü güncellemesi mevcut — tek tıkla kurulabilir.",
                                    Severity = FindingSeverity.High,
                                    IsPendingWindowsUpdate = true,
                                    UpdateId = updateId
                                });
                            }
                        }
                    }
                }
                catch { }
            }

            // 3. Audit Installed Key Hardware Drivers (GPU, Network, Audio, Bluetooth, Storage, Chipset)
            try
            {
                using var drvSearcher = new ManagementObjectSearcher(
                    "SELECT DeviceName, DeviceClass, DriverProviderName, DriverVersion, DriverDate FROM Win32_PnPSignedDriver");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (ManagementObject obj in drvSearcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    string cls = obj["DeviceClass"]?.ToString()?.Trim() ?? "";
                    if (!KeyDeviceClasses.Contains(cls)) continue;

                    string name = obj["DeviceName"]?.ToString()?.Trim() ?? "";
                    if (string.IsNullOrWhiteSpace(name) || !seen.Add($"{cls}|{name}")) continue;

                    string provider = obj["DriverProviderName"]?.ToString()?.Trim() ?? "";
                    if (cls.Equals("System", StringComparison.OrdinalIgnoreCase) &&
                        provider.Equals("Microsoft", StringComparison.OrdinalIgnoreCase))
                        continue; // Skip noisy generic Microsoft ACPI/PCI system stubs

                    string version = obj["DriverVersion"]?.ToString()?.Trim() ?? "—";
                    string rawDate = obj["DriverDate"]?.ToString() ?? "";
                    DateTime? drvDate = ParseWmiDate(rawDate);

                    double ageYears = drvDate.HasValue ? (DateTime.Now - drvDate.Value).TotalDays / 365.25 : 0;
                    bool isCriticalClass = cls is "Display" or "Net" or "MEDIA";
                    bool isOld = drvDate.HasValue && ((isCriticalClass && ageYears >= 1.8) || ageYears >= 3.5);

                    items.Add(new DriverAuditItem
                    {
                        DeviceName = name,
                        DeviceClass = TranslateClass(cls),
                        Provider = string.IsNullOrWhiteSpace(provider) ? "Bilinmiyor" : provider,
                        CurrentVersion = version,
                        DriverDateText = drvDate.HasValue ? drvDate.Value.ToString("dd.MM.yyyy") : "—",
                        StatusBadge = isOld ? "ESKİ SÜRÜCÜ" : "Güncel",
                        Recommendation = isOld
                            ? $"Bu {TranslateClass(cls)} sürücüsü {ageYears:F1} yıllık — performans ve kararlılık için üretici/Windows Update kontrolü önerilir."
                            : "Sürücü kurulu ve normal çalışıyor.",
                        Severity = isOld ? FindingSeverity.Medium : FindingSeverity.Info
                    });
                }
            }
            catch { }

            var ordered = items
                .OrderByDescending(i => i.Severity)
                .ThenBy(i => i.DeviceClass)
                .ThenBy(i => i.DeviceName)
                .Take(90)
                .ToList();

            int oldCount = ordered.Count(i => i.Severity == FindingSeverity.Medium);
            string summary = (pendingCount > 0 || errorCount > 0)
                ? $"Tarama bitti: {pendingCount} Windows Update sürücüsü hazır, {errorCount} eksik/hatalı aygıt, {oldCount} eski sürücü tespit edildi."
                : oldCount > 0
                    ? $"Eksik veya bekleyen Windows Update sürücüsü yok • {oldCount} sürücü yaşı nedeniyle kontrol edilebilir."
                    : $"Harika! Tüm temel sürücüler kurulu ve bekleyen Windows Update sürücü güncellemesi yok ({ordered.Count} sürücü incelendi).";

            return (ordered, pendingCount, errorCount, summary);
        }, ct);
    }

    public async Task<(bool Ok, string Message)> InstallPendingDriverUpdatesAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
                var collType = Type.GetTypeFromProgID("Microsoft.Update.UpdateColl");
                if (sessionType == null || collType == null)
                    return (false, "Windows Update Agent COM arabirimi kullanılamıyor.");

                progress?.Report("Windows Update sürücüleri aranıyor...");
                dynamic session = Activator.CreateInstance(sessionType)!;
                dynamic searcher = session.CreateUpdateSearcher();
                dynamic searchRes = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
                int count = (int)searchRes.Updates.Count;
                if (count == 0)
                    return (true, "Kurulmayı bekleyen resmi Windows Update sürücüsü bulunamadı.");

                dynamic toInstall = Activator.CreateInstance(collType)!;
                for (int i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    dynamic u = searchRes.Updates.Item(i);
                    try { if ((bool)u.EulaAccepted == false) u.AcceptEula(); } catch { }
                    toInstall.Add(u);
                }

                progress?.Report($"{count} sürücü güncellemesi indiriliyor (birkaç dakika sürebilir)...");
                dynamic downloader = session.CreateUpdateDownloader();
                downloader.Updates = toInstall;
                downloader.Download();

                ct.ThrowIfCancellationRequested();
                progress?.Report($"{count} sürücü güncellemesi kuruluyor...");
                dynamic installer = session.CreateUpdateInstaller();
                installer.Updates = toInstall;
                dynamic instRes = installer.Install();

                int resultCode = (int)instRes.ResultCode; // 2 = Succeeded, 3 = SucceededWithErrors
                bool reboot = false;
                try { reboot = (bool)instRes.RebootRequired; } catch { }

                if (resultCode is 2 or 3)
                {
                    string rebootNote = reboot ? " Değişikliklerin tamamlanması için bilgisayarı yeniden başlatmanız önerilir." : "";
                    return (true, $"{count} sürücü güncellemesi başarıyla kuruldu!{rebootNote}");
                }

                return (false, $"Sürücü kurulumu tamamlanamadı (WUA Kod: {resultCode}). İsteğe bağlı Windows Update ekranından kurmayı deneyebilirsiniz.");
            }
            catch (OperationCanceledException)
            {
                return (false, "Sürücü kurulumu iptal edildi.");
            }
            catch (Exception ex)
            {
                return (false, $"Windows Update sürücü kurulum hatası: {ex.Message}");
            }
        }, ct);
    }

    public static void OpenWindowsOptionalUpdates()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:windowsupdate-optionalupdates") { UseShellExecute = true }); }
        catch { }
    }

    public static void OpenVendorDriverPortal(string gpuOrVendor)
    {
        string lower = (gpuOrVendor ?? "").ToLowerInvariant();
        string url = lower.Contains("nvidia") || lower.Contains("geforce") || lower.Contains("rtx") || lower.Contains("gtx")
            ? "https://www.nvidia.com/en-us/drivers/"
            : lower.Contains("amd") || lower.Contains("radeon")
                ? "https://www.amd.com/en/support/download/drivers.html"
                : "https://www.intel.com/content/www/us/en/support/detect.html";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private static DateTime? ParseWmiDate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length < 8) return null;
        if (DateTime.TryParseExact(raw[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt;
        return null;
    }

    private static string TranslateClass(string cls) => cls.ToUpperInvariant() switch
    {
        "DISPLAY" => "Ekran Kartı (GPU)",
        "NET" => "Ağ / Wi-Fi",
        "MEDIA" => "Ses / Mikrofon",
        "BLUETOOTH" => "Bluetooth",
        "SCSIADAPTER" or "DISKDRIVE" => "Depolama / NVMe",
        "USB" => "USB Denetleyici",
        "SYSTEM" => "Anakart / Chipset",
        _ => cls
    };
}
