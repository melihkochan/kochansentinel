using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using KochanSentinel.Core.Models;

namespace KochanSentinel.Core.Services;

/// <summary>
/// One-click Secure DNS & Windows 11 DoH Switcher (Cloudflare, AdGuard, Quad9, Google, or DHCP Auto).
/// </summary>
public sealed class DnsSwitcherService
{
    public static List<DnsProfileItem> GetProfiles() => new()
    {
        new()
        {
            Id = "auto",
            Name = "Otomatik (ISP / DHCP)",
            Description = "İnternet sağlayıcınızın varsayılan DNS ayarlarına güvenle geri döner.",
            IsAutomatic = true
        },
        new()
        {
            Id = "cloudflare",
            Name = "Cloudflare Hızlı & Oyun (1.1.1.1)",
            Description = "En düşük ping ve en hızlı DNS çözümlemesi. Oyun & günlük kullanım için ideal.",
            PrimaryDns = "1.1.1.1",
            SecondaryDns = "1.0.0.1",
            DohTemplate = "https://cloudflare-dns.com/dns-query"
        },
        new()
        {
            Id = "cloudflare_sec",
            Name = "Cloudflare Zararlı Korumalı (1.1.1.2)",
            Description = "Bilinen zararlı yazılım ve oltalama (phishing) alan adlarını DNS katmanında engeller.",
            PrimaryDns = "1.1.1.2",
            SecondaryDns = "1.0.0.2",
            DohTemplate = "https://security.cloudflare-dns.com/dns-query"
        },
        new()
        {
            Id = "adguard",
            Name = "AdGuard Reklam & İzleyici Engelleyici",
            Description = "Sistem genelinde reklamları, takipçileri ve zararlı alan adlarını otomatik filtreler.",
            PrimaryDns = "94.140.14.14",
            SecondaryDns = "94.140.15.15",
            DohTemplate = "https://dns.adguard-dns.com/dns-query"
        },
        new()
        {
            Id = "quad9",
            Name = "Quad9 Tehdit Kalkanı (9.9.9.9)",
            Description = "Siber tehdit istihbaratı ile zararlı C2/botnet alan adlarını anında bloklar.",
            PrimaryDns = "9.9.9.9",
            SecondaryDns = "149.112.112.112",
            DohTemplate = "https://dns.quad9.net/dns-query"
        },
        new()
        {
            Id = "google",
            Name = "Google Public DNS (8.8.8.8)",
            Description = "Yüksek kararlılık ve geniş global önbellek ağı.",
            PrimaryDns = "8.8.8.8",
            SecondaryDns = "8.8.4.4",
            DohTemplate = "https://dns.google/dns-query"
        }
    };

    public List<string> GetActiveAdapterNames()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                            !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                .Select(n => n.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    public string GetCurrentDnsSummary(string adapterName)
    {
        try
        {
            var ni = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase));
            if (ni == null) return "Bağdaştırıcı seçilmedi";

            var ips = ni.GetIPProperties().DnsAddresses
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                .Select(ip => ip.ToString())
                .ToList();

            return ips.Count > 0 ? string.Join(", ", ips) : "Otomatik / Algılanmadı";
        }
        catch
        {
            return "Okunamadı";
        }
    }

    public async Task<(bool Ok, string Message)> ApplyProfileAsync(string adapterName, DnsProfileItem profile, bool enableDoh = true)
    {
        if (string.IsNullOrWhiteSpace(adapterName))
            return (false, "Lütfen önce bir ağ bağdaştırıcısı seçin.");

        return await Task.Run(() =>
        {
            try
            {
                string safeName = adapterName.Replace("\"", "");
                if (profile.IsAutomatic)
                {
                    bool ok = RunProcess("netsh.exe", $"interface ip set dns name=\"{safeName}\" source=dhcp");
                    RunProcess("ipconfig.exe", "/flushdns");
                    return ok
                        ? (true, $"\"{adapterName}\" için DNS otomatik (DHCP) moda döndürüldü ve önbellek temizlendi.")
                        : (false, "DNS değiştirilemedi (Yönetici izni gerekir).");
                }

                bool ok1 = RunProcess("netsh.exe", $"interface ip set dns name=\"{safeName}\" static {profile.PrimaryDns} validate=no");
                if (!ok1)
                    return (false, $"Birincil DNS ({profile.PrimaryDns}) uygulanamadı.");

                if (!string.IsNullOrWhiteSpace(profile.SecondaryDns))
                    RunProcess("netsh.exe", $"interface ip add dns name=\"{safeName}\" {profile.SecondaryDns} index=2 validate=no");

                // Register Windows 11 DoH (DNS-over-HTTPS) template if available
                if (enableDoh && !string.IsNullOrWhiteSpace(profile.DohTemplate))
                {
                    RunProcess("netsh.exe", $"dns add encryption server={profile.PrimaryDns} dohtemplate={profile.DohTemplate} autoupgrade=yes udpfallback=no");
                    if (!string.IsNullOrWhiteSpace(profile.SecondaryDns))
                        RunProcess("netsh.exe", $"dns add encryption server={profile.SecondaryDns} dohtemplate={profile.DohTemplate} autoupgrade=yes udpfallback=no");
                }

                RunProcess("ipconfig.exe", "/flushdns");
                return (true, $"\"{adapterName}\" → {profile.Name} ({profile.PrimaryDns}) uygulandı ve DNS önbelleği temizlendi.");
            }
            catch (Exception ex)
            {
                return (false, $"DNS hatası: {ex.Message}");
            }
        });
    }

    private static bool RunProcess(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
