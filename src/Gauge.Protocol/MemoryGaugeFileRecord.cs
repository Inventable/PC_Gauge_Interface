namespace Gauge.Protocol;

public enum MemoryGaugeFileRecordType : byte
{
    Start = 0,
    Continue = 1
}

public sealed record MemoryGaugeFileRecord(
    int Index,
    GaugeMemoryAddress DataAddress,
    MemoryGaugeFileRecordType RecordType,
    ushort MeasurementInterval,
    byte ResetCause,
    byte StoredCrc,
    byte ComputedCrc)
{
    public const int Length = 16;

    public bool IsCrcValid => StoredCrc == ComputedCrc;

    public static bool IsEmpty(ReadOnlySpan<byte> record)
    {
        if (record.Length != Length)
        {
            throw new ArgumentException($"A file record must be {Length} bytes.", nameof(record));
        }

        foreach (var value in record)
        {
            if (value != 0xFF)
            {
                return false;
            }
        }

        return true;
    }

    public static MemoryGaugeFileRecord Parse(int index, ReadOnlySpan<byte> record)
    {
        if (record.Length != Length)
        {
            throw new ArgumentException($"A file record must be {Length} bytes.", nameof(record));
        }

        var computedCrc = Crc8.Compute(record[..15]);
        return new MemoryGaugeFileRecord(
            index,
            GaugeMemoryAddress.FromLittleEndian(record[..4]),
            (MemoryGaugeFileRecordType)record[4],
            (ushort)(record[5] | (record[6] << 8)),
            record[8],
            record[15],
            computedCrc);
    }

    public static IReadOnlyList<MemoryGaugeFileRecord> ParseTable(ReadOnlySpan<byte> table)
    {
        var records = new List<MemoryGaugeFileRecord>();
        var recordCount = table.Length / Length;

        for (var index = 0; index < recordCount; index++)
        {
            var recordBytes = table.Slice(index * Length, Length);
            if (IsEmpty(recordBytes))
            {
                break;
            }

            var record = Parse(index, recordBytes);
            if (!record.IsCrcValid)
            {
                break;
            }

            if (record.RecordType == MemoryGaugeFileRecordType.Start)
            {
                records.Add(record);
            }
        }

        return records;
    }
}
