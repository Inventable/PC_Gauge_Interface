namespace Gauge.Protocol;

public readonly record struct GaugeMemoryAddress(uint Value)
{
    public byte Byte => (byte)Value;

    public byte Page => (byte)(Value >> 8);

    public byte Block => (byte)(Value >> 16);

    public byte Extended => (byte)(Value >> 24);

    public static GaugeMemoryAddress FromLittleEndian(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4)
        {
            throw new ArgumentException("A gauge memory address requires four bytes.", nameof(bytes));
        }

        return new GaugeMemoryAddress((uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24)));
    }

    public override string ToString()
    {
        return $"0x{Value:X8}";
    }
}
