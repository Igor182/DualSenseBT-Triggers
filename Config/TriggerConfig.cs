using System.Text.Json.Serialization;

namespace DualSenseBTTriggers.Config;

public enum TriggerMode : byte
{
    Off = 0x00,
    Continuous = 0x01,
    Weapon = 0x02,
    Vibration = 0x06
}

public sealed class AppConfig
{
    [JsonPropertyName("settings")]
    public AppSettings Settings { get; set; } = new();

    [JsonPropertyName("default_profile")]
    public TriggerPairConfig DefaultProfile { get; set; } = TriggerPairConfig.CreateOff();

    [JsonPropertyName("profiles")]
    public Dictionary<string, GameProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AppSettings
{
    [JsonPropertyName("reset_on_alt_tab")]
    public bool ResetOnAltTab { get; set; } = true;
}

public sealed class TriggerPairConfig
{
    [JsonPropertyName("l2")]
    public TriggerEffectConfig? L2 { get; set; }

    [JsonPropertyName("r2")]
    public TriggerEffectConfig? R2 { get; set; }

    public TriggerEffectConfig EffectiveL2 => L2 ?? TriggerEffectConfig.Off;
    public TriggerEffectConfig EffectiveR2 => R2 ?? TriggerEffectConfig.Off;

    public static TriggerPairConfig CreateOff() => new()
    {
        L2 = TriggerEffectConfig.CreateOff(),
        R2 = TriggerEffectConfig.CreateOff()
    };

    public bool HasSameEffectAs(TriggerPairConfig? other)
    {
        if (other is null)
        {
            return false;
        }

        return EffectiveL2.HasSameEffectAs(other.EffectiveL2) &&
               EffectiveR2.HasSameEffectAs(other.EffectiveR2);
    }
}

public sealed class GameProfile
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("ignore")]
    public bool Ignore { get; set; }

    [JsonPropertyName("l2")]
    public TriggerEffectConfig? L2 { get; set; }

    [JsonPropertyName("r2")]
    public TriggerEffectConfig? R2 { get; set; }

    public TriggerPairConfig ToTriggerPair() => new()
    {
        L2 = L2 ?? TriggerEffectConfig.Off,
        R2 = R2 ?? TriggerEffectConfig.Off
    };
}

public sealed class TriggerEffectConfig
{
    public static TriggerEffectConfig Off { get; } = CreateOff();

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "off";

    [JsonPropertyName("params")]
    public int[]? Params { get; set; }

    public static TriggerEffectConfig CreateOff() => new()
    {
        Mode = "off",
        Params = [0, 0, 0]
    };

    public TriggerMode ParseMode()
    {
        return Mode?.Trim().ToLowerInvariant() switch
        {
            "continuous" => TriggerMode.Continuous,
            "weapon" => TriggerMode.Weapon,
            "vibration" => TriggerMode.Vibration,
            _ => TriggerMode.Off
        };
    }

    public int GetParam(int index, int defaultValue = 0)
    {
        if (Params is null || index < 0 || index >= Params.Length)
        {
            return defaultValue;
        }

        return Params[index];
    }

    public bool HasSameEffectAs(TriggerEffectConfig? other)
    {
        if (other is null)
        {
            return false;
        }

        TriggerMode modeA = ParseMode();
        TriggerMode modeB = other.ParseMode();
        if (modeA != modeB)
        {
            return false;
        }

        return modeA switch
        {
            TriggerMode.Off => true,
            TriggerMode.Continuous =>
                ClampByte(GetParam(0)) == ClampByte(other.GetParam(0)) &&
                ClampByte(GetParam(1)) == ClampByte(other.GetParam(1)),
            TriggerMode.Weapon =>
                ClampByte(GetParam(0)) == ClampByte(other.GetParam(0)) &&
                ClampByte(GetParam(1)) == ClampByte(other.GetParam(1)) &&
                ClampByte(GetParam(2)) == ClampByte(other.GetParam(2)),
            TriggerMode.Vibration =>
                ClampByte(GetParam(0, 1), 1, 255) == ClampByte(other.GetParam(0, 1), 1, 255) &&
                ClampByte(GetParam(1)) == ClampByte(other.GetParam(1)) &&
                ClampByte(GetParam(2)) == ClampByte(other.GetParam(2)),
            _ => true
        };
    }

    private static byte ClampByte(int value, int min = 0, int max = 255) =>
        (byte)Math.Clamp(value, min, max);
}
