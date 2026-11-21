using System.Diagnostics;
using System.Drawing;
using DualSenseBTTriggers.Services;
using Microsoft.Win32;

namespace DualSenseBTTriggers.Tray;

/// <summary>
/// Windowless System Tray ApplicationContext managing the NotifyIcon, context menu,
/// Windows autostart toggle, and clean adaptive-trigger reset on exit.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private const int MaxNotifyIconTooltipLength = 63;

    private readonly TriggerDaemonService _daemon;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;

    private readonly ToolStripMenuItem _statusMenuItem;
    private readonly ToolStripMenuItem _profileMenuItem;
    private readonly ToolStripMenuItem _pauseResumeMenuItem;
    private readonly ToolStripMenuItem _reloadMenuItem;
    private readonly ToolStripMenuItem _openProfilesMenuItem;
    private readonly ToolStripMenuItem _startupMenuItem;
    private readonly ToolStripMenuItem _exitMenuItem;

    private readonly Dictionary<TrayVisualState, Icon> _iconCache = new();
    private bool _exiting;

    public TrayApplicationContext()
    {
        _daemon = new TriggerDaemonService();
        _daemon.StateChanged += OnDaemonStateChanged;

        foreach (TrayVisualState state in Enum.GetValues<TrayVisualState>())
        {
            _iconCache[state] = TrayIconFactory.CreateIcon(state);
        }

        _statusMenuItem = new ToolStripMenuItem("DualSense BT: Inicializando...")
        {
            Enabled = false
        };

        _profileMenuItem = new ToolStripMenuItem("Perfil: Padrão / Livre")
        {
            Enabled = false
        };

        _pauseResumeMenuItem = new ToolStripMenuItem("Pausar", null, OnPauseResumeClicked);
        _reloadMenuItem = new ToolStripMenuItem("Recarregar profiles.json", null, OnReloadClicked);
        _openProfilesMenuItem = new ToolStripMenuItem("Abrir profiles.json", null, OnOpenProfilesClicked);

        _startupMenuItem = new ToolStripMenuItem("Iniciar com o Windows", null, OnStartupToggleClicked)
        {
            CheckOnClick = false,
            Checked = StartupManager.IsEnabled()
        };

        _exitMenuItem = new ToolStripMenuItem("Sair", null, OnExitClicked);

        _contextMenu = new ContextMenuStrip();
        _contextMenu.Items.AddRange(new ToolStripItem[]
        {
            _statusMenuItem,
            _profileMenuItem,
            new ToolStripSeparator(),
            _pauseResumeMenuItem,
            _reloadMenuItem,
            _openProfilesMenuItem,
            new ToolStripSeparator(),
            _startupMenuItem,
            new ToolStripSeparator(),
            _exitMenuItem
        });

        _notifyIcon = new NotifyIcon
        {
            Icon = _iconCache[TrayVisualState.Disconnected],
            ContextMenuStrip = _contextMenu,
            Text = "DualSenseBT-Triggers",
            Visible = true
        };

        SystemEvents.SessionEnding += OnSessionEnding;

        bool configOk = _daemon.Start(out string? configError);
        UpdateTrayUi();

        if (!configOk && !string.IsNullOrWhiteSpace(configError))
        {
            ShowBalloon("Aviso de configuração", configError, ToolTipIcon.Warning);
        }
    }

    private void OnDaemonStateChanged(object? sender, EventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        UpdateTrayUi();
    }

    private void UpdateTrayUi()
    {
        DaemonStatusSnapshot snapshot = _daemon.GetStatusSnapshot();

        _statusMenuItem.Text = snapshot.ConnectionStatusText;
        _profileMenuItem.Text = snapshot.ActiveProfileText;
        _pauseResumeMenuItem.Text = snapshot.IsPaused ? "Retomar" : "Pausar";
        _notifyIcon.Icon = _iconCache[snapshot.VisualState];

        string tooltip = $"{snapshot.ConnectionStatusText}\n{snapshot.ActiveProfileText}";
        _notifyIcon.Text = TruncateTooltip(tooltip);
    }

    private void OnPauseResumeClicked(object? sender, EventArgs e)
    {
        _daemon.TogglePause();
    }

    private void OnReloadClicked(object? sender, EventArgs e)
    {
        if (_daemon.ReloadProfiles(out string? errorMessage))
        {
            ShowBalloon(
                "Perfis recarregados",
                "profiles.json recarregado com sucesso.",
                ToolTipIcon.Info);
        }
        else
        {
            ShowBalloon(
                "Erro ao recarregar profiles.json",
                errorMessage ?? "Falha desconhecida ao ler profiles.json.",
                ToolTipIcon.Error);
        }
    }

    private void OnOpenProfilesClicked(object? sender, EventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _daemon.ProfilesFilePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowBalloon(
                "Erro ao abrir profiles.json",
                ex.Message,
                ToolTipIcon.Error);
        }
    }

    private void OnStartupToggleClicked(object? sender, EventArgs e)
    {
        bool targetState = !_startupMenuItem.Checked;
        if (StartupManager.SetEnabled(targetState, out string? errorMessage))
        {
            _startupMenuItem.Checked = targetState;
        }
        else
        {
            ShowBalloon(
                "Inicialização com o Windows",
                errorMessage ?? "Não foi possível alterar a configuração de inicialização.",
                ToolTipIcon.Error);
        }
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        ShutdownAndExit();
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        ShutdownAndExit();
    }

    private void ShutdownAndExit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        SystemEvents.SessionEnding -= OnSessionEnding;
        _daemon.StateChanged -= OnDaemonStateChanged;

        // Sends reset (off) to L2 and R2 and releases HID & Win32 hooks cleanly
        _daemon.Dispose();

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();

        foreach (Icon icon in _iconCache.Values)
        {
            icon.Dispose();
        }
        _iconCache.Clear();

        ExitThread();
    }

    private void ShowBalloon(string title, string message, ToolTipIcon icon)
    {
        if (_exiting)
        {
            return;
        }

        _notifyIcon.ShowBalloonTip(3000, title, message, icon);
    }

    private static string TruncateTooltip(string text)
    {
        if (text.Length <= MaxNotifyIconTooltipLength)
        {
            return text;
        }

        return string.Concat(text.AsSpan(0, MaxNotifyIconTooltipLength - 1), "…");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_exiting)
        {
            ShutdownAndExit();
        }

        base.Dispose(disposing);
    }
}
