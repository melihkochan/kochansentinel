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
    private readonly HashSet<string> _blockedSanitizedNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public FirewallService()
    {
        _ = RefreshBlockedRulesAsync();
    }

    public async Task RefreshBlockedRulesAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("netsh.exe", "advfirewall firewall show rule name=all dir=out")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            };
            using var p = Process.Start(psi);
            if (p == null) return;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();

            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int idx = line.IndexOf(RulePrefix, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var name = line[(idx + RulePrefix.Length)..].Trim();
                    if (!string.IsNullOrEmpty(name))
                        found.Add(name);
                }
            }

            lock (_lock)
            {
                _blockedSanitizedNames.Clear();
                foreach (var item in found) _blockedSanitizedNames.Add(item);
            }
        }
        catch { }
    }

    public bool IsBlocked(string processName)
    {
        var key = SanitizeName(processName);
        lock (_lock)
            return _blockedSanitizedNames.Contains(key);
    }

    public List<FirewallRuleItem> GetBlockedRulesSnapshot()
    {
        lock (_lock)
        {
            return _blockedSanitizedNames
                .OrderBy(n => n)
                .Select(n => new FirewallRuleItem
                {
                    RuleName = RulePrefix + n,
                    ProcessName = n,
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
            lock (_lock) _blockedSanitizedNames.Add(sanitized);
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
            lock (_lock) _blockedSanitizedNames.Remove(sanitized);
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
