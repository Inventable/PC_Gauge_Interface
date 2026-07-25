using System.Buffers.Binary;

namespace Gauge.Protocol;

public enum V3PageStatus
{
    Ok,
    Corrected,
    Erased,
    Uncorrectable,
    CrcFailure,
    StructuralFailure,
    Unsupported
}

public enum V3PageType : byte
{
    HeaderBody = 1,
    HeaderCommit = 2,
    Data = 3,
    Footer = 4,
    Checkpoint = 5,
    Diagnostic = 6
}

public sealed record V3Envelope(
    V3PageType Type,
    uint FileId,
    uint PageSequence,
    uint FirstSampleSequence,
    uint FirstTimestamp,
    uint NominalInterval,
    byte SampleCount,
    byte PageFlags,
    ushort PayloadLength,
    byte[] FixedMetadata,
    byte[] Payload);

public sealed record V3PageDecodeResult(
    V3PageStatus Status,
    byte[] RawBytes,
    byte[]? DecodedBytes,
    V3Envelope? Envelope,
    IReadOnlyList<int> CorrectedBitLocations,
    bool IsCrcValid,
    string? StructuralFailure)
{
    public int CorrectedBitCount => CorrectedBitLocations.Count;
    public bool IsAccepted => Status is V3PageStatus.Ok or V3PageStatus.Corrected;
}

public static class V3PageCodec
{
    public const int PhysicalBytes = 256;
    public const int EnvelopeBytes = 233;
    public const int PayloadOffset = 40;
    public const int PayloadCapacity = 185;
    public const int CrcOffset = 225;
    public const int MaximumSamples = 18;

    private static ReadOnlySpan<byte> Magic => "MG3P"u8;

    public static V3PageDecodeResult Decode(ReadOnlySpan<byte> physical)
    {
        if (physical.Length != PhysicalBytes)
        {
            throw new ArgumentException($"A V3 physical page must be {PhysicalBytes} bytes.", nameof(physical));
        }

        var raw = physical.ToArray();
        if (physical.IndexOfAnyExcept((byte)0xFF) < 0)
        {
            return Failure(V3PageStatus.Erased, raw, null, false, "Page is erased.");
        }

        var candidate = raw;
        IReadOnlyList<int> corrections = [];
        var crcValid = HasValidCrc(candidate);
        if (!crcValid)
        {
            var bch = V3Bch16.Decode(candidate);
            if (!bch.IsDecodable)
            {
                return Failure(V3PageStatus.Uncorrectable, raw, null, false, "BCH correction failed.");
            }

            candidate = bch.CorrectedPage;
            corrections = bch.CorrectedBitLocations;
            crcValid = HasValidCrc(candidate);
            if (!crcValid)
            {
                return Failure(V3PageStatus.CrcFailure, raw, candidate, false, "CRC64 is invalid after BCH decoding.", corrections);
            }
        }

        var structural = ValidateAndParse(candidate, out var envelope, out var unsupported);
        if (structural is not null)
        {
            return Failure(
                unsupported ? V3PageStatus.Unsupported : V3PageStatus.StructuralFailure,
                raw,
                candidate,
                true,
                structural,
                corrections);
        }

        return new V3PageDecodeResult(
            corrections.Count == 0 ? V3PageStatus.Ok : V3PageStatus.Corrected,
            raw,
            candidate,
            envelope,
            corrections,
            true,
            null);
    }

    private static bool HasValidCrc(ReadOnlySpan<byte> page)
    {
        if (page.Length < EnvelopeBytes)
        {
            return false;
        }

        var stored = BinaryPrimitives.ReadUInt64LittleEndian(page[CrcOffset..EnvelopeBytes]);
        return Crc64Ecma.Compute(page[..CrcOffset]) == stored;
    }

    private static string? ValidateAndParse(
        ReadOnlySpan<byte> page,
        out V3Envelope? envelope,
        out bool unsupported)
    {
        envelope = null;
        unsupported = false;
        if (!page[..4].SequenceEqual(Magic))
        {
            return "Page magic is not MG3P.";
        }

        if (page[4] != 3)
        {
            unsupported = true;
            return $"Storage major version {page[4]} is unsupported.";
        }

        if (page[5] != 0)
        {
            unsupported = true;
            return $"Storage minor version {page[5]} uses unknown required fields.";
        }

        if (!Enum.IsDefined((V3PageType)page[6]))
        {
            unsupported = true;
            return $"Page type {page[6]} is unsupported.";
        }

        if (page[7] != 1)
        {
            unsupported = true;
            return $"Codec ID {page[7]} is unsupported.";
        }

        if (page[^1] != 0xFF)
        {
            return "Reserved physical byte is not erased.";
        }

        var fileId = BinaryPrimitives.ReadUInt32LittleEndian(page[8..12]);
        if (fileId == 0)
        {
            return "File ID is zero.";
        }

        var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(page[30..32]);
        if (payloadLength > PayloadCapacity)
        {
            return $"Payload length {payloadLength} exceeds {PayloadCapacity}.";
        }

        if (page.Slice(PayloadOffset + payloadLength, CrcOffset - PayloadOffset - payloadLength)
            .IndexOfAnyExcept((byte)0xFF) >= 0)
        {
            return "Payload padding is not erased.";
        }

        envelope = new V3Envelope(
            (V3PageType)page[6],
            fileId,
            BinaryPrimitives.ReadUInt32LittleEndian(page[12..16]),
            BinaryPrimitives.ReadUInt32LittleEndian(page[16..20]),
            BinaryPrimitives.ReadUInt32LittleEndian(page[20..24]),
            BinaryPrimitives.ReadUInt32LittleEndian(page[24..28]),
            page[28],
            page[29],
            payloadLength,
            page[32..40].ToArray(),
            page.Slice(PayloadOffset, payloadLength).ToArray());
        return null;
    }

    private static V3PageDecodeResult Failure(
        V3PageStatus status,
        byte[] raw,
        byte[]? decoded,
        bool crcValid,
        string reason,
        IReadOnlyList<int>? corrections = null) =>
        new(status, raw, decoded, null, corrections ?? [], crcValid, reason);
}

public sealed record V3DataSample(
    uint SampleSequence,
    uint Timestamp,
    uint PressureCounts,
    uint TemperatureCounts,
    byte SensorIteration,
    byte QualityFlags);

public sealed record V3DataPage(
    V3PageDecodeResult Page,
    uint FileId,
    uint PageSequence,
    uint FirstSampleSequence,
    uint FirstTimestamp,
    uint NominalInterval,
    byte PageFlags,
    IReadOnlyList<V3DataSample> Samples);

public static class V3DataDecoder
{
    public static V3DataPage DecodePage(ReadOnlySpan<byte> physical)
    {
        var page = V3PageCodec.Decode(physical);
        if (!page.IsAccepted)
        {
            throw new InvalidDataException(page.StructuralFailure ?? $"V3 page status is {page.Status}.");
        }

        var envelope = page.Envelope!;
        if (envelope.Type != V3PageType.Data)
        {
            throw new InvalidDataException($"Expected a DATA page, found {envelope.Type}.");
        }

        if (envelope.FixedMetadata.AsSpan().IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new InvalidDataException("DATA fixed metadata is not zero.");
        }

        if (envelope.SampleCount is 0 or > V3PageCodec.MaximumSamples ||
            envelope.PayloadLength != envelope.SampleCount * 10)
        {
            throw new InvalidDataException("DATA sample count and payload length are inconsistent.");
        }

        var samples = new List<V3DataSample>(envelope.SampleCount);
        uint previousTimestamp = envelope.FirstTimestamp;
        for (var index = 0; index < envelope.SampleCount; index++)
        {
            var entry = envelope.Payload.AsSpan(index * 10, 10);
            var delta = BinaryPrimitives.ReadUInt16LittleEndian(entry[6..8]);
            if (envelope.FirstTimestamp > uint.MaxValue - delta)
            {
                throw new InvalidDataException("DATA timestamp delta overflows.");
            }

            var timestamp = envelope.FirstTimestamp + delta;
            if (index != 0 && timestamp < previousTimestamp)
            {
                throw new InvalidDataException("DATA timestamps are not monotonic.");
            }

            if (envelope.FirstSampleSequence > uint.MaxValue - (uint)index)
            {
                throw new InvalidDataException("DATA sample sequence overflows.");
            }

            samples.Add(new V3DataSample(
                envelope.FirstSampleSequence + (uint)index,
                timestamp,
                ReadUInt24LittleEndian(entry[..3]),
                ReadUInt24LittleEndian(entry[3..6]),
                entry[8],
                entry[9]));
            previousTimestamp = timestamp;
        }

        return new V3DataPage(
            page,
            envelope.FileId,
            envelope.PageSequence,
            envelope.FirstSampleSequence,
            envelope.FirstTimestamp,
            envelope.NominalInterval,
            envelope.PageFlags,
            samples);
    }

    public static IReadOnlyList<V3DataPage> DecodeSequence(
        ReadOnlySpan<byte> bytes,
        uint? expectedFileId = null)
    {
        if (bytes.Length % V3PageCodec.PhysicalBytes != 0)
        {
            throw new InvalidDataException("V3 data length is not a whole number of physical pages.");
        }

        var pages = new List<V3DataPage>();
        uint? nextPage = null;
        uint? nextSample = null;
        for (var offset = 0; offset < bytes.Length; offset += V3PageCodec.PhysicalBytes)
        {
            var physical = bytes.Slice(offset, V3PageCodec.PhysicalBytes);
            if (physical.IndexOfAnyExcept((byte)0xFF) < 0)
            {
                break;
            }

            var page = DecodePage(physical);
            expectedFileId ??= page.FileId;
            nextPage ??= page.PageSequence;
            nextSample ??= page.FirstSampleSequence;
            if (page.FileId != expectedFileId ||
                page.PageSequence != nextPage ||
                page.FirstSampleSequence != nextSample)
            {
                throw new InvalidDataException("V3 file/page/sample sequence is non-monotonic or contains an implicit gap.");
            }

            pages.Add(page);
            nextPage++;
            nextSample += (uint)page.Samples.Count;
        }

        return pages;
    }

    private static uint ReadUInt24LittleEndian(ReadOnlySpan<byte> bytes) =>
        (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16));
}
