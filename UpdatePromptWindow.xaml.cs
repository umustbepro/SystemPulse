using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SystemPulse.Services;

namespace SystemPulse;

public partial class UpdatePromptWindow : Window
{
    private readonly DispatcherTimer _countdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _secondsRemaining = 10;
    private bool _completed;

    public UpdatePromptWindow(UpdateRelease release)
    {
        InitializeComponent();
        VersionText.Text = $"Version {release.Tag} has been detected.";
        UpdateCountdownText();
        _countdownTimer.Tick += CountdownTimer_Tick;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) => _countdownTimer.Stop();
    }

    public UpdatePromptChoice Choice { get; private set; } = UpdatePromptChoice.Skip;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        FitToOwner();
        _countdownTimer.Start();
    }

    private void FitToOwner()
    {
        if (Owner?.ActualWidth is not > 0 || Owner.ActualHeight is not > 0)
            return;

        MinWidth = Math.Min(440, Owner.ActualWidth * 0.86);
        MinHeight = Math.Min(330, Owner.ActualHeight * 0.78);
        MaxWidth = Math.Max(MinWidth, Owner.ActualWidth * 0.92);
        MaxHeight = Math.Max(MinHeight, Owner.ActualHeight * 0.88);
        Width = Math.Clamp(600, MinWidth, MaxWidth);
        Height = Math.Clamp(390, MinHeight, MaxHeight);
    }

    private void CountdownTimer_Tick(object? sender, EventArgs e)
    {
        _secondsRemaining--;
        if (_secondsRemaining <= 0)
        {
            Complete(UpdatePromptChoice.Download);
            return;
        }
        UpdateCountdownText();
    }

    private void UpdateCountdownText() => CountdownText.Text =
        $"The update will begin automatically in {_secondsRemaining} second{(_secondsRemaining == 1 ? string.Empty : "s")}.";

    private void DownloadNowButton_Click(object sender, RoutedEventArgs e) =>
        Complete(UpdatePromptChoice.Download);

    private void SkipButton_Click(object sender, RoutedEventArgs e) =>
        Complete(UpdatePromptChoice.Skip);

    private void Complete(UpdatePromptChoice choice)
    {
        if (_completed)
            return;
        _completed = true;
        Choice = choice;
        _countdownTimer.Stop();
        DialogResult = choice == UpdatePromptChoice.Download;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_completed)
        {
            _completed = true;
            Choice = UpdatePromptChoice.Skip;
        }
        _countdownTimer.Stop();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}

public enum UpdatePromptChoice
{
    Download,
    Skip
}
