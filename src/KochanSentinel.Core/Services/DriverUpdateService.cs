using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KochanSentinel.Core.Models;

namespace KochanSentinel.Core.Services;

/// <summary>
/// Audits missing/errored Device Manager hardware (with PCI/USB Hardware ID vendor lookup),
/// scans genuinely outdated third-party drivers (filtering Microsoft 21.06.2006 inbox stubs),
/// queries official Windows Update Agent (WUA COM) for pending WHQL driver updates,
/// and downloads/installs them with live percentage, MB/s speed, elapsed time, and cancel support.
/// </summary>
public sealed class DriverUpdateService
{
    private static readonly HashSet<string> KeyDeviceClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Display", "Net", "MEDIA", "Bluetooth", "SCSIAdapter", "DiskDrive", "USB", "System"
    };

    private static readonly DateTime MicrosoftInboxPlaceholderDate = new(2006, 6, 21);

    private static readonly Dictionary<string, string> PciVendorLookup = new(StringComparer.OrdinalIgnoreCase)
    {
        ["8086"] = "Intel",
        ["1022"] = "AMD",
        ["10DE"] = "NVIDIA",
        ["10EC"] = "Realtek",
        ["14C3"] = "MediaTek",
        ["17CB"] = "Qualcomm",
        ["168C"] = "Qualcomm Atheros",
        ["14E4"] = "Broadcom",
        ["1B21"] = "ASMedia",
        ["1912"] = "Renesas",
        ["1102"] = "Creative",
        ["1043"] = "ASUS",
        ["1462"] = "MSI",
        ["1458"] = "Gigabyte",
        ["15B7"] = "SanDisk / WD",
        ["144D"] = "Samsung",
        ["1E0F"] = "Kioxia",
        ["C0A9"] = "Micron / Crucial"
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
                    "SELECT Name, PNPClass, ConfigManagerErrorCode, Manufacturer, HardwareID, PNPDeviceID FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
                foreach (ManagementObject obj in errSearcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    uint code = obj["ConfigManagerErrorCode"] != null ? Convert.ToUInt32(obj["ConfigManagerErrorCode"]) : 0;
                    if (code == 0 || code == 22) continue; // 22 = manually disabled by user

                    string name = obj["Name"]?.ToString()?.Trim() ?? "Bilinmeyen Aygıt";
                    string cls = obj["PNPClass"]?.ToString()?.Trim() ?? "Aygıt";
                    string mfg = obj["Manufacturer"]?.ToString()?.Trim() ?? "";
                    string pnpId = obj["PNPDeviceID"]?.ToString()?.Trim() ?? "";

                    string shortHwId = ExtractShortHardwareId(obj["HardwareID"] as string[], pnpId);
                    string detectedVendor = DetectVendorFromHardwareId(shortHwId, pnpId);
                    if (string.IsNullOrWhiteSpace(mfg) || mfg.Contains("standart", StringComparison.OrdinalIgnoreCase) || mfg.Equals("(Standard system devices)", StringComparison.OrdinalIgnoreCase))
                        mfg = string.IsNullOrEmpty(detectedVendor) ? "Bilinmiyor" : detectedVendor;

                    errorCount++;

                    string rec;
                    if (code == 28 && !string.IsNullOrEmpty(detectedVendor))
                    {
                        bool isChipsetLike = name.Contains("SM Bus", StringComparison.OrdinalIgnoreCase) ||
                                             name.Contains("PCI", StringComparison.OrdinalIgnoreCase) ||
                                             name.Contains("Bilinmeyen", StringComparison.OrdinalIgnoreCase) ||
                                             name.Contains("Unknown", StringComparison.OrdinalIgnoreCase);
                        rec = isChipsetLike
                            ? $"{detectedVendor} Chipset / Anakart sürücüsü eksik ({shortHwId}). Windows Update'te yoksa sağdaki 'Katalogda Ara' ile veya {detectedVendor} Chipset paketiyle kurabilirsin."
                            : $"{detectedVendor} sürücüsü kurulmamış ({shortHwId}). Windows Update veya 'Katalogda Ara' ile indirilebilir.";
                    }
                    else if (code == 28)
                    {
                        rec = $"Bu donanımın sürücüsü kurulmamış ({(string.IsNullOrEmpty(shortHwId) ? "Kod 28" : shortHwId)}). Windows Update veya 'Katalogda Ara' ile kurabilirsin.";
                    }
                    else
                    {
                        rec = $"Aygıt Yöneticisi hata kodu ({code}). Sürücüyü yeniden kurmak veya güncellemek önerilir.";
                    }

                    items.Add(new DriverAuditItem
                    {
                        DeviceName = name,
                        DeviceClass = cls,
                        Provider = mfg,
                        CurrentVersion = $"Hata Kodu {code}",
                        DriverDateText = "Eksik / Hatalı",
                        StatusBadge = "EKSİK SÜRÜCÜ",
                        Recommendation = rec,
                        Severity = FindingSeverity.Critical,
                        IsMissingDevice = true,
                        HardwareId = shortHwId
                    });
                }
            }
            catch { }

            // 2. Query official Windows Update Agent COM API for Pending Driver Updates (including optional drivers)
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
                                string driverClass = "Sürücü";
                                string provider = "Windows Update";
                                string verDate = "Yeni Sürüm";
                                string updateId = "";
                                string sizeText = "—";

                                try { driverClass = upd.DriverClass?.ToString() ?? driverClass; } catch { }
                                try { provider = upd.DriverProvider?.ToString() ?? provider; } catch { }
                                try { updateId = upd.Identity?.UpdateID?.ToString() ?? ""; } catch { }
                                try
                                {
                                    long maxBytes = Convert.ToInt64(upd.MaxDownloadSize);
                                    if (maxBytes > 0) sizeText = FormatBytes(maxBytes);
                                }
                                catch { }
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
                                    DeviceClass = TranslateClass(driverClass),
                                    Provider = provider,
                                    CurrentVersion = "Güncelleme Hazır",
                                    DriverDateText = verDate,
                                    SizeText = sizeText,
                                    StatusBadge = "GÜNCELLEME VAR",
                                    Recommendation = "Resmi Windows Update WHQL sürücü güncellemesi hazır — tek tıkla indirip kurabilirsin.",
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

            // 3. Audit Installed Key Hardware Drivers (filter Microsoft 21.06.2006 inbox stubs & virtual WAN Miniports)
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

                    // Skip virtual Windows inbox miniports and generic system stubs
                    if (name.StartsWith("WAN Miniport", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Kernel Debug Network", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Wi-Fi Direct Virtual", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("Disk drive", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string provider = obj["DriverProviderName"]?.ToString()?.Trim() ?? "";
                    if (cls.Equals("System", StringComparison.OrdinalIgnoreCase) &&
                        provider.Equals("Microsoft", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string version = obj["DriverVersion"]?.ToString()?.Trim() ?? "—";
                    string rawDate = obj["DriverDate"]?.ToString() ?? "";
                    DateTime? drvDate = ParseWmiDate(rawDate);

                    // Microsoft intentionally stamps all Windows 10/11 built-in inbox drivers with 21.06.2006
                    // so PnP ranks third-party drivers higher. Never falsely flag them as "20 years old"!
                    bool isMicrosoftInbox =
                        (drvDate.HasValue && drvDate.Value.Date == MicrosoftInboxPlaceholderDate) ||
                        (provider.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) && version.StartsWith("10.0.", StringComparison.Ordinal));

                    double ageYears = (drvDate.HasValue && !isMicrosoftInbox)
                        ? (DateTime.Now - drvDate.Value).TotalDays / 365.25
                        : 0;

                    bool isCriticalClass = cls is "Display" or "Net" or "MEDIA";
                    bool isOld = !isMicrosoftInbox && drvDate.HasValue && ((isCriticalClass && ageYears >= 2.0) || ageYears >= 4.0);

                    items.Add(new DriverAuditItem
                    {
                        DeviceName = name,
                        DeviceClass = TranslateClass(cls),
                        Provider = string.IsNullOrWhiteSpace(provider) ? "Bilinmiyor" : provider,
                        CurrentVersion = version,
                        DriverDateText = isMicrosoftInbox ? "Windows Dahili" : (drvDate.HasValue ? drvDate.Value.ToString("dd.MM.yyyy") : "—"),
                        StatusBadge = isOld ? "ESKİ SÜRÜCÜ" : "Güncel",
                        Recommendation = isOld
                            ? $"Bu {TranslateClass(cls)} sürücüsü {ageYears:F1} yıllık — üretici veya Katalog üzerinden güncelleme kontrolü önerilir."
                            : isMicrosoftInbox
                                ? "Windows işletim sistemi dahili sürücüsü (Windows Update ile güncel tutulur)."
                                : "Sürücü kurulu ve güncel.",
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
                ? $"{pendingCount} Windows Update sürücüsü kuruluma hazır • {errorCount} eksik aygıt (Kod 28) • {oldCount} eski üçüncü parti sürücü."
                : oldCount > 0
                    ? $"Bekleyen Windows Update sürücüsü yok • {oldCount} eski sürücü için üretici kontrolü yapılabilir."
                    : $"Harika! Tüm temel sürücüler güncel ve bekleyen Windows Update sürücüsü yok ({ordered.Count} sürücü incelendi).";

            return (ordered, pendingCount, errorCount, summary);
        }, ct);
    }

    public async Task<(bool Ok, int InstalledCount, int FailedCount, bool RebootRequired, string Message)> InstallDriverUpdatesAsync(
        string? targetUpdateId = null,
        IProgress<DriverInstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
                var collType = Type.GetTypeFromProgID("Microsoft.Update.UpdateColl");
                if (sessionType == null || collType == null)
                    return (false, 0, 0, false, "Windows Update Agent COM arabirimi kullanılamıyor.");

                progress?.Report(new DriverInstallProgress
                {
                    Phase = "Hazırlanıyor",
                    Percent = 3,
                    CurrentDriverName = "Windows Update sunucusuna bağlanılıyor...",
                    ElapsedText = FormatElapsed(sw.Elapsed),
                    DetailText = "Bekleyen WHQL sürücü paketleri sorgulanıyor..."
                });

                dynamic session = Activator.CreateInstance(sessionType)!;
                dynamic searcher = session.CreateUpdateSearcher();
                dynamic searchRes = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
                int totalFound = (int)searchRes.Updates.Count;
                if (totalFound == 0)
                    return (true, 0, 0, false, "Kurulmayı bekleyen resmi Windows Update sürücüsü bulunamadı (Eksik 'Kod 28' aygıtları için satırdaki 'Katalogda Ara' butonunu kullanabilirsin).");

                var selectedUpdates = new List<(dynamic Update, string Title, long EstBytes)>();
                for (int i = 0; i < totalFound; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    dynamic u = searchRes.Updates.Item(i);
                    string uid = "";
                    try { uid = u.Identity?.UpdateID?.ToString() ?? ""; } catch { }
                    if (!string.IsNullOrWhiteSpace(targetUpdateId) &&
                        !uid.Equals(targetUpdateId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string title = u.Title?.ToString() ?? $"Sürücü #{i + 1}";
                    long estBytes = 0;
                    try { estBytes = Convert.ToInt64(u.MaxDownloadSize); } catch { }
                    try { if ((bool)u.EulaAccepted == false) u.AcceptEula(); } catch { }
                    selectedUpdates.Add((u, title, estBytes));
                }

                if (selectedUpdates.Count == 0)
                    return (false, 0, 0, false, "Seçilen sürücü Windows Update listesinde artık görünmüyor (zaten kurulmuş olabilir).");

                int totalCount = selectedUpdates.Count;
                int succeeded = 0;
                int failed = 0;
                bool anyReboot = false;
                var failedDetails = new List<string>();

                // Download & install EACH driver sequentially so progress %, speed (MB/s), and errors are 100% transparent!
                for (int idx = 0; idx < totalCount; idx++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (upd, title, estBytes) = selectedUpdates[idx];

                    dynamic singleColl = Activator.CreateInstance(collType)!;
                    singleColl.Add(upd);

                    // --- PHASE A: ASYNC DOWNLOAD WITH LIVE PROGRESS & SPEED ---
                    bool alreadyDownloaded = false;
                    try { alreadyDownloaded = (bool)upd.IsDownloaded; } catch { }

                    if (!alreadyDownloaded)
                    {
                        bool dlOk = DownloadSingleUpdateWithProgress(
                            session, singleColl, title, estBytes, idx, totalCount, sw, progress, ct, out string dlErr);
                        if (!dlOk)
                        {
                            failed++;
                            failedDetails.Add($"{ShortenTitle(title)} ({dlErr})");
                            continue;
                        }
                    }

                    ct.ThrowIfCancellationRequested();

                    // --- PHASE B: ASYNC INSTALL WITH LIVE PROGRESS ---
                    bool instOk = InstallSingleUpdateWithProgress(
                        session, singleColl, title, idx, totalCount, sw, progress, ct, out bool needsReboot, out string instErr);

                    if (instOk)
                    {
                        succeeded++;
                        if (needsReboot) anyReboot = true;
                    }
                    else
                    {
                        failed++;
                        failedDetails.Add($"{ShortenTitle(title)} ({instErr})");
                    }
                }

                // Trigger Plug-and-Play device rescan so newly installed drivers bind immediately
                if (succeeded > 0)
                {
                    progress?.Report(new DriverInstallProgress
                    {
                        Phase = "Tamamlanıyor",
                        Percent = 98,
                        CurrentIndex = totalCount,
                        TotalCount = totalCount,
                        CurrentDriverName = "Windows Aygıt Yöneticisi (PnP) yenileniyor...",
                        ElapsedText = FormatElapsed(sw.Elapsed),
                        DetailText = "Kurulan sürücüler donanımlara bağlanıyor..."
                    });
                    RunPnpRescan();
                }

                progress?.Report(new DriverInstallProgress
                {
                    Phase = "Tamamlandı",
                    Percent = 100,
                    CurrentIndex = totalCount,
                    TotalCount = totalCount,
                    CurrentDriverName = succeeded > 0 ? $"{succeeded}/{totalCount} sürücü kuruldu" : "Kurulum tamamlanamadı",
                    ElapsedText = FormatElapsed(sw.Elapsed),
                    DetailText = failed > 0 ? $"Başarısız: {string.Join(", ", failedDetails.Take(2))}" : "Tüm işlemler başarıyla tamamlandı."
                });

                if (succeeded > 0 && failed == 0)
                {
                    string rebootNote = anyReboot ? " (Değişikliklerin tamamen etkinleşmesi için yeniden başlatma önerilir.)" : "";
                    return (true, succeeded, 0, anyReboot, $"{succeeded} sürücü başarıyla indirildi ve kuruldu!{rebootNote}");
                }
                if (succeeded > 0 && failed > 0)
                {
                    string rebootNote = anyReboot ? " Yeniden başlatma önerilir." : "";
                    return (true, succeeded, failed, anyReboot,
                        $"{succeeded}/{totalCount} sürücü kuruldu, {failed} sürücü atlandı: {string.Join("; ", failedDetails.Take(2))}.{rebootNote}");
                }

                return (false, 0, failed, false,
                    $"Sürücü kurulumu tamamlanamadı: {string.Join("; ", failedDetails.Take(2))}. İsteğe Bağlı Güncellemeler ekranından kurmayı deneyebilirsin.");
            }
            catch (OperationCanceledException)
            {
                return (false, 0, 0, false, "Sürücü indirme/kurulum işlemi kullanıcı tarafından iptal edildi.");
            }
            catch (Exception ex)
            {
                return (false, 0, 0, false, $"Windows Update sürücü kurulum hatası: {ex.Message}");
            }
        }, ct);
    }

    private static bool DownloadSingleUpdateWithProgress(
        dynamic session,
        dynamic singleColl,
        string title,
        long estBytes,
        int idx,
        int totalCount,
        Stopwatch totalSw,
        IProgress<DriverInstallProgress>? progress,
        CancellationToken ct,
        out string errorText)
    {
        errorText = "";
        try
        {
            dynamic downloader = session.CreateUpdateDownloader();
            downloader.Updates = singleColl;

            double basePct = (idx * 100.0) / totalCount;
            double slicePct = 60.0 / totalCount; // Download = 60% of each driver's bar

            dynamic dlJob = downloader.BeginDownload(null, null, null);
            long prevBytes = 0;
            long prevNicBytes = GetTotalNetworkRxBytes();
            var sampleSw = Stopwatch.StartNew();
            double smoothSpeedMbps = 0;

            while (!(bool)dlJob.IsCompleted)
            {
                if (ct.IsCancellationRequested)
                {
                    try { dlJob.RequestAbort(); } catch { }
                    ct.ThrowIfCancellationRequested();
                }

                int curPct = 0;
                long downloaded = 0;
                long toDownload = estBytes;
                try
                {
                    dynamic p = dlJob.GetProgress();
                    curPct = Convert.ToInt32(p.PercentComplete);
                    downloaded = Convert.ToInt64(p.CurrentUpdateBytesDownloaded);
                    long wuaTotal = Convert.ToInt64(p.CurrentUpdateBytesToDownload);
                    if (wuaTotal > 0) toDownload = wuaTotal;
                }
                catch { }

                double dtSec = sampleSw.Elapsed.TotalSeconds;
                if (dtSec >= 0.45)
                {
                    long deltaWua = Math.Max(0, downloaded - prevBytes);
                    long nowNic = GetTotalNetworkRxBytes();
                    long deltaNic = Math.Max(0, nowNic - prevNicBytes);
                    long effectiveDelta = deltaWua > 0 ? deltaWua : deltaNic;

                    double instMbps = (effectiveDelta / dtSec) / (1024.0 * 1024.0);
                    smoothSpeedMbps = smoothSpeedMbps <= 0.01 ? instMbps : (smoothSpeedMbps * 0.55 + instMbps * 0.45);
                    prevBytes = downloaded;
                    prevNicBytes = nowNic;
                    sampleSw.Restart();
                }

                double overallPct = Math.Clamp(basePct + (curPct / 100.0) * slicePct, 1, 97);
                string bytesLabel = toDownload > 0
                    ? $"{FormatBytes(downloaded)} / {FormatBytes(toDownload)}"
                    : (downloaded > 0 ? FormatBytes(downloaded) : "Paket hazırlanıyor...");

                progress?.Report(new DriverInstallProgress
                {
                    Phase = "İndiriliyor",
                    Percent = overallPct,
                    CurrentIndex = idx + 1,
                    TotalCount = totalCount,
                    CurrentDriverName = title,
                    SpeedMbps = smoothSpeedMbps,
                    BytesDownloaded = downloaded,
                    TotalBytes = toDownload,
                    ElapsedText = FormatElapsed(totalSw.Elapsed),
                    DetailText = $"İndirme %{curPct} • {bytesLabel}"
                });

                Thread.Sleep(350);
            }

            dynamic dlRes = downloader.EndDownload(dlJob);
            int code = Convert.ToInt32(dlRes.ResultCode); // 2 = Succeeded, 3 = SucceededWithErrors
            if (code is 2 or 3) return true;

            errorText = $"İndirme Kodu {code}";
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errorText = ex.Message;
            return false;
        }
    }

    private static bool InstallSingleUpdateWithProgress(
        dynamic session,
        dynamic singleColl,
        string title,
        int idx,
        int totalCount,
        Stopwatch totalSw,
        IProgress<DriverInstallProgress>? progress,
        CancellationToken ct,
        out bool rebootRequired,
        out string errorText)
    {
        rebootRequired = false;
        errorText = "";
        try
        {
            dynamic installer = session.CreateUpdateInstaller();
            installer.Updates = singleColl;

            double basePct = (idx * 100.0) / totalCount + (60.0 / totalCount);
            double slicePct = 40.0 / totalCount; // Install = 40% of each driver's bar

            dynamic instJob = installer.BeginInstall(null, null, null);
            while (!(bool)instJob.IsCompleted)
            {
                if (ct.IsCancellationRequested)
                {
                    try { instJob.RequestAbort(); } catch { }
                    ct.ThrowIfCancellationRequested();
                }

                int curPct = 0;
                try
                {
                    dynamic p = instJob.GetProgress();
                    curPct = Convert.ToInt32(p.PercentComplete);
                }
                catch { }

                double overallPct = Math.Clamp(basePct + (curPct / 100.0) * slicePct, 1, 97);
                progress?.Report(new DriverInstallProgress
                {
                    Phase = "Kuruluyor",
                    Percent = overallPct,
                    CurrentIndex = idx + 1,
                    TotalCount = totalCount,
                    CurrentDriverName = title,
                    SpeedMbps = 0,
                    ElapsedText = FormatElapsed(totalSw.Elapsed),
                    DetailText = $"Sistem sürücüsü kuruluyor (%{curPct})..."
                });

                Thread.Sleep(350);
            }

            dynamic instRes = installer.EndInstall(instJob);
            int code = Convert.ToInt32(instRes.ResultCode);
            try { rebootRequired = (bool)instRes.RebootRequired; } catch { }

            if (code is 2 or 3) return true;

            int hr = 0;
            try { hr = Convert.ToInt32(instRes.GetUpdateResult(0).HResult); } catch { }
            errorText = hr != 0 ? $"Kurulum Kodu {code} (0x{hr:X8})" : $"Kurulum Kodu {code}";
            return false;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errorText = ex.Message;
            return false;
        }
    }

    private static void RunPnpRescan()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = "/scan-devices",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            proc?.WaitForExit(4500);
        }
        catch { }
    }

    private static long GetTotalNetworkRxBytes()
    {
        long total = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                total += nic.GetIPStatistics().BytesReceived;
            }
        }
        catch { }
        return total;
    }

    private static string ExtractShortHardwareId(string[]? hwIds, string pnpDeviceId)
    {
        string raw = (hwIds is { Length: > 0 } ? hwIds[0] : pnpDeviceId) ?? "";
        var pciMatch = Regex.Match(raw, @"(PCI\\VEN_[0-9A-Fa-f]{4}&DEV_[0-9A-Fa-f]{4})", RegexOptions.IgnoreCase);
        if (pciMatch.Success) return pciMatch.Groups[1].Value.ToUpperInvariant();

        var usbMatch = Regex.Match(raw, @"(USB\\VID_[0-9A-Fa-f]{4}&PID_[0-9A-Fa-f]{4})", RegexOptions.IgnoreCase);
        if (usbMatch.Success) return usbMatch.Groups[1].Value.ToUpperInvariant();

        var acpiMatch = Regex.Match(raw, @"(ACPI\\[A-Za-z0-9_]+)", RegexOptions.IgnoreCase);
        if (acpiMatch.Success) return acpiMatch.Groups[1].Value.ToUpperInvariant();

        return raw.Length > 36 ? raw[..36] : raw;
    }

    private static string DetectVendorFromHardwareId(string shortHwId, string pnpId)
    {
        string src = $"{shortHwId} {pnpId}";
        var m = Regex.Match(src, @"VEN_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase);
        if (m.Success && PciVendorLookup.TryGetValue(m.Groups[1].Value, out string? vendor))
            return vendor;
        if (src.Contains("INTC", StringComparison.OrdinalIgnoreCase) || src.Contains("INT3", StringComparison.OrdinalIgnoreCase))
            return "Intel";
        if (src.Contains("AMDI", StringComparison.OrdinalIgnoreCase) || src.Contains("AMD", StringComparison.OrdinalIgnoreCase))
            return "AMD";
        return "";
    }

    public static void OpenDriverCatalogLookup(DriverAuditItem item)
    {
        string query = !string.IsNullOrWhiteSpace(item.HardwareId)
            ? item.HardwareId
            : item.DeviceName;
        string url = $"https://www.catalog.update.microsoft.com/Search.aspx?q={Uri.EscapeDataString(query)}";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
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

    private static string ShortenTitle(string title) =>
        title.Length > 42 ? title[..42] + "…" : title;

    private static string FormatElapsed(TimeSpan ts) =>
        ts.TotalHours >= 1 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L) return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        if (bytes >= 1024L * 1024L) return $"{bytes / (1024.0 * 1024.0):F1} MB";
        if (bytes >= 1024L) return $"{bytes / 1024.0:F0} KB";
        return $"{bytes} B";
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
        "NET" or "NETWORKING" => "Ağ / Wi-Fi",
        "MEDIA" or "AUDIO" => "Ses / Mikrofon",
        "BLUETOOTH" => "Bluetooth",
        "SCSIADAPTER" or "DISKDRIVE" => "Depolama / NVMe",
        "USB" => "USB Denetleyici",
        "SYSTEM" => "Anakart / Chipset",
        "EXTENSION" => "Eklenti Sürücüsü",
        "OTHERHARDWARE" => "Çevre Birimi",
        _ => cls
    };
}
