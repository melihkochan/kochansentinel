using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KochanSentinel.App.ViewModels;

namespace KochanSentinel.App.Views;

public partial class HardwareStudioView : System.Windows.Controls.UserControl
{
    private readonly DispatcherTimer _brightnessDebounce;
    private readonly DispatcherTimer _contrastDebounce;
    private bool _loaded;

    public HardwareStudioView()
    {
        InitializeComponent();
        Loaded += (_, _) => _loaded = true;

        _brightnessDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _brightnessDebounce.Tick += (_, _) =>
        {
            _brightnessDebounce.Stop();
            if (DataContext is MainViewModel vm)
                vm.ApplyMonitorBrightnessLive(vm.MonitorBrightnessValue);
        };

        _contrastDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _contrastDebounce.Tick += (_, _) =>
        {
            _contrastDebounce.Stop();
            if (DataContext is MainViewModel vm)
                vm.ApplyMonitorContrastLive(vm.MonitorContrastValue);
        };
    }

    private void StudioSubTab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton rb &&
            int.TryParse(rb.Tag?.ToString(), out int subIdx) &&
            PanelMonitorColor != null && PanelDriversAudit != null && PanelServicesBloat != null)
        {
            PanelMonitorColor.Visibility = subIdx == 0 ? Visibility.Visible : Visibility.Collapsed;
            PanelDriversAudit.Visibility = subIdx == 1 ? Visibility.Visible : Visibility.Collapsed;
            PanelServicesBloat.Visibility = subIdx == 2 ? Visibility.Visible : Visibility.Collapsed;

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

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded || !IsVisible) return;
        _brightnessDebounce.Stop();
        _brightnessDebounce.Start();
    }

    private void ContrastSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded || !IsVisible) return;
        _contrastDebounce.Stop();
        _contrastDebounce.Start();
    }
}
