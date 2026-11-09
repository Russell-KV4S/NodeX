namespace NodeX.Core;

/// <summary>A BLE device seen while scanning for a PTT button.</summary>
public sealed record BleDeviceInfo(string Address, string Name, int Rssi, bool Bonded = false)
{
    public string Display => string.IsNullOrWhiteSpace(Name) ? Address : $"{Name} ({Address})";
}

/// <summary>
/// What a BLE PTT button sends, learned by the Learn wizard: which characteristic notifies, and the exact bytes for
/// "pressed" and "released". Only this exact pattern keys the transmitter; nothing else a device sends is ever
/// interpreted as PTT.
/// </summary>
public sealed record BlePttProfile(
    string DeviceAddress,
    string DeviceName,
    string CharacteristicUuid,
    string PressHex,
    string ReleaseHex)
{
    /// <summary>False for a button that only sends one packet (no release): it is then treated as a tap.</summary>
    public bool HasRelease => ReleaseHex.Length > 0;

    public bool IsComplete =>
        DeviceAddress.Length > 0 && CharacteristicUuid.Length > 0 && PressHex.Length > 0;

    public bool IsPress(string characteristicUuid, ReadOnlySpan<byte> data) => Matches(characteristicUuid, data, PressHex);

    public bool IsRelease(string characteristicUuid, ReadOnlySpan<byte> data) =>
        HasRelease && Matches(characteristicUuid, data, ReleaseHex);

    private bool Matches(string characteristicUuid, ReadOnlySpan<byte> data, string hex) =>
        string.Equals(characteristicUuid, CharacteristicUuid, StringComparison.OrdinalIgnoreCase)
        && hex.Length > 0
        && string.Equals(Convert.ToHexString(data), hex, StringComparison.OrdinalIgnoreCase);

    public static string ToHex(ReadOnlySpan<byte> data) => Convert.ToHexString(data);
}
