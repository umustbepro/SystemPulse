using System.Windows;
using System.Windows.Input;

namespace SystemPulse;

public partial class ChangelogWindow : Window
{
    public ChangelogWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => SizeForDisplay();
    }

    private void SizeForDisplay()
    {
        var workArea = SystemParameters.WorkArea;
        Width = Math.Clamp(workArea.Width * 0.72, MinWidth, 1180);
        Height = Math.Clamp(workArea.Height * 0.82, MinHeight, 900);
        MaxWidth = Math.Max(MinWidth, workArea.Width * 0.94);
        MaxHeight = Math.Max(MinHeight, workArea.Height * 0.94);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs eventArgs) => Close();
}
