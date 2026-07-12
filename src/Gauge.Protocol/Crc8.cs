namespace Gauge.Protocol;

public static class Crc8
{
    public const byte Polynomial = 0x9B;

    public static byte Compute(ReadOnlySpan<byte> data, byte initial = 0)
    {
        var crc = initial;

        foreach (var input in data)
        {
            var value = (byte)(crc ^ input);

            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 0x80) != 0
                    ? (byte)((value << 1) ^ Polynomial)
                    : (byte)(value << 1);
            }

            crc = value;
        }

        return crc;
    }
}

