using DualSenseBTTriggers.Native;

namespace DualSenseBTTriggers.Services;

/// <summary>
/// Event-driven foreground window monitor using SetWinEventHook (EVENT_SYSTEM_FOREGROUND).
/// Resolves executable names via OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) + QueryFullProcessImageName.
/// </summary>
public sealed class ForegroundWindowMonitor : IDisposable
{
    private const int InitialPathBufferLength = 1024;

    // Keep delegate alive for the entire hook lifetime to prevent GC collection while native code holds it.
    private readonly NativeMethods.WinEventDelegate _winEventCallback;
    private IntPtr _hookHandle;
    private bool _disposed;

    public event EventHandler<string?>? ForegroundExecutableChanged;

    public ForegroundWindowMonitor()
    {
        _winEventCallback = OnWinEvent;
        _hookHandle = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _winEventCallback,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
    }

    public string? GetCurrentForegroundExecutable()
    {
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        return ResolveExecutableName(hwnd);
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (_disposed || eventType != NativeMethods.EVENT_SYSTEM_FOREGROUND || hwnd == IntPtr.Zero)
        {
            return;
        }

        string? exeName = ResolveExecutableName(hwnd);
        ForegroundExecutableChanged?.Invoke(this, exeName);
    }

    private static string? ResolveExecutableName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == 0)
        {
            return null;
        }

        IntPtr hProcess = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
            bInheritHandle: false,
            processId);

        if (hProcess == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            char[] buffer = new char[InitialPathBufferLength];
            uint size = (uint)buffer.Length;

            if (!NativeMethods.QueryFullProcessImageName(hProcess, 0, buffer, ref size) || size == 0)
            {
                return null;
            }

            string fullPath = new(buffer, 0, (int)size);
            return Path.GetFileName(fullPath);
        }
        finally
        {
            NativeMethods.CloseHandle(hProcess);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }
}
