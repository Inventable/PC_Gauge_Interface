namespace Gauge.Protocol;

public sealed record BootloaderVersion(
    byte Major,
    byte Minor,
    uint MaximumPacketSize,
    ushort DeviceId,
    byte EraseBlockSize,
    byte WriteBlockSize,
    byte[] ConfigurationBytes)
{
    public static BootloaderVersion Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != BootloaderProtocolConstants.VersionPayloadLength)
        {
            throw new GaugeProtocolException(
                $"Bootloader version payload must contain {BootloaderProtocolConstants.VersionPayloadLength} bytes.");
        }

        var maximumPacketSize = (uint)(payload[2]
            | (payload[3] << 8)
            | (payload[4] << 16)
            | (payload[5] << 24));
        var deviceId = (ushort)(payload[6] | (payload[7] << 8));

        return new BootloaderVersion(
            payload[1],
            payload[0],
            maximumPacketSize,
            deviceId,
            payload[10],
            payload[11],
            payload[12..16].ToArray());
    }
}
