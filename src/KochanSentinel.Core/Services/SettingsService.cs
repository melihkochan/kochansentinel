using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using System.Text.Json;
using KochanSentinel.Core.Interop;
using KochanSentinel.Core.Models;
using Microsoft.Win32;

namespace KochanSentinel.Core.Services;

public sealed class SettingsService
{
    private const string DpapiPrefix = "dpapi:";
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "KochanSentinel");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "settings.json");
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRunName = "KochanSentinel";
    public const string StartupTaskName = "KochanSentinelAutoStart";

    public AppSettings Load()
    {
        AppSettings? settings = null;
        bool migratedPlaintextSecret = false;
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                {
                    migratedPlaintextSecret |= UnprotectSecretField(settings.VirusTotalApiKey, out var vt);
                    settings.VirusTotalApiKey = vt;
                    migratedPlaintextSecret |= UnprotectSecretField(settings.MalwareBazaarAuthKey, out var mb);
                    settings.MalwareBazaarAuthKey = mb;
                    migratedPlaintextSecret |= UnprotectSecretField(settings.DiscordWebhookUrl, out var wh);
                    settings.DiscordWebhookUrl = wh;
                }
            }
        }
        catch { }

        if (settings == null)
        {
            // Fresh install: enable auto-startup by default
            settings = new AppSettings
            {
                StartWithWindows = true,
                StartupDefaultMigrated = true
            };
            Save(settings);
            return settings;
        }

        if (!settings.StartupDefaultMigrated)
        {
            // First launch after enabling default auto-startup (user can disable later in Settings)
            settings.StartWithWindows = true;
            settings.StartupDefaultMigrated = true;
            Save(settings);
            return settings;
        }

        if (migratedPlaintextSecret)
            Save(settings);

        // Keep Task Scheduler path synced if StartWithWindows is enabled
        if (settings.StartWithWindows)
            ApplyWindowsStartup(true);

        return settings;
    }

    public bool Save(AppSettings settings)
    {
        try
        {
            settings.StartupDefaultMigrated = true;
            Directory.CreateDirectory(ConfigDir);

            // Serialize a shallow copy with DPAPI-encrypted secret tokens
            var rawJson = JsonSerializer.Serialize(settings);
            var diskCopy = JsonSerializer.Deserialize<AppSettings>(rawJson) ?? settings;
            diskCopy.VirusTotalApiKey = ProtectSecretField(settings.VirusTotalApiKey);
            diskCopy.MalwareBazaarAuthKey = ProtectSecretField(settings.MalwareBazaarAuthKey);
            diskCopy.DiscordWebhookUrl = ProtectSecretField(settings.DiscordWebhookUrl);

            var json = JsonSerializer.Serialize(diskCopy, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
            return ApplyWindowsStartup(settings.StartWithWindows);
        }
        catch
        {
            return false;
        }
    }

    public static AppSettings CreateRedactedExportCopy(AppSettings settings)
    {
        var rawJson = JsonSerializer.Serialize(settings);
        var copy = JsonSerializer.Deserialize<AppSettings>(rawJson) ?? new AppSettings();
        copy.VirusTotalApiKey = "";
        copy.MalwareBazaarAuthKey = "";
        copy.DiscordWebhookUrl = "";
        return copy;
    }

    private static string ProtectSecretField(string? plain)
    {
        if (string.IsNullOrWhiteSpace(plain)) return "";
        if (plain.StartsWith(DpapiPrefix, StringComparison.Ordinal)) return plain;
        try
        {
            var encrypted = NativeMethods.ProtectDpapi(Encoding.UTF8.GetBytes(plain.Trim()));
            return DpapiPrefix + Convert.ToBase64String(encrypted);
        }
        catch
        {
            return plain;
        }
    }

    private static bool UnprotectSecretField(string? stored, out string plain)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            plain = "";
            return false;
        }
        if (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            try
            {
                var bytes = Convert.FromBase64String(stored[DpapiPrefix.Length..]);
                plain = Encoding.UTF8.GetString(NativeMethods.UnprotectDpapi(bytes));
                return false;
            }
            catch
            {
                plain = "";
                return false;
            }
        }
        // Plaintext legacy key -> signal migration to DPAPI
        plain = stored.Trim();
        return true;
    }

    private static bool ApplyWindowsStartup(bool enable)
    {
        // Always remove legacy HKCU\...\Run value because Windows blocks requireAdministrator EXEs on Run keys
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(AppRunName, throwOnMissingValue: false);
        }
        catch { }

        try
        {
            if (!enable)
            {
                RunSchtasks($"/Delete /TN \"{StartupTaskName}\" /F");
                return true;
            }

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return false;
            if (exePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                var candidate = Path.ChangeExtension(exePath, ".exe");
                if (File.Exists(candidate)) exePath = candidate;
            }

            // Create Task Scheduler logon task with HighestAvailable privileges (no UAC block at boot)
            string escapedExe = SecurityElement.Escape(exePath) ?? exePath;
            string escapedWorkDir = SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? "") ?? "";
            string xml = $"""
                <?xml version="1.0" encoding="UTF-16"?>
                <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                  <RegistrationInfo>
                    <Description>KOCHAN Sentinel arka plan sistem performansı ve güvenlik kalkanı otomatik başlatma görevi.</Description>
                  </RegistrationInfo>
                  <Triggers>
                    <LogonTrigger>
                      <Enabled>true</Enabled>
                      <Delay>PT3S</Delay>
                    </LogonTrigger>
                  </Triggers>
                  <Principals>
                    <Principal id="Author">
                      <LogonType>InteractiveToken</LogonType>
                      <RunLevel>HighestAvailable</RunLevel>
                    </Principal>
                  </Principals>
                  <Settings>
                    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                    <AllowHardTerminate>true</AllowHardTerminate>
                    <StartWhenAvailable>true</StartWhenAvailable>
                    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                    <Priority>5</Priority>
                  </Settings>
                  <Actions Context="Author">
                    <Exec>
                      <Command>"{escapedExe}"</Command>
                      <Arguments>--minimized</Arguments>
                      <WorkingDirectory>{escapedWorkDir}</WorkingDirectory>
                    </Exec>
                  </Actions>
                </Task>
                """;

            var tmpXml = Path.Combine(Path.GetTempPath(), $"kochan_autostart_{Environment.ProcessId}.xml");
            try
            {
                File.WriteAllText(tmpXml, xml, Encoding.Unicode);
                if (RunSchtasks($"/Create /TN \"{StartupTaskName}\" /XML \"{tmpXml}\" /F"))
                    return true;
            }
            finally
            {
                try { if (File.Exists(tmpXml)) File.Delete(tmpXml); } catch { }
            }

            return RunSchtasks($"/Create /TN \"{StartupTaskName}\" /TR \"\\\"{exePath}\\\" --minimized\" /SC ONLOGON /RL HIGHEST /F");
        }
        catch
        {
            return false;
        }
    }

    private static bool RunSchtasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(3500);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
