using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KochanSentinel.Core.Interop;
using KochanSentinel.Core.Models;

namespace KochanSentinel.Core.Services;

/// <summary>
/// Maps live active outbound TCP connections (IPv4 + IPv6) with cached GeoIP country/ISP metadata,
/// highlights suspicious C2 ports, and dumps the local Windows DNS resolver cache.
/// </summary>
public sealed class NetworkShieldService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private static readonly ConcurrentDictionary<string, string> GeoCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<int> RiskyPorts = new() { 4444, 5555, 6666, 7777, 8888, 1337, 31337, 1604, 3333, 14444 };

    public async Task<List<NetworkConnectionEntry>> ScanExternalConnectionsAsync(CancellationToken ct = default)
    {
        var entries = await Task.Run(() =>
        {
            var procMeta = new Dictionary<int, (string Name, string Path)>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    string path = "";
                    try { path = p.MainModule?.FileName ?? ""; } catch { }
                    procMeta[p.Id] = (p.ProcessName, path);
                }
                catch { }
                finally { p.Dispose(); }
            }

            var list = new List<NetworkConnectionEntry>();
            CollectSockets(list, procMeta, isIPv6: false);
            CollectSockets(list, procMeta, isIPv6: true);
            return list
                .GroupBy(x => $"{x.Pid}|{x.RemoteIp}|{x.RemotePort}")
                .Select(g => g.First())
                .Take(120)
                .ToList();
        }, ct);

        // Concurrent GeoIP resolution for distinct public IPs (capped cleanly)
        var publicIps = entries
            .Select(e => e.RemoteIp)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(ip => !IsPrivateOrLocalIp(ip) && !GeoCache.ContainsKey(ip))
            .Take(16)
            .ToList();

        if (publicIps.Count > 0)
        {
            await Task.WhenAll(publicIps.Select(ip => ResolveGeoIpAsync(ip, ct)));
        }

        foreach (var e in entries)
        {
            if (IsPrivateOrLocalIp(e.RemoteIp))
                e.CountryOrOrg = "Yerel Ağ (LAN)";
            else if (GeoCache.TryGetValue(e.RemoteIp, out var geo))
                e.CountryOrOrg = geo;
            else
                e.CountryOrOrg = "Dış IP";

            bool riskyFolder = e.ExecutablePath.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase) ||
                               e.ExecutablePath.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase);

            if (RiskyPorts.Contains(e.RemotePort))
            {
                e.IsSuspicious = true;
                e.Severity = FindingSeverity.Critical;
                e.Reason = $"Şüpheli RAT / C2 / Madenci portu (:{e.RemotePort})";
            }
            else if (riskyFolder && !IsPrivateOrLocalIp(e.RemoteIp))
            {
                e.IsSuspicious = true;
                e.Severity = FindingSeverity.Medium;
                e.Reason = "Temp/AppData dizininden dış ağ bağlantısı";
            }
            else
            {
                e.Reason = "Aktif TCP oturumu";
            }
        }

        return entries
            .OrderByDescending(e => e.Severity)
            .ThenBy(e => e.ProcessName)
            .ToList();
    }

    private static async Task ResolveGeoIpAsync(string ip, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync($"http://ip-api.com/json/{Uri.EscapeDataString(ip)}?fields=status,countryCode,country,isp", ct);
            if (!resp.IsSuccessStatusCode) return;
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("status", out var st) && st.GetString() == "success")
            {
                string cc = doc.RootElement.TryGetProperty("countryCode", out var cEl) ? (cEl.GetString() ?? "") : "";
                string isp = doc.RootElement.TryGetProperty("isp", out var iEl) ? (iEl.GetString() ?? "") : "";
                GeoCache[ip] = string.IsNullOrEmpty(isp) ? cc : $"[{cc}] {isp}";
            }
        }
        catch { }
    }

    private static void CollectSockets(
        List<NetworkConnectionEntry> list,
        Dictionary<int, (string Name, string Path)> procMeta,
        bool isIPv6)
    {
        int af = isIPv6 ? NativeMethods.AF_INET6 : NativeMethods.AF_INET;
        int size = 0;
        NativeMethods.GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, NativeMethods.TCP_TABLE_OWNER_PID_ALL);
        if (size <= 0) return;

        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (NativeMethods.GetExtendedTcpTable(buf, ref size, false, af, NativeMethods.TCP_TABLE_OWNER_PID_ALL) != 0)
                return;

            int rows = Marshal.ReadInt32(buf);
            IntPtr ptr = buf + 4;

            if (!isIPv6)
            {
                int rowSize = Marshal.SizeOf<NativeMethods.MIB_TCPROW_OWNER_PID>();
                for (int i = 0; i < rows; i++)
                {
                    var r = Marshal.PtrToStructure<NativeMethods.MIB_TCPROW_OWNER_PID>(ptr);
                    ptr += rowSize;
                    if (r.dwState != 5 || r.dwRemoteAddr == 0 || r.dwRemoteAddr == 0x0100007F) continue;

                    int pid = (int)r.dwOwningPid;
                    int port = (ushort)IPAddress.NetworkToHostOrder((short)r.dwRemotePort);
                    procMeta.TryGetValue(pid, out var meta);
                    list.Add(new NetworkConnectionEntry
                    {
                        Pid = pid,
                        ProcessName = string.IsNullOrEmpty(meta.Name) ? $"PID {pid}" : meta.Name,
                        ExecutablePath = meta.Path ?? "",
                        RemoteIp = new IPAddress(r.dwRemoteAddr).ToString(),
                        RemotePort = port
                    });
                }
            }
            else
            {
                int rowSize = Marshal.SizeOf<NativeMethods.MIB_TCP6ROW_OWNER_PID>();
                for (int i = 0; i < rows; i++)
                {
                    var r = Marshal.PtrToStructure<NativeMethods.MIB_TCP6ROW_OWNER_PID>(ptr);
                    ptr += rowSize;
                    if (r.dwState != 5 || r.ucRemoteAddr == null) continue;

                    var ip = new IPAddress(r.ucRemoteAddr);
                    if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None))
                        continue;

                    int pid = (int)r.dwOwningPid;
                    int port = (ushort)IPAddress.NetworkToHostOrder((short)r.dwRemotePort);
                    procMeta.TryGetValue(pid, out var meta);
                    list.Add(new NetworkConnectionEntry
                    {
                        Pid = pid,
                        ProcessName = string.IsNullOrEmpty(meta.Name) ? $"PID {pid}" : meta.Name,
                        ExecutablePath = meta.Path ?? "",
                        RemoteIp = ip.ToString(),
                        RemotePort = port
                    });
                }
            }
        }
        catch { }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public async Task<List<DnsCacheEntry>> ReadDnsCacheAsync()
    {
        return await Task.Run(() =>
        {
            var list = new List<DnsCacheEntry>();
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -Command \"Get-DnsClientCache | Select-Object -First 80 Entry,Data,Type | ConvertTo-Json -Compress\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                };
                using var p = Process.Start(psi);
                if (p == null) return list;
                var json = p.StandardOutput.ReadToEnd();
                p.WaitForExit(4500);
                if (string.IsNullOrWhiteSpace(json)) return list;

                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                        AddDnsElement(el, list);
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    AddDnsElement(doc.RootElement, list);
                }
            }
            catch { }
            return list;
        });
    }

    private static void AddDnsElement(JsonElement el, List<DnsCacheEntry> list)
    {
        var domain = el.TryGetProperty("Entry", out var eEl) ? (eEl.GetString() ?? "") : "";
        var data = el.TryGetProperty("Data", out var dEl) ? (dEl.GetString() ?? "") : "";
        var type = el.TryGetProperty("Type", out var tEl) ? tEl.ToString() : "";
        if (string.IsNullOrWhiteSpace(domain)) return;

        bool riskyTld = domain.EndsWith(".onion", StringComparison.OrdinalIgnoreCase) ||
                        domain.EndsWith(".tk", StringComparison.OrdinalIgnoreCase) ||
                        domain.Contains("duckdns.org", StringComparison.OrdinalIgnoreCase) ||
                        domain.Contains("ngrok", StringComparison.OrdinalIgnoreCase) ||
                        domain.Contains("pastebin", StringComparison.OrdinalIgnoreCase);

        list.Add(new DnsCacheEntry
        {
            Domain = domain,
            RecordData = data,
            RecordType = type switch { "1" => "A (IPv4)", "28" => "AAAA (IPv6)", "5" => "CNAME", _ => type },
            IsSuspicious = riskyTld
        });
    }

    private static bool IsPrivateOrLocalIp(string ipStr)
    {
        if (!IPAddress.TryParse(ipStr, out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        var bytes = ip.GetAddressBytes();
        if (bytes.Length == 4)
        {
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 169 && bytes[1] == 254);
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal;
    }
}
