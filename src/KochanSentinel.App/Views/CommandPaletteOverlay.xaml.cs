using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KochanSentinel.App.ViewModels;

namespace KochanSentinel.App.Views;

public partial class CommandPaletteOverlay : System.Windows.Controls.UserControl
{
    public CommandPaletteOverlay()
    {
        InitializeComponent();
    }

    private void Overlay_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            Dispatcher.BeginInvoke(() =>
            {
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
                SearchBox.SelectAll();
            });
        }
    }

    private void Backdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.CloseCommandPaletteCommand.Execute(null);
    }

    private void Card_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void SearchBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        if (e.Key == Key.Escape)
        {
            vm.CloseCommandPaletteCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            vm.ExecutePaletteActionCommand.Execute(vm.SelectedPaletteAction);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && ResultsList.Items.Count > 0)
        {
            int idx = ResultsList.SelectedIndex;
            ResultsList.SelectedIndex = (idx + 1) % ResultsList.Items.Count;
            ResultsList.ScrollIntoView(ResultsList.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Up && ResultsList.Items.Count > 0)
        {
            int idx = ResultsList.SelectedIndex;
            ResultsList.SelectedIndex = idx <= 0 ? ResultsList.Items.Count - 1 : idx - 1;
            ResultsList.ScrollIntoView(ResultsList.SelectedItem);
            e.Handled = true;
        }
    }

    private void ResultsList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm && ResultsList.SelectedItem is CommandPaletteAction act)
            vm.ExecutePaletteActionCommand.Execute(act);
    }

    private void ResultsList_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm && ResultsList.SelectedItem is CommandPaletteAction act)
            vm.ExecutePaletteActionCommand.Execute(act);
    }
}
