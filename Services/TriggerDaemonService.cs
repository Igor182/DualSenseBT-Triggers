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

public sealed record RecentAppEntry(
    string ExecutableName,
    GameProfile? ConfiguredProfile,
    bool IsRecentSessionApp);

/// <summary>
/// Coordinates event-driven hardware discovery, foreground window tracking, profile resolution,
/// and Bluetooth HID adaptive trigger packet dispatch.
/// </summary>
public sealed class TriggerDaemonService : IDisposable
{
    private const string DefaultProfileLabel = "Padrão / Livre";
    private const int MaxRecentExecutables = 12;
    public const int DefaultKeepAliveIntervalMs = 500;

    private static readonly HashSet<string> IgnoredSystemExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "DualSenseBT-Triggers.exe",
        "explorer.exe",
        "SearchHost.exe",
        "ShellExperienceHost.exe",
        "StartMenuExperienceHost.exe",
        "ApplicationFrameHost.exe",
        "LockApp.exe",
        "Taskmgr.exe",
        "SystemSettings.exe",
        "TextInputHost.exe",
        "dwm.exe",
        "sihost.exe",
        "conhost.exe",
        "cmd.exe",
        "powershell.exe",
        "pwsh.exe"
    };

    private readonly ProfileManager _profileManager;
    private readonly DualSenseHidManager _hidManager;
    private readonly DualSensePacketBuilder _packetBuilder;
    private readonly DeviceWatcher _deviceWatcher;
    private readonly ForegroundWindowMonitor _windowMonitor;
    private readonly System.Windows.Forms.Timer _keepAliveTimer;
    private readonly List<string> _recentExecutables = new();

    private TriggerPairConfig? _lastSentPair;
    private TriggerPairConfig? _activeKeepAlivePair;
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

        _keepAliveTimer = new System.Windows.Forms.Timer();
        _keepAliveTimer.Tick += OnKeepAliveTick;
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
            StopKeepAlive();

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

    public string SendManualTestPacket()
    {
        if (_disposed)
        {
            return "Serviço encerrado.";
        }

        _ = _hidManager.RefreshDevices();
        if (!_hidManager.IsConnected)
        {
            return "Nenhum DualSense Bluetooth conectado.";
        }

        var testPair = new TriggerPairConfig
        {
            L2 = new TriggerEffectConfig { Mode = "continuous", Params = [20, 220, 0] },
            R2 = new TriggerEffectConfig { Mode = "weapon", Params = [30, 120, 255] }
        };

        byte[] packet = _packetBuilder.BuildTriggerPacket(testPair);
        bool sent = _hidManager.SendOutputReport(packet, out _);
        return sent
            ? $"Pacote de teste enviado!\n\n{_hidManager.LastWriteStatus}\n\nTeste L2 (resistência contínua) e R2 (clique de arma) ANTES de clicar em OK."
            : $"Falha ao enviar pacote de teste.\n\n{_hidManager.LastWriteStatus}";
    }

    public void ReapplyCurrentForegroundProfile()
    {
        if (_disposed || _isPaused)
        {
            return;
        }

        _lastSentPair = null;
        string? currentExe = _windowMonitor.GetCurrentForegroundExecutable();
        ApplyForExecutable(currentExe, forceSend: true);
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

    public IReadOnlyList<RecentAppEntry> GetRecentAndConfiguredApps()
    {
        var results = new List<RecentAppEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string exe in _recentExecutables)
        {
            if (seen.Add(exe))
            {
                _profileManager.TryGetProfile(exe, out GameProfile? profile);
                results.Add(new RecentAppEntry(exe, profile, IsRecentSessionApp: true));
            }
        }

        foreach (KeyValuePair<string, GameProfile> kvp in _profileManager.CurrentConfig.Profiles)
        {
            if (seen.Add(kvp.Key))
            {
                results.Add(new RecentAppEntry(kvp.Key, kvp.Value, IsRecentSessionApp: false));
            }
        }

        return results;
    }

    public bool ApplyPresetToExecutable(
        string executableName,
        TriggerPresetDefinition preset,
        out string? errorMessage)
    {
        if (_disposed)
        {
            errorMessage = "Serviço encerrado.";
            return false;
        }

        GameProfile profile = preset.CreateProfile(executableName);
        if (_profileManager.TryGetProfile(executableName, out GameProfile? existing) && existing is not null)
        {
            profile.KeepAliveMs = existing.KeepAliveMs;
        }

        if (!_profileManager.UpsertProfile(executableName, profile, out errorMessage))
        {
            return false;
        }

        // Immediately send the preset to the controller so the user can feel it right away
        if (!_isPaused && !profile.Ignore)
        {
            TriggerPairConfig pair = profile.ToTriggerPair();
            _lastSentPair = null;
            SendTriggerPair(pair, forceSend: true);
            ConfigureKeepAlive(profile.KeepAliveMs, pair);
        }
        else
        {
            StopKeepAlive();
        }

        RaiseStateChanged();
        return true;
    }

    public bool ToggleKeepAlive(string executableName, out bool enabled, out string? errorMessage)
    {
        enabled = false;
        errorMessage = null;

        if (_disposed)
        {
            errorMessage = "Serviço encerrado.";
            return false;
        }

        if (!_profileManager.TryGetProfile(executableName, out GameProfile? existing) || existing is null)
        {
            errorMessage = $"Perfil não encontrado para {executableName}.";
            return false;
        }

        existing.KeepAliveMs = existing.KeepAliveMs > 0 ? 0 : DefaultKeepAliveIntervalMs;
        enabled = existing.KeepAliveMs > 0;

        if (!_profileManager.UpsertProfile(executableName, existing, out errorMessage))
        {
            return false;
        }

        if (!_isPaused && !existing.Ignore)
        {
            TriggerPairConfig pair = existing.ToTriggerPair();
            _lastSentPair = null;
            SendTriggerPair(pair, forceSend: true);
            ConfigureKeepAlive(existing.KeepAliveMs, pair);
        }
        else
        {
            StopKeepAlive();
        }

        RaiseStateChanged();
        return true;
    }

    public bool RemoveExecutableProfile(string executableName, out string? errorMessage)
    {
        if (_disposed)
        {
            errorMessage = "Serviço encerrado.";
            return false;
        }

        if (!_profileManager.RemoveProfile(executableName, out errorMessage))
        {
            return false;
        }

        _lastSentPair = null;
        string? currentExe = _windowMonitor.GetCurrentForegroundExecutable();
        ApplyForExecutable(currentExe, forceSend: true);
        RaiseStateChanged();
        return true;
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
        TrackRecentExecutable(executableName);

        if (_profileManager.TryGetProfile(executableName, out GameProfile? matchedProfile) &&
            matchedProfile is not null)
        {
            if (matchedProfile.Ignore)
            {
                // Native DualSense wireless support: send NO packet to the controller.
                StopKeepAlive();
                _activeProfileDisplay = $"{matchedProfile.Name} [Ignorado]";
                _hasActiveGameProfile = true;
                _isCurrentAppIgnored = true;
                _lastSentPair = null;
                return;
            }

            _activeProfileDisplay = matchedProfile.KeepAliveMs > 0
                ? $"{matchedProfile.Name} [Anti-Reset]"
                : matchedProfile.Name;
            _hasActiveGameProfile = true;
            _isCurrentAppIgnored = false;

            if (!_isPaused)
            {
                TriggerPairConfig pair = matchedProfile.ToTriggerPair();
                SendTriggerPair(pair, forceSend);
                ConfigureKeepAlive(matchedProfile.KeepAliveMs, pair);
            }
            else
            {
                StopKeepAlive();
            }

            return;
        }

        // Foreground window is not registered in profiles.json
        if (_profileManager.CurrentConfig.Settings.ResetOnAltTab || !_hasActiveGameProfile)
        {
            StopKeepAlive();
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

    private void TrackRecentExecutable(string? executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return;
        }

        string clean = executableName.Trim();
        if (IgnoredSystemExecutables.Contains(clean))
        {
            return;
        }

        _recentExecutables.RemoveAll(e => string.Equals(e, clean, StringComparison.OrdinalIgnoreCase));
        _recentExecutables.Insert(0, clean);

        if (_recentExecutables.Count > MaxRecentExecutables)
        {
            _recentExecutables.RemoveRange(MaxRecentExecutables, _recentExecutables.Count - MaxRecentExecutables);
        }
    }

    private void ConfigureKeepAlive(int intervalMs, TriggerPairConfig pair)
    {
        if (intervalMs <= 0 || _disposed || _isPaused || !_hidManager.IsConnected)
        {
            StopKeepAlive();
            return;
        }

        _activeKeepAlivePair = pair;
        _keepAliveTimer.Interval = Math.Clamp(intervalMs, 100, 10000);
        if (!_keepAliveTimer.Enabled)
        {
            _keepAliveTimer.Start();
        }
    }

    private void StopKeepAlive()
    {
        _activeKeepAlivePair = null;
        if (_keepAliveTimer.Enabled)
        {
            _keepAliveTimer.Stop();
        }
    }

    private void OnKeepAliveTick(object? sender, EventArgs e)
    {
        if (_disposed || _isPaused || _activeKeepAlivePair is null || !_hidManager.IsConnected)
        {
            StopKeepAlive();
            return;
        }

        SendTriggerPair(_activeKeepAlivePair, forceSend: true);
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
        StopKeepAlive();
        _keepAliveTimer.Tick -= OnKeepAliveTick;
        _keepAliveTimer.Dispose();

        _deviceWatcher.HidDevicesChanged -= OnHidDevicesChanged;
        _windowMonitor.ForegroundExecutableChanged -= OnForegroundExecutableChanged;

        _windowMonitor.Dispose();
        _deviceWatcher.Dispose();
        _hidManager.Dispose();
    }
}
