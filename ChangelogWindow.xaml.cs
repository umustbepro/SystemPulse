using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

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
        var workArea = GetOwnerMonitorWorkArea();
        var ownerWidth = Owner?.ActualWidth > 0 ? Owner.ActualWidth : workArea.Width;
        var ownerHeight = Owner?.ActualHeight > 0 ? Owner.ActualHeight : workArea.Height;

        MinWidth = Math.Min(640, workArea.Width * 0.88);
        MinHeight = Math.Min(520, workArea.Height * 0.88);
        MaxWidth = Math.Max(MinWidth, workArea.Width * 0.94);
        MaxHeight = Math.Max(MinHeight, workArea.Height * 0.94);
        Width = Math.Clamp(ownerWidth * 0.78, MinWidth, Math.Min(1180, MaxWidth));
        Height = Math.Clamp(ownerHeight * 0.8, MinHeight, Math.Min(900, MaxHeight));

        WindowStartupLocation = WindowStartupLocation.Manual;
        var ownerLeft = Owner?.Left ?? workArea.Left;
        var ownerTop = Owner?.Top ?? workArea.Top;
        Left = Math.Clamp(ownerLeft + (ownerWidth - Width) / 2, workArea.Left, workArea.Right - Width);
        Top = Math.Clamp(ownerTop + (ownerHeight - Height) / 2, workArea.Top, workArea.Bottom - Height);
    }

    private Rect GetOwnerMonitorWorkArea()
    {
        var ownerHandle = Owner is null ? IntPtr.Zero : new WindowInteropHelper(Owner).Handle;
        var monitor = NativeMethods.MonitorFromWindow(ownerHandle, NativeMethods.MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info))
            return SystemParameters.WorkArea;

        var transform = Owner is null
            ? Matrix.Identity
            : PresentationSource.FromVisual(Owner)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new System.Windows.Point(info.WorkArea.Left, info.WorkArea.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(info.WorkArea.Right, info.WorkArea.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (eventArgs.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs eventArgs) => Close();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    private static class NativeMethods
    {
        internal const uint MonitorDefaultToNearest = 2;

        [DllImport("user32.dll")]
        internal static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    }
}
