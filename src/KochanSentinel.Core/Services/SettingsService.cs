using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using System.Text.Json;
using KochanSentinel.Core.Models;
using Microsoft.Win32;

namespace KochanSentinel.Core.Services;

public sealed class SettingsService
{
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
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                settings = JsonSerializer.Deserialize<AppSettings>(json);
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
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
            return ApplyWindowsStartup(settings.StartWithWindows);
        }
        catch
        {
            return false;
        }
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
