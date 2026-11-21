using Microsoft.Win32;

namespace DualSenseBTTriggers.Services;

/// <summary>
/// Manages the current-user Windows autostart entry under HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// </summary>
public static class StartupManager
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValueName = "DualSenseBT-Triggers";

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
            string? value = key?.GetValue(AppValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enable, out string? errorMessage)
    {
        errorMessage = null;

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunRegistryPath, writable: true);

            if (key is null)
            {
                errorMessage = "Não foi possível abrir a chave de inicialização no Registro do Windows.";
                return false;
            }

            if (enable)
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    errorMessage = "Não foi possível determinar o caminho do executável.";
                    return false;
                }

                key.SetValue(AppValueName, $"\"{exePath}\"", RegistryValueKind.String);
            }
            else
            {
                if (key.GetValue(AppValueName) is not null)
                {
                    key.DeleteValue(AppValueName, throwOnMissingValue: false);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"Falha ao atualizar inicialização com o Windows: {ex.Message}";
            return false;
        }
    }
}
