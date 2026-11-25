using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KochanSentinel.Core.Interop;
using KochanSentinel.Core.Models;

namespace KochanSentinel.Core.Services;

/// <summary>
/// Covers USB device insertion scanning, live injected process DLL auditing,
/// high-privilege browser extension auditing, and hijacked .lnk shortcut inspection.
/// </summary>
public sealed class UsbAndModuleGuardService : IDisposable
{
    private ManagementEventWatcher? _usbWatcher;
    private readonly ConcurrentDictionary<string, bool> _sigCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] HighRiskExtPermissions =
        { "<all_urls>", "http://*/*", "https://*/*", "cookies", "webRequestBlocking", "debugger", "nativeMessaging", "clipboardRead" };

    public event Action<SecurityEvent>? OnSecurityEvent;
    public bool IsUsbShieldActive => _usbWatcher != null;

    public void StartUsbWatcher()
    {
        if (_usbWatcher != null) return;
        try
        {
            // EventType = 2 means Device Arrival
            var q = new WqlEventQuery("SELECT * FROM Win32_VolumeChangeEvent WHERE EventType = 2");
            _usbWatcher = new ManagementEventWatcher(q);
            _usbWatcher.EventArrived += (_, args) =>
            {
                try
                {
                    var driveName = args.NewEvent["DriveName"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(driveName))
                        _ = Task.Run(() => ScanRemovableDrive(driveName));
                }
                catch { }
            };
            _usbWatcher.Start();
            OnSecurityEvent?.Invoke(new SecurityEvent
            {
                Source = "USB Kalkanı",
                Message = "USB / Harici bellek otomatik zararlı taraması aktif.",
                Severity = FindingSeverity.Info
            });
        }
        catch { }
    }

    public void StopUsbWatcher()
    {
        try
        {
            _usbWatcher?.Stop();
            _usbWatcher?.Dispose();
        }
        catch { }
        _usbWatcher = null;
    }

    private void ScanRemovableDrive(string driveName)
    {
        try
        {
            var root = driveName.EndsWith('\\') ? driveName : driveName + @"\";
            if (!Directory.Exists(root)) return;

            var hits = new List<string>();
            var autorun = Path.Combine(root, "autorun.inf");
            if (File.Exists(autorun))
                hits.Add("autorun.inf");

            foreach (var file in Directory.EnumerateFiles(root))
            {
                try
                {
                    var attrs = File.GetAttributes(file);
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    bool suspiciousScript = ext is ".vbs" or ".bat" or ".scr" or ".hta" or ".ps1";
                    bool hiddenLnk = ext == ".lnk" && (attrs & FileAttributes.Hidden) != 0;
                    if (suspiciousScript || hiddenLnk || (ext == ".exe" && (attrs & FileAttributes.Hidden) != 0))
                        hits.Add(Path.GetFileName(file));
                }
                catch { }
            }

            if (hits.Count > 0)
            {
                OnSecurityEvent?.Invoke(new SecurityEvent
                {
                    Source = "USB Tehdit Uyarısı",
                    Message = $"{driveName} takılan sürücüde şüpheli solucan/autorun dosyası tespit edildi: {string.Join(", ", hits.Take(5))}",
                    Severity = FindingSeverity.Critical
                });
            }
            else
            {
                OnSecurityEvent?.Invoke(new SecurityEvent
                {
                    Source = "USB Kalkanı",
                    Message = $"{driveName} sürücüsü takıldı — kök dizin temiz görünüyor.",
                    Severity = FindingSeverity.Info
                });
            }
        }
        catch { }
    }

    public async Task<List<LoadedDllModuleInfo>> ScanInjectedDllsAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var results = new List<LoadedDllModuleInfo>();
            var seenKey = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var proc in Process.GetProcesses())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (proc.Id <= 4 || proc.Id == Environment.ProcessId) continue;

                    ProcessModuleCollection? modules = null;
                    try { modules = proc.Modules; } catch { continue; }

                    foreach (ProcessModule mod in modules)
                    {
                        var path = mod.FileName;
                        if (string.IsNullOrWhiteSpace(path)) continue;

                        if (path.StartsWith(@"C:\Windows\System32\", StringComparison.OrdinalIgnoreCase) ||
                            path.StartsWith(@"C:\Windows\SysWOW64\", StringComparison.OrdinalIgnoreCase) ||
                            path.StartsWith(@"C:\Windows\WinSxS\", StringComparison.OrdinalIgnoreCase) ||
                            path.Contains(@"\dotnet\", StringComparison.OrdinalIgnoreCase) ||
                            path.Contains("KochanSentinel", StringComparison.OrdinalIgnoreCase))
                            continue;

                        bool signed = _sigCache.GetOrAdd(path, NativeMethods.VerifyFileOrCatalogSignature);
                        if (!signed)
                        {
                            string key = $"{proc.Id}|{path}";
                            if (!seenKey.Add(key)) continue;

                            bool riskyLocation = path.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase) ||
                                                 path.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase);

                            results.Add(new LoadedDllModuleInfo
                            {
                                Pid = proc.Id,
                                ProcessName = proc.ProcessName,
                                ModuleName = mod.ModuleName ?? Path.GetFileName(path),
                                FilePath = path,
                                SizeKb = mod.ModuleMemorySize / 1024,
                                IsSigned = false,
                                Reason = riskyLocation
                                    ? "Temp/AppData dizininden yüklü imzasız DLL (Enjeksiyon Şüphesi)!"
                                    : "Süreç belleğine yüklü imzasız harici DLL."
                            });
                        }
                    }
                }
                catch { }
                finally { proc.Dispose(); }
            }

            return results
                .OrderByDescending(r => r.Reason.Contains("Enjeksiyon"))
                .ThenBy(r => r.ProcessName)
                .Take(120)
                .ToList();
        }, ct);
    }

    public async Task<List<BrowserExtensionIssue>> ScanBrowserExtensionsAndShortcutsAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var issues = new List<BrowserExtensionIssue>();
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            var browserRoots = new (string Browser, string Path)[]
            {
                ("Chrome", Path.Combine(localApp, @"Google\Chrome\User Data")),
                ("Edge", Path.Combine(localApp, @"Microsoft\Edge\User Data")),
                ("Brave", Path.Combine(localApp, @"BraveSoftware\Brave-Browser\User Data"))
            };

            foreach (var (browser, root) in browserRoots)
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    foreach (var profDir in Directory.EnumerateDirectories(root))
                    {
                        var profName = Path.GetFileName(profDir);
                        if (!profName.Equals("Default", StringComparison.OrdinalIgnoreCase) &&
                            !profName.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var extBase = Path.Combine(profDir, "Extensions");
                        if (!Directory.Exists(extBase)) continue;

                        foreach (var extIdDir in Directory.EnumerateDirectories(extBase))
                        {
                            ct.ThrowIfCancellationRequested();
                            var extId = Path.GetFileName(extIdDir);
                            if (extId.Equals("Temp", StringComparison.OrdinalIgnoreCase)) continue;

                            var verDir = Directory.EnumerateDirectories(extIdDir).OrderByDescending(x => x).FirstOrDefault();
                            if (verDir == null) continue;

                            var manifestPath = Path.Combine(verDir, "manifest.json");
                            if (!File.Exists(manifestPath)) continue;

                            try
                            {
                                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                                var el = doc.RootElement;
                                string rawName = el.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? extId) : extId;
                                if (rawName.StartsWith("__MSG_", StringComparison.Ordinal))
                                    rawName = TryResolveExtensionMessage(verDir, rawName) ?? extId;

                                var perms = new List<string>();
                                CollectPerms(el, "permissions", perms);
                                CollectPerms(el, "host_permissions", perms);

                                var matchedRisky = perms
                                    .Where(p => HighRiskExtPermissions.Any(hr => p.Contains(hr, StringComparison.OrdinalIgnoreCase)))
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToList();

                                bool hasUpdateUrl = el.TryGetProperty("update_url", out _);
                                if (matchedRisky.Count >= 2 || (!hasUpdateUrl && matchedRisky.Count >= 1))
                                {
                                    issues.Add(new BrowserExtensionIssue
                                    {
                                        Browser = browser,
                                        Profile = profName,
                                        Name = rawName,
                                        ExtensionId = extId,
                                        FolderPath = verDir,
                                        Severity = !hasUpdateUrl ? FindingSeverity.High : FindingSeverity.Medium,
                                        RiskReason = !hasUpdateUrl
                                            ? $"Mağaza dışı/Sideloaded yüksek yetkili eklenti ({string.Join(", ", matchedRisky.Take(3))})"
                                            : $"Geniş web erişim izinleri ({string.Join(", ", matchedRisky.Take(4))})"
                                    });
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            ScanBrowserShortcuts(issues);
            return issues;
        }, ct);
    }

    private static void ScanBrowserShortcuts(List<BrowserExtensionIssue> issues)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var quickLaunch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch");
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;

        dynamic? shell = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell == null) return;

            foreach (var dir in new[] { desktop, quickLaunch }.Where(Directory.Exists))
            {
                foreach (var lnk in Directory.EnumerateFiles(dir, "*.lnk"))
                {
                    try
                    {
                        var sc = shell.CreateShortcut(lnk);
                        string target = sc.TargetPath?.ToString() ?? "";
                        string args = sc.Arguments?.ToString() ?? "";
                        if (!string.IsNullOrWhiteSpace(args) &&
                            (target.EndsWith("chrome.exe", StringComparison.OrdinalIgnoreCase) ||
                             target.EndsWith("msedge.exe", StringComparison.OrdinalIgnoreCase) ||
                             target.EndsWith("brave.exe", StringComparison.OrdinalIgnoreCase) ||
                             target.EndsWith("firefox.exe", StringComparison.OrdinalIgnoreCase)) &&
                            (args.Contains("http", StringComparison.OrdinalIgnoreCase) || args.Contains("--load-extension", StringComparison.OrdinalIgnoreCase)))
                        {
                            issues.Add(new BrowserExtensionIssue
                            {
                                Browser = "Kısayol Hijack",
                                Profile = Path.GetFileName(lnk),
                                Name = $"{Path.GetFileName(target)} Kısayolu",
                                ExtensionId = Path.GetFileName(lnk),
                                FolderPath = lnk,
                                Severity = FindingSeverity.High,
                                RiskReason = $"Tarayıcı kısayoluna parametre enjekte edilmiş: {args}"
                            });
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    private static void CollectPerms(JsonElement root, string prop, List<string> target)
    {
        if (!root.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                target.Add(item.GetString() ?? "");
        }
    }

    private static string? TryResolveExtensionMessage(string verDir, string msgToken)
    {
        try
        {
            var key = msgToken.Replace("__MSG_", "").TrimEnd('_');
            foreach (var loc in new[] { "tr", "en", "en_US" })
            {
                var p = Path.Combine(verDir, "_locales", loc, "messages.json");
                if (!File.Exists(p)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(p));
                if (doc.RootElement.TryGetProperty(key, out var entry) && entry.TryGetProperty("message", out var msg))
                    return msg.GetString();
            }
        }
        catch { }
        return null;
    }

    public void Dispose() => StopUsbWatcher();
}
