namespace Gauge.Protocol;

public static class Crc32C
{
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                var mask = 0u - (crc & 1u);
                crc = (crc >> 1) ^ (0x82F63B78u & mask);
            }
        }

        return ~crc;
    }
}

public static class Crc64Ecma
{
    private const ulong Polynomial = 0x42F0E1EBA9EA3693ul;

    public static ulong Compute(ReadOnlySpan<byte> data)
    {
        ulong crc = 0;
        foreach (var value in data)
        {
            crc ^= (ulong)value << 56;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000000000000000ul) != 0
                    ? (crc << 1) ^ Polynomial
                    : crc << 1;
            }
        }

        return crc;
    }
}
