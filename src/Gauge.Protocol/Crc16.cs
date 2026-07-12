namespace Gauge.Protocol;

public static class Crc16
{
    public const ushort Polynomial = 0x8005;

    public static ushort Compute(ReadOnlySpan<byte> data, ushort initial = 0)
    {
        var crc = initial;

        foreach (var input in data)
        {
            var value = input;

            for (var bit = 0; bit < 8; bit++)
            {
                var xor = (((crc & 0x8000) >> 8) ^ (value & 0x80)) != 0;
                crc = xor
                    ? (ushort)((crc << 1) ^ Polynomial)
                    : (ushort)(crc << 1);
                value = (byte)(value << 1);
            }
        }

        return crc;
    }
}

