using System.Buffers.Binary;

namespace Gauge.Protocol;

[Flags]
public enum V3CapabilityFlags : byte
{
    Mirror = 1,
    Catalog = 2,
    Bch = 4,
    IndependentCrc = 8
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
    ushort MaximumSerialPayload)
{
    public const int PayloadLength = 32;
    public const byte Schema = 1;
    public const byte StorageMajor = 3;

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

        if (payload[2] != 0)
        {
            throw new GaugeProtocolException($"Unsupported V3 storage minor version {payload[2]}.");
        }

        if ((payload[3] & 0xF0) != 0 || !payload[29..32].SequenceEqual("\0\0\0"u8))
        {
            throw new GaugeProtocolException("V3 capabilities contain non-zero reserved fields.");
        }

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
            BinaryPrimitives.ReadUInt16LittleEndian(payload[27..29]));

        const V3CapabilityFlags required =
            V3CapabilityFlags.Mirror | V3CapabilityFlags.Catalog |
            V3CapabilityFlags.Bch | V3CapabilityFlags.IndependentCrc;
        if ((result.Flags & required) != required ||
            result.PageBytes != V3PageCodec.PhysicalBytes ||
            result.SectorBytes == 0 || result.SectorBytes % result.PageBytes != 0 ||
            result.CatalogLength == 0 || result.CatalogLength % result.SectorBytes != 0 ||
            result.CatalogStart > result.DataStart ||
            result.DataStart % result.SectorBytes != 0 ||
            result.StorageEnd <= result.DataStart ||
            result.MaximumSamplesPerPage is 0 or > V3PageCodec.MaximumSamples ||
            result.BchCorrectionLimit != V3Bch16.CorrectionLimit ||
            result.ChecksumId != 1 ||
            result.MaximumSerialPayload is 0 or > 792)
        {
            throw new GaugeProtocolException("V3 capabilities contain unsupported or impossible geometry.");
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
