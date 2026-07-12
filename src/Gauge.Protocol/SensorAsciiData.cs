using System.Globalization;
using System.Text;

namespace Gauge.Protocol;

public static class SensorAsciiData
{
    public static string DecodePayload(ReadOnlySpan<byte> payload)
    {
        var text = Encoding.ASCII.GetString(payload).Replace("\r\n=", string.Empty, StringComparison.Ordinal);
        return text.Trim();
    }

    public static IReadOnlyList<IReadOnlyList<double>> ParseHexDoubleRows(ReadOnlySpan<byte> payload)
    {
        var text = DecodePayload(payload);
        var rows = new List<IReadOnlyList<double>>();

        foreach (var line in text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            var values = new List<double>();
            foreach (var token in line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                values.Add(ParseHexDouble(token));
            }

            rows.Add(values);
        }

        return rows;
    }

    public static double ParseHexDouble(string hex)
    {
        if (hex.Length != 16)
        {
            throw new ArgumentException("A sensor coefficient must be 16 hexadecimal characters.", nameof(hex));
        }

        Span<byte> bytes = stackalloc byte[8];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = byte.Parse(hex.AsSpan(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        if (BitConverter.IsLittleEndian)
        {
            bytes.Reverse();
        }

        return BitConverter.ToDouble(bytes);
    }
}
