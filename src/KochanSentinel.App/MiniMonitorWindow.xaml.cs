using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using KochanSentinel.Core.Models;

namespace KochanSentinel.App;

public partial class MiniMonitorWindow : Window
{
    private static readonly string PosPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "KochanSentinel", "mini_hud_pos.json");

    private readonly ObservableCollection<double> _cpuPts = new();
    private readonly ObservableCollection<double> _ramPts = new();

    public event Func<Task<string>>? OnBoostAsyncRequested;
    public event Action? OnOpenMainRequested;
    public event Action? OnClosedByUser;

    public MiniMonitorWindow()
    {
        InitializeComponent();
        for (int i = 0; i < 24; i++)
        {
            _cpuPts.Add(0);
            _ramPts.Add(0);
        }
        SparkCpu.Values = _cpuPts;
        SparkRam.Values = _ramPts;
        LoadPosition();
    }

    private void LoadPosition()
    {
        try
        {
            if (File.Exists(PosPath))
            {
                var pos = JsonSerializer.Deserialize<double[]>(File.ReadAllText(PosPath));
                if (pos is { Length: 2 })
                {
                    double minX = SystemParameters.VirtualScreenLeft;
                    double maxX = minX + SystemParameters.VirtualScreenWidth - 180;
                    double minY = SystemParameters.VirtualScreenTop;
                    double maxY = minY + SystemParameters.VirtualScreenHeight - 40;
                    Left = Math.Clamp(pos[0], minX, Math.Max(minX, maxX));
                    Top = Math.Clamp(pos[1], minY, Math.Max(minY, maxY));
                    return;
                }
            }
        }
        catch { }

        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + 14;
        Top = workArea.Top + 14;
    }

    private void SavePosition()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PosPath)!);
            File.WriteAllText(PosPath, JsonSerializer.Serialize(new[] { Left, Top }));
        }
        catch { }
    }

    public void ApplyPreferences(AppSettings settings)
    {
        PanelHudCpu.Visibility = settings.HudShowCpu ? Visibility.Visible : Visibility.Collapsed;
        PanelHudRam.Visibility = settings.HudShowRam ? Visibility.Visible : Visibility.Collapsed;
        PanelHudGpu.Visibility = settings.HudShowGpu ? Visibility.Visible : Visibility.Collapsed;
        PanelHudFps.Visibility = settings.HudShowFps ? Visibility.Visible : Visibility.Collapsed;
        PanelHudNet.Visibility = settings.HudShowNet ? Visibility.Visible : Visibility.Collapsed;
        PanelHudPing.Visibility = settings.HudShowPing ? Visibility.Visible : Visibility.Collapsed;
        HudRootBorder.Opacity = Math.Clamp(settings.HudOpacity, 0.45, 1.0);
    }

    public void UpdateMetrics(SystemMetrics m)
    {
        string cpuTemp = m.CpuTempCelsius.HasValue ? $" ({m.CpuTempCelsius:F0}°)" : "";
        TxtCpu.Text = $"{m.CpuUsagePercent:F0}%{cpuTemp}";
        TxtRam.Text = $"{m.RamUsagePercent:F0}% ({m.RamUsedGb:F1}G)";

        if (m.GpuTempCelsius.HasValue || m.GpuLoadPercent.HasValue)
        {
            string gl = m.GpuLoadPercent.HasValue ? $"{m.GpuLoadPercent:F0}%" : "";
            string gt = m.GpuTempCelsius.HasValue ? $" {m.GpuTempCelsius:F0}°" : "";
            TxtGpu.Text = (gl + gt).Trim();
        }
        else
        {
            TxtGpu.Text = "--";
        }

        TxtNet.Text = $"↓{m.NetworkDownloadMbps:F1}M";
        TxtFps.Text = m.CurrentFps.HasValue ? $"{m.CurrentFps}" : "--";
        TxtPing.Text = m.PingMs.HasValue ? $"{m.PingMs}ms" : "--";

        _cpuPts.Add(m.CpuUsagePercent);
        if (_cpuPts.Count > 24) _cpuPts.RemoveAt(0);
        _ramPts.Add(m.RamUsagePercent);
        if (_ramPts.Count > 24) _ramPts.RemoveAt(0);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 1)
        {
            try
            {
                DragMove();
                SavePosition();
            }
            catch { }
        }
    }

    private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OnOpenMainRequested?.Invoke();
    }

    private async void BoostBtn_Click(object sender, RoutedEventArgs e)
    {
        if (OnBoostAsyncRequested == null) return;
        BtnBoost.IsEnabled = false;
        BtnBoost.Content = "⏳";
        TxtBoostFeedback.Text = "Optimize ediliyor...";
        BoostFeedbackPill.Visibility = Visibility.Visible;

        try
        {
            string summary = await OnBoostAsyncRequested.Invoke();
            TxtBoostFeedback.Text = summary;
            await Task.Delay(3500);
        }
        catch
        {
            TxtBoostFeedback.Text = "Tamamlandı";
            await Task.Delay(2000);
        }
        finally
        {
            BoostFeedbackPill.Visibility = Visibility.Collapsed;
            BtnBoost.Content = "⚡";
            BtnBoost.IsEnabled = true;
        }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        OnClosedByUser?.Invoke();
    }
}
