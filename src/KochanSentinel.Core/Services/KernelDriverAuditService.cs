using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using KochanSentinel.Core.Interop;
using KochanSentinel.Core.Models;

namespace KochanSentinel.Core.Services;

/// <summary>
/// Audits loaded Windows kernel (.sys) drivers against BYOVD (Bring Your Own Vulnerable Driver)
/// attacks, unsigned drivers, and drivers loaded outside System32\drivers.
/// </summary>
public sealed class KernelDriverAuditService
{
    // Well-known BYOVD vulnerable drivers frequently abused by cheats/rootkits to gain kernel R/W
    private static readonly Dictionary<string, string> KnownVulnerableByovd = new(StringComparer.OrdinalIgnoreCase)
    {
        ["iqvw64e.sys"] = "Intel Network Driver (CVE-2015-2291 - kdmapper BYOVD sömürüsü!)",
        ["RTCore64.sys"] = "MSI Afterburner Driver (CVE-2019-16098 - Fiziksel bellek okuma/yazma açığı!)",
        ["dbutil_2_3.sys"] = "Dell BIOS Utility Driver (CVE-2021-21551 - Çekirdek yetki yükseltme!)",
        ["gdrv.sys"] = "Gigabyte Driver (CVE-2018-19320 - BYOVD bellek haritalama açığı!)",
        ["AsUpIO.sys"] = "ASUS Driver (Bilinen BYOVD çekirdek açığı)",
        ["ene.sys"] = "ENE RGB Driver (CVE-2020-12446 - Çekirdek port/bellek açığı)",
        ["mhyprot2.sys"] = "Anti-Cheat sürücüsü (Zararlılar tarafından AV öldürmek için BYOVD amaçlı kullanılır)",
        ["zam64.sys"] = "Zemana Driver (BYOVD EDR-Killer saldırılarında yaygın kullanılır!)",
        ["zamguard64.sys"] = "Zemana Guard Driver (BYOVD EDR-Killer saldırılarında kullanılır!)",
        ["dbk64.sys"] = "Cheat Engine Kernel Driver",
        ["phymemx64.sys"] = "Fiziksel Bellek Erişim Sürücüsü (BYOVD)",
        ["winio64.sys"] = "WinIO Doğrudan Port/Bellek Sürücüsü"
    };

    public async Task<List<KernelDriverItem>> ScanLoadedDriversAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var list = new List<KernelDriverItem>();
            var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var stdDriversDir = Path.Combine(winDir, "System32", "drivers");

            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, DisplayName, PathName, State FROM Win32_SystemDriver");
                foreach (ManagementObject obj in searcher.Get())
                {
                    ct.ThrowIfCancellationRequested();
                    var name = obj["Name"]?.ToString() ?? "";
                    var display = obj["DisplayName"]?.ToString() ?? name;
                    var rawPath = obj["PathName"]?.ToString() ?? "";
                    var state = obj["State"]?.ToString() ?? "";

                    if (!state.Equals("Running", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(rawPath))
                        continue;

                    var resolvedPath = ResolveDriverPath(rawPath, winDir);
                    if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
                        continue;

                    var fileName = Path.GetFileName(resolvedPath);
                    bool outsideStd = !resolvedPath.StartsWith(stdDriversDir, StringComparison.OrdinalIgnoreCase) &&
                                      !resolvedPath.StartsWith(winDir, StringComparison.OrdinalIgnoreCase);

                    bool isByovd = KnownVulnerableByovd.TryGetValue(fileName, out var byovdDesc);
                    bool isSensorRing0 = fileName.Contains("WinRing0", StringComparison.OrdinalIgnoreCase) ||
                                         name.Contains("LibreHardwareMonitor", StringComparison.OrdinalIgnoreCase);

                    // Fast check signature for non-System32 or suspicious/stopped drivers
                    bool signed = (!outsideStd && !isByovd && !isSensorRing0)
                        ? true // System32 WHQL drivers checked on demand or if outside/known vulnerable
                        : NativeMethods.VerifyFileOrCatalogSignature(resolvedPath);

                    if (!signed && !outsideStd && !isByovd)
                        signed = NativeMethods.VerifyFileOrCatalogSignature(resolvedPath);

                    if (isByovd)
                    {
                        list.Add(new KernelDriverItem
                        {
                            Name = fileName,
                            DisplayName = display,
                            ImagePath = resolvedPath,
                            State = state,
                            IsSigned = signed,
                            IsOutsideSystem32 = outsideStd,
                            IsKnownVulnerableByovd = true,
                            Severity = FindingSeverity.Critical,
                            Reason = $"BYOVD UYARISI: {byovdDesc}"
                        });
                    }
                    else if (!signed && !isSensorRing0)
                    {
                        list.Add(new KernelDriverItem
                        {
                            Name = fileName,
                            DisplayName = display,
                            ImagePath = resolvedPath,
                            State = state,
                            IsSigned = false,
                            IsOutsideSystem32 = outsideStd,
                            Severity = state.Equals("Running", StringComparison.OrdinalIgnoreCase) ? FindingSeverity.Critical : FindingSeverity.High,
                            Reason = "İmzasız Kernel (.sys) sürücüsü!"
                        });
                    }
                    else if (outsideStd || isSensorRing0)
                    {
                        list.Add(new KernelDriverItem
                        {
                            Name = fileName,
                            DisplayName = display,
                            ImagePath = resolvedPath,
                            State = state,
                            IsSigned = signed,
                            IsOutsideSystem32 = outsideStd,
                            Severity = isSensorRing0 ? FindingSeverity.Low : FindingSeverity.Medium,
                            Reason = isSensorRing0
                                ? "Donanım sensör sürücüsü (KOCHAN / LibreHardwareMonitor ısı okuma sürücüsü)."
                                : "System32\\drivers dışından yüklenen imzalı 3. parti sürücü."
                        });
                    }
                }
            }
            catch { }

            return list
                .OrderByDescending(d => d.Severity)
                .ThenBy(d => d.Name)
                .ToList();
        }, ct);
    }

    private static string ResolveDriverPath(string raw, string winDir)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var p = raw.Trim().Trim('"');
        if (p.StartsWith(@"\??\", StringComparison.Ordinal))
            p = p[4..];
        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            p = Path.Combine(winDir, p[12..]);
        else if (p.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
            p = Path.Combine(winDir, p);
        return Environment.ExpandEnvironmentVariables(p);
    }
}
