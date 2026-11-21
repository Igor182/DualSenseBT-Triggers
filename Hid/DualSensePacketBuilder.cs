using System.Buffers.Binary;
using DualSenseBTTriggers.Config;

namespace DualSenseBTTriggers.Hid;

/// <summary>
/// Builds 78-byte Sony DualSense Bluetooth HID output reports (Report ID 0x31)
/// isolated exclusively to adaptive triggers (L2 and R2), with IEEE 802.3 CRC-32.
/// </summary>
public sealed class DualSensePacketBuilder
{
    public const int BluetoothOutputReportLength = 78;
    public const byte BluetoothReportId = 0x31;
    public const byte BluetoothOutputTag = 0x10; // DS_OUTPUT_TAG required by DualSense BT firmware

    // Valid Flag 0 (byte[3]): 0x04 (R2 trigger) | 0x08 (L2 trigger) = 0x0C.
    // Bits 0x01 and 0x02 (vibration motors) remain 0 to avoid conflicting with native/Steam rumble.
    public const byte ValidFlag0TriggersOnly = 0x0C;

    // Valid Flag 1 (byte[4]): 0x00 so Steam/games can control LEDs/Lightbar without resetting triggers.
    public const byte ValidFlag1None = 0x00;

    // R2 block: byte[13] (mode) + byte[14..22] (9 effect params) + byte[23] (0x00)
    public const int RightTriggerOffset = 13;

    // L2 block: byte[24] (mode) + byte[25..33] (9 effect params) + byte[34] (0x00)
    public const int LeftTriggerOffset = 24;

    // CRC-32 occupies bytes 74..77 (Little-Endian)
    public const int CrcOffset = 74;

    // Bluetooth HID Output Report CRC-32 seed byte
    public const byte BluetoothCrcSeed = 0xA2;

    private const uint Crc32Polynomial = 0xEDB88320u;
    private static readonly uint[] Crc32Table = BuildCrc32Table();
    private byte _sequenceCounter;

    public byte[] BuildTriggerPacket(TriggerPairConfig pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        return BuildTriggerPacket(pair.EffectiveL2, pair.EffectiveR2);
    }

    public byte[] BuildTriggerPacket(TriggerEffectConfig l2, TriggerEffectConfig r2)
    {
        ArgumentNullException.ThrowIfNull(l2);
        ArgumentNullException.ThrowIfNull(r2);

        byte[] buffer = new byte[BluetoothOutputReportLength];

        buffer[0] = BluetoothReportId;
        buffer[1] = (byte)((_sequenceCounter++ & 0x0F) << 4);
        buffer[2] = BluetoothOutputTag;
        buffer[3] = ValidFlag0TriggersOnly;
        buffer[4] = ValidFlag1None;

        WriteTriggerBlock(buffer, RightTriggerOffset, r2);
        WriteTriggerBlock(buffer, LeftTriggerOffset, l2);

        uint crc = ComputeBluetoothCrc32(buffer.AsSpan(0, CrcOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(CrcOffset, 4), crc);

        return buffer;
    }

    public byte[] BuildResetPacket() =>
        BuildTriggerPacket(TriggerEffectConfig.Off, TriggerEffectConfig.Off);

    /// <summary>
    /// Computes standard CRC-32 (polynomial 0xEDB88320) seeded with 0xA2 followed by bytes 0..73.
    /// </summary>
    public static uint ComputeBluetoothCrc32(ReadOnlySpan<byte> payloadWithoutCrc)
    {
        uint crc = 0xFFFFFFFFu;

        // Feed Bluetooth HID Output seed byte 0xA2 first
        crc = (crc >> 8) ^ Crc32Table[(byte)(crc ^ BluetoothCrcSeed)];

        for (int i = 0; i < payloadWithoutCrc.Length; i++)
        {
            crc = (crc >> 8) ^ Crc32Table[(byte)(crc ^ payloadWithoutCrc[i])];
        }

        return ~crc;
    }

    private static void WriteTriggerBlock(byte[] buffer, int offset, TriggerEffectConfig config)
    {
        // Bytes [offset .. offset + 10] are already zero-initialized.
        // offset      = trigger mode code
        // offset+1..9 = effect parameters (9 bytes)
        TriggerMode mode = config.ParseMode();
        buffer[offset] = (byte)mode;

        switch (mode)
        {
            case TriggerMode.Off:
                // All parameters remain 0x00.
                break;

            case TriggerMode.Continuous:
                // [start_pos (0-255), force (0-255)]
                buffer[offset + 1] = ClampByte(config.GetParam(0));
                buffer[offset + 2] = ClampByte(config.GetParam(1));
                break;

            case TriggerMode.Weapon:
                // [start_pos (0-255), end_pos (0-255), force (0-255)]
                buffer[offset + 1] = ClampByte(config.GetParam(0));
                buffer[offset + 2] = ClampByte(config.GetParam(1));
                buffer[offset + 3] = ClampByte(config.GetParam(2));
                break;

            case TriggerMode.Vibration:
                // [frequency_hz (1-255), amplitude (0-255), start_pos (0-255)]
                buffer[offset + 1] = ClampByte(config.GetParam(0, defaultValue: 1), min: 1, max: 255);
                buffer[offset + 2] = ClampByte(config.GetParam(1));
                buffer[offset + 3] = ClampByte(config.GetParam(2));
                break;
        }
    }

    private static byte ClampByte(int value, int min = 0, int max = 255) =>
        (byte)Math.Clamp(value, min, max);

    private static uint[] BuildCrc32Table()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ Crc32Polynomial : crc >> 1;
            }
            table[i] = crc;
        }
        return table;
    }
}
