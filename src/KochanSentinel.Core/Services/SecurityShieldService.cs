using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KochanSentinel.Core.Interop;
using KochanSentinel.Core.Models;
using Microsoft.Win32;

namespace KochanSentinel.Core.Services;

public sealed class SecurityShieldService : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static readonly HashSet<string> WatchExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".msi", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".scr", ".hta"
    };

    private static readonly string[] SuspiciousPeApis =
    {
        "VirtualAllocEx", "WriteProcessMemory", "CreateRemoteThread",
        "NtUnmapViewOfSection", "SetWindowsHookEx", "URLDownloadToFile",
        "GetAsyncKeyState", "CryptEncrypt", "OpenProcess", "AdjustTokenPrivileges"
    };

    private static readonly string[] SuspiciousScriptTokens =
    {
        "-enc ", "-encodedcommand", "frombase64string", "invoke-expression",
        "downloadstring", "net.webclient", "certutil -urlcache", "mshta ", "wscript.shell"
    };

    private readonly List<FileSystemWatcher> _watchers = new();
    private ManagementEventWatcher? _processTraceWatcher;
    private EventLogWatcher? _psScriptBlockWatcher;
    private string _virusTotalApiKey = "";
    private string _malwareBazaarAuthKey = "";

    private static readonly (string RuleName, string[] Tokens, int MinMatches)[] YaraStyleRules =
    {
        ("Stealer_Credential_Harvester", new[] { "Login Data", "Local State", "os_crypt", "encrypted_key", "discord" }, 3),
        ("Ransomware_ShadowCopy_Destroyer", new[] { "vssadmin", "delete shadows", "wbadmin", "bcdedit", "recoveryenabled" }, 2),
        ("Mimikatz_LSASS_Dumper", new[] { "sekurlsa", "lsass.exe", "MiniDumpWriteDump", "SeDebugPrivilege" }, 2),
        ("Obfuscated_Reverse_Shell", new[] { "System.Net.Sockets.TCPClient", "GetStream", "cmd.exe", "/c " }, 3),
        ("Keylogger_Hook_Pattern", new[] { "SetWindowsHookEx", "GetAsyncKeyState", "GetForegroundWindow", "ToUnicode" }, 3),
        ("AsyncRAT_DcRAT_Trojan", new[] { "AsyncClient", "ClientSocket", "Pastebin", "CheckRemoteDebuggerPresent", "Mutex", "Stub" }, 3),
        ("Remcos_NjRAT_Trojan", new[] { "njq8", "SEE_MASK_NOZONECHECKS", "GetKeyState", "capGetDriverDescription", "DeleteValue" }, 3),
        ("Lumma_RedLine_InfoStealer", new[] { "CryptoWallets", "Autofills", "BrowserData", "Exodus", "MetaMask", "Passwords.txt" }, 3),
        ("Discord_Webhook_Token_Stealer", new[] { "discord.com/api/webhooks", @"Local Storage\leveldb", "dQw4w9WgXcQ", "tokens" }, 2),
        ("XMRig_CryptoMiner", new[] { "stratum+tcp", "randomx", "cryptonight", "donate-level", "rx/0" }, 2),
        ("Process_Hollowing_Injection", new[] { "NtUnmapViewOfSection", "ZwUnmapViewOfSection", "SetThreadContext", "ResumeThread", "VirtualAllocEx" }, 4)
    };

    private static readonly string[] WatchScriptAndExeOnly = { ".exe", ".bat", ".cmd", ".ps1", ".vbs", ".hta", ".scr" };
    private static readonly HashSet<string> HollowableLolBins = new(StringComparer.OrdinalIgnoreCase)
        { "RegAsm", "RegSvcs", "InstallUtil", "cvtres", "vbc", "jsc", "mshta" };
    private static readonly HashSet<string> SystemMasqueradeNames = new(StringComparer.OrdinalIgnoreCase)
        { "svchost", "csrss", "lsass", "services", "smss", "winlogon", "taskhostw" };
    private static readonly string[] KnownC2OrMinerPorts =
        { ":4444", ":5555", ":6666", ":7777", ":8888", ":1337", ":31337", ":1604", ":3333", ":14444" };

    public bool IsRealTimeEnabled { get; private set; }
    public bool AutoKillMaliciousScripts { get; set; } = true;
    public event Action<SecurityEvent>? OnSecurityEvent;
    public event Action<FileScanResult>? OnRealTimeFileScanned;

    public void StartRealTimeShield(string virusTotalApiKey = "", string malwareBazaarAuthKey = "")
    {
        _virusTotalApiKey = virusTotalApiKey;
        _malwareBazaarAuthKey = malwareBazaarAuthKey;
        if (IsRealTimeEnabled) return;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dirsToWatch = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Path.GetTempPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            appData
        };

        foreach (var dir in dirsToWatch.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                bool isAppData = dir.Equals(appData, StringComparison.OrdinalIgnoreCase);
                var watcher = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = isAppData || dir.EndsWith("Downloads", StringComparison.OrdinalIgnoreCase),
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };
                watcher.Created += (s, e) => OnFileChanged(s, e, isAppData);
                watcher.Renamed += (s, e) => OnFileChanged(s, new FileSystemEventArgs(WatcherChangeTypes.Created, Path.GetDirectoryName(e.FullPath)!, e.Name), isAppData);
                _watchers.Add(watcher);
            }
            catch { }
        }

        IsRealTimeEnabled = true;
        RaiseEvent("Kalkan", "Gerçek zamanlı İndirme, Masaüstü, Temp, Startup & AppData koruma kalkanı aktif.", FindingSeverity.Info);
    }

    public void StartHiddenProcessDetector()
    {
        if (_processTraceWatcher == null)
        {
            try
            {
                var query = new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace");
                _processTraceWatcher = new ManagementEventWatcher(query);
                _processTraceWatcher.EventArrived += (_, args) =>
                {
                    try
                    {
                        string procName = args.NewEvent["ProcessName"]?.ToString() ?? "";
                        if (!procName.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) &&
                            !procName.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase) &&
                            !procName.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) &&
                            !procName.Equals("mshta.exe", StringComparison.OrdinalIgnoreCase) &&
                            !procName.Equals("wscript.exe", StringComparison.OrdinalIgnoreCase) &&
                            !procName.Equals("cscript.exe", StringComparison.OrdinalIgnoreCase) &&
                            !procName.Equals("certutil.exe", StringComparison.OrdinalIgnoreCase) &&
                            !procName.Equals("bitsadmin.exe", StringComparison.OrdinalIgnoreCase) &&
                            !procName.Equals("vssadmin.exe", StringComparison.OrdinalIgnoreCase) &&
                            !procName.Equals("regsvr32.exe", StringComparison.OrdinalIgnoreCase))
                            return;

                        uint pid = Convert.ToUInt32(args.NewEvent["ProcessID"]);
                        if (pid <= 4 || pid == (uint)Environment.ProcessId) return;

                        // 1. Sub-millisecond native NtQueryInformationProcess lookup (catches ultra-short scripts)
                        string? cmd = NativeMethods.GetProcessCommandLineFast(pid);
                        if (string.IsNullOrWhiteSpace(cmd))
                        {
                            using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
                            foreach (var obj in searcher.Get())
                            {
                                cmd = obj["CommandLine"]?.ToString();
                                break;
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(cmd) &&
                            cmd.IndexOf("KochanSentinel", StringComparison.OrdinalIgnoreCase) < 0 &&
                            !IsBenignToastOrIdeCommand(cmd) &&
                            IsSuspiciousCommandLine(cmd))
                        {
                            bool isHighConf = IsHighConfidenceMaliciousCommand(cmd);
                            bool killed = false;
                            if (AutoKillMaliciousScripts && isHighConf)
                            {
                                try
                                {
                                    using var proc = Process.GetProcessById((int)pid);
                                    proc.Kill(entireProcessTree: true);
                                    killed = true;
                                }
                                catch { }
                            }

                            string sourceTag = killed ? "OTOMATİK ENGELLEME (Auto-Kill)" : "Gizli Süreç Alarmı";
                            string msg = killed
                                ? $"Zararlı betik/dropper anında sonlandırıldı ({procName}, PID {pid}): {cmd}"
                                : $"Gizli pencere/komut izlendi ({procName}, PID {pid}): {cmd}";
                            RaiseEvent(sourceTag, msg, (killed || isHighConf) ? FindingSeverity.Critical : FindingSeverity.Medium);
                        }
                    }
                    catch { }
                };
                _processTraceWatcher.Start();
                RaiseEvent("Süreç Dedektörü", "Gizli PowerShell / Betik süreç dedektörü aktif (Fast NtQuery + PS 4104).", FindingSeverity.Info);
            }
            catch { }
        }

        // 2. Windows PowerShell ScriptBlock Event Log (4104) catches decoded scripts even after short process exits
        if (_psScriptBlockWatcher == null)
        {
            try
            {
                var eq = new EventLogQuery("Microsoft-Windows-PowerShell/Operational", PathType.LogName, "*[System[(EventID=4104)]]");
                _psScriptBlockWatcher = new EventLogWatcher(eq);
                _psScriptBlockWatcher.EventRecordWritten += (_, e) =>
                {
                    try
                    {
                        var text = e.EventRecord?.FormatDescription() ?? "";
                        if (!string.IsNullOrWhiteSpace(text) && text.IndexOf("KochanSentinel", StringComparison.OrdinalIgnoreCase) < 0 &&
                            !IsBenignToastOrIdeCommand(text) &&
                            IsSuspiciousCommandLine(text))
                        {
                            string snippet = text.Length > 180 ? text[..180] + "..." : text;
                            var sev = IsHighConfidenceMaliciousCommand(text) ? FindingSeverity.Critical : FindingSeverity.Medium;
                            RaiseEvent("Gizli Süreç Alarmı", $"PowerShell ScriptBlock (Event 4104) betik izlendi: {snippet}", sev);
                        }
                    }
                    catch { }
                };
                _psScriptBlockWatcher.Enabled = true;
            }
            catch { }
        }
    }

    private static bool IsBenignToastOrIdeCommand(string text)
    {
        var lower = text.ToLowerInvariant();
        bool isKnownToast = lower.Contains("windows.ui.notifications.toastnotificationmanager") ||
                            lower.Contains("agy_notify_app_id") ||
                            lower.Contains("agy_notify_url");
        if (!isKnownToast) return false;

        // Still inspect if real dropper/download primitives are smuggled inside
        bool hasDangerToken = lower.Contains("-enc") || lower.Contains("-encodedcommand") ||
                              lower.Contains("frombase64") || lower.Contains("downloadstring") ||
                              lower.Contains("downloaddata") || lower.Contains("invoke-expression") ||
                              lower.Contains("iex(") || lower.Contains("mshta http") ||
                              lower.Contains("delete shadows") || lower.Contains("add-mppreference");
        return !hasDangerToken;
    }

    private static bool IsSuspiciousCommandLine(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains("-enc") || lower.Contains("-encodedcommand") ||
               lower.Contains("-w hidden") || lower.Contains("windowstyle hidden") ||
               lower.Contains("frombase64") || lower.Contains("downloadstring") ||
               lower.Contains("-urlcache") || lower.Contains("mshta http") ||
               lower.Contains("invoke-expression") || lower.Contains("iex(") ||
               lower.Contains("delete shadows") || lower.Contains("add-mppreference");
    }

    private static bool IsHighConfidenceMaliciousCommand(string text)
    {
        var lower = text.ToLowerInvariant();
        bool hasEncodedOrDownload = (lower.Contains("-enc") || lower.Contains("-encodedcommand") || lower.Contains("frombase64")) &&
                                    (lower.Contains("iex") || lower.Contains("invoke-expression") || lower.Contains("downloadstring") || lower.Contains("downloaddata") || lower.Contains("webclient") || lower.Contains("-w hidden") || lower.Contains("windowstyle hidden"));
        bool isMalwareStager = lower.Contains("mshta http") ||
                               lower.Contains("certutil -urlcache") ||
                               lower.Contains("vssadmin") && lower.Contains("delete shadows") ||
                               lower.Contains("bcdedit") && lower.Contains("recoveryenabled no") ||
                               (lower.Contains("add-mppreference") && lower.Contains("-exclusion"));
        return hasEncodedOrDownload || isMalwareStager;
    }

    public void StopHiddenProcessDetector()
    {
        try
        {
            _processTraceWatcher?.Stop();
            _processTraceWatcher?.Dispose();
        }
        catch { }
        _processTraceWatcher = null;

        try
        {
            if (_psScriptBlockWatcher != null)
            {
                _psScriptBlockWatcher.Enabled = false;
                _psScriptBlockWatcher.Dispose();
            }
        }
        catch { }
        _psScriptBlockWatcher = null;
    }

    public void UpdateApiKeys(string virusTotalApiKey, string malwareBazaarAuthKey)
    {
        _virusTotalApiKey = virusTotalApiKey ?? "";
        _malwareBazaarAuthKey = malwareBazaarAuthKey ?? "";
    }

    public void StopRealTimeShield()
    {
        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();
        IsRealTimeEnabled = false;
        RaiseEvent("Kalkan", "Gerçek zamanlı dosya kalkanı durduruldu.", FindingSeverity.Info);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e, bool appDataStrictMode)
    {
        var ext = Path.GetExtension(e.FullPath);
        if (appDataStrictMode)
        {
            if (!WatchScriptAndExeOnly.Contains(ext, StringComparer.OrdinalIgnoreCase)) return;
            if (e.FullPath.Contains(@"\Cache", StringComparison.OrdinalIgnoreCase) ||
                e.FullPath.Contains(@"\GPUCache", StringComparison.OrdinalIgnoreCase) ||
                e.FullPath.Contains(@"\node_modules\", StringComparison.OrdinalIgnoreCase))
                return;
        }
        else if (!WatchExtensions.Contains(ext))
        {
            return;
        }

        if (e.FullPath.Contains("KochanSentinel", StringComparison.OrdinalIgnoreCase)) return;

        _ = Task.Run(async () =>
        {
            // Wait briefly for browser download / file copy to finish locking
            await Task.Delay(1200);
            if (!File.Exists(e.FullPath)) return;

            var result = await ScanFileAsync(e.FullPath, _virusTotalApiKey, _malwareBazaarAuthKey);
            OnRealTimeFileScanned?.Invoke(result);

            var sev = result.RiskLevel;
            RaiseEvent("Anlık Tarama", $"{result.FileName} -> {result.Verdict}", sev);
        });
    }

    public async Task<FileScanResult> ScanFileAsync(string filePath, string? virusTotalApiKey = null, string? malwareBazaarAuthKey = null, CancellationToken ct = default)
    {
        var result = new FileScanResult
        {
            FilePath = filePath,
            FileName = Path.GetFileName(filePath)
        };

        if (!File.Exists(filePath))
        {
            result.Verdict = "Dosya bulunamadı";
            return result;
        }

        var fi = new FileInfo(filePath);
        result.FileSizeBytes = fi.Length;

        // 1. SHA-256
        result.Sha256 = await ComputeSha256Async(filePath, ct);

        // 2. Double extension check
        var nameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
        if (nameWithoutExt.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
            nameWithoutExt.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            nameWithoutExt.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            nameWithoutExt.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
            nameWithoutExt.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
        {
            result.HeuristicFlags.Add("Çift uzantı hilesi tespit edildi!");
        }

        // 3. Digital signature
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext is ".exe" or ".dll" or ".msi" or ".scr")
        {
            result.IsSigned = NativeMethods.VerifyFileOrCatalogSignature(filePath);
            if (result.IsSigned)
            {
                result.SignerName = TryGetSignerSubject(filePath);
            }
            else
            {
                result.SignerName = "İmzasız (Doğrulanmamış)";
                result.HeuristicFlags.Add("Dijital imza yok");
            }
        }

        // 4. Static Entropy + Suspicious API / Script Tokens (limit read to first 15 MB for speed)
        await AnalyzeFileContentAsync(filePath, ext, result, ct);

        // 5. Windows Defender CLI Scan
        result.DefenderResult = await RunDefenderScanAsync(filePath, ct);

        // 6. Cloud Threat Intel (MalwareBazaar + optional VirusTotal)
        result.CloudIntelResult = await CheckCloudIntelAsync(result.Sha256, virusTotalApiKey, malwareBazaarAuthKey, ct);

        // 7. Compute Final Verdict & Risk Level
        EvaluateVerdict(result);
        return result;
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        try
        {
            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var hash = await SHA256.HashDataAsync(fs, ct);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch
        {
            return "Hesaplanamadı";
        }
    }

    private static string TryGetSignerSubject(string filePath)
    {
        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
            var subj = cert.GetNameInfo(X509NameType.SimpleName, false);
            return string.IsNullOrWhiteSpace(subj) ? "Geçerli İmza" : subj;
        }
        catch
        {
            return "Geçerli İmza";
        }
    }

    private static async Task AnalyzeFileContentAsync(string filePath, string ext, FileScanResult result, CancellationToken ct)
    {
        try
        {
            const int maxRead = 15 * 1024 * 1024;
            byte[] buffer;
            await using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int toRead = (int)Math.Min(fs.Length, maxRead);
                buffer = new byte[toRead];
                int read = 0;
                while (read < toRead)
                {
                    int r = await fs.ReadAsync(buffer.AsMemory(read, toRead - read), ct);
                    if (r == 0) break;
                    read += r;
                }
                if (read < toRead) Array.Resize(ref buffer, read);
            }

            if (buffer.Length == 0) return;

            // Shannon Entropy
            var counts = new int[256];
            foreach (byte b in buffer) counts[b]++;
            double entropy = 0;
            double len = buffer.Length;
            for (int i = 0; i < 256; i++)
            {
                if (counts[i] == 0) continue;
                double p = counts[i] / len;
                entropy -= p * Math.Log2(p);
            }
            result.ShannonEntropy = Math.Round(entropy, 2);
            if (entropy > 7.25 && ext is ".exe" or ".dll" or ".scr")
            {
                result.IsPackedOrEncrypted = true;
                result.HeuristicFlags.Add($"Yüksek Entropi ({result.ShannonEntropy:F2}/8.0) - Paketlenmiş/Şifreli olabilir");
            }

            // ASCII string scan for APIs / Script commands
            string ascii = Encoding.ASCII.GetString(buffer);
            if (ext is ".exe" or ".dll" or ".scr")
            {
                foreach (var api in SuspiciousPeApis)
                {
                    if (ascii.Contains(api, StringComparison.Ordinal))
                        result.SuspiciousApis.Add(api);
                }
            }
            else if (ext is ".ps1" or ".bat" or ".cmd" or ".vbs" or ".js" or ".hta")
            {
                foreach (var token in SuspiciousScriptTokens)
                {
                    if (ascii.Contains(token, StringComparison.OrdinalIgnoreCase))
                        result.HeuristicFlags.Add($"Şüpheli betik komutu: {token.Trim()}");
                }
            }

            // Local YARA-style multi-pattern signature scan
            foreach (var (ruleName, tokens, minMatches) in YaraStyleRules)
            {
                int hits = tokens.Count(t => ascii.Contains(t, StringComparison.OrdinalIgnoreCase));
                if (hits >= minMatches)
                {
                    result.HeuristicFlags.Add($"YARA Kural Eşleşmesi: {ruleName} ({hits}/{tokens.Length})");
                }
            }
        }
        catch { }
    }

    private static async Task<string> RunDefenderScanAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var mpCmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
            if (!File.Exists(mpCmd))
                return "Defender CLI bulunamadı";

            var psi = new ProcessStartInfo
            {
                FileName = mpCmd,
                Arguments = $"-Scan -ScanType 3 -File \"{filePath}\" -DisableRemediation",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return "Başlatılamadı";

            var output = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode == 0) return "Temiz (Windows Defender)";
            if (proc.ExitCode == 2) return "TEHDİT TESPİT EDİLDİ (Windows Defender)!";
            return $"Defender Çıkış Kodu: {proc.ExitCode}";
        }
        catch
        {
            return "Defender taraması atlandı";
        }
    }

    private static async Task<string> CheckCloudIntelAsync(string sha256, string? vtKey, string? mbAuthKey, CancellationToken ct)
    {
        if (sha256.Length != 64) return "Hash geçersiz";

        // 1. Optional VirusTotal API v3
        string? vtSummary = null;
        if (!string.IsNullOrWhiteSpace(vtKey))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"https://www.virustotal.com/api/v3/files/{sha256}");
                req.Headers.Add("x-apikey", vtKey.Trim());
                using var resp = await Http.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    var stats = doc.RootElement.GetProperty("data").GetProperty("attributes").GetProperty("last_analysis_stats");
                    int malicious = stats.GetProperty("malicious").GetInt32();
                    int suspicious = stats.GetProperty("suspicious").GetInt32();
                    if (malicious > 0 || suspicious > 0)
                        return $"VirusTotal: {malicious} ZARARLI, {suspicious} şüpheli!";
                    vtSummary = "VirusTotal: Temiz (0 tespit)";
                }
                else if ((int)resp.StatusCode == 404)
                {
                    vtSummary = "VT: Veritabanında yok";
                }
                else if ((int)resp.StatusCode is 401 or 403 or 429)
                {
                    vtSummary = $"VT: Limit/Anahtar ({(int)resp.StatusCode})";
                }
            }
            catch { }
        }

        // 2. MalwareBazaar (abuse.ch) hash lookup (with optional Auth-Key header)
        string mbSummary;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://mb-api.abuse.ch/api/v1/")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["query"] = "get_info",
                    ["hash"] = sha256
                })
            };
            if (!string.IsNullOrWhiteSpace(mbAuthKey))
                req.Headers.Add("Auth-Key", mbAuthKey.Trim());

            using var resp = await Http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                if (json.Contains("\"query_status\":\"ok\"", StringComparison.OrdinalIgnoreCase) ||
                    json.Contains("\"query_status\": \"ok\"", StringComparison.OrdinalIgnoreCase))
                {
                    return "MalwareBazaar: BİLİNEN ZARARLI YAZILIM!";
                }
                mbSummary = "MalwareBazaar: Temiz / Kayıt yok";
            }
            else if ((int)resp.StatusCode is 401 or 403)
            {
                mbSummary = "MalwareBazaar: Ayarlar'dan ücretsiz Auth-Key girilebilir";
            }
            else
            {
                mbSummary = $"MalwareBazaar: HTTP {(int)resp.StatusCode}";
            }
        }
        catch
        {
            mbSummary = "Bulut erişilemedi (Çevrimdışı)";
        }

        return vtSummary != null ? $"{vtSummary} | {mbSummary}" : mbSummary;
    }

    private static void EvaluateVerdict(FileScanResult r)
    {
        if (r.DefenderResult.Contains("TEHDİT", StringComparison.OrdinalIgnoreCase) ||
            r.CloudIntelResult.Contains("ZARARLI", StringComparison.OrdinalIgnoreCase) ||
            r.HeuristicFlags.Any(f => f.Contains("YARA Kural", StringComparison.OrdinalIgnoreCase)))
        {
            r.RiskLevel = FindingSeverity.Critical;
            r.Verdict = "KRİTİK TEHDİT TESPİT EDİLDİ";
            return;
        }

        int riskPoints = 0;
        if (!r.IsSigned && r.HeuristicFlags.Any(f => f.Contains("Dijital imza"))) riskPoints += 1;
        if (r.IsPackedOrEncrypted) riskPoints += 2;
        if (r.SuspiciousApis.Count >= 3) riskPoints += 2;
        else if (r.SuspiciousApis.Count >= 1) riskPoints += 1;
        if (r.HeuristicFlags.Any(f => f.Contains("Çift uzantı") || f.Contains("Şüpheli betik"))) riskPoints += 3;

        if (riskPoints >= 4)
        {
            r.RiskLevel = FindingSeverity.High;
            r.Verdict = "Yüksek Riskli / Şüpheli Davranış";
        }
        else if (riskPoints >= 2)
        {
            r.RiskLevel = FindingSeverity.Medium;
            r.Verdict = "Orta Risk (İmzasız / Paketli - Dikkatli Olun)";
        }
        else
        {
            r.RiskLevel = FindingSeverity.Info;
            r.Verdict = r.IsSigned ? "Güvenli (İmzalı & Temiz)" : "Temiz Görünüyor";
        }
    }

    // =========================================================================
    // DEEP PERSISTENCE & LEFTOVER AUDIT ("İndirip sildiğim şeyden kalıntı var mı?")
    // =========================================================================
    private static readonly HashSet<string> BuiltInSafeBinaries = new(StringComparer.OrdinalIgnoreCase)
    {
        "SecurityHealthSystray.exe", "ctfmon.exe", "explorer.exe", "KochanSentinel.exe", "KochanSentinel.App.exe"
    };

    public async Task<List<SecurityFinding>> RunPersistenceAuditAsync(
        IEnumerable<string>? trustedWhitelist = null,
        IEnumerable<ProcessItem>? activeProcesses = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var findings = new List<SecurityFinding>();
            var whitelistSet = new HashSet<string>(trustedWhitelist ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            if (activeProcesses != null)
                findings.AddRange(ScanRunningProcessesForThreats(activeProcesses, whitelistSet));

            ScanRegistryRunKeys(findings);
            ScanStartupFolders(findings);
            ScanScheduledTasks(findings);
            ScanSuspiciousServices(findings);
            ScanHostsFile(findings);
            ScanProxyHijack(findings);
            ScanDefenderExclusions(findings);
            ScanWmiPersistence(findings);
            ScanIfeoAndWinlogon(findings);
            ScanLeftoverExecutables(findings, ct);

            return findings
                .Where(f => !IsWhitelisted(f, whitelistSet))
                .OrderByDescending(f => f.Severity)
                .ThenBy(f => f.Category)
                .ToList();
        }, ct);
    }

    public List<SecurityFinding> ScanRunningProcessesForThreats(
        IEnumerable<ProcessItem> runningProcesses,
        IEnumerable<string>? trustedWhitelist = null)
    {
        var whitelistSet = trustedWhitelist as HashSet<string> ??
                           new HashSet<string>(trustedWhitelist ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var list = new List<SecurityFinding>();

        foreach (var p in runningProcesses)
        {
            if (p.Pid <= 4 || p.Pid == Environment.ProcessId || string.IsNullOrWhiteSpace(p.ExecutablePath))
                continue;

            // 1. System Binary Masquerading (e.g. svchost.exe or lsass.exe running from AppData/Temp)
            if (SystemMasqueradeNames.Contains(p.Name) &&
                !p.ExecutablePath.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase))
            {
                var f = new SecurityFinding
                {
                    Category = "Aktif RAT / Süreç Tehdidi",
                    Name = $"{p.Name}.exe (PID {p.Pid})",
                    ProcessId = p.Pid,
                    Location = $"Sahte Sistem Konumu (Ağ: {p.ActiveConnections})",
                    TargetPath = p.ExecutablePath,
                    Reason = "KRİTİK: Windows sistem işlemi adıyla sahte klasörden çalışan şüpheli yazılım (Masquerading)!",
                    Severity = FindingSeverity.Critical,
                    IsSigned = p.IsSigned,
                    CanDisableOrDelete = true
                };
                if (!IsWhitelisted(f, whitelistSet)) list.Add(f);
                continue;
            }

            // 2. Process Hollowing / LOLBin Outbound C2 Activity (e.g. RegAsm/InstallUtil holding open sockets)
            if (HollowableLolBins.Contains(p.Name) && p.ActiveConnections > 0)
            {
                var f = new SecurityFinding
                {
                    Category = "Aktif RAT / Süreç Tehdidi",
                    Name = $"{p.Name}.exe (PID {p.Pid})",
                    ProcessId = p.Pid,
                    Location = $"Dış Ağ: {p.RemoteEndpoints}",
                    TargetPath = p.ExecutablePath,
                    Reason = "KRİTİK: Process Hollowing şüphesi! Sistem LOLBin aracı dış ağ bağlantısı kurmuş (AsyncRAT/Loader davranışı).",
                    Severity = FindingSeverity.Critical,
                    IsSigned = p.IsSigned,
                    CanDisableOrDelete = true
                };
                if (!IsWhitelisted(f, whitelistSet)) list.Add(f);
                continue;
            }

            // 3. Known C2 / RAT / CryptoMiner Port Connection from unsigned executable
            if (!p.IsSigned && p.ActiveConnections > 0 && !string.IsNullOrEmpty(p.RemoteEndpoints) &&
                KnownC2OrMinerPorts.Any(port => p.RemoteEndpoints.Contains(port, StringComparison.Ordinal)))
            {
                var f = new SecurityFinding
                {
                    Category = "Aktif RAT / Süreç Tehdidi",
                    Name = $"{p.Name}.exe (PID {p.Pid})",
                    ProcessId = p.Pid,
                    Location = $"C2 / Madenci Portu: {p.RemoteEndpoints}",
                    TargetPath = p.ExecutablePath,
                    Reason = "KRİTİK: İmzasız süreç bilinen RAT / C2 veya Kripto Madenci portuna aktif bağlı!",
                    Severity = FindingSeverity.Critical,
                    IsSigned = false,
                    CanDisableOrDelete = true
                };
                if (!IsWhitelisted(f, whitelistSet)) list.Add(f);
                continue;
            }

            // 4. Unsigned AppData / Temp / ProgramData executable communicating over internet
            bool riskyFolder = p.ExecutablePath.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase) ||
                               p.ExecutablePath.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase) ||
                               p.ExecutablePath.Contains(@"\ProgramData\", StringComparison.OrdinalIgnoreCase) ||
                               p.ExecutablePath.Contains(@"\Users\Public\", StringComparison.OrdinalIgnoreCase);

            if (!p.IsSigned && riskyFolder && p.ActiveConnections > 0)
            {
                var f = new SecurityFinding
                {
                    Category = "Aktif RAT / Süreç Tehdidi",
                    Name = $"{p.Name}.exe (PID {p.Pid})",
                    ProcessId = p.Pid,
                    Location = $"Aktif Ağ: {p.RemoteEndpoints}",
                    TargetPath = p.ExecutablePath,
                    Reason = "Temp/AppData dizininden çalışan imzasız süreç dış sunucu ile aktif iletişim halinde.",
                    Severity = FindingSeverity.High,
                    IsSigned = false,
                    CanDisableOrDelete = true
                };
                if (!IsWhitelisted(f, whitelistSet)) list.Add(f);
            }
        }

        return list;
    }

    private static bool IsWhitelisted(SecurityFinding f, HashSet<string> whitelistSet)
    {
        var fileName = "";
        try { fileName = Path.GetFileName(f.TargetPath); } catch { }

        if (BuiltInSafeBinaries.Contains(f.Name) || BuiltInSafeBinaries.Contains(fileName))
            return true;

        if (whitelistSet.Contains(f.TargetPath) || whitelistSet.Contains(f.Name) ||
            (!string.IsNullOrEmpty(fileName) && whitelistSet.Contains(fileName)))
            return true;

        return false;
    }

    private static void ScanRegistryRunKeys(List<SecurityFinding> findings)
    {
        var targets = new (RegistryKey Root, string HiveName, string SubKey)[]
        {
            (Registry.CurrentUser, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\Run"),
            (Registry.CurrentUser, "HKCU", @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
            (Registry.LocalMachine, "HKLM", @"Software\Microsoft\Windows\CurrentVersion\Run"),
            (Registry.LocalMachine, "HKLM", @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
            (Registry.LocalMachine, "HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
            (Registry.LocalMachine, "HKLM", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce")
        };

        foreach (var (root, hiveName, subKey) in targets)
        {
            try
            {
                using var key = root.OpenSubKey(subKey, false);
                if (key == null) continue;

                foreach (var valName in key.GetValueNames())
                {
                    var rawVal = key.GetValue(valName)?.ToString() ?? "";
                    var exePath = ExtractExecutablePath(rawVal);
                    bool exists = !string.IsNullOrEmpty(exePath) && File.Exists(exePath);
                    bool signed = exists && NativeMethods.VerifyFileOrCatalogSignature(exePath);

                    var sev = FindingSeverity.Info;
                    var reason = "Normal başlangıç kaydı.";

                    if (!exists && !string.IsNullOrEmpty(exePath))
                    {
                        sev = FindingSeverity.Medium;
                        reason = "Kırık/Kalıntı Başlangıç Kaydı (Dosyası silinmiş ama kayıt defterinde duruyor!)";
                    }
                    else if (IsSuspiciousPathOrCommand(rawVal, exePath, signed))
                    {
                        sev = FindingSeverity.High;
                        reason = "Şüpheli konumdan (Temp/AppData) çalışan imzasız veya gizli komutlu başlangıç kaydı!";
                    }
                    else if (!signed && exists)
                    {
                        sev = FindingSeverity.Low;
                        reason = "İmzasız başlangıç uygulaması.";
                    }

                    if (sev >= FindingSeverity.Low)
                    {
                        findings.Add(new SecurityFinding
                        {
                            Category = "Başlangıç (Registry)",
                            Name = valName,
                            Location = $@"{hiveName}\{subKey}",
                            TargetPath = string.IsNullOrEmpty(exePath) ? rawVal : exePath,
                            Reason = reason,
                            Severity = sev,
                            IsSigned = signed,
                            RegistryHive = hiveName,
                            RegistryKeyPath = subKey,
                            RegistryValueName = valName
                        });
                    }
                }
            }
            catch { }
        }
    }

    private static void ScanStartupFolders(List<SecurityFinding> findings)
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
        };

        foreach (var folder in folders.Where(Directory.Exists))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    if (Path.GetFileName(file).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    var ext = Path.GetExtension(file);
                    bool isScript = ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
                                    ext.Equals(".vbs", StringComparison.OrdinalIgnoreCase) ||
                                    ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase);

                    findings.Add(new SecurityFinding
                    {
                        Category = "Başlangıç Klasörü",
                        Name = Path.GetFileName(file),
                        Location = folder,
                        TargetPath = file,
                        Reason = isScript ? "Başlangıçta otomatik çalışan betik (Script)!" : "Başlangıç klasöründe otomatik çalışan öğe.",
                        Severity = isScript ? FindingSeverity.High : FindingSeverity.Low
                    });
                }
            }
            catch { }
        }
    }

    private static void ScanScheduledTasks(List<SecurityFinding> findings)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c \"chcp 65001 >nul & schtasks.exe /query /fo CSV /v /nh\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using var proc = Process.Start(psi);
            if (proc == null) return;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(6000);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var cols = ParseCsvLine(line);
                if (cols.Count < 12) continue;

                string taskName = cols[1].Trim('"');
                string status = cols[3].Trim('"');
                string taskState = cols[11].Trim('"');
                string action = cols[8].Trim('"');

                if (taskName.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase) ||
                    taskName.Equals($"\\{SettingsService.StartupTaskName}", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(action) || !seen.Add(taskName))
                    continue;

                // Skip already disabled scheduled tasks
                if (status.Contains("Disabled", StringComparison.OrdinalIgnoreCase) ||
                    status.Contains("Devre", StringComparison.OrdinalIgnoreCase) ||
                    taskState.Contains("Disabled", StringComparison.OrdinalIgnoreCase) ||
                    taskState.Contains("Devre", StringComparison.OrdinalIgnoreCase))
                    continue;

                var exe = ExtractExecutablePath(action);
                if (!string.IsNullOrEmpty(exe) && exe.Contains("KochanSentinel", StringComparison.OrdinalIgnoreCase))
                    continue;
                bool signed = !string.IsNullOrEmpty(exe) && File.Exists(exe) && NativeMethods.VerifyFileOrCatalogSignature(exe);

                if (IsSuspiciousPathOrCommand(action, exe, signed))
                {
                    findings.Add(new SecurityFinding
                    {
                        Category = "Zamanlanmış Görev",
                        Name = taskName,
                        Location = "Task Scheduler",
                        TargetPath = action,
                        Reason = "Microsoft dışı, Temp/AppData veya betik çalıştıran zamanlanmış görev!",
                        Severity = FindingSeverity.High,
                        IsSigned = signed
                    });
                }
            }
        }
        catch { }
    }

    private static void ScanSuspiciousServices(List<SecurityFinding> findings)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, DisplayName, PathName, State FROM Win32_Service");
            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString() ?? "";
                var display = obj["DisplayName"]?.ToString() ?? name;
                var pathName = obj["PathName"]?.ToString() ?? "";
                var exe = ExtractExecutablePath(pathName);

                if (string.IsNullOrEmpty(exe) || exe.Contains(@"\Windows\System32\", StringComparison.OrdinalIgnoreCase))
                    continue;

                bool exists = File.Exists(exe);
                bool inUserSpace = exe.Contains(@"\Users\", StringComparison.OrdinalIgnoreCase) ||
                                   exe.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase);

                if (inUserSpace || !exists)
                {
                    bool signed = exists && NativeMethods.VerifyFileOrCatalogSignature(exe);
                    if (!signed)
                    {
                        findings.Add(new SecurityFinding
                        {
                            Category = "Windows Servisi",
                            Name = $"{display} ({name})",
                            ServiceName = name,
                            Location = "Services.msc",
                            TargetPath = exe,
                            Reason = !exists
                                ? "Dosyası silinmiş kalıntı Windows servisi!"
                                : "Kullanıcı dizininden çalışan imzasız Windows servisi!",
                            Severity = exists ? FindingSeverity.High : FindingSeverity.Medium,
                            CanDisableOrDelete = true,
                            RequiresConfirmation = true
                        });
                    }
                }
            }
        }
        catch { }
    }

    private static void ScanHostsFile(List<SecurityFinding> findings)
    {
        try
        {
            var hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
            if (!File.Exists(hostsPath)) return;

            var customLines = File.ReadAllLines(hostsPath)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith('#'))
                .ToList();

            if (customLines.Count > 0)
            {
                findings.Add(new SecurityFinding
                {
                    Category = "Hosts Dosyası",
                    Name = $"{customLines.Count} Özel Yönlendirme",
                    Location = hostsPath,
                    TargetPath = string.Join(" | ", customLines.Take(3)),
                    Reason = "Hosts dosyasında alan adı yönlendirmesi var (bazı zararlılar güvenlik sitelerini buradan engeller).",
                    Severity = customLines.Any(l => l.Contains("virustotal", StringComparison.OrdinalIgnoreCase) ||
                                                    l.Contains("microsoft", StringComparison.OrdinalIgnoreCase) ||
                                                    l.Contains("kaspersky", StringComparison.OrdinalIgnoreCase))
                        ? FindingSeverity.Critical
                        : FindingSeverity.Low,
                    CanDisableOrDelete = false
                });
            }
        }
        catch { }
    }

    private static void ScanProxyHijack(List<SecurityFinding> findings)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", false);
            if (key == null) return;
            var proxyEnabled = key.GetValue("ProxyEnable");
            var proxyServer = key.GetValue("ProxyServer")?.ToString() ?? "";
            if (proxyEnabled is int en && en == 1 && !string.IsNullOrWhiteSpace(proxyServer))
            {
                findings.Add(new SecurityFinding
                {
                    Category = "Ağ / Proxy Ayarı",
                    Name = "Sistem Proxy Aktif",
                    Location = @"HKCU\...\Internet Settings",
                    TargetPath = proxyServer,
                    Reason = "Windows sistem proxy'si açık! Eğer VPN/Proxy kullanmıyorsanız zararlı yazılımlar trafiğinizi buradan yönlendiriyor olabilir.",
                    Severity = FindingSeverity.High,
                    RegistryHive = "HKCU",
                    RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings",
                    RegistryValueName = "ProxyEnable"
                });
            }
        }
        catch { }
    }

    private static void ScanDefenderExclusions(List<SecurityFinding> findings)
    {
        var exclusionSubKeys = new[]
        {
            @"SOFTWARE\Microsoft\Windows Defender\Exclusions\Paths",
            @"SOFTWARE\Microsoft\Windows Defender\Exclusions\Processes"
        };

        foreach (var subKey in exclusionSubKeys)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(subKey, false);
                if (key == null) continue;
                foreach (var val in key.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(val)) continue;
                    bool risky = val.Contains(@"\Users\", StringComparison.OrdinalIgnoreCase) ||
                                 val.Contains(@"\Temp", StringComparison.OrdinalIgnoreCase) ||
                                 val.Contains(@"\AppData", StringComparison.OrdinalIgnoreCase) ||
                                 val.Equals(@"C:\", StringComparison.OrdinalIgnoreCase);

                    findings.Add(new SecurityFinding
                    {
                        Category = "Defender İstisnası",
                        Name = Path.GetFileName(val.TrimEnd('\\')) is { Length: > 0 } n ? n : val,
                        Location = $@"HKLM\{subKey}",
                        TargetPath = val,
                        RegistryHive = "HKLM",
                        RegistryKeyPath = subKey,
                        RegistryValueName = val,
                        Reason = risky
                            ? "KRİTİK: Kullanıcı/Temp klasörü Windows Defender tarama istisnasına eklenmiş! Zararlılar buradan gizlenebilir."
                            : "Windows Defender tarama dışı bırakılmış yol/işlem.",
                        Severity = risky ? FindingSeverity.Critical : FindingSeverity.Medium,
                        CanDisableOrDelete = true,
                        RequiresConfirmation = true
                    });
                }
            }
            catch { }
        }
    }

    private static void ScanWmiPersistence(List<SecurityFinding> findings)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\subscription", "SELECT Name, CommandLineTemplate, ExecutablePath FROM CommandLineEventConsumer");
            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString() ?? "WMI Consumer";
                var cmd = obj["CommandLineTemplate"]?.ToString() ?? obj["ExecutablePath"]?.ToString() ?? "";
                findings.Add(new SecurityFinding
                {
                    Category = "WMI Dosyasız Kalıcılık",
                    Name = name,
                    Location = @"root\subscription\CommandLineEventConsumer",
                    TargetPath = cmd,
                    Reason = "Dosyasız (Fileless) WMI olay tetikleyicisi tespit edildi! Çok gelişmiş kalıcılık tekniğidir.",
                    Severity = FindingSeverity.Critical,
                    CanDisableOrDelete = true,
                    RequiresConfirmation = true
                });
            }
        }
        catch { }
    }

    private static void ScanIfeoAndWinlogon(List<SecurityFinding> findings)
    {
        try
        {
            // 1. IFEO Debugger hijack check
            const string ifeoPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
            using var ifeoKey = Registry.LocalMachine.OpenSubKey(ifeoPath, false);
            if (ifeoKey != null)
            {
                foreach (var subName in ifeoKey.GetSubKeyNames())
                {
                    using var sub = ifeoKey.OpenSubKey(subName, false);
                    var dbg = sub?.GetValue("Debugger")?.ToString();
                    if (!string.IsNullOrWhiteSpace(dbg) &&
                        !dbg.Contains("vsjitdebugger", StringComparison.OrdinalIgnoreCase))
                    {
                        findings.Add(new SecurityFinding
                        {
                            Category = "IFEO Debugger Tuzağı",
                            Name = subName,
                            Location = $@"HKLM\{ifeoPath}\{subName}",
                            TargetPath = dbg,
                            Reason = $"{subName} açıldığında yerine başka bir program ({dbg}) çalıştırılacak şekilde değiştirilmiş!",
                            Severity = FindingSeverity.Critical,
                            RegistryHive = "HKLM",
                            RegistryKeyPath = $@"{ifeoPath}\{subName}",
                            RegistryValueName = "Debugger"
                        });
                    }
                }
            }

            // 2. Winlogon Shell / Userinit tampering check
            const string winlogonPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
            using var wlKey = Registry.LocalMachine.OpenSubKey(winlogonPath, false);
            if (wlKey != null)
            {
                var shell = wlKey.GetValue("Shell")?.ToString()?.Trim() ?? "";
                if (!string.IsNullOrEmpty(shell) && !shell.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new SecurityFinding
                    {
                        Category = "Winlogon Shell",
                        Name = "Winlogon Shell Değişimi",
                        Location = $@"HKLM\{winlogonPath}",
                        TargetPath = shell,
                        Reason = "Windows masaüstü kabuğu (explorer.exe) dışında ek bir komut/program çalıştırılıyor!",
                        Severity = FindingSeverity.Critical,
                        CanDisableOrDelete = false
                    });
                }

                var userinit = wlKey.GetValue("Userinit")?.ToString()?.Trim() ?? "";
                if (!string.IsNullOrEmpty(userinit))
                {
                    var parts = userinit.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length > 1 || !userinit.EndsWith("userinit.exe,", StringComparison.OrdinalIgnoreCase) && !userinit.EndsWith("userinit.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        findings.Add(new SecurityFinding
                        {
                            Category = "Winlogon Userinit",
                            Name = "Winlogon Userinit Değişimi",
                            Location = $@"HKLM\{winlogonPath}",
                            TargetPath = userinit,
                            Reason = "Oturum açılışında standart userinit.exe dışında ek program zincirlenmiş!",
                            Severity = FindingSeverity.Critical,
                            CanDisableOrDelete = false
                        });
                    }
                }
            }

            // 3. AppInit_DLLs check
            const string appInitPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows";
            using var aiKey = Registry.LocalMachine.OpenSubKey(appInitPath, false);
            if (aiKey != null)
            {
                var dlls = aiKey.GetValue("AppInit_DLLs")?.ToString()?.Trim() ?? "";
                var load = aiKey.GetValue("LoadAppInit_DLLs");
                if (!string.IsNullOrEmpty(dlls) && load is int l && l == 1)
                {
                    findings.Add(new SecurityFinding
                    {
                        Category = "Global DLL Enjeksiyonu",
                        Name = "AppInit_DLLs Aktif",
                        Location = $@"HKLM\{appInitPath}",
                        TargetPath = dlls,
                        RegistryHive = "HKLM",
                        RegistryKeyPath = appInitPath,
                        RegistryValueName = "LoadAppInit_DLLs",
                        Reason = "Her açılan programa otomatik DLL enjekte eden AppInit_DLLs özelliği aktif!",
                        Severity = FindingSeverity.Critical,
                        CanDisableOrDelete = true,
                        RequiresConfirmation = true
                    });
                }
            }
        }
        catch { }
    }

    private static void ScanLeftoverExecutables(List<SecurityFinding> findings, CancellationToken ct)
    {
        var scanDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Path.GetTempPath()
        };

        foreach (var dir in scanDirs.Where(Directory.Exists))
        {
            try
            {
                var opts = new System.IO.EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 2 };
                foreach (var file in Directory.EnumerateFiles(dir, "*", opts))
                {
                    ct.ThrowIfCancellationRequested();
                    var ext = Path.GetExtension(file);
                    if (!WatchExtensions.Contains(ext) || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (file.Contains("KochanSentinel", StringComparison.OrdinalIgnoreCase))
                        continue;

                    bool isHidden = (File.GetAttributes(file) & FileAttributes.Hidden) != 0;
                    bool signed = (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".msi", StringComparison.OrdinalIgnoreCase))
                                  && NativeMethods.VerifyFileOrCatalogSignature(file);

                    if (!signed || isHidden)
                    {
                        findings.Add(new SecurityFinding
                        {
                            Category = "Kalıntı / Şüpheli Dosya",
                            Name = Path.GetFileName(file),
                            Location = Path.GetDirectoryName(file) ?? dir,
                            TargetPath = file,
                            Reason = isHidden
                                ? "Gizli (Hidden) çalıştırılabilir dosya/betik!"
                                : "Temp/AppData içinde duran imzasız çalıştırılabilir dosya.",
                            Severity = isHidden ? FindingSeverity.High : FindingSeverity.Medium,
                            IsSigned = signed
                        });
                    }

                    if (findings.Count > 150) return;
                }
            }
            catch { }
        }
    }

    public bool RemoveOrDisableFinding(SecurityFinding finding, bool allowConfirmedSystemChanges = false)
    {
        try
        {
            if (finding.RequiresConfirmation && !allowConfirmedSystemChanges)
                return false;

            if (finding.Category == "Başlangıç (Registry)" && !string.IsNullOrEmpty(finding.RegistryValueName))
            {
                var root = finding.RegistryHive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
                using var key = root.OpenSubKey(finding.RegistryKeyPath, writable: true);
                key?.DeleteValue(finding.RegistryValueName, false);
                return true;
            }

            if (finding.Category == "Zamanlanmış Görev")
            {
                var psi = new ProcessStartInfo("schtasks.exe", $"/change /tn \"{finding.Name}\" /disable")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(3000);
                return p?.ExitCode == 0;
            }

            if ((finding.Category == "Başlangıç Klasörü" || finding.Category == "Kalıntı / Şüpheli Dosya") &&
                File.Exists(finding.TargetPath))
            {
                File.SetAttributes(finding.TargetPath, FileAttributes.Normal);
                File.Delete(finding.TargetPath);
                return true;
            }

            if (finding.Category == "Ağ / Proxy Ayarı" && !string.IsNullOrEmpty(finding.RegistryKeyPath))
            {
                using var key = Registry.CurrentUser.OpenSubKey(finding.RegistryKeyPath, writable: true);
                key?.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                return true;
            }

            if (finding.Category == "IFEO Debugger Tuzağı" && !string.IsNullOrEmpty(finding.RegistryKeyPath))
            {
                using var key = Registry.LocalMachine.OpenSubKey(finding.RegistryKeyPath, writable: true);
                key?.DeleteValue("Debugger", false);
                return true;
            }

            if (finding.Category == "Windows Servisi" && !string.IsNullOrWhiteSpace(finding.ServiceName))
            {
                RunProcessWait("sc.exe", $"stop \"{finding.ServiceName}\"");
                return RunProcessWait("sc.exe", $"config \"{finding.ServiceName}\" start= disabled") == 0;
            }

            if (finding.Category == "Defender İstisnası" && !string.IsNullOrWhiteSpace(finding.TargetPath))
            {
                bool isProc = finding.RegistryKeyPath.EndsWith("Processes", StringComparison.OrdinalIgnoreCase);
                string escaped = finding.TargetPath.Replace("'", "''");
                string psArg = isProc
                    ? $"-NoProfile -Command \"Remove-MpPreference -ExclusionProcess '{escaped}' -ErrorAction SilentlyContinue\""
                    : $"-NoProfile -Command \"Remove-MpPreference -ExclusionPath '{escaped}' -ErrorAction SilentlyContinue\"";
                RunProcessWait("powershell.exe", psArg);
                using var key = Registry.LocalMachine.OpenSubKey(finding.RegistryKeyPath, writable: true);
                key?.DeleteValue(finding.RegistryValueName, false);
                return true;
            }

            if (finding.Category == "WMI Dosyasız Kalıcılık" && !string.IsNullOrWhiteSpace(finding.Name))
            {
                using var searcher = new ManagementObjectSearcher(@"root\subscription", $"SELECT * FROM CommandLineEventConsumer WHERE Name = '{finding.Name.Replace("'", "''")}'");
                bool any = false;
                foreach (ManagementObject obj in searcher.Get())
                {
                    obj.Delete();
                    any = true;
                }
                return any;
            }

            if (finding.Category == "Global DLL Enjeksiyonu")
            {
                using var key = Registry.LocalMachine.OpenSubKey(finding.RegistryKeyPath, writable: true);
                key?.SetValue("LoadAppInit_DLLs", 0, RegistryValueKind.DWord);
                return true;
            }

            if (finding.Category == "Aktif RAT / Süreç Tehdidi" && finding.ProcessId > 4)
            {
                try
                {
                    using var p = Process.GetProcessById(finding.ProcessId);
                    p.Kill(entireProcessTree: true);
                }
                catch { }
                return true;
            }
        }
        catch { }
        return false;
    }

    private static int RunProcessWait(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
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

    private static bool IsSuspiciousPathOrCommand(string rawCommand, string exePath, bool isSigned)
    {
        var lower = rawCommand.ToLowerInvariant();
        if (lower.Contains("-enc") || lower.Contains("frombase64") ||
            lower.Contains("mshta") || lower.Contains("wscript") ||
            lower.Contains("powershell") && lower.Contains("http"))
            return true;

        if (!isSigned && (exePath.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase) ||
                          exePath.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase) ||
                          exePath.Contains(@"\ProgramData\", StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    private static string ExtractExecutablePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "";
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            if (end > 1) return Environment.ExpandEnvironmentVariables(command[1..end]);
        }
        int exeIdx = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIdx > 0)
            return Environment.ExpandEnvironmentVariables(command[..(exeIdx + 4)]);
        return Environment.ExpandEnvironmentVariables(command.Split(' ')[0]);
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        bool inQuotes = false;
        var current = new StringBuilder();
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        result.Add(current.ToString());
        return result;
    }

    private void RaiseEvent(string source, string message, FindingSeverity severity)
    {
        OnSecurityEvent?.Invoke(new SecurityEvent
        {
            Source = source,
            Message = message,
            Severity = severity
        });
    }

    private static readonly string HistoryFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "KochanSentinel", "security_history.json");

    public List<SecurityHistoryEntry> LoadHistory()
    {
        try
        {
            if (File.Exists(HistoryFilePath))
            {
                var list = JsonSerializer.Deserialize<List<SecurityHistoryEntry>>(File.ReadAllText(HistoryFilePath));
                if (list != null) return list;
            }
        }
        catch { }
        return new List<SecurityHistoryEntry>();
    }

    public void SaveHistory(IEnumerable<SecurityHistoryEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryFilePath)!);
            var trimmed = entries.Take(200).ToList();
            File.WriteAllText(HistoryFilePath, JsonSerializer.Serialize(trimmed, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public void Dispose()
    {
        StopHiddenProcessDetector();
        StopRealTimeShield();
    }
}
