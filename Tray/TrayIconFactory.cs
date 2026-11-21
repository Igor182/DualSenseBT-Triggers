using System.Drawing;
using System.Drawing.Drawing2D;
using DualSenseBTTriggers.Native;
using DualSenseBTTriggers.Services;

namespace DualSenseBTTriggers.Tray;

/// <summary>
/// Generates crisp programmatic System Tray icons using System.Drawing
/// without requiring external .ico asset files.
/// </summary>
internal static class TrayIconFactory
{
    public static Icon CreateIcon(TrayVisualState state)
    {
        const int size = 32;

        using var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        Color accentColor = state switch
        {
            TrayVisualState.ConnectedActiveProfile => Color.FromArgb(0, 229, 255),
            TrayVisualState.ConnectedIdle => Color.FromArgb(41, 121, 255),
            TrayVisualState.Paused => Color.FromArgb(255, 179, 0),
            _ => Color.FromArgb(130, 140, 152)
        };

        Color bodyFill = Color.FromArgb(28, 32, 40);
        Color triggerFill = state == TrayVisualState.ConnectedActiveProfile
            ? accentColor
            : Color.FromArgb(190, accentColor);

        // Outer dark badge
        using (var bgBrush = new SolidBrush(bodyFill))
        using (var borderPen = new Pen(accentColor, 2.2f))
        {
            using GraphicsPath rounded = CreateRoundedRect(new RectangleF(1.5f, 1.5f, size - 3f, size - 3f), 7f);
            g.FillPath(bgBrush, rounded);
            g.DrawPath(borderPen, rounded);
        }

        // Stylized DualSense L2 / R2 top trigger bars
        using (var triggerBrush = new SolidBrush(triggerFill))
        {
            using GraphicsPath l2 = CreateRoundedRect(new RectangleF(6f, 6.5f, 8f, 4.2f), 2f);
            using GraphicsPath r2 = CreateRoundedRect(new RectangleF(18f, 6.5f, 8f, 4.2f), 2f);
            g.FillPath(triggerBrush, l2);
            g.FillPath(triggerBrush, r2);
        }

        // Stylized controller body silhouette
        using (var controllerBrush = new SolidBrush(Color.FromArgb(238, 242, 248)))
        {
            // Center bridge
            using GraphicsPath bridge = CreateRoundedRect(new RectangleF(7f, 12f, 18f, 8.5f), 4f);
            g.FillPath(controllerBrush, bridge);

            // Left & right grips
            g.FillEllipse(controllerBrush, 5.5f, 14.5f, 7.5f, 11.5f);
            g.FillEllipse(controllerBrush, 19f, 14.5f, 7.5f, 11.5f);
        }

        // Center touchpad lightbar accent
        using (var lightbarBrush = new SolidBrush(accentColor))
        {
            using GraphicsPath touchpad = CreateRoundedRect(new RectangleF(12.2f, 13.5f, 7.6f, 4.2f), 1.5f);
            g.FillPath(lightbarBrush, touchpad);
        }

        // Small pause bars overlay if paused
        if (state == TrayVisualState.Paused)
        {
            using var pauseBrush = new SolidBrush(bodyFill);
            g.FillRectangle(pauseBrush, 14f, 14.2f, 1.4f, 2.8f);
            g.FillRectangle(pauseBrush, 16.6f, 14.2f, 1.4f, 2.8f);
        }

        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            // Clone so the managed Icon owns its own native copy, then destroy the temporary HICON.
            using Icon temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath CreateRoundedRect(RectangleF bounds, float radius)
    {
        float diameter = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
