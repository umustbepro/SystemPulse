using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using SystemPulse.Models;
using SystemPulse.Services;

namespace SystemPulse.ViewModels;

public sealed class StorageCleanupViewModel : INotifyPropertyChanged
{
    private readonly StorageCleanupService _service = new();
    private readonly Queue<CleanupCandidate> _reviewQueue = new();
    private List<CleanupCandidate> _temporaryFiles = new();
    private CleanupCandidate? _currentReview;
    private bool _isScanning;
    private string _scanStatus = "Choose a drive and start a scan.";
    private string _outputLog = "Storage Cleanup is ready. No files are deleted during scanning.";
    private string _temporarySummary = "No temporary files scanned";

    public StorageCleanupViewModel()
    {
        ScanDriveCommand = new AsyncRelayCommand(ScanDriveAsync, parameter => parameter is CleanupDriveItem && !IsScanning);
        DeleteTemporaryFilesCommand = new AsyncRelayCommand(DeleteTemporaryFilesAsync, _ => HasTemporaryFiles && !IsScanning);
        DeleteReviewFileCommand = new AsyncRelayCommand(DeleteReviewFileAsync, _ => HasReviewCandidate && !IsScanning);
        SkipReviewFileCommand = new RelayCommand(_ => SkipReviewFile(), _ => HasReviewCandidate && !IsScanning);
        OpenReviewFolderCommand = new RelayCommand(_ => OpenReviewFolder(), _ => HasReviewCandidate);
        RefreshDrives();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<CleanupDriveItem> Drives { get; } = new();
    public ICommand ScanDriveCommand { get; }
    public ICommand DeleteTemporaryFilesCommand { get; }
    public ICommand DeleteReviewFileCommand { get; }
    public ICommand SkipReviewFileCommand { get; }
    public ICommand OpenReviewFolderCommand { get; }

    public bool IsScanning { get => _isScanning; private set { if (Set(ref _isScanning, value)) RefreshCommands(); } }
    public string ScanStatus { get => _scanStatus; private set => Set(ref _scanStatus, value); }
    public string OutputLog { get => _outputLog; private set => Set(ref _outputLog, value); }
    public string TemporarySummary { get => _temporarySummary; private set => Set(ref _temporarySummary, value); }
    public bool HasTemporaryFiles => _temporaryFiles.Count > 0;
    public bool HasReviewCandidate => _currentReview is not null;
    public string ReviewPath => _currentReview?.FullPath ?? "No file awaiting review";
    public string ReviewDetails => _currentReview is null
        ? "Files requiring approval will appear here."
        : $"{FormatBytes(_currentReview.SizeBytes)} · Last activity {_currentReview.LastActivity:yyyy-MM-dd}\n" +
          (_service.WillDeleteContainingFolder(_currentReview)
              ? "Approval deletes this EXE's containing folder and all associated files."
              : "Approval deletes this file only.");
    public string ReviewDeleteLabel => _currentReview is not null && _service.WillDeleteContainingFolder(_currentReview)
        ? "Delete EXE folder"
        : "Delete file";

    private void RefreshDrives()
    {
        Drives.Clear();
        foreach (var drive in _service.GetDrives())
            Drives.Add(new CleanupDriveItem(drive));
    }

    private async Task ScanDriveAsync(object? parameter)
    {
        if (parameter is not CleanupDriveItem drive)
            return;

        IsScanning = true;
        ScanStatus = $"Scanning {drive.DisplayName}…";
        OutputLog = $"Started a read-only scan of {drive.RootPath}\n";
        _temporaryFiles.Clear();
        _reviewQueue.Clear();
        SetCurrentReview(null);
        NotifyCandidateState();

        try
        {
            var result = await Task.Run(() => _service.Scan(drive.RootPath));
            _temporaryFiles = result.TemporaryFiles.ToList();
            foreach (var candidate in result.ReviewFiles)
                _reviewQueue.Enqueue(candidate);

            TemporarySummary = _temporaryFiles.Count == 0
                ? "No eligible temporary files found"
                : $"{_temporaryFiles.Count:N0} temporary files · {FormatBytes(_temporaryFiles.Sum(file => file.SizeBytes))}";
            AppendLog($"Scan complete: {TemporarySummary}.");
            AppendLog($"{_reviewQueue.Count:N0} large files inactive for 6+ months require individual review.");
            if (result.SkippedLocations > 0)
                AppendLog($"Skipped {result.SkippedLocations:N0} protected or inaccessible locations.");
            AdvanceReview();
            ScanStatus = $"Scan complete for {drive.DisplayName}";
        }
        catch (Exception exception)
        {
            ScanStatus = "Scan could not be completed.";
            AppendLog($"Scan error: {exception.Message}");
        }
        finally
        {
            IsScanning = false;
            NotifyCandidateState();
        }
    }

    private async Task DeleteTemporaryFilesAsync(object? parameter)
    {
        if (!HasTemporaryFiles)
            return;
        IsScanning = true;
        var candidates = _temporaryFiles.ToList();
        _temporaryFiles.Clear();
        var result = await Task.Run(() =>
        {
            var deleted = 0;
            long bytes = 0;
            foreach (var candidate in candidates)
            {
                var deletion = _service.Delete(candidate);
                if (!deletion.Success)
                    continue;
                deleted++;
                bytes += candidate.SizeBytes;
            }
            return (deleted, bytes);
        });
        TemporarySummary = "Temporary cleanup complete";
        AppendLog($"Deleted {result.deleted:N0} temporary files and recovered {FormatBytes(result.bytes)}.");
        IsScanning = false;
        NotifyCandidateState();
    }

    private async Task DeleteReviewFileAsync(object? parameter)
    {
        var candidate = _currentReview;
        if (candidate is null)
            return;
        IsScanning = true;
        var result = await Task.Run(() => _service.DeleteApprovedReview(candidate));
        AppendLog(result.Success
            ? $"Deleted after approval: {result.Message}"
            : $"Could not delete {candidate.FullPath}: {result.Message}");
        IsScanning = false;
        AdvanceReview();
    }

    private void SkipReviewFile()
    {
        if (_currentReview is not null)
            AppendLog($"Kept: {_currentReview.FullPath}");
        AdvanceReview();
    }

    private void OpenReviewFolder()
    {
        if (_currentReview is null)
            return;
        try
        {
            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add("/select,");
            startInfo.ArgumentList.Add(_currentReview.FullPath);
            _ = Process.Start(startInfo);
            AppendLog($"Opened source folder: {_currentReview.FullPath}");
        }
        catch (Exception exception)
        {
            AppendLog($"Could not open the source folder: {exception.Message}");
        }
    }

    private void AdvanceReview()
    {
        SetCurrentReview(_reviewQueue.Count > 0 ? _reviewQueue.Dequeue() : null);
        if (_currentReview is not null)
            AppendLog($"Review required: {_currentReview.FullPath}");
        else
            AppendLog("No more non-temporary files are awaiting review.");
    }

    private void SetCurrentReview(CleanupCandidate? candidate)
    {
        _currentReview = candidate;
        OnPropertyChanged(nameof(HasReviewCandidate));
        OnPropertyChanged(nameof(ReviewPath));
        OnPropertyChanged(nameof(ReviewDetails));
        OnPropertyChanged(nameof(ReviewDeleteLabel));
        RefreshCommands();
    }

    private void NotifyCandidateState()
    {
        OnPropertyChanged(nameof(HasTemporaryFiles));
        OnPropertyChanged(nameof(HasReviewCandidate));
        RefreshCommands();
    }

    private void AppendLog(string message) => OutputLog += $"\n{DateTime.Now:HH:mm:ss}  {message}";

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / Math.Pow(1024, 3):0.##} GB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / Math.Pow(1024, 2):0.##} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024d:0.##} KB";
        return $"{bytes} B";
    }

    private void RefreshCommands() => CommandManager.InvalidateRequerySuggested();

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public sealed class CleanupDriveItem
    {
        public CleanupDriveItem(CleanupDriveInfo drive)
        {
            RootPath = drive.RootPath;
            DisplayName = drive.DisplayName;
            SpaceText = $"{FormatBytes(drive.FreeBytes)} free of {FormatBytes(drive.TotalBytes)}";
        }

        public string RootPath { get; }
        public string DisplayName { get; }
        public string SpaceText { get; }
    }

    private sealed class RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
        public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => execute(parameter);
    }

    private sealed class AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
        public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
        public async void Execute(object? parameter) => await execute(parameter);
    }
}
