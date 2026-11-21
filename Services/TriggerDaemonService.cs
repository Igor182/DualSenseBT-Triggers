using DualSenseBTTriggers.Config;
using DualSenseBTTriggers.Hid;

namespace DualSenseBTTriggers.Services;

public enum TrayVisualState
{
    Disconnected,
    ConnectedIdle,
    ConnectedActiveProfile,
    Paused
}

public sealed record DaemonStatusSnapshot(
    bool IsConnected,
    string ConnectionStatusText,
    string ActiveProfileText,
    bool IsPaused,
    TrayVisualState VisualState);

/// <summary>
/// Coordinates event-driven hardware discovery, foreground window tracking, profile resolution,
/// and Bluetooth HID adaptive trigger packet dispatch.
/// </summary>
public sealed class TriggerDaemonService : IDisposable
{
    private const string DefaultProfileLabel = "Padrão / Livre";

    private readonly ProfileManager _profileManager;
    private readonly DualSenseHidManager _hidManager;
    private readonly DualSensePacketBuilder _packetBuilder;
    private readonly DeviceWatcher _deviceWatcher;
    private readonly ForegroundWindowMonitor _windowMonitor;

    private TriggerPairConfig? _lastSentPair;
    private string _activeProfileDisplay = DefaultProfileLabel;
    private string _lastForegroundExe = "(nenhum)";
    private bool _hasActiveGameProfile;
    private bool _isCurrentAppIgnored;
    private bool _isPaused;
    private bool _disposed;

    public event EventHandler? StateChanged;

    public string ProfilesFilePath => _profileManager.ConfigFilePath;
    public bool IsPaused => _isPaused;
    public string GetHidDiagnosticReport() =>
        $"{_hidManager.LastDiagnosticReport}\n\n" +
        $"Última janela em foco: {_lastForegroundExe}\n" +
        $"Perfil ativo: {_activeProfileDisplay}\n" +
        $"Último envio HID: {_hidManager.LastWriteStatus}";

    public void ForceRescanDevices()
    {
        if (_disposed)
        {
            return;
        }

        _ = _hidManager.RefreshDevices();
        if (_hidManager.IsConnected && !_isPaused)
        {
            _lastSentPair = null;
            string? currentExe = _windowMonitor.GetCurrentForegroundExecutable();
            ApplyForExecutable(currentExe, forceSend: true);
        }

        RaiseStateChanged();
    }

    public TriggerDaemonService()
    {
        _profileManager = new ProfileManager();
        _hidManager = new DualSenseHidManager();
        _packetBuilder = new DualSensePacketBuilder();
        _deviceWatcher = new DeviceWatcher();
        _windowMonitor = new ForegroundWindowMonitor();
    }

    public bool Start(out string? configError)
    {
        bool configOk = _profileManager.LoadOrCreateDefault(out configError);

        _deviceWatcher.HidDevicesChanged += OnHidDevicesChanged;
        _windowMonitor.ForegroundExecutableChanged += OnForegroundExecutableChanged;

        // Initial hardware scan on startup
        _hidManager.RefreshDevices();

        // Evaluate whichever window currently has focus
        string? currentExe = _windowMonitor.GetCurrentForegroundExecutable();
        ApplyForExecutable(currentExe, forceSend: true);

        RaiseStateChanged();
        return configOk;
    }

    public DaemonStatusSnapshot GetStatusSnapshot()
    {
        IReadOnlyList<ConnectedDualSenseInfo> devices = _hidManager.GetConnectedDevices();
        bool isConnected = devices.Count > 0;

        string connectionText = devices.Count switch
        {
            0 => "DualSense BT: Desconectado",
            1 => $"DualSense BT: Conectado ({devices[0].ModelName})",
            _ => $"DualSense BT: Conectado ({devices.Count} controles)"
        };

        string profileText = _isPaused
            ? $"Perfil: Pausado ({_activeProfileDisplay})"
            : $"Perfil: {_activeProfileDisplay}";

        TrayVisualState visualState;
        if (_isPaused)
        {
            visualState = TrayVisualState.Paused;
        }
        else if (!isConnected)
        {
            visualState = TrayVisualState.Disconnected;
        }
        else if (_hasActiveGameProfile && !_isCurrentAppIgnored)
        {
            visualState = TrayVisualState.ConnectedActiveProfile;
        }
        else
        {
            visualState = TrayVisualState.ConnectedIdle;
        }

        return new DaemonStatusSnapshot(
            isConnected,
            connectionText,
            profileText,
            _isPaused,
            visualState);
    }

    public void TogglePause()
    {
        if (_disposed)
        {
            return;
        }

        _isPaused = !_isPaused;

        if (_isPaused)
        {
            // Release triggers when pausing unless the focused game manages them natively.
            if (!_isCurrentAppIgnored)
            {
                SendTriggerPair(TriggerPairConfig.CreateOff(), forceSend: true);
            }
        }
        else
        {
            _lastSentPair = null;
            string? currentExe = _windowMonitor.GetCurrentForegroundExecutable();
            ApplyForExecutable(currentExe, forceSend: true);
        }

        RaiseStateChanged();
    }

    public bool ReloadProfiles(out string? errorMessage)
    {
        if (_disposed)
        {
            errorMessage = "Serviço encerrado.";
            return false;
        }

        bool ok = _profileManager.Reload(out errorMessage);
        if (ok)
        {
            _lastSentPair = null;
            string? currentExe = _windowMonitor.GetCurrentForegroundExecutable();
            ApplyForExecutable(currentExe, forceSend: true);
            RaiseStateChanged();
        }

        return ok;
    }

    /// <summary>
    /// Sends a final reset (off) report to L2 and R2 before closing HID handles.
    /// </summary>
    public void ResetTriggersOnExit()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            byte[] resetPacket = _packetBuilder.BuildResetPacket();
            _hidManager.SendOutputReport(resetPacket, out _);
        }
        catch
        {
            // Best effort on shutdown.
        }
    }

    private void OnHidDevicesChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        HidScanResult scan = _hidManager.RefreshDevices();

        if (scan.ConnectedCount == 0)
        {
            _lastSentPair = null;
        }
        else if (scan.AnyAdded)
        {
            // Controller reconnected: re-apply current foreground profile immediately.
            _lastSentPair = null;
            if (!_isPaused)
            {
                string? currentExe = _windowMonitor.GetCurrentForegroundExecutable();
                ApplyForExecutable(currentExe, forceSend: true);
            }
        }

        if (scan.AnyAdded || scan.AnyRemoved)
        {
            RaiseStateChanged();
        }
    }

    private void OnForegroundExecutableChanged(object? sender, string? executableName)
    {
        if (_disposed)
        {
            return;
        }

        ApplyForExecutable(executableName, forceSend: false);
        RaiseStateChanged();
    }

    private void ApplyForExecutable(string? executableName, bool forceSend)
    {
        _lastForegroundExe = string.IsNullOrWhiteSpace(executableName) ? "(desconhecido)" : executableName;

        if (_profileManager.TryGetProfile(executableName, out GameProfile? matchedProfile) &&
            matchedProfile is not null)
        {
            if (matchedProfile.Ignore)
            {
                // Native DualSense wireless support: send NO packet to the controller.
                _activeProfileDisplay = $"{matchedProfile.Name} [Ignorado]";
                _hasActiveGameProfile = true;
                _isCurrentAppIgnored = true;
                _lastSentPair = null;
                return;
            }

            _activeProfileDisplay = matchedProfile.Name;
            _hasActiveGameProfile = true;
            _isCurrentAppIgnored = false;

            if (!_isPaused)
            {
                SendTriggerPair(matchedProfile.ToTriggerPair(), forceSend);
            }

            return;
        }

        // Foreground window is not registered in profiles.json
        if (_profileManager.CurrentConfig.Settings.ResetOnAltTab || !_hasActiveGameProfile)
        {
            _activeProfileDisplay = DefaultProfileLabel;
            _hasActiveGameProfile = false;
            _isCurrentAppIgnored = false;

            if (!_isPaused)
            {
                SendTriggerPair(_profileManager.CurrentConfig.DefaultProfile, forceSend);
            }
        }
    }

    private void SendTriggerPair(TriggerPairConfig targetPair, bool forceSend)
    {
        if (!_hidManager.IsConnected)
        {
            _lastSentPair = null;
            return;
        }

        if (!forceSend && targetPair.HasSameEffectAs(_lastSentPair))
        {
            return;
        }

        byte[] packet = _packetBuilder.BuildTriggerPacket(targetPair);
        bool sent = _hidManager.SendOutputReport(packet, out bool connectionLost);

        if (sent)
        {
            _lastSentPair = targetPair;
        }
        else if (connectionLost)
        {
            _lastSentPair = null;
            RaiseStateChanged();
        }
    }

    private void RaiseStateChanged()
    {
        if (!_disposed)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ResetTriggersOnExit();

        _disposed = true;
        _deviceWatcher.HidDevicesChanged -= OnHidDevicesChanged;
        _windowMonitor.ForegroundExecutableChanged -= OnForegroundExecutableChanged;

        _windowMonitor.Dispose();
        _deviceWatcher.Dispose();
        _hidManager.Dispose();
    }
}
