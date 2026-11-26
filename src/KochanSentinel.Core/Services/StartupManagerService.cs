using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using KochanSentinel.Core.Interop;
using KochanSentinel.Core.Models;
using Microsoft.Win32;

namespace KochanSentinel.Core.Services;

/// <summary>
/// Reads Windows Boot Performance Diagnostic Event Log (Event ID 100) and manages
/// all Windows Startup entries cleanly using Microsoft's StartupApproved binary flags
/// (non-destructive enable/disable, synced with Windows Task Manager).
/// </summary>
public sealed class StartupManagerService
{
    public async Task<BootPerformanceInfo> ReadLastBootPerformanceAsync()
    {
        return await Task.Run(() =>
        {
            var info = new BootPerformanceInfo();
            try
            {
                var query = new EventLogQuery(
                    "Microsoft-Windows-Diagnostics-Performance/Operational",
                    PathType.LogName,
                    "*[System[(EventID=100)]]")
                {
                    ReverseDirection = true
                };

                using var reader = new EventLogReader(query);
                using var rec = reader.ReadEvent();
                if (rec != null)
                {
                    var xml = XDocument.Parse(rec.ToXml());
                    XNamespace ns = xml.Root?.Name.Namespace ?? XNamespace.None;
                    var dataNodes = xml.Descendants(ns + "Data")
                        .ToDictionary(
                            x => x.Attribute("Name")?.Value ?? "",
                            x => x.Value,
                            StringComparer.OrdinalIgnoreCase);

                    if (dataNodes.TryGetValue("BootTime", out var btStr) && double.TryParse(btStr, out double btMs))
                        info.LastBootSeconds = Math.Round(btMs / 1000.0, 1);
                    if (dataNodes.TryGetValue("MainPathBootTime", out var mpStr) && double.TryParse(mpStr, out double mpMs))
                        info.MainPathSeconds = Math.Round(mpMs / 1000.0, 1);
                    if (dataNodes.TryGetValue("BootPostBootTime", out var pbStr) && double.TryParse(pbStr, out double pbMs))
                        info.PostBootSeconds = Math.Round(pbMs / 1000.0, 1);

                    info.BootTime = rec.TimeCreated;
                    if (info.LastBootSeconds.HasValue)
                    {
                        string grade = info.LastBootSeconds.Value <= 18 ? "Çok Hızlı" :
                                       info.LastBootSeconds.Value <= 35 ? "Normal" : "Yavaş (Başlangıç Temizliği Önerilir)";
                        info.SummaryText = $"Son Windows Açılış Süresi: {info.LastBootSeconds:F1} sn ({grade})  •  Çekirdek: {info.MainPathSeconds ?? 0:F1} sn  •  Masaüstü Yükleme: {info.PostBootSeconds ?? 0:F1} sn";
                    }
                }
            }
            catch { }
            return info;
        });
    }

    public async Task<List<StartupAppItem>> ScanStartupAppsAsync()
    {
        return await Task.Run(() =>
        {
            var items = new List<StartupAppItem>();

            ScanRunKey(items, Registry.CurrentUser, "HKCU",
                @"Software\Microsoft\Windows\CurrentVersion\Run",
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");

            ScanRunKey(items, Registry.LocalMachine, "HKLM",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");

            ScanRunKey(items, Registry.LocalMachine, "HKLM",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32");

            ScanStartupFolder(items,
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                "HKCU",
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder");

            return items
                .OrderByDescending(i => i.IsEnabled)
                .ThenBy(i => i.Name)
                .ToList();
        });
    }

    private static void ScanRunKey(List<StartupAppItem> items, RegistryKey root, string hiveName, string runSubKey, string approvedSubKey)
    {
        try
        {
            using var runKey = root.OpenSubKey(runSubKey, false);
            if (runKey == null) return;

            using var approvedKey = root.OpenSubKey(approvedSubKey, false);
            foreach (var valName in runKey.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(valName)) continue;
                var cmd = runKey.GetValue(valName)?.ToString() ?? "";
                var exe = ExtractExe(cmd);
                bool enabled = ReadStartupApprovedState(approvedKey, valName);
                bool signed = !string.IsNullOrEmpty(exe) && File.Exists(exe) && NativeMethods.VerifyFileOrCatalogSignature(exe);

                items.Add(new StartupAppItem
                {
                    Id = $"{hiveName}|{runSubKey}|{valName}",
                    Name = valName,
                    Command = cmd,
                    ExecutablePath = exe,
                    Source = $"{hiveName} Run",
                    RegistryHive = hiveName,
                    RegistrySubKey = runSubKey,
                    ApprovedSubKey = approvedSubKey,
                    IsEnabled = enabled,
                    IsSigned = signed,
                    ImpactLevel = EstimateImpact(valName, exe)
                });
            }
        }
        catch { }
    }

    private static void ScanStartupFolder(List<StartupAppItem> items, string folderPath, string hiveName, string approvedSubKey)
    {
        if (!Directory.Exists(folderPath)) return;
        try
        {
            using var approvedKey = Registry.CurrentUser.OpenSubKey(approvedSubKey, false);
            foreach (var file in Directory.EnumerateFiles(folderPath))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                bool enabled = ReadStartupApprovedState(approvedKey, fileName);
                items.Add(new StartupAppItem
                {
                    Id = $"FOLDER|{fileName}",
                    Name = fileName,
                    Command = file,
                    ExecutablePath = file,
                    Source = "Başlangıç Klasörü",
                    RegistryHive = hiveName,
                    RegistrySubKey = folderPath,
                    ApprovedSubKey = approvedSubKey,
                    IsEnabled = enabled,
                    IsSigned = File.Exists(file) && NativeMethods.VerifyFileOrCatalogSignature(file),
                    ImpactLevel = EstimateImpact(fileName, file)
                });
            }
        }
        catch { }
    }

    public (bool Ok, string Message) SetStartupEnabled(StartupAppItem item, bool enable)
    {
        try
        {
            var root = item.RegistryHive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
            using var approvedKey = root.CreateSubKey(item.ApprovedSubKey);
            if (approvedKey == null)
                return (false, "StartupApproved anahtarına yazılamadı.");

            // Windows StartupApproved format: 12 bytes, first byte 0x02 = Enabled, 0x03 = Disabled
            byte[] data = new byte[12];
            data[0] = enable ? (byte)0x02 : (byte)0x03;
            if (!enable)
            {
                var ft = BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc());
                Buffer.BlockCopy(ft, 0, data, 4, 8);
            }

            approvedKey.SetValue(item.Name, data, RegistryValueKind.Binary);
            item.IsEnabled = enable;
            return (true, $"{item.Name} başlangıçta {(enable ? "etkinleştirildi" : "devre dışı bırakıldı")}.");
        }
        catch (Exception ex)
        {
            return (false, $"Başlangıç ayarı değiştirilemedi: {ex.Message}");
        }
    }

    private static bool ReadStartupApprovedState(RegistryKey? approvedKey, string name)
    {
        try
        {
            if (approvedKey?.GetValue(name) is byte[] bytes && bytes.Length > 0)
            {
                // Even first byte (0x02, 0x06) => Enabled; Odd (0x03, 0x07) => Disabled
                return (bytes[0] & 0x01) == 0;
            }
        }
        catch { }
        return true;
    }

    private static string EstimateImpact(string name, string exePath)
    {
        string combined = $"{name} {exePath}".ToLowerInvariant();
        if (combined.Contains("steam") || combined.Contains("epic") || combined.Contains("discord") ||
            combined.Contains("teams") || combined.Contains("adobe") || combined.Contains("spotify") ||
            combined.Contains("onedrive") || combined.Contains("riot") || combined.Contains("razer") ||
            combined.Contains("icue") || combined.Contains("armoury"))
            return "Yüksek";

        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
        {
            try
            {
                long sz = new FileInfo(exePath).Length;
                if (sz > 35 * 1024 * 1024) return "Yüksek";
                if (sz > 5 * 1024 * 1024) return "Orta";
            }
            catch { }
        }
        return "Düşük";
    }

    private static string ExtractExe(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return "";
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            if (end > 1) return Environment.ExpandEnvironmentVariables(cmd[1..end]);
        }
        int idx = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (idx > 0) return Environment.ExpandEnvironmentVariables(cmd[..(idx + 4)]);
        return Environment.ExpandEnvironmentVariables(cmd.Split(' ')[0]);
    }
}
