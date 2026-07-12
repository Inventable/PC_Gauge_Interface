namespace Gauge.Protocol;

public sealed record GaugeFrame(
    GaugeCommand Command,
    ushort DataLength,
    uint Address,
    byte[] Payload)
{
    public static GaugeFrame Create(GaugeCommand command, uint address = 0, ReadOnlySpan<byte> payload = default)
    {
        if (payload.Length > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "Payload is too large for the gauge frame length field.");
        }

        return new GaugeFrame(command, (ushort)payload.Length, address, payload.ToArray());
    }

    public static GaugeFrame CreateReadRequest(GaugeCommand command, uint address, ushort dataLength)
    {
        return new GaugeFrame(command, dataLength, address, []);
    }
}
