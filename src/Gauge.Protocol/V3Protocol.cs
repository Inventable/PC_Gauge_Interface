using System.Buffers.Binary;

namespace Gauge.Protocol;

[Flags]
public enum V3CapabilityFlags : byte
{
    Mirror = 1,
    Catalog = 2,
    Bch = 4,
    IndependentCrc = 8,
    DiagnosticJournal = 16
}

public enum V3MemoryMode : byte
{
    Full = 0,
    Mirror = 1
}

public sealed record V3Capabilities(
    byte StorageMinor,
    V3CapabilityFlags Flags,
    uint CatalogStart,
    uint CatalogLength,
    uint DataStart,
    uint StorageEnd,
    ushort PageBytes,
    ushort SectorBytes,
    byte MaximumSamplesPerPage,
    byte BchCorrectionLimit,
    byte ChecksumId,
    ushort MaximumSerialPayload,
    V3MemoryMode MemoryMode = V3MemoryMode.Mirror,
    byte WriteTargetMask = 0x03,
    bool IsLegacyLayout = false)
{
    public const int PayloadLength = 32;
    public const byte Schema = 1;
    public const byte StorageMajor = 3;
    public const uint CatalogStartAddress = 0x00000000;
    public const uint CatalogBytes = 0x00010000;
    public const uint DataStartAddress = 0x00010000;
    public const uint MirrorStorageEnd = 0x01FF0000;
    public const uint FullStorageEnd = 0x03FF0000;
    public const uint PhysicalChipBoundary = 0x02000000;
    public const ushort MaximumResponseBytes = 792;

    public bool UsesMirror => MemoryMode == V3MemoryMode.Mirror;

    public uint DiagnosticStart => MemoryMode == V3MemoryMode.Mirror
        ? MirrorStorageEnd
        : FullStorageEnd;

    public static V3Capabilities Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != PayloadLength)
        {
            throw new GaugeProtocolException($"V3 capabilities returned {payload.Length} byte(s); expected {PayloadLength}.");
        }

        if (payload[0] != Schema || payload[1] != StorageMajor)
        {
            throw new GaugeProtocolException($"Unsupported V3 capabilities schema/storage version {payload[0]}/{payload[1]}.");
        }

        if (payload[2] != 1)
        {
            throw new GaugeProtocolException($"Unsupported V3 storage minor version {payload[2]}.");
        }

        const byte supportedFlags = 0x1F;
        if ((payload[3] & ~supportedFlags) != 0 || payload[31] != 0)
        {
            throw new GaugeProtocolException("V3 capabilities contain non-zero reserved fields.");
        }

        var memoryMode = payload[29] switch
            {
                0 => V3MemoryMode.Full,
                1 => V3MemoryMode.Mirror,
                _ => throw new GaugeProtocolException(
                    $"V3 capabilities report unknown memory mode {payload[29]}.")
            };
        var writeTargetMask = payload[30];

        var result = new V3Capabilities(
            payload[2],
            (V3CapabilityFlags)payload[3],
            BinaryPrimitives.ReadUInt32LittleEndian(payload[4..8]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[8..12]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[12..16]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[16..20]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[20..22]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[22..24]),
            payload[24],
            payload[25],
            payload[26],
            BinaryPrimitives.ReadUInt16LittleEndian(payload[27..29]),
            memoryMode,
            writeTargetMask,
            false);

        const V3CapabilityFlags required =
            V3CapabilityFlags.Mirror | V3CapabilityFlags.Catalog |
            V3CapabilityFlags.Bch | V3CapabilityFlags.IndependentCrc;
        var requiredFlags = required | V3CapabilityFlags.DiagnosticJournal;
        var expectedStorageEnd = result.MemoryMode == V3MemoryMode.Full
            ? FullStorageEnd
            : MirrorStorageEnd;
        var expectedTargetMask = result.MemoryMode == V3MemoryMode.Full
            ? (byte)0x01
            : (byte)0x03;
        if ((result.Flags & requiredFlags) != requiredFlags ||
            result.CatalogStart != CatalogStartAddress ||
            result.CatalogLength != CatalogBytes ||
            result.DataStart != DataStartAddress ||
            result.StorageEnd != expectedStorageEnd ||
            result.WriteTargetMask != expectedTargetMask ||
            result.PageBytes != V3PageCodec.PhysicalBytes ||
            result.SectorBytes != 4096 ||
            result.MaximumSamplesPerPage != V3PageCodec.CompactMaximumSamples ||
            result.BchCorrectionLimit != V3Bch16.CorrectionLimit ||
            result.ChecksumId != 2 ||
            result.MaximumSerialPayload != MaximumResponseBytes)
        {
            throw new GaugeProtocolException("V3 capabilities contain unsupported or impossible geometry.");
        }

        return result;
    }
}

[Flags]
public enum V3DiagnosticFlags : byte
{
    Recovered = 1,
    RamEventsPending = 2,
    WriteInhibited = 4,
    Degraded = 8,
    CrashCapsuleValid = 16,
    LoggingQualified = 32,
    PriorSessionOutcomePending = 64,
    StorageFailoverCapsuleValid = 128
}

public sealed record V3DiagnosticStatus(
    V3DiagnosticFlags Flags,
    byte JournalRecoveryStatus,
    byte DegradedReplicaMask,
    uint BootId,
    uint CommittedPageCount,
    uint RegionStart,
    uint RegionLength,
    byte PendingRamEventCount,
    byte FailedChipMask,
    ushort LatestEventId,
    uint RamFallbackFailureCount,
    uint CrashCapsuleGeneration)
{
    public const int PayloadLength = 32;
    public const byte Schema = 1;
    public const uint RegionBytes = 0x00010000;

    public bool HasStorageFailover =>
        Flags.HasFlag(V3DiagnosticFlags.StorageFailoverCapsuleValid);

    public bool RequiresMemoryService =>
        HasStorageFailover || FailedChipMask != 0 || DegradedReplicaMask != 0;

    public bool ProbeBothFirst => FailedChipMask == 0x03;

    public int PreferredReplicaId => FailedChipMask == 0x01 ? 1 : 0;

    public static V3DiagnosticStatus Parse(
        ReadOnlySpan<byte> payload,
        V3Capabilities capabilities)
    {
        if (payload.Length != PayloadLength)
        {
            throw new GaugeProtocolException(
                $"V3 diagnostic status returned {payload.Length} byte(s); expected {PayloadLength}.");
        }

        if (payload[0] != Schema)
        {
            throw new GaugeProtocolException(
                $"Unsupported V3 diagnostic status schema {payload[0]}.");
        }

        var result = new V3DiagnosticStatus(
            (V3DiagnosticFlags)payload[1],
            payload[2],
            payload[3],
            BinaryPrimitives.ReadUInt32LittleEndian(payload[4..8]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[8..12]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[12..16]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[16..20]),
            payload[20],
            payload[21],
            BinaryPrimitives.ReadUInt16LittleEndian(payload[22..24]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[24..28]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[28..32]));

        if ((result.DegradedReplicaMask & 0xFC) != 0 ||
            (result.FailedChipMask & 0xFC) != 0 ||
            result.RegionStart != capabilities.DiagnosticStart ||
            result.RegionLength != RegionBytes ||
            (result.HasStorageFailover != (result.FailedChipMask != 0)))
        {
            throw new GaugeProtocolException(
                "V3 diagnostic status contains inconsistent failover or region values.");
        }

        return result;
    }
}

public sealed record V3CatalogSummary(
    byte RecoveryStatus,
    byte ValidReplicaMask,
    byte DegradedReplicaMask,
    uint RecordCount,
    uint LatestCatalogSequence,
    uint LatestFileId,
    uint LatestFileStart,
    uint SamplesCommitted)
{
    public const int PayloadLength = 24;

    public bool IsEmpty => RecordCount == 0;
    public bool SampleCountRequiresRecovery => SamplesCommitted == uint.MaxValue;

    public static V3CatalogSummary Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != PayloadLength)
        {
            throw new GaugeProtocolException($"V3 catalog summary returned {payload.Length} byte(s); expected {PayloadLength}.");
        }

        if (payload[0] != 1)
        {
            throw new GaugeProtocolException($"Unsupported V3 catalog summary schema {payload[0]}.");
        }

        var result = new V3CatalogSummary(
            payload[1],
            payload[2],
            payload[3],
            BinaryPrimitives.ReadUInt32LittleEndian(payload[4..8]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[8..12]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[12..16]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[16..20]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[20..24]));

        if ((result.ValidReplicaMask & 0xFC) != 0 ||
            (result.DegradedReplicaMask & 0xFC) != 0 ||
            (result.ValidReplicaMask & result.DegradedReplicaMask) != 0 ||
            result.RecordCount > 256 ||
            (result.IsEmpty && (result.LatestFileId != 0 || result.LatestFileStart != 0)) ||
            (!result.IsEmpty && (result.LatestCatalogSequence + 1 != result.RecordCount ||
                                 result.LatestFileId == 0)))
        {
            throw new GaugeProtocolException("V3 catalog summary contains inconsistent values.");
        }

        return result;
    }
}
