namespace Gauge.Protocol;

public enum MemoryGaugeDataRecordType : byte
{
    LegacyPressureTemperature = 1,
    PressureTemperature = 2,
    LowBatteryPressureTemperature = 3,
    VeryLowBatteryPressureTemperature = 4,
    AcousticReceived = 5,
    AcousticReceiveFailed = 6,
    AcousticSent = 7,
    Timestamp = 8,
    AcousticBitCountsLow = 9,
    AcousticBitCountsHigh = 10,
    AcousticAdc = 11,
    RecordTypeError = 0xFE
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

    public bool IsPressureTemperature => RecordType is
        MemoryGaugeDataRecordType.LegacyPressureTemperature or
        MemoryGaugeDataRecordType.PressureTemperature or
        MemoryGaugeDataRecordType.LowBatteryPressureTemperature or
        MemoryGaugeDataRecordType.VeryLowBatteryPressureTemperature;

    public bool IsAcoustic => RecordType is
        MemoryGaugeDataRecordType.AcousticReceived or
        MemoryGaugeDataRecordType.AcousticReceiveFailed or
        MemoryGaugeDataRecordType.AcousticSent;

    public bool IsKnownAuxiliary => RecordType is
        MemoryGaugeDataRecordType.Timestamp or
        MemoryGaugeDataRecordType.AcousticBitCountsLow or
        MemoryGaugeDataRecordType.AcousticBitCountsHigh or
        MemoryGaugeDataRecordType.AcousticAdc;

    public bool IsAcousticDiagnostic => RecordType is
        MemoryGaugeDataRecordType.AcousticBitCountsLow or
        MemoryGaugeDataRecordType.AcousticBitCountsHigh;

    public bool IsRawAcoustic => RecordType == MemoryGaugeDataRecordType.AcousticAdc;

    public bool IsTimestamp => RecordType == MemoryGaugeDataRecordType.Timestamp;

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

    public static IReadOnlyList<MemoryGaugeDataRecord> ParseMany(
        uint startAddress,
        ReadOnlySpan<byte> bytes,
        int firstRecordIndex = 0)
    {
        if (firstRecordIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(firstRecordIndex));
        }

        var records = new List<MemoryGaugeDataRecord>();
        var recordCount = bytes.Length / Length;

        for (var offset = 0; offset < recordCount; offset++)
        {
            var index = firstRecordIndex + offset;
            var address = startAddress + (uint)(offset * Length);
            records.Add(Parse(index, address, bytes.Slice(offset * Length, Length)));
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

public sealed record MemoryGaugeRecordSummary(
    int TotalRecordCount,
    int PressureTemperatureRecordCount,
    int AcousticRecordCount,
    int FailedAcousticRecordCount,
    int AcousticDiagnosticRecordCount,
    int RawAcousticRecordCount,
    int TimestampRecordCount,
    int UnknownRecordCount,
    int CrcErrorCount)
{
    public static MemoryGaugeRecordSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    public int ExcludedRecordCount => TotalRecordCount - PressureTemperatureRecordCount;

    public int AuxiliaryRecordCount => AcousticDiagnosticRecordCount + RawAcousticRecordCount + TimestampRecordCount;

    public static MemoryGaugeRecordSummary Analyze(ReadOnlySpan<byte> bytes, uint startAddress = 0)
    {
        var completeLength = bytes.Length / MemoryGaugeDataRecord.Length * MemoryGaugeDataRecord.Length;
        var records = MemoryGaugeDataRecord.ParseMany(startAddress, bytes[..completeLength]);

        return new MemoryGaugeRecordSummary(
            records.Count,
            records.Count(record => record.IsPressureTemperature),
            records.Count(record => record.IsAcoustic),
            records.Count(record => record.RecordType == MemoryGaugeDataRecordType.AcousticReceiveFailed),
            records.Count(record => record.IsAcousticDiagnostic),
            records.Count(record => record.IsRawAcoustic),
            records.Count(record => record.IsTimestamp),
            records.Count(record => !record.IsPressureTemperature && !record.IsAcoustic && !record.IsKnownAuxiliary),
            records.Count(record => !record.IsCrcValid));
    }

    public MemoryGaugeRecordSummary Combine(MemoryGaugeRecordSummary other)
    {
        return new MemoryGaugeRecordSummary(
            TotalRecordCount + other.TotalRecordCount,
            PressureTemperatureRecordCount + other.PressureTemperatureRecordCount,
            AcousticRecordCount + other.AcousticRecordCount,
            FailedAcousticRecordCount + other.FailedAcousticRecordCount,
            AcousticDiagnosticRecordCount + other.AcousticDiagnosticRecordCount,
            RawAcousticRecordCount + other.RawAcousticRecordCount,
            TimestampRecordCount + other.TimestampRecordCount,
            UnknownRecordCount + other.UnknownRecordCount,
            CrcErrorCount + other.CrcErrorCount);
    }
}
