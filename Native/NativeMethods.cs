using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DualSenseBTTriggers.Native;

internal static class NativeMethods
{
    // HID device interface GUID: {4D1E55B2-F16F-11CF-88CB-001111000030}
    public static readonly Guid GuidDevinterfaceHid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    public static readonly IntPtr InvalidHandleValue = new(-1);

    // SetupDiGetClassDevs flags
    public const uint DIGCF_PRESENT = 0x00000002;
    public const uint DIGCF_DEVICEINTERFACE = 0x00000010;

    // CreateFile access & sharing flags
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;

    // HID status
    public const int HIDP_STATUS_SUCCESS = 0x00110000;

    // WM_DEVICECHANGE constants
    public const int WM_DEVICECHANGE = 0x0219;
    public const int DBT_DEVICEARRIVAL = 0x8000;
    public const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    public const int DBT_DEVTYP_DEVICEINTERFACE = 0x00000005;
    public const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;

    // SetWinEventHook constants
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    // OpenProcess access rights
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // Win32 error codes indicating device disconnection
    public const int ERROR_INVALID_HANDLE = 6;
    public const int ERROR_GEN_FAILURE = 31;
    public const int ERROR_NO_SUCH_DEVICE = 433;
    public const int ERROR_DEVICE_NOT_CONNECTED = 1167;

    #region Structs

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;

        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DEV_BROADCAST_HDR
    {
        public int dbch_size;
        public int dbch_devicetype;
        public int dbch_reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
        public short dbcc_name;
    }

    #endregion

    #region Delegates

    public delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    #endregion

    #region setupapi.dll

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    public static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        int deviceInterfaceDetailDataSize,
        out int requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    #endregion

    #region hid.dll

    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
    public static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool HidD_GetAttributes(
        SafeFileHandle hidDeviceObject,
        ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool HidD_GetPreparsedData(
        SafeFileHandle hidDeviceObject,
        out IntPtr preparsedData);

    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool HidD_GetFeature(
        SafeFileHandle hidDeviceObject,
        byte[] reportBuffer,
        int reportBufferLength);

    [DllImport("hid.dll", ExactSpelling = true, SetLastError = true)]
    public static extern int HidP_GetCaps(
        IntPtr preparsedData,
        out HIDP_CAPS capabilities);

    #endregion

    #region kernel32.dll

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    public static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WriteFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    public static extern IntPtr OpenProcess(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        uint dwProcessId);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(
        IntPtr hProcess,
        uint dwFlags,
        char[] lpExeName,
        ref uint lpdwSize);

    #endregion

    #region user32.dll

    [DllImport("user32.dll", EntryPoint = "RegisterDeviceNotificationW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    public static extern IntPtr RegisterDeviceNotification(
        IntPtr hRecipient,
        IntPtr notificationFilter,
        uint flags);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterDeviceNotification(IntPtr handle);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    #endregion
}
