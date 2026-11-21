using System.Runtime.InteropServices;
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
    /// Bluetooth connections are strictly distinguished from USB via OutputReportByteLength == 78.
    /// </summary>
    public HidScanResult RefreshDevices()
    {
        lock (_syncLock)
        {
            if (_disposed)
            {
                return new HidScanResult(false, false, 0);
            }

            Dictionary<string, DiscoveredBluetoothDevice> discovered = EnumerateBluetoothDualSenseDevices();

            bool anyRemoved = false;
            bool anyAdded = false;

            // Close and remove devices that are no longer present
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

            // Open newly arrived Bluetooth DualSense devices
            foreach (DiscoveredBluetoothDevice candidate in discovered.Values)
            {
                if (_devices.ContainsKey(candidate.DevicePath))
                {
                    continue;
                }

                OpenDualSenseDevice? opened = TryOpenDevice(candidate);
                if (opened is not null)
                {
                    _devices[candidate.DevicePath] = opened;
                    anyAdded = true;
                }
            }

            return new HidScanResult(anyAdded, anyRemoved, _devices.Count);
        }
    }

    /// <summary>
    /// Writes a 78-byte Bluetooth HID output report to all connected DualSense Bluetooth controllers.
    /// Returns true if at least one controller received the report cleanly.
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

                bool written = NativeMethods.WriteFile(
                    device.Handle,
                    report,
                    (uint)report.Length,
                    out uint bytesWritten,
                    IntPtr.Zero);

                if (written && bytesWritten == report.Length)
                {
                    anySucceeded = true;
                }
                else
                {
                    int error = Marshal.GetLastWin32Error();
                    if (IsDisconnectError(error))
                    {
                        disconnectedPaths ??= [];
                        disconnectedPaths.Add(device.DevicePath);
                    }
                }
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

    private static Dictionary<string, DiscoveredBluetoothDevice> EnumerateBluetoothDualSenseDevices()
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

                DiscoveredBluetoothDevice? probed = ProbeBluetoothDualSense(devicePath);
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
        // Query required buffer size
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
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize is 8 bytes on x64 and 6 bytes on x86
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

            // DevicePath starts immediately after the 4-byte DWORD cbSize field
            return Marshal.PtrToStringUni(detailBuffer + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(detailBuffer);
        }
    }

    /// <summary>
    /// Probes HID metadata using access=0 (non-intrusive) to verify VID/PID and Bluetooth report size (78 bytes).
    /// </summary>
    private static DiscoveredBluetoothDevice? ProbeBluetoothDualSense(string devicePath)
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

        if (attributes.VendorID != SonyVendorId || !IsSupportedProductId(attributes.ProductID))
        {
            return null;
        }

        if (!NativeMethods.HidD_GetPreparsedData(probeHandle, out IntPtr preparsedData) ||
            preparsedData == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            int status = NativeMethods.HidP_GetCaps(preparsedData, out NativeMethods.HIDP_CAPS caps);
            if (status != NativeMethods.HIDP_STATUS_SUCCESS)
            {
                return null;
            }

            // Bluetooth DualSense exposes OutputReportByteLength == 78 (USB exposes 48/64)
            if (caps.OutputReportByteLength != DualSensePacketBuilder.BluetoothOutputReportLength)
            {
                return null;
            }

            return new DiscoveredBluetoothDevice(
                devicePath,
                attributes.ProductID,
                caps.FeatureReportByteLength);
        }
        finally
        {
            NativeMethods.HidD_FreePreparsedData(preparsedData);
        }
    }

    private static OpenDualSenseDevice? TryOpenDevice(DiscoveredBluetoothDevice candidate)
    {
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
            handle.Dispose();
            return null;
        }

        // Best-effort read of Feature Report 0x05 (calibration) to ensure the DualSense
        // transitions from basic BT mode into full extended 0x31 Bluetooth report mode.
        TryEnsureBluetoothExtendedMode(handle, candidate.FeatureReportByteLength);

        return new OpenDualSenseDevice(
            candidate.DevicePath,
            candidate.ProductId,
            GetModelName(candidate.ProductId),
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
        ushort FeatureReportByteLength);

    private sealed class OpenDualSenseDevice(
        string devicePath,
        ushort productId,
        string modelName,
        SafeFileHandle handle) : IDisposable
    {
        public string DevicePath { get; } = devicePath;
        public ushort ProductId { get; } = productId;
        public string ModelName { get; } = modelName;
        public SafeFileHandle Handle { get; } = handle;

        public void Dispose() => Handle.Dispose();
    }
}
