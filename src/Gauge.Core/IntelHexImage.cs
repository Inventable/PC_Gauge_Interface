using System.Globalization;

namespace Gauge.Core;

public sealed class IntelHexImage
{
    private IntelHexImage(IReadOnlyDictionary<uint, byte> bytes)
    {
        Bytes = bytes;
    }

    public IReadOnlyDictionary<uint, byte> Bytes { get; }

    public static IntelHexImage Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadLines(path));
    }

    public static IntelHexImage Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var bytes = new SortedDictionary<uint, byte>();
        uint addressBase = 0;
        var endOfFileSeen = false;
        var lineNumber = 0;

        foreach (var rawLine in lines)
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (endOfFileSeen)
            {
                throw new FormatException($"Intel HEX contains data after EOF at line {lineNumber}.");
            }

            var record = ParseRecord(line, lineNumber);
            var byteCount = record[0];
            var offset = (ushort)((record[1] << 8) | record[2]);
            var recordType = record[3];
            var data = record.AsSpan(4, byteCount);

            switch (recordType)
            {
                case 0x00:
                    for (var index = 0; index < data.Length; index++)
                    {
                        var address = checked(addressBase + offset + (uint)index);
                        if (bytes.TryGetValue(address, out var existing) && existing != data[index])
                        {
                            throw new FormatException(
                                $"Intel HEX assigns conflicting values at address 0x{address:X8}.");
                        }

                        bytes[address] = data[index];
                    }
                    break;

                case 0x01:
                    RequireLength(data, 0, lineNumber, "EOF");
                    endOfFileSeen = true;
                    break;

                case 0x02:
                    RequireLength(data, 2, lineNumber, "extended segment address");
                    addressBase = (uint)((data[0] << 8) | data[1]) << 4;
                    break;

                case 0x03:
                    RequireLength(data, 4, lineNumber, "start segment address");
                    break;

                case 0x04:
                    RequireLength(data, 2, lineNumber, "extended linear address");
                    addressBase = (uint)((data[0] << 8) | data[1]) << 16;
                    break;

                case 0x05:
                    RequireLength(data, 4, lineNumber, "start linear address");
                    break;

                default:
                    throw new FormatException($"Unsupported Intel HEX record type 0x{recordType:X2} at line {lineNumber}.");
            }
        }

        if (!endOfFileSeen)
        {
            throw new FormatException("Intel HEX is missing its EOF record.");
        }

        return new IntelHexImage(bytes);
    }

    private static byte[] ParseRecord(string line, int lineNumber)
    {
        if (line[0] != ':' || line.Length < 11 || ((line.Length - 1) & 1) != 0)
        {
            throw new FormatException($"Invalid Intel HEX record shape at line {lineNumber}.");
        }

        var record = new byte[(line.Length - 1) / 2];
        for (var index = 0; index < record.Length; index++)
        {
            if (!byte.TryParse(
                    line.AsSpan(1 + (index * 2), 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out record[index]))
            {
                throw new FormatException($"Invalid hexadecimal byte at line {lineNumber}.");
            }
        }

        var expectedLength = record[0] + 5;
        if (record.Length != expectedLength)
        {
            throw new FormatException(
                $"Intel HEX byte count mismatch at line {lineNumber}. Expected {expectedLength}, got {record.Length}.");
        }

        var checksum = 0;
        foreach (var value in record)
        {
            checksum = (checksum + value) & 0xFF;
        }

        if (checksum != 0)
        {
            throw new FormatException($"Intel HEX checksum failed at line {lineNumber}.");
        }

        return record;
    }

    private static void RequireLength(ReadOnlySpan<byte> data, int expected, int lineNumber, string recordName)
    {
        if (data.Length != expected)
        {
            throw new FormatException(
                $"Intel HEX {recordName} record at line {lineNumber} must contain {expected} data bytes.");
        }
    }
}
