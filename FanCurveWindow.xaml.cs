using System.Windows;
using System.Windows.Input;
using SystemPulse.ViewModels;

namespace SystemPulse;

public partial class FanCurveWindow : Window
{
    private readonly FanChannelViewModel _channel;
    private readonly FanChannelSettings _original;
    private bool _accepted;
    public FanCurveWindow(FanChannelViewModel channel)
    {
        _channel = channel;
        _original = channel.ToSettings();
        InitializeComponent();
        DataContext = channel;
        Loaded += (_, _) => FitToOwner();
    }
    private void FitToOwner()
    {
        var availableWidth = Owner?.ActualWidth ?? SystemParameters.WorkArea.Width;
        var availableHeight = Owner?.ActualHeight ?? SystemParameters.WorkArea.Height;
        Width = Math.Min(780, Math.Max(MinWidth, availableWidth - 80));
        Height = Math.Min(760, Math.Max(MinHeight, availableHeight - 80));
    }
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void Apply_Click(object sender, RoutedEventArgs e) { _accepted = true; DialogResult = true; Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_accepted) _channel.Apply(_original);
        base.OnClosing(e);
    }
}
