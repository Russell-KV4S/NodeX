namespace NodeX.Core;

/// <summary>
/// Device profile type for Bluetooth PTT buttons.
/// </summary>
public enum BluetoothPttDeviceProfile
{
    AutoDetect,
    /// <summary>PRYMEBLU BT-PTT BLE button</summary>
    PrymeBlu,
    /// <summary>B01 / Dellking BLE button</summary>
    B01,
    /// <summary>Standard BLE HID over GATT key reports</summary>
    GenericBleHid,
    /// <summary>TexMicro / Boxchip BLE PTT Protocol</summary>
    TexMicro
}

/// <summary>
/// Parser for raw BLE GATT characteristic notifications and HID input reports
/// across popular amateur radio Bluetooth PTT hardware.
/// </summary>
public static class BluetoothPttPacketParser
{
    /// <summary>
    /// Parses a BLE GATT notification packet into a PTT state (true = pressed, false = released).
    /// Returns null if the packet does not represent a valid PTT event.
    /// </summary>
    public static bool? TryParsePacket(
        ReadOnlySpan<byte> rawData,
        BluetoothPttDeviceProfile profile = BluetoothPttDeviceProfile.AutoDetect)
    {
        if (rawData.IsEmpty) return null;

        return profile switch
        {
            BluetoothPttDeviceProfile.PrymeBlu => ParsePryme(rawData),
            BluetoothPttDeviceProfile.B01 => ParseB01(rawData),
            BluetoothPttDeviceProfile.GenericBleHid => ParseBleHid(rawData),
            BluetoothPttDeviceProfile.TexMicro => ParseTexMicro(rawData),
            _ => AutoDetectAndParse(rawData)
        };
    }

    private static bool? AutoDetectAndParse(ReadOnlySpan<byte> data)
    {
        // 1. Single-byte standard GATT notification (e.g. 0x01 = Down, 0x00 = Up)
        if (data.Length == 1)
        {
            return data[0] switch
            {
                0x01 => true,
                0x00 => false,
                _ => null
            };
        }

        // 2. PRYMEBLU format (often [0xAA, 0x01] / [0xAA, 0x00] or [0x01, 0x01])
        if (data.Length >= 2 && data[0] == 0xAA)
        {
            return ParsePryme(data);
        }

        // 3. TexMicro preamble [0x55, 0xAA, ...]
        if (data.Length >= 4 && data[0] == 0x55 && data[1] == 0xAA)
        {
            return ParseTexMicro(data);
        }

        // 4. B01 / Dellking (typically 2 to 3 bytes starting with 0x01 or 0x02)
        if (data.Length >= 2 && (data[0] == 0x01 || data[0] == 0x02))
        {
            return ParseB01(data);
        }

        // 5. Fallback HID report analysis
        return ParseBleHid(data);
    }

    private static bool? ParsePryme(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return null;

        // Byte 1 indicates state (0x01 = PTT down, 0x00 = PTT up)
        return data[1] switch
        {
            0x01 => true,
            0x00 => false,
            _ => null
        };
    }

    private static bool? ParseB01(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return null;

        // B01 / Dellking state byte
        byte stateByte = data[1];
        return (stateByte & 0x01) != 0;
    }

    private static bool? ParseTexMicro(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return null;

        // TexMicro packet: [0x55, 0xAA, Command, State, ...]
        // Command 0x01 = PTT event, State 0x01 = Pressed, 0x00 = Released
        if (data[2] == 0x01)
        {
            return data[3] switch
            {
                0x01 => true,
                0x00 => false,
                _ => null
            };
        }

        return null;
    }

    private static bool? ParseBleHid(ReadOnlySpan<byte> data)
    {
        // Standard HID Keyboard report:
        // [Modifier, Reserved, KeyCode1, KeyCode2, ...]
        // If any key is active, consider PTT pressed; if all keycodes are 0, released.
        if (data.Length >= 3)
        {
            for (int i = 2; i < data.Length; i++)
            {
                if (data[i] != 0) return true;
            }
            return false;
        }

        return null;
    }
}
