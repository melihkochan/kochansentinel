using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KochanSentinel.App.ViewModels;

namespace KochanSentinel.App.Views;

public partial class HardwareStudioView : System.Windows.Controls.UserControl
{
    public HardwareStudioView()
    {
        InitializeComponent();
    }

    private void StudioSubTab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton rb &&
            int.TryParse(rb.Tag?.ToString(), out int subIdx) &&
            PanelMonitorColor != null && PanelDriversAudit != null &&
            PanelServicesBloat != null && PanelGithubTweaks != null)
        {
            PanelMonitorColor.Visibility = subIdx == 0 ? Visibility.Visible : Visibility.Collapsed;
            PanelDriversAudit.Visibility = subIdx == 1 ? Visibility.Visible : Visibility.Collapsed;
            PanelServicesBloat.Visibility = subIdx == 2 ? Visibility.Visible : Visibility.Collapsed;
            PanelGithubTweaks.Visibility = subIdx == 3 ? Visibility.Visible : Visibility.Collapsed;

            if (DataContext is MainViewModel vm)
            {
                if (subIdx == 0 && vm.Monitors.Count == 0)
                    _ = vm.ScanMonitorsCommand.ExecuteAsync(null);
                else if (subIdx == 1 && vm.DriverAuditItems.Count == 0 && !vm.IsScanningDrivers)
                    _ = vm.ScanHardwareDriversCommand.ExecuteAsync(null);
                else if (subIdx == 2 && vm.UnusedServices.Count == 0)
                    _ = vm.ScanServicesAndBloatCommand.ExecuteAsync(null);
            }
        }
    }

    private void BrightnessSlider_LostCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.ApplyMonitorHardwareBrightness(vm.MonitorBrightnessValue);
    }

    private void ContrastSlider_LostCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.ApplyMonitorHardwareContrast(vm.MonitorContrastValue);
    }
}
