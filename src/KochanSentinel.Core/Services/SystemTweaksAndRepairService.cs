using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KochanSentinel.Core.Models;
using Microsoft.Win32;

namespace KochanSentinel.Core.Services;

/// <summary>
/// Handles Windows System Restore Points, SFC/DISM system repair, safe reversible
/// gaming/privacy registry tweaks, and optional GitHub Release update checks.
/// </summary>
public sealed class SystemTweaksAndRepairService
{
    public static string CurrentAppVersion => UpdateService.GetCurrentAppVersion();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, int[] pvParam, uint fWinIni);

    public async Task<(bool Ok, string Message)> CreateRestorePointAsync(string description)
    {
        return await Task.Run(() =>
        {
            try
            {
                // Allow back-to-back restore points cleanly if permitted by Windows
                try
                {
                    using var srKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", writable: true);
                    srKey?.SetValue("SystemRestorePointCreationFrequency", 0, RegistryValueKind.DWord);
                }
                catch { }

                var scope = new ManagementScope(@"\\.\root\default");
                scope.Connect();
                using var srClass = new ManagementClass(scope, new ManagementPath("SystemRestore"), new ObjectGetOptions());
                using var inParams = srClass.GetMethodParameters("CreateRestorePoint");
                inParams["Description"] = description;
                inParams["RestorePointType"] = 12; // MODIFY_SETTINGS
                inParams["EventType"] = 100;        // BEGIN_SYSTEM_CHANGE

                using var outParams = srClass.InvokeMethod("CreateRestorePoint", inParams, null);
                uint ret = outParams?["ReturnValue"] != null ? Convert.ToUInt32(outParams["ReturnValue"]) : 1;
                if (ret == 0)
                    return (true, $"Sistem Geri Yükleme Noktası oluşturuldu: \"{description}\"");
                return (false, $"Geri Yükleme Noktası oluşturulamadı (Kod: {ret} — Windows Sistem Koruması kapalı olabilir).");
            }
            catch (Exception ex)
            {
                return (false, $"Geri yükleme noktası hatası: {ex.Message}");
            }
        });
    }

    public List<SystemTweakItem> LoadTweaksState()
    {
        return new List<SystemTweakItem>
        {
            new()
            {
                Id = "mouse_accel",
                Category = "Oyuncu",
                Name = "Fare İvmesini Kapat (1:1 Raw Aim)",
                Description = "Windows 'İşaretçi hassasiyetini artır' ayarını kapatarak FPS oyunlarında kas hafızası tutarlılığı sağlar.",
                IsApplied = IsMouseAccelDisabled()
            },
            new()
            {
                Id = "game_dvr",
                Category = "Oyuncu",
                Name = "Xbox Game DVR Arka Plan Kaydını Kapat",
                Description = "Oyun sırasında gizli arka plan video yakalama yükünü durdurarak %1-low FPS'i iyileştirir.",
                IsApplied = IsGameDvrDisabled()
            },
            new()
            {
                Id = "net_throttle",
                Category = "Ağ & Oyun",
                Name = "Network Throttling İyileştirmesi",
                Description = "Multimedya/oyun oynatılırken Windows ağ paket hız sınırını kaldırır (düşük ping dalgalanması).",
                IsApplied = IsNetworkThrottlingDisabled()
            },
            new()
            {
                Id = "hags",
                Category = "GPU & Gecikme",
                Name = "Donanım Hızlandırmalı GPU Zamanlama (HAGS)",
                Description = "Ekran kartı bellek yönetimini GPU'ya devreder (değişiklik için Windows yeniden başlatılmalıdır).",
                IsApplied = IsHagsEnabled()
            },
            new()
            {
                Id = "privacy_ad",
                Category = "Gizlilik",
                Name = "Windows Reklam Kimliği & Telemetri Azaltma",
                Description = "Uygulamalar arası reklam izleme kimliğini kapatır ve Windows tanı verisi toplama seviyesini düşürür.",
                IsApplied = IsPrivacyHardened()
            }
        };
    }

    public (bool Ok, string Message) ToggleTweak(string id)
    {
        try
        {
            switch (id)
            {
                case "mouse_accel":
                {
                    bool disable = !IsMouseAccelDisabled();
                    using var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Mouse");
                    key.SetValue("MouseSpeed", disable ? "0" : "1", RegistryValueKind.String);
                    key.SetValue("MouseThreshold1", disable ? "0" : "6", RegistryValueKind.String);
                    key.SetValue("MouseThreshold2", disable ? "0" : "10", RegistryValueKind.String);
                    int[] parms = disable ? new[] { 0, 0, 0 } : new[] { 6, 10, 1 };
                    SystemParametersInfo(0x0004 /* SPI_SETMOUSE */, 0, parms, 0x01 | 0x02);
                    return (true, disable ? "Fare ivmesi (Enhance Pointer Precision) kapatıldı." : "Fare ivmesi varsayılana döndürüldü.");
                }
                case "game_dvr":
                {
                    bool disable = !IsGameDvrDisabled();
                    using var k1 = Registry.CurrentUser.CreateSubKey(@"System\GameConfigStore");
                    k1.SetValue("GameDVR_Enabled", disable ? 0 : 1, RegistryValueKind.DWord);
                    using var k2 = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\GameDVR");
                    k2.SetValue("AppCaptureEnabled", disable ? 0 : 1, RegistryValueKind.DWord);
                    return (true, disable ? "Game DVR arka plan kaydı kapatıldı." : "Game DVR varsayılana döndürüldü.");
                }
                case "net_throttle":
                {
                    bool disable = !IsNetworkThrottlingDisabled();
                    using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile");
                    key.SetValue("NetworkThrottlingIndex", disable ? unchecked((int)0xFFFFFFFF) : 10, RegistryValueKind.DWord);
                    return (true, disable ? "Ağ paket throttling sınırı kaldırıldı." : "Ağ throttling varsayılana (10) döndürüldü.");
                }
                case "hags":
                {
                    bool enable = !IsHagsEnabled();
                    using var key = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
                    key.SetValue("HwSchMode", enable ? 2 : 1, RegistryValueKind.DWord);
                    return (true, enable ? "HAGS açıldı (etkinleşmesi için yeniden başlatma gerekir)." : "HAGS kapatıldı (etkinleşmesi için yeniden başlatma gerekir).");
                }
                case "privacy_ad":
                {
                    bool harden = !IsPrivacyHardened();
                    using var kAd = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo");
                    kAd.SetValue("Enabled", harden ? 0 : 1, RegistryValueKind.DWord);
                    using var kTel = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection");
                    kTel.SetValue("AllowTelemetry", harden ? 1 : 3, RegistryValueKind.DWord);
                    return (true, harden ? "Reklam izleme kimliği kapatıldı ve telemetri azaltıldı." : "Gizlilik/Telemetri ayarları varsayılana döndürüldü.");
                }
            }
        }
        catch (Exception ex)
        {
            return (false, $"Ayar değiştirilemedi: {ex.Message}");
        }
        return (false, "Bilinmeyen ayar.");
    }

    public async Task<int> RunSystemRepairAsync(bool useDism, IProgress<string> log, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            try
            {
                string file = useDism ? "DISM.exe" : "sfc.exe";
                string args = useDism ? "/Online /Cleanup-Image /RestoreHealth" : "/scannow";
                var psi = new ProcessStartInfo(file, args)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = useDism ? Encoding.UTF8 : Encoding.Unicode
                };
                using var p = Process.Start(psi);
                if (p == null) return -1;

                using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
                while (!p.StandardOutput.EndOfStream)
                {
                    var line = p.StandardOutput.ReadLine();
                    if (!string.IsNullOrWhiteSpace(line))
                        log.Report(line.Replace("\0", "").Trim());
                }
                p.WaitForExit();
                return p.ExitCode;
            }
            catch (Exception ex)
            {
                log.Report($"Hata: {ex.Message}");
                return -1;
            }
        }, ct);
    }

    public async Task<(bool HasUpdate, string LatestVersion, string ReleaseUrl, string Message)> CheckGitHubUpdateAsync(string repoSlug)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(repoSlug)) repoSlug = "melihkochan/kochansentinel";
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repoSlug.Trim().Trim('/')}/releases/latest");
            req.Headers.Add("User-Agent", $"KochanSentinel/{CurrentAppVersion}");
            using var resp = await Http.SendAsync(req);

            if ((int)resp.StatusCode == 404)
                return (false, CurrentAppVersion, $"https://github.com/{repoSlug}", $"Güncel sürüm kullanılıyor (v{CurrentAppVersion}). Henüz GitHub Release yayınlanmamış.");
            if (!resp.IsSuccessStatusCode)
                return (false, CurrentAppVersion, "", $"GitHub sürüm kontrolü başarısız (HTTP {(int)resp.StatusCode}).");

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.TryGetProperty("tag_name", out var tEl) ? (tEl.GetString() ?? "") : "";
            var htmlUrl = doc.RootElement.TryGetProperty("html_url", out var uEl) ? (uEl.GetString() ?? "") : "";

            var cleanTag = tag.TrimStart('v', 'V');
            bool newer = Version.TryParse(cleanTag, out var remoteVer) &&
                         Version.TryParse(CurrentAppVersion, out var curVer) &&
                         remoteVer > curVer;

            return newer
                ? (true, tag, htmlUrl, $"Yeni sürüm mevcut: {tag} (Mevcut: v{CurrentAppVersion})")
                : (false, tag, htmlUrl, $"Uygulama güncel (v{CurrentAppVersion}).");
        }
        catch (Exception ex)
        {
            return (false, CurrentAppVersion, "", $"Bağlantı kurulamadı: {ex.Message}");
        }
    }

    private static bool IsMouseAccelDisabled()
    {
        try { return Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse")?.GetValue("MouseSpeed")?.ToString() == "0"; }
        catch { return false; }
    }

    private static bool IsGameDvrDisabled()
    {
        try { return Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore")?.GetValue("GameDVR_Enabled") is int v && v == 0; }
        catch { return false; }
    }

    private static bool IsNetworkThrottlingDisabled()
    {
        try
        {
            var val = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile")?.GetValue("NetworkThrottlingIndex");
            return val is int iv && iv == -1;
        }
        catch { return false; }
    }

    private static bool IsHagsEnabled()
    {
        try { return Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers")?.GetValue("HwSchMode") is int v && v == 2; }
        catch { return false; }
    }

    private static bool IsPrivacyHardened()
    {
        try { return Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo")?.GetValue("Enabled") is int v && v == 0; }
        catch { return false; }
    }
}
