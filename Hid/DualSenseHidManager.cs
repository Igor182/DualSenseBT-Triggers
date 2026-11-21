using System.Runtime.InteropServices;
using System.Text;
using DualSenseBTTriggers.Native;
using Microsoft.Win32.SafeHandles;

namespace DualSenseBTTriggers.Hid;

public sealed record ConnectedDualSenseInfo(string DevicePath, ushort ProductId, string ModelName);

public sealed record HidScanResult(bool AnyAdded, bool AnyRemoved, int ConnectedCount);

/// <summary>
/// Discovers and communicates with Sony DualSense / DualSense Edge controllers over Bluetooth
/// using non-exclusive Win32 HID handles (FILE_SHARE_READ | FILE_SHARE_WRITE).
/// </summary>
public sealed class DualSenseHidManager : IDisposable
{
    public const ushort SonyVendorId = 0x054C;
    public const ushort DualSenseProductId = 0x0CE6;
    public const ushort DualSenseEdgeProductId = 0x0DF2;

    private const byte CalibrationFeatureReportId = 0x05;

    private readonly object _syncLock = new();
    private readonly Dictionary<string, OpenDualSenseDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private string _lastDiagnosticReport = "Nenhuma varredura executada ainda.";
    private string _lastWriteStatus = "Nenhum pacote enviado ainda.";
    private bool _disposed;

    public bool IsConnected
    {
        get
        {
            lock (_syncLock)
            {
                return _devices.Count > 0;
            }
        }
    }

    public int ConnectedCount
    {
        get
        {
            lock (_syncLock)
            {
                return _devices.Count;
            }
        }
    }

    public string LastDiagnosticReport
    {
        get
        {
            lock (_syncLock)
            {
                return _lastDiagnosticReport;
            }
        }
    }

    public string LastWriteStatus
    {
        get
        {
            lock (_syncLock)
            {
                return _lastWriteStatus;
            }
        }
    }

    public IReadOnlyList<ConnectedDualSenseInfo> GetConnectedDevices()
    {
        lock (_syncLock)
        {
            return _devices.Values
                .Select(d => new ConnectedDualSenseInfo(d.DevicePath, d.ProductId, d.ModelName))
                .ToList();
        }
    }

    /// <summary>
    /// Scans Windows HID interfaces via SetupAPI and reconciles open Bluetooth DualSense handles.
    /// Bluetooth DualSense is distinguished from USB (48/64 bytes) by OutputReportByteLength >= 78
    /// (typically 78 or 547 depending on firmware/Windows BT stack).
    /// </summary>
    public HidScanResult RefreshDevices()
    {
        lock (_syncLock)
        {
            if (_disposed)
            {
                return new HidScanResult(false, false, 0);
            }

            var diag = new StringBuilder();
            Dictionary<string, DiscoveredBluetoothDevice> discovered = EnumerateBluetoothDualSenseDevices(diag);

            bool anyRemoved = false;
            bool anyAdded = false;

            List<string> stalePaths = _devices.Keys
                .Where(path => !discovered.ContainsKey(path))
                .ToList();

            foreach (string stalePath in stalePaths)
            {
                if (_devices.Remove(stalePath, out OpenDualSenseDevice? oldDevice))
                {
                    oldDevice.Dispose();
                    anyRemoved = true;
                }
            }

            foreach (DiscoveredBluetoothDevice candidate in discovered.Values)
            {
                if (_devices.ContainsKey(candidate.DevicePath))
                {
                    continue;
                }

                OpenDualSenseDevice? opened = TryOpenDevice(candidate, out int openError);
                if (opened is not null)
                {
                    _devices[candidate.DevicePath] = opened;
                    anyAdded = true;
                    diag.AppendLine($"  -> Aberto com sucesso (OutputLen={candidate.OutputReportByteLength}).");
                }
                else
                {
                    diag.AppendLine($"  -> Falha CreateFile(RW): Win32Error={openError}.");
                }
            }

            if (discovered.Count == 0 && diag.Length == 0)
            {
                diag.AppendLine("Nenhum dispositivo Sony (VID 0x054C) encontrado na árvore HID.");
            }

            _lastDiagnosticReport = diag.ToString().TrimEnd();
            return new HidScanResult(anyAdded, anyRemoved, _devices.Count);
        }
    }

    /// <summary>
    /// Writes a 78-byte Bluetooth HID output report to all connected DualSense Bluetooth controllers.
    /// Pads the WriteFile buffer if the OS HID descriptor reports OutputReportByteLength > 78 (e.g. 547).
    /// </summary>
    public bool SendOutputReport(byte[] report, out bool connectionLost)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Length != DualSensePacketBuilder.BluetoothOutputReportLength)
        {
            throw new ArgumentException(
                $"Bluetooth DualSense output report must be exactly {DualSensePacketBuilder.BluetoothOutputReportLength} bytes.",
                nameof(report));
        }

        connectionLost = false;

        lock (_syncLock)
        {
            if (_disposed || _devices.Count == 0)
            {
                return false;
            }

            bool anySucceeded = false;
            List<string>? disconnectedPaths = null;

            foreach (OpenDualSenseDevice device in _devices.Values)
            {
                if (device.Handle.IsInvalid || device.Handle.IsClosed)
                {
                    disconnectedPaths ??= [];
                    disconnectedPaths.Add(device.DevicePath);
                    continue;
                }

                byte[] writeBuffer = report;
                if (device.OutputReportByteLength > report.Length)
                {
                    writeBuffer = new byte[device.OutputReportByteLength];
                    Buffer.BlockCopy(report, 0, writeBuffer, 0, report.Length);
                }

                // On Windows Bluetooth, HidD_SetOutputReport (control pipe) is much more reliable
                // than WriteFile (interrupt pipe), because many BT adapters silently drop WriteFile.
                if (NativeMethods.HidD_SetOutputReport(device.Handle, writeBuffer, writeBuffer.Length))
                {
                    anySucceeded = true;
                    _lastWriteStatus = $"HidD_SetOutputReport({writeBuffer.Length} bytes): OK";
                    continue;
                }

                int setOutPaddedErr = Marshal.GetLastWin32Error();

                if (writeBuffer.Length != report.Length &&
                    NativeMethods.HidD_SetOutputReport(device.Handle, report, report.Length))
                {
                    anySucceeded = true;
                    _lastWriteStatus = $"HidD_SetOutputReport(78 bytes): OK (padded err={setOutPaddedErr})";
                    continue;
                }

                int setOut78Err = Marshal.GetLastWin32Error();

                // Fallback to WriteFile (interrupt pipe)
                bool written = NativeMethods.WriteFile(
                    device.Handle,
                    writeBuffer,
                    (uint)writeBuffer.Length,
                    out uint bytesWritten,
                    IntPtr.Zero);

                if (written && bytesWritten == writeBuffer.Length)
                {
                    anySucceeded = true;
                    _lastWriteStatus = $"WriteFile({writeBuffer.Length} bytes): OK (SetOutputReport err={setOutPaddedErr}/{setOut78Err})";
                    continue;
                }

                int writeError = Marshal.GetLastWin32Error();
                if (IsDisconnectError(writeError))
                {
                    _lastWriteStatus = $"Desconectado durante WriteFile (err={writeError})";
                    disconnectedPaths ??= [];
                    disconnectedPaths.Add(device.DevicePath);
                    continue;
                }

                _lastWriteStatus = $"FALHA envio: SetOut({writeBuffer.Length})={setOutPaddedErr}, SetOut(78)={setOut78Err}, WriteFile({writeBuffer.Length})={writeError}";
            }

            if (disconnectedPaths is not null)
            {
                foreach (string path in disconnectedPaths)
                {
                    if (_devices.Remove(path, out OpenDualSenseDevice? broken))
                    {
                        broken.Dispose();
                        connectionLost = true;
                    }
                }
            }

            return anySucceeded;
        }
    }

    private static Dictionary<string, DiscoveredBluetoothDevice> EnumerateBluetoothDualSenseDevices(StringBuilder diag)
    {
        var found = new Dictionary<string, DiscoveredBluetoothDevice>(StringComparer.OrdinalIgnoreCase);

        NativeMethods.HidD_GetHidGuid(out Guid hidGuid);
        IntPtr deviceInfoSet = NativeMethods.SetupDiGetClassDevs(
            ref hidGuid,
            null,
            IntPtr.Zero,
            NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_DEVICEINTERFACE);

        if (deviceInfoSet == IntPtr.Zero || deviceInfoSet == NativeMethods.InvalidHandleValue)
        {
            diag.AppendLine($"SetupDiGetClassDevs falhou: Win32Error={Marshal.GetLastWin32Error()}");
            return found;
        }

        try
        {
            var interfaceData = new NativeMethods.SP_DEVICE_INTERFACE_DATA
            {
                cbSize = Marshal.SizeOf<NativeMethods.SP_DEVICE_INTERFACE_DATA>()
            };

            uint memberIndex = 0;
            while (NativeMethods.SetupDiEnumDeviceInterfaces(
                deviceInfoSet,
                IntPtr.Zero,
                ref hidGuid,
                memberIndex,
                ref interfaceData))
            {
                memberIndex++;

                string? devicePath = GetDevicePath(deviceInfoSet, ref interfaceData);
                if (string.IsNullOrEmpty(devicePath))
                {
                    continue;
                }

                DiscoveredBluetoothDevice? probed = ProbeBluetoothDualSense(devicePath, diag);
                if (probed is not null)
                {
                    found[probed.DevicePath] = probed;
                }
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return found;
    }

    private static string? GetDevicePath(
        IntPtr deviceInfoSet,
        ref NativeMethods.SP_DEVICE_INTERFACE_DATA interfaceData)
    {
        NativeMethods.SetupDiGetDeviceInterfaceDetail(
            deviceInfoSet,
            ref interfaceData,
            IntPtr.Zero,
            0,
            out int requiredSize,
            IntPtr.Zero);

        if (requiredSize <= 4)
        {
            return null;
        }

        IntPtr detailBuffer = Marshal.AllocHGlobal(requiredSize);
        try
        {
            int structSize = IntPtr.Size == 8 ? 8 : 6;
            Marshal.WriteInt32(detailBuffer, structSize);

            bool ok = NativeMethods.SetupDiGetDeviceInterfaceDetail(
                deviceInfoSet,
                ref interfaceData,
                detailBuffer,
                requiredSize,
                out _,
                IntPtr.Zero);

            if (!ok)
            {
                return null;
            }

            return Marshal.PtrToStringUni(detailBuffer + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(detailBuffer);
        }
    }

    /// <summary>
    /// Probes HID metadata using access=0 (non-intrusive) to verify VID/PID and Bluetooth report capabilities.
    /// </summary>
    private static DiscoveredBluetoothDevice? ProbeBluetoothDualSense(string devicePath, StringBuilder diag)
    {
        using SafeFileHandle probeHandle = NativeMethods.CreateFile(
            devicePath,
            dwDesiredAccess: 0,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (probeHandle.IsInvalid)
        {
            return null;
        }

        var attributes = new NativeMethods.HIDD_ATTRIBUTES
        {
            Size = Marshal.SizeOf<NativeMethods.HIDD_ATTRIBUTES>()
        };

        if (!NativeMethods.HidD_GetAttributes(probeHandle, ref attributes))
        {
            return null;
        }

        if (attributes.VendorID != SonyVendorId)
        {
            return null;
        }

        if (!IsSupportedProductId(attributes.ProductID))
        {
            diag.AppendLine($"Sony ignorado: PID=0x{attributes.ProductID:X4}");
            return null;
        }

        if (!NativeMethods.HidD_GetPreparsedData(probeHandle, out IntPtr preparsedData) ||
            preparsedData == IntPtr.Zero)
        {
            diag.AppendLine($"DualSense PID=0x{attributes.ProductID:X4}: HidD_GetPreparsedData falhou (Win32Error={Marshal.GetLastWin32Error()})");
            return null;
        }

        try
        {
            int status = NativeMethods.HidP_GetCaps(preparsedData, out NativeMethods.HIDP_CAPS caps);
            if (status != NativeMethods.HIDP_STATUS_SUCCESS)
            {
                diag.AppendLine($"DualSense PID=0x{attributes.ProductID:X4}: HidP_GetCaps status=0x{status:X8}");
                return null;
            }

            diag.AppendLine(
                $"DualSense encontrado: PID=0x{attributes.ProductID:X4}, UsagePage=0x{caps.UsagePage:X2}, Usage=0x{caps.Usage:X2}, " +
                $"In={caps.InputReportByteLength}, Out={caps.OutputReportByteLength}, Feat={caps.FeatureReportByteLength}");

            // USB DualSense uses OutputReportByteLength <= 64 (48 or 64).
            // Bluetooth DualSense uses OutputReportByteLength >= 78 (78 or 547 depending on firmware/stack).
            if (caps.OutputReportByteLength < DualSensePacketBuilder.BluetoothOutputReportLength)
            {
                diag.AppendLine("  -> Ignorado: tratado como USB (OutputReportByteLength < 78).");
                return null;
            }

            return new DiscoveredBluetoothDevice(
                devicePath,
                attributes.ProductID,
                caps.OutputReportByteLength,
                caps.FeatureReportByteLength);
        }
        finally
        {
            NativeMethods.HidD_FreePreparsedData(preparsedData);
        }
    }

    private static OpenDualSenseDevice? TryOpenDevice(DiscoveredBluetoothDevice candidate, out int win32Error)
    {
        win32Error = 0;

        // Shared access (FILE_SHARE_READ | FILE_SHARE_WRITE) ensures Windows, Steam Input,
        // and games keep uninterrupted access to the DualSense controller.
        SafeFileHandle handle = NativeMethods.CreateFile(
            candidate.DevicePath,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            win32Error = Marshal.GetLastWin32Error();
            handle.Dispose();
            return null;
        }

        TryEnsureBluetoothExtendedMode(handle, candidate.FeatureReportByteLength);

        return new OpenDualSenseDevice(
            candidate.DevicePath,
            candidate.ProductId,
            GetModelName(candidate.ProductId),
            candidate.OutputReportByteLength,
            handle);
    }

    private static void TryEnsureBluetoothExtendedMode(SafeFileHandle handle, ushort featureReportLength)
    {
        try
        {
            int length = Math.Max((int)featureReportLength, 41);
            byte[] featureBuffer = new byte[length];
            featureBuffer[0] = CalibrationFeatureReportId;
            _ = NativeMethods.HidD_GetFeature(handle, featureBuffer, featureBuffer.Length);
        }
        catch
        {
            // Non-fatal: controller may already be in full BT mode via Steam or Windows.
        }
    }

    private static bool IsSupportedProductId(ushort productId) =>
        productId is DualSenseProductId or DualSenseEdgeProductId;

    private static string GetModelName(ushort productId) => productId switch
    {
        DualSenseEdgeProductId => "DualSense Edge",
        _ => "DualSense"
    };

    private static bool IsDisconnectError(int win32Error) => win32Error is
        NativeMethods.ERROR_DEVICE_NOT_CONNECTED or
        NativeMethods.ERROR_GEN_FAILURE or
        NativeMethods.ERROR_INVALID_HANDLE or
        NativeMethods.ERROR_NO_SUCH_DEVICE;

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (OpenDualSenseDevice device in _devices.Values)
            {
                device.Dispose();
            }
            _devices.Clear();
        }
    }

    private sealed record DiscoveredBluetoothDevice(
        string DevicePath,
        ushort ProductId,
        ushort OutputReportByteLength,
        ushort FeatureReportByteLength);

    private sealed class OpenDualSenseDevice(
        string devicePath,
        ushort productId,
        string modelName,
        ushort outputReportByteLength,
        SafeFileHandle handle) : IDisposable
    {
        public string DevicePath { get; } = devicePath;
        public ushort ProductId { get; } = productId;
        public string ModelName { get; } = modelName;
        public ushort OutputReportByteLength { get; } = outputReportByteLength;
        public SafeFileHandle Handle { get; } = handle;

        public void Dispose() => Handle.Dispose();
    }
}
