using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KochanSentinel.Core.Models;

namespace KochanSentinel.Core.Services;

public sealed class FirewallService
{
    private const string RulePrefix = "KOCHAN_BLOCK_";
    private readonly Dictionary<string, string> _blockedRulesMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public FirewallService()
    {
        _ = RefreshBlockedRulesAsync();
    }

    public async Task RefreshBlockedRulesAsync()
    {
        await Task.Run(() =>
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool usedCom = false;

            // 1. Fast, locale-independent Windows Firewall COM API (HNetCfg.FwPolicy2)
            try
            {
                var fwType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
                if (fwType != null)
                {
                    dynamic? policy = Activator.CreateInstance(fwType);
                    if (policy != null)
                    {
                        foreach (dynamic rule in policy.Rules)
                        {
                            try
                            {
                                string name = rule.Name?.ToString() ?? "";
                                if (name.StartsWith(RulePrefix, StringComparison.OrdinalIgnoreCase))
                                {
                                    string key = name[RulePrefix.Length..].Trim();
                                    string appPath = rule.ApplicationName?.ToString() ?? "";
                                    if (!string.IsNullOrEmpty(key))
                                        found[key] = appPath;
                                }
                            }
                            catch { }
                        }
                        usedCom = true;
                    }
                }
            }
            catch { }

            // 2. Fallback to netsh verbose output if COM unavailable
            if (!usedCom)
            {
                try
                {
                    var psi = new ProcessStartInfo("netsh.exe", "advfirewall firewall show rule name=all dir=out verbose")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true
                    };
                    using var p = Process.Start(psi);
                    if (p != null)
                    {
                        var output = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(5000);
                        string? currentKey = null;
                        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            int idx = line.IndexOf(RulePrefix, StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0)
                            {
                                currentKey = line[(idx + RulePrefix.Length)..].Trim();
                                if (!string.IsNullOrEmpty(currentKey) && !found.ContainsKey(currentKey))
                                    found[currentKey] = "";
                            }
                            else if (currentKey != null && line.Contains(":\\", StringComparison.Ordinal))
                            {
                                int colon = line.IndexOf(':');
                                if (colon > 0 && colon + 1 < line.Length)
                                {
                                    var candidate = line[(colon + 1)..].Trim();
                                    if (candidate.Contains(":\\"))
                                    {
                                        found[currentKey] = candidate;
                                        currentKey = null;
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            lock (_lock)
            {
                _blockedRulesMap.Clear();
                foreach (var kv in found) _blockedRulesMap[kv.Key] = kv.Value;
            }
        });
    }

    public bool IsBlocked(string processName)
    {
        var key = SanitizeName(processName);
        lock (_lock)
            return _blockedRulesMap.ContainsKey(key);
    }

    public List<FirewallRuleItem> GetBlockedRulesSnapshot()
    {
        lock (_lock)
        {
            return _blockedRulesMap
                .OrderBy(kv => kv.Key)
                .Select(kv => new FirewallRuleItem
                {
                    RuleName = RulePrefix + kv.Key,
                    ProcessName = kv.Key,
                    ProgramPath = string.IsNullOrWhiteSpace(kv.Value) ? "Kural Kayıtlı (Yol belirtilmedi)" : kv.Value,
                    Enabled = true
                })
                .ToList();
        }
    }

    public async Task<(bool Ok, string Message)> BlockOutboundAsync(string exePath, string processName)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return (false, "Uygulama yolu bulunamadı.");

        string sanitized = SanitizeName(processName);
        string ruleName = RulePrefix + sanitized;
        if (await RunNetshAsync($"advfirewall firewall add rule name=\"{ruleName}\" dir=out action=block program=\"{exePath}\" enable=yes"))
        {
            lock (_lock) _blockedRulesMap[sanitized] = exePath;
            return (true, $"{processName} internet erişimi Güvenlik Duvarında engellendi.");
        }
        return (false, $"{processName} engellenemedi (Yönetici izni gerekir).");
    }

    public async Task<(bool Ok, string Message)> UnblockOutboundAsync(string processName)
    {
        string sanitized = SanitizeName(processName);
        string ruleName = RulePrefix + sanitized;
        if (await RunNetshAsync($"advfirewall firewall delete rule name=\"{ruleName}\""))
        {
            lock (_lock) _blockedRulesMap.Remove(sanitized);
            return (true, $"{processName} internet engeli kaldırıldı.");
        }
        return (false, $"{processName} için engel kuralı bulunamadı.");
    }

    private static async Task<bool> RunNetshAsync(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string SanitizeName(string name)
        => string.Concat(name.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-'));
}
