using System.Buffers.Binary;

namespace Gauge.Protocol;

public sealed record V3CatalogRecord(
    uint CatalogSequence,
    uint FileId,
    uint FileStart,
    uint CreationBootId,
    uint NominalInterval,
    byte Flags,
    V3PageDecodeResult Page);

public sealed record V3CatalogReplica(
    int ReplicaId,
    IReadOnlyList<V3CatalogRecord> Records,
    V3PageDecodeResult? TerminalPage,
    bool IsValid,
    string? Failure,
    bool WasInspected = true);

public sealed record V3CatalogRecovery(
    IReadOnlyList<V3CatalogRecord> Records,
    IReadOnlyList<V3CatalogReplica> Replicas,
    int SelectedReplicaId,
    bool HasMirrorDivergence);

public static class V3CatalogDecoder
{
    public static V3CatalogRecord DecodeRecord(ReadOnlySpan<byte> physical)
    {
        var page = V3PageCodec.Decode(physical);
        if (!page.IsAccepted)
        {
            throw new InvalidDataException(page.StructuralFailure ?? $"Catalog page status is {page.Status}.");
        }

        var envelope = page.Envelope!;
        var metadata = envelope.FixedMetadata.AsSpan();
        if (envelope.Type != V3PageType.Checkpoint ||
            envelope.FirstSampleSequence != 0 ||
            envelope.FirstTimestamp != 0 ||
            envelope.NominalInterval != 0 ||
            envelope.SampleCount != 0 ||
            envelope.PageFlags != 0 ||
            envelope.PayloadLength != 21 ||
            BinaryPrimitives.ReadUInt32LittleEndian(metadata[..4]) != 1)
        {
            throw new InvalidDataException("Catalog page metadata is invalid.");
        }

        var repeatedSequence = BinaryPrimitives.ReadUInt32LittleEndian(metadata[4..8]);
        var payload = envelope.Payload.AsSpan();
        var repeatedFileId = BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]);
        var fileStart = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..8]);
        var nominalInterval = BinaryPrimitives.ReadUInt32LittleEndian(payload[12..16]);
        if (repeatedSequence != envelope.PageSequence ||
            repeatedFileId != envelope.FileId ||
            fileStart % 4096 != 0 ||
            nominalInterval == 0 ||
            !payload[17..21].SequenceEqual(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }))
        {
            throw new InvalidDataException("Catalog record fields are inconsistent.");
        }

        return new V3CatalogRecord(
            envelope.PageSequence,
            envelope.FileId,
            fileStart,
            BinaryPrimitives.ReadUInt32LittleEndian(payload[8..12]),
            nominalInterval,
            payload[16],
            page);
    }

    public static V3CatalogReplica DecodeReplica(
        int replicaId,
        ReadOnlySpan<byte> bytes,
        int maximumRecords = 256)
    {
        if (replicaId is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(replicaId));
        }

        if (maximumRecords is < 0 or > 256 ||
            bytes.Length % V3PageCodec.PhysicalBytes != 0 ||
            bytes.Length / V3PageCodec.PhysicalBytes > maximumRecords)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), "Catalog input exceeds its bounded record count.");
        }

        var records = new List<V3CatalogRecord>();
        V3PageDecodeResult? terminal = null;
        string? failure = null;
        for (var index = 0; index < bytes.Length / V3PageCodec.PhysicalBytes; index++)
        {
            var physical = bytes.Slice(index * V3PageCodec.PhysicalBytes, V3PageCodec.PhysicalBytes);
            var decoded = V3PageCodec.Decode(physical);
            if (decoded.Status == V3PageStatus.Erased)
            {
                terminal = decoded;
                break;
            }

            try
            {
                var record = DecodeRecord(physical);
                if (record.CatalogSequence != (uint)index)
                {
                    failure = $"Catalog sequence {record.CatalogSequence} was expected to be {index}.";
                    terminal = decoded;
                    break;
                }

                records.Add(record);
            }
            catch (InvalidDataException ex)
            {
                failure = ex.Message;
                terminal = decoded;
                break;
            }
        }

        return new V3CatalogReplica(replicaId, records, terminal, failure is null, failure);
    }

    public static V3CatalogRecovery Recover(
        ReadOnlySpan<byte> replica0,
        ReadOnlySpan<byte> replica1,
        int maximumRecords = 256,
        int preferredReplicaId = 0)
    {
        if (preferredReplicaId is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(preferredReplicaId));
        }

        var replicas = new[]
        {
            DecodeReplica(0, replica0, maximumRecords),
            DecodeReplica(1, replica1, maximumRecords)
        };
        var prefix = Math.Max(replicas[0].Records.Count, replicas[1].Records.Count);
        var recovered = new List<V3CatalogRecord>(prefix);

        var shared = Math.Min(replicas[0].Records.Count, replicas[1].Records.Count);
        for (var index = 0; index < prefix; index++)
        {
            if (index >= shared)
            {
                recovered.Add(
                    index < replicas[0].Records.Count
                        ? replicas[0].Records[index]
                        : replicas[1].Records[index]);
                continue;
            }

            var record0 = replicas[0].Records[index];
            var record1 = replicas[1].Records[index];
            if (!SameRecord(record0, record1))
            {
                throw new InvalidDataException($"V3 catalog replicas diverge at record {index}.");
            }

            recovered.Add(
                record0.Page.Status == V3PageStatus.Ok &&
                record1.Page.Status != V3PageStatus.Ok
                    ? record0
                    : record1.Page.Status == V3PageStatus.Ok &&
                      record0.Page.Status != V3PageStatus.Ok
                        ? record1
                        : preferredReplicaId == 0 ? record0 : record1);
        }

        var selected = replicas[1 - preferredReplicaId].Records.Count >
            replicas[preferredReplicaId].Records.Count
                ? 1 - preferredReplicaId
                : preferredReplicaId;
        return new V3CatalogRecovery(
            recovered,
            replicas,
            selected,
            false);
    }

    private static bool SameRecord(V3CatalogRecord left, V3CatalogRecord right) =>
        left.CatalogSequence == right.CatalogSequence &&
        left.FileId == right.FileId &&
        left.FileStart == right.FileStart &&
        left.CreationBootId == right.CreationBootId &&
        left.NominalInterval == right.NominalInterval &&
        left.Flags == right.Flags;
}
