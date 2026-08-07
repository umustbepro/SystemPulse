using SystemPulse.ViewModels;

namespace SystemPulse.Controls;

public partial class FanControlPage : System.Windows.Controls.UserControl, IDisposable
{
    public FanControlViewModel ViewModel { get; } = new();
    public FanControlPage()
    {
        InitializeComponent();
        DataContext = ViewModel;
    }
    private void ConfigureCurve_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if ((sender as System.Windows.FrameworkElement)?.DataContext is not FanChannelViewModel channel)
            return;
        var owner = System.Windows.Window.GetWindow(this);
        var dialog = new FanCurveWindow(channel) { Owner = owner };
        _ = dialog.ShowDialog();
    }
    private void FanCategory_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton { Tag: string category })
            ViewModel.SelectCategory(category);
    }
    public void Dispose() => ViewModel.Dispose();
}
