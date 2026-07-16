namespace Gauge.Protocol;

public sealed record DeviceData(
    byte FirmwareMajor,
    byte FirmwareMinor,
    uint DeviceType,
    uint DeviceSerial,
    uint PcbType,
    uint PcbSerial,
    ushort MeasurementInterval,
    byte MemoryMode,
    byte? EraseStatus)
{
    public static DeviceData DecodeMemoryGauge(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 22)
        {
            throw new GaugeProtocolException("Memory gauge identify payload is too short.");
        }

        return new DeviceData(
            payload[0],
            payload[1],
            ReadUInt32LittleEndian(payload[2..6]),
            ReadUInt32LittleEndian(payload[6..10]),
            ReadUInt32LittleEndian(payload[10..14]),
            ReadUInt32LittleEndian(payload[14..18]),
            ReadUInt16LittleEndian(payload[18..20]),
            payload[20],
            payload[21]);
    }

    public static DeviceData DecodeAcousticGauge(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 32)
        {
            throw new GaugeProtocolException("Acoustic gauge identify payload is too short.");
        }

        return new DeviceData(
            payload[0],
            payload[1],
            ReadUInt32LittleEndian(payload[2..6]),
            ReadUInt32LittleEndian(payload[6..10]),
            ReadUInt32LittleEndian(payload[10..14]),
            ReadUInt32LittleEndian(payload[14..18]),
            ReadUInt16LittleEndian(payload[18..20]),
            payload[20],
            payload[29]);
    }

    private static ushort ReadUInt16LittleEndian(ReadOnlySpan<byte> value)
    {
        return (ushort)(value[0] | (value[1] << 8));
    }

    private static uint ReadUInt32LittleEndian(ReadOnlySpan<byte> value)
    {
        return (uint)(value[0] | (value[1] << 8) | (value[2] << 16) | (value[3] << 24));
    }
}
