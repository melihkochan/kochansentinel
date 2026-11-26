using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    public bool RepairStartupRegistration(bool enable) => ApplyWindowsStartup(enable);

    public (bool Registered, bool TargetExists, string RegisteredPath, string StatusSummary) GetStartupRegistrationStatus(bool settingEnabled)
    {
        if (!settingEnabled)
            return (false, false, "", "○ Otomatik başlangıç kapalı (Ayarlar'dan dilediğin zaman açabilirsin).");

        // Use native UTF-16 Unicode Task Scheduler COM API (Schedule.Service) — zero OEM codepage / Turkish char loss!
        try
        {
            var svcType = Type.GetTypeFromProgID("Schedule.Service");
            if (svcType != null)
            {
                dynamic service = Activator.CreateInstance(svcType)!;
                service.Connect();
                dynamic root = service.GetFolder("\\");
                dynamic task;
                try
                {
                    task = root.GetTask(StartupTaskName);
                }
                catch
                {
                    return (false, false, "", "⚠ Görev Zamanlayıcı kaydı bulunamadı — 'Başlangıç Kaydını Yenile' butonuna tıkla.");
                }

                bool enabled = true;
                try { enabled = (bool)task.Enabled; } catch { }

                string rawCmd = "";
                try
                {
                    dynamic actions = task.Definition.Actions;
                    if ((int)actions.Count >= 1)
                    {
                        dynamic act = actions.Item(1); // 1-based COM collection
                        rawCmd = (act.Path?.ToString() ?? "").Trim().Trim('"');
                    }
                }
                catch { }

                if (!enabled)
                    return (true, false, rawCmd, "⚠ Başlangıç görevi devre dışı bırakılmış — 'Başlangıç Kaydını Yenile'ye tıkla.");

                bool exists = !string.IsNullOrWhiteSpace(rawCmd) && File.Exists(rawCmd);
                if (!exists)
                    return (true, false, rawCmd, $"⚠ Kayıtlı EXE yolu bulunamıyor ({rawCmd}) — 'Başlangıç Kaydını Yenile'ye tıkla!");

                bool isOneDrive = rawCmd.Contains("OneDrive", StringComparison.OrdinalIgnoreCase);
                string oneDriveHint = isOneDrive ? " (OneDrive gecikme/tekrar korumalı)" : "";
                return (true, true, rawCmd, $"✓ Görev Zamanlayıcı Aktif (UAC'sız Yönetici){oneDriveHint}\nHedef: {rawCmd}");
            }
        }
        catch { }

        return (false, false, "", "⚠ Görev Zamanlayıcı durumu okunamadı — 'Başlangıç Kaydını Yenile' butonuna tıkla.");
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

        // Primary path: Native UTF-16 Unicode Task Scheduler COM API (Schedule.Service)
        // Preserves Turkish/Unicode paths like "Masaüstü" with 100% fidelity and zero schtasks.exe codepage bugs.
        try
        {
            var svcType = Type.GetTypeFromProgID("Schedule.Service");
            if (svcType != null)
            {
                dynamic service = Activator.CreateInstance(svcType)!;
                service.Connect();
                dynamic root = service.GetFolder("\\");

                if (!enable)
                {
                    try { root.DeleteTask(StartupTaskName, 0); } catch { }
                    return true;
                }

                var exePath = ResolveCurrentExePath();
                if (string.IsNullOrEmpty(exePath)) return false;

                string userSid = "";
                try { userSid = WindowsIdentity.GetCurrent().User?.Value ?? ""; } catch { }

                dynamic td = service.NewTask(0);
                td.RegistrationInfo.Description = "KOCHAN Sentinel arka plan sistem performansı ve güvenlik kalkanı otomatik başlatma görevi.";
                td.Principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN
                td.Principal.RunLevel = 1;  // TASK_RUNLEVEL_HIGHEST (no UAC prompt at boot)
                if (!string.IsNullOrWhiteSpace(userSid))
                    td.Principal.UserId = userSid;

                td.Settings.Enabled = true;
                td.Settings.MultipleInstances = 2; // TASK_INSTANCES_IGNORE_NEW
                td.Settings.DisallowStartIfOnBatteries = false;
                td.Settings.StopIfGoingOnBatteries = false;
                td.Settings.AllowHardTerminate = true;
                td.Settings.StartWhenAvailable = true;
                td.Settings.RunOnlyIfNetworkAvailable = false;
                td.Settings.ExecutionTimeLimit = "PT0S";
                td.Settings.Priority = 5;
                td.Settings.RestartInterval = "PT1M";
                td.Settings.RestartCount = 2;

                dynamic trigger = td.Triggers.Create(9); // TASK_TRIGGER_LOGON
                trigger.Enabled = true;
                trigger.Delay = "PT8S";
                if (!string.IsNullOrWhiteSpace(userSid))
                    trigger.UserId = userSid;

                dynamic action = td.Actions.Create(0); // TASK_ACTION_EXEC
                action.Path = exePath; // Raw native UTF-16 BSTR — preserves Masaüstü / Unicode perfectly!
                action.Arguments = "--minimized";
                action.WorkingDirectory = Path.GetDirectoryName(exePath) ?? "";

                // 6 = TASK_CREATE_OR_UPDATE, 3 = TASK_LOGON_INTERACTIVE_TOKEN
                root.RegisterTaskDefinition(StartupTaskName, td, 6, null, null, 3, null);
                return true;
            }
        }
        catch { }

        // Fallback: schtasks.exe with UTF-16 XML file
        try
        {
            if (!enable)
            {
                RunSchtasks($"/Delete /TN \"{StartupTaskName}\" /F");
                return true;
            }

            var exePath = ResolveCurrentExePath();
            if (string.IsNullOrEmpty(exePath)) return false;

            string userSid = "";
            try { userSid = WindowsIdentity.GetCurrent().User?.Value ?? ""; } catch { }
            string userIdXml = string.IsNullOrWhiteSpace(userSid) ? "" : $"<UserId>{SecurityElement.Escape(userSid)}</UserId>";
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
                      <Delay>PT8S</Delay>
                      {userIdXml}
                    </LogonTrigger>
                  </Triggers>
                  <Principals>
                    <Principal id="Author">
                      {userIdXml}
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
                    <RestartOnFailure>
                      <Interval>PT1M</Interval>
                      <Count>2</Count>
                    </RestartOnFailure>
                    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                    <Priority>5</Priority>
                  </Settings>
                  <Actions Context="Author">
                    <Exec>
                      <Command>{escapedExe}</Command>
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
                return RunSchtasks($"/Create /TN \"{StartupTaskName}\" /XML \"{tmpXml}\" /F");
            }
            finally
            {
                try { if (File.Exists(tmpXml)) File.Delete(tmpXml); } catch { }
            }
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveCurrentExePath()
    {
        var exePath = Environment.ProcessPath ?? "";
        if (exePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var candidate = Path.ChangeExtension(exePath, ".exe");
            if (File.Exists(candidate)) exePath = candidate;
        }
        return exePath;
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
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(4500))
            {
                try { p.Kill(); } catch { }
                return false;
            }
            _ = outTask.Wait(500);
            _ = errTask.Wait(500);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
