namespace Gauge.Protocol;

public enum MemoryGaugeDataRecordType : byte
{
    PressureTemperature = 2,
    LowBatteryPressureTemperature = 3,
    VeryLowBatteryPressureTemperature = 4
}

public sealed record MemoryGaugeSample(
    int SampleIndex,
    uint TemperatureCounts,
    uint PressureCounts);

public sealed record MemoryGaugeDataRecord(
    int Index,
    uint Address,
    MemoryGaugeDataRecordType RecordType,
    MemoryGaugeSample FirstSample,
    MemoryGaugeSample SecondSample,
    ushort Counter,
    byte StoredCrc,
    byte ComputedCrc)
{
    public const int Length = 16;

    public bool IsCrcValid => StoredCrc == ComputedCrc;

    public byte BatteryStatus => RecordType switch
    {
        MemoryGaugeDataRecordType.LowBatteryPressureTemperature => 1,
        MemoryGaugeDataRecordType.VeryLowBatteryPressureTemperature => 2,
        _ => 0
    };

    public static MemoryGaugeDataRecord Parse(int index, uint address, ReadOnlySpan<byte> record)
    {
        if (record.Length != Length)
        {
            throw new ArgumentException($"A data record must be {Length} bytes.", nameof(record));
        }

        return new MemoryGaugeDataRecord(
            index,
            address,
            (MemoryGaugeDataRecordType)record[0],
            new MemoryGaugeSample(index * 2, ReadUInt24LittleEndian(record[1..4]), ReadUInt24LittleEndian(record[4..7])),
            new MemoryGaugeSample(index * 2 + 1, ReadUInt24LittleEndian(record[7..10]), ReadUInt24LittleEndian(record[10..13])),
            (ushort)(record[13] | (record[14] << 8)),
            record[15],
            Crc8.Compute(record[..15]));
    }

    public static IReadOnlyList<MemoryGaugeDataRecord> ParseMany(uint startAddress, ReadOnlySpan<byte> bytes)
    {
        var records = new List<MemoryGaugeDataRecord>();
        var recordCount = bytes.Length / Length;

        for (var index = 0; index < recordCount; index++)
        {
            var address = startAddress + (uint)(index * Length);
            records.Add(Parse(index, address, bytes.Slice(index * Length, Length)));
        }

        return records;
    }

    private static uint ReadUInt24LittleEndian(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 3)
        {
            throw new ArgumentException("A 24-bit count requires three bytes.", nameof(bytes));
        }

        return (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16));
    }
}
