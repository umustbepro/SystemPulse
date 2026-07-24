using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SystemPulse.Services.PawnIo;

/// <summary>
/// Minimal user-mode client for PawnIO's documented device-control protocol.
/// SystemPulse talks directly to the driver; PawnIOLib.dll is not required.
/// </summary>
internal sealed class PawnIoClient : IDisposable
{
    private const uint IoctlLoadBinary = 0xA1B22084;
    private const uint IoctlExecuteFunction = 0xA1B22104;
    private const int FunctionNameLength = 32;
    private const int CellSize = sizeof(ulong);
    private readonly SafeFileHandle _handle;

    private PawnIoClient(SafeFileHandle handle) => _handle = handle;

    public static PawnIoClient Load(string modulePath)
    {
        if (!File.Exists(modulePath))
            throw new PawnIoException($"PawnIO module is missing: {modulePath}");

        var handle = NativeMethods.CreateFile(
            @"\\?\GLOBALROOT\Device\PawnIO",
            NativeMethods.GenericRead | NativeMethods.GenericWrite,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            IntPtr.Zero,
            NativeMethods.OpenExisting,
            NativeMethods.FileAttributeNormal,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var code = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new PawnIoException("PawnIO driver is not available.", code);
        }

        var client = new PawnIoClient(handle);
        try
        {
            client.LoadBinary(File.ReadAllBytes(modulePath));
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public ulong ReadMsr(uint register) => Execute("ioctl_read_msr", [register], 1)[0];

    public uint ReadSmn(uint address) => (uint)Execute("ioctl_read_smn", [address], 1)[0];

    private void LoadBinary(byte[] module)
    {
        if (!NativeMethods.DeviceIoControl(
                _handle, IoctlLoadBinary, module, (uint)module.Length,
                null, 0, out _, IntPtr.Zero))
        {
            throw CreateIoException("PawnIO rejected the signed sensor module");
        }
    }

    private ulong[] Execute(string functionName, ulong[] input, int outputCellCount)
    {
        if (functionName.Length >= FunctionNameLength)
            throw new ArgumentOutOfRangeException(nameof(functionName));

        var inputBuffer = new byte[FunctionNameLength + input.Length * CellSize];
        var name = System.Text.Encoding.ASCII.GetBytes(functionName);
        Buffer.BlockCopy(name, 0, inputBuffer, 0, name.Length);
        Buffer.BlockCopy(input, 0, inputBuffer, FunctionNameLength, input.Length * CellSize);

        var outputBuffer = new byte[outputCellCount * CellSize];
        if (!NativeMethods.DeviceIoControl(
                _handle, IoctlExecuteFunction,
                inputBuffer, (uint)inputBuffer.Length,
                outputBuffer, (uint)outputBuffer.Length,
                out var bytesReturned, IntPtr.Zero))
        {
            throw CreateIoException($"PawnIO sensor call '{functionName}' failed");
        }

        if (bytesReturned < (uint)outputBuffer.Length)
            throw new PawnIoException($"PawnIO returned {bytesReturned} bytes; {outputBuffer.Length} were expected.");

        var output = new ulong[outputCellCount];
        Buffer.BlockCopy(outputBuffer, 0, output, 0, outputBuffer.Length);
        return output;
    }

    private static PawnIoException CreateIoException(string message)
    {
        var code = Marshal.GetLastWin32Error();
        return new PawnIoException($"{message}: {new Win32Exception(code).Message}", code);
    }

    public void Dispose() => _handle.Dispose();

    private static class NativeMethods
    {
        internal const uint GenericRead = 0x80000000;
        internal const uint GenericWrite = 0x40000000;
        internal const uint FileShareRead = 0x00000001;
        internal const uint FileShareWrite = 0x00000002;
        internal const uint OpenExisting = 3;
        internal const uint FileAttributeNormal = 0x00000080;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint controlCode,
            byte[] inputBuffer,
            uint inputBufferSize,
            [Out] byte[]? outputBuffer,
            uint outputBufferSize,
            out uint bytesReturned,
            IntPtr overlapped);
    }
}

internal sealed class PawnIoException : Exception
{
    public PawnIoException(string message, int nativeErrorCode = 0) : base(message) =>
        NativeErrorCode = nativeErrorCode;

    public int NativeErrorCode { get; }
}
