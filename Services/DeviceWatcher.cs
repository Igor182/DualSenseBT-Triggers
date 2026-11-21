using System.Runtime.InteropServices;
using DualSenseBTTriggers.Native;

namespace DualSenseBTTriggers.Services;

/// <summary>
/// Event-driven Win32 HID device arrival/removal watcher using a hidden NativeWindow
/// and RegisterDeviceNotification (GUID_DEVINTERFACE_HID). Zero polling.
/// </summary>
public sealed class DeviceWatcher : NativeWindow, IDisposable
{
    private const int DebounceDelayMs = 350;

    private readonly System.Windows.Forms.Timer _debounceTimer;
    private IntPtr _notificationHandle = IntPtr.Zero;
    private bool _disposed;

    public event EventHandler? HidDevicesChanged;

    public DeviceWatcher()
    {
        // Hidden top-level window (NOT HWND_MESSAGE) so Windows reliably delivers WM_DEVICECHANGE.
        var createParams = new CreateParams
        {
            Caption = "DualSenseBTTriggers_DeviceWatcher"
        };
        CreateHandle(createParams);

        _debounceTimer = new System.Windows.Forms.Timer
        {
            Interval = DebounceDelayMs
        };
        _debounceTimer.Tick += OnDebounceTimerTick;

        RegisterForHidNotifications();
    }

    private void RegisterForHidNotifications()
    {
        var filter = new NativeMethods.DEV_BROADCAST_DEVICEINTERFACE
        {
            dbcc_size = Marshal.SizeOf<NativeMethods.DEV_BROADCAST_DEVICEINTERFACE>(),
            dbcc_devicetype = NativeMethods.DBT_DEVTYP_DEVICEINTERFACE,
            dbcc_reserved = 0,
            dbcc_classguid = NativeMethods.GuidDevinterfaceHid,
            dbcc_name = 0
        };

        IntPtr buffer = Marshal.AllocHGlobal(filter.dbcc_size);
        try
        {
            Marshal.StructureToPtr(filter, buffer, fDeleteOld: false);
            _notificationHandle = NativeMethods.RegisterDeviceNotification(
                Handle,
                buffer,
                NativeMethods.DEVICE_NOTIFY_WINDOW_HANDLE);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (!_disposed && m.Msg == NativeMethods.WM_DEVICECHANGE)
        {
            int eventType = m.WParam.ToInt32();
            if (eventType is NativeMethods.DBT_DEVICEARRIVAL or NativeMethods.DBT_DEVICEREMOVECOMPLETE)
            {
                if (IsHidInterfaceNotification(m.LParam))
                {
                    // Coalesce burst Bluetooth HID notifications and allow the HID pipe to settle.
                    _debounceTimer.Stop();
                    _debounceTimer.Start();
                }
            }
        }

        base.WndProc(ref m);
    }

    private static bool IsHidInterfaceNotification(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero)
        {
            return true;
        }

        var header = Marshal.PtrToStructure<NativeMethods.DEV_BROADCAST_HDR>(lParam);
        return header.dbch_devicetype == NativeMethods.DBT_DEVTYP_DEVICEINTERFACE;
    }

    private void OnDebounceTimerTick(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();
        if (!_disposed)
        {
            HidDevicesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _debounceTimer.Stop();
        _debounceTimer.Tick -= OnDebounceTimerTick;
        _debounceTimer.Dispose();

        if (_notificationHandle != IntPtr.Zero)
        {
            NativeMethods.UnregisterDeviceNotification(_notificationHandle);
            _notificationHandle = IntPtr.Zero;
        }

        if (Handle != IntPtr.Zero)
        {
            DestroyHandle();
        }
    }
}
