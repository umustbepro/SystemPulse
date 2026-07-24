using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SystemPulse.Services;

internal static class NvmeHealthReader
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint StorageDeviceProtocolSpecificProperty = 50;
    private const uint PropertyStandardQuery = 0;
    private const uint ProtocolTypeNvme = 3;
    private const uint NvmeDataTypeLogPage = 2;
    private const uint NvmeLogPageHealthInfo = 2;
    private const int ProtocolSpecificDataSize = 40;
    private const int HealthLogSize = 512;
    private const int QueryHeaderSize = 8;

    public static NvmeHealthSnapshot? Read(int physicalDriveNumber)
    {
        try
        {
            using var handle = CreateFile(
                $@"\\.\PhysicalDrive{physicalDriveNumber}",
                0,
                FileShare.Read | FileShare.Write,
                IntPtr.Zero,
                FileMode.Open,
                0,
                IntPtr.Zero);
            if (handle.IsInvalid)
                return null;

            var buffer = new byte[QueryHeaderSize + ProtocolSpecificDataSize + HealthLogSize];
            Write(buffer, 0, StorageDeviceProtocolSpecificProperty);
            Write(buffer, 4, PropertyStandardQuery);
            Write(buffer, 8, ProtocolTypeNvme);
            Write(buffer, 12, NvmeDataTypeLogPage);
            Write(buffer, 16, NvmeLogPageHealthInfo);
            Write(buffer, 20, 0);
            Write(buffer, 24, ProtocolSpecificDataSize);
            Write(buffer, 28, HealthLogSize);

            if (!DeviceIoControl(handle, IoctlStorageQueryProperty, buffer, buffer.Length, buffer, buffer.Length, out var returned, IntPtr.Zero) || returned < 48)
                return null;

            var descriptorVersion = ReadUInt32(buffer, 0);
            var descriptorSize = ReadUInt32(buffer, 4);
            var dataOffset = ReadUInt32(buffer, 24);
            var dataLength = ReadUInt32(buffer, 28);
            if (descriptorVersion < 48 || descriptorSize < 48 || dataOffset < ProtocolSpecificDataSize || dataLength < HealthLogSize)
                return null;

            var healthOffset = checked(QueryHeaderSize + (int)dataOffset);
            if (healthOffset < 0 || healthOffset + HealthLogSize > buffer.Length)
                return null;

            var kelvin = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(healthOffset + 1, 2));
            float? temperature = kelvin is > 273 and < 473 ? kelvin - 273f : null;
            var percentageUsed = buffer[healthOffset + 5];
            var criticalWarning = buffer[healthOffset];
            return new NvmeHealthSnapshot(
                temperature,
                percentageUsed <= 100 ? percentageUsed : (byte)100,
                ReadUInt128Low(buffer, healthOffset + 128),
                ReadUInt128Low(buffer, healthOffset + 160),
                ReadUInt128Low(buffer, healthOffset + 144),
                criticalWarning);
        }
        catch
        {
            return null;
        }
    }

    private static ulong ReadUInt128Low(byte[] buffer, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset, 8));
    private static uint ReadUInt32(byte[] buffer, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4));
    private static void Write(byte[] buffer, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), value);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        [In, Out] byte[] inputBuffer,
        int inputBufferSize,
        [In, Out] byte[] outputBuffer,
        int outputBufferSize,
        out int bytesReturned,
        IntPtr overlapped);
}

internal sealed record NvmeHealthSnapshot(
    float? Temperature,
    byte PercentageUsed,
    ulong PowerOnHours,
    ulong MediaErrors,
    ulong UnsafeShutdowns,
    byte CriticalWarning);
