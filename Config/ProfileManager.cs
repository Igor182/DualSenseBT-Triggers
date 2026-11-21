using System.Text;
using System.Text.Json;

namespace DualSenseBTTriggers.Config;

public sealed class ProfileManager
{
    private const string ConfigFileName = "profiles.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    private const string DefaultProfilesJson = """
{
  "settings": {
    "reset_on_alt_tab": true
  },
  "default_profile": {
    "l2": { "mode": "off", "params": [0, 0, 0] },
    "r2": { "mode": "off", "params": [0, 0, 0] }
  },
  "profiles": {
    "re4.exe": {
      "name": "Resident Evil 4 Remake",
      "ignore": false,
      "l2": { "mode": "continuous", "params": [30, 140, 0] },
      "r2": { "mode": "weapon", "params": [40, 120, 200] }
    },
    "ForzaHorizon5.exe": {
      "name": "Forza Horizon 5",
      "ignore": false,
      "l2": { "mode": "continuous", "params": [15, 190, 0] },
      "r2": { "mode": "continuous", "params": [10, 90, 0] }
    },
    "AlanWake2.exe": {
      "name": "Alan Wake 2 (Suporte Nativo)",
      "ignore": true
    }
  }
}
""";

    public string ConfigFilePath { get; }
    public AppConfig CurrentConfig { get; private set; }

    public ProfileManager(string? baseDirectory = null)
    {
        string dir = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : baseDirectory;

        ConfigFilePath = Path.Combine(dir, ConfigFileName);
        CurrentConfig = new AppConfig();
    }

    /// <summary>
    /// Ensures profiles.json exists (creating the default template if absent) and loads it.
    /// </summary>
    public bool LoadOrCreateDefault(out string? errorMessage)
    {
        errorMessage = null;

        try
        {
            if (!File.Exists(ConfigFilePath))
            {
                string? parentDir = Path.GetDirectoryName(ConfigFilePath);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                File.WriteAllText(ConfigFilePath, DefaultProfilesJson + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch (Exception ex)
        {
            errorMessage = $"Não foi possível criar {ConfigFileName}: {ex.Message}";
            CurrentConfig = DeserializeOrFallback(DefaultProfilesJson);
            return false;
        }

        return Reload(out errorMessage);
    }

    /// <summary>
    /// Reloads profiles.json from disk. Keeps the previous valid configuration on failure.
    /// </summary>
    public bool Reload(out string? errorMessage)
    {
        errorMessage = null;

        try
        {
            if (!File.Exists(ConfigFilePath))
            {
                return LoadOrCreateDefault(out errorMessage);
            }

            string json = File.ReadAllText(ConfigFilePath, Encoding.UTF8);
            AppConfig? parsed = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions);
            if (parsed is null)
            {
                errorMessage = $"{ConfigFileName} está vazio ou inválido.";
                return false;
            }

            CurrentConfig = Normalize(parsed);
            return true;
        }
        catch (JsonException ex)
        {
            errorMessage = $"Erro de sintaxe em {ConfigFileName}: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            errorMessage = $"Erro ao ler {ConfigFileName}: {ex.Message}";
            return false;
        }
    }

    public bool TryGetProfile(string? executableName, out GameProfile? profile)
    {
        profile = null;
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return false;
        }

        return CurrentConfig.Profiles.TryGetValue(executableName, out profile);
    }

    private static AppConfig Normalize(AppConfig raw)
    {
        raw.Settings ??= new AppSettings();
        raw.DefaultProfile ??= TriggerPairConfig.CreateOff();
        raw.DefaultProfile.L2 ??= TriggerEffectConfig.CreateOff();
        raw.DefaultProfile.R2 ??= TriggerEffectConfig.CreateOff();

        var caseInsensitiveProfiles = new Dictionary<string, GameProfile>(StringComparer.OrdinalIgnoreCase);
        if (raw.Profiles is not null)
        {
            foreach (KeyValuePair<string, GameProfile> kvp in raw.Profiles)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value is null)
                {
                    continue;
                }

                string exeKey = kvp.Key.Trim();
                GameProfile profile = kvp.Value;
                if (string.IsNullOrWhiteSpace(profile.Name))
                {
                    profile.Name = exeKey;
                }

                caseInsensitiveProfiles[exeKey] = profile;
            }
        }

        raw.Profiles = caseInsensitiveProfiles;
        return raw;
    }

    private static AppConfig DeserializeOrFallback(string json)
    {
        AppConfig? parsed = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions);
        return Normalize(parsed ?? new AppConfig());
    }
}
