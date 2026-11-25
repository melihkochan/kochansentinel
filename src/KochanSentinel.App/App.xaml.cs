using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace KochanSentinel.App;

public partial class App : System.Windows.Application
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "KochanSentinel");
    public static readonly string CleanExitFlagPath = Path.Combine(ConfigDir, "clean_exit.flag");

    private Mutex? _singleInstanceMutex;
    private Process? _watchdogProc;

    protected override void OnStartup(StartupEventArgs e)
    {
        SetupExceptionLogging();

        // Companion Watchdog Mode (Self-Defense): relaunches KOCHAN Sentinel if terminated externally without clean_exit.flag
        if (e.Args.Length >= 3 && e.Args[0].Equals("--watchdog", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(e.Args[1], out int targetPid))
        {
            RunWatchdogLoop(targetPid, e.Args[2]);
            Shutdown();
            return;
        }

        _singleInstanceMutex = new Mutex(true, "KochanSentinel_SingleInstance_Mutex", out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                "KOCHAN Sentinel arka planda zaten çalışıyor. Sağ alttaki sistem tepsisinden açabilirsiniz.",
                "KOCHAN Sentinel",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        try { if (File.Exists(CleanExitFlagPath)) File.Delete(CleanExitFlagPath); } catch { }

        base.OnStartup(e);

        bool startMinimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        var mainWindow = new MainWindow(startMinimized);
        MainWindow = mainWindow;
        if (!startMinimized)
            mainWindow.Show();

        TryStartWatchdogHelper(mainWindow.IsSelfDefenseEnabled);
    }

    public void SyncWatchdogState(bool enable)
    {
        if (enable)
            TryStartWatchdogHelper(true);
        else
            StopWatchdogHelper();
    }

    private void TryStartWatchdogHelper(bool enable)
    {
        if (!enable) return;
        try
        {
            if (_watchdogProc != null && !_watchdogProc.HasExited) return;
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            if (exe.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                var c = Path.ChangeExtension(exe, ".exe");
                if (File.Exists(c)) exe = c;
            }
            _watchdogProc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--watchdog {Environment.ProcessId} \"{exe}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch { }
    }

    private void StopWatchdogHelper()
    {
        try
        {
            if (_watchdogProc != null && !_watchdogProc.HasExited)
                _watchdogProc.Kill();
        }
        catch { }
        _watchdogProc = null;
    }

    public static void MarkCleanExit()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(CleanExitFlagPath, DateTime.UtcNow.Ticks.ToString());
        }
        catch { }
    }

    private static void RunWatchdogLoop(int parentPid, string exePath)
    {
        try
        {
            using var proc = Process.GetProcessById(parentPid);
            proc.WaitForExit();

            // Check whether user/system closed KOCHAN cleanly
            if (File.Exists(CleanExitFlagPath))
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(CleanExitFlagPath);
                if (age.TotalSeconds < 20)
                    return;
            }

            // Unexpected external termination (taskkill / RAT kill) -> recover shield cleanly!
            Thread.Sleep(450);
            if (File.Exists(exePath))
            {
                Process.Start(new ProcessStartInfo(exePath, "--minimized") { UseShellExecute = true });
            }
        }
        catch { }
    }

    private void SetupExceptionLogging()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                LogCrash("AppDomain.UnhandledException", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }

    public static void LogCrash(string source, Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KochanSentinel");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex}\r\n\r\n");
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        MarkCleanExit();
        StopWatchdogHelper();
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch { }
        base.OnExit(e);
    }
}
