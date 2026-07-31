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

public enum V3DataEncoding : byte
{
    LegacyCrc64 = 0,
    CompactCrc32C = 1,
    CompactCrc64Fallback = 2
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
    string? StructuralFailure,
    V3DataEncoding? DataEncoding = null,
    byte StorageMinor = 0)
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
    public const int LegacyCrcOffset = 225;
    public const int CompactCrcOffset = 229;
    public const int LegacyMaximumSamples = 18;
    public const int CompactMaximumSamples = 33;
    public const int MaximumSamples = CompactMaximumSamples;

    public static V3PageDecodeResult Decode(ReadOnlySpan<byte> physical)
    {
        if (physical.Length != PhysicalBytes)
        {
            throw new ArgumentException(
                $"A V3 physical page must be {PhysicalBytes} bytes.",
                nameof(physical));
        }

        var raw = physical.ToArray();
        if (physical.IndexOfAnyExcept((byte)0xFF) < 0)
        {
            return Failure(V3PageStatus.Erased, raw, null, false, "Page is erased.");
        }

        // BCH protects bytes 0-254. A zero syndrome is the clean-page fast path.
        var bch = V3Bch16.Decode(raw);
        if (!bch.IsDecodable)
        {
            return Failure(
                V3PageStatus.Uncorrectable,
                raw,
                null,
                false,
                "BCH correction failed.");
        }

        var candidate = bch.CorrectedPage;
        var structural = ValidateAndParse(
            candidate,
            out var envelope,
            out var encoding,
            out var storageMinor,
            out var unsupported);
        if (structural is not null)
        {
            return Failure(
                unsupported ? V3PageStatus.Unsupported : V3PageStatus.StructuralFailure,
                raw,
                candidate,
                false,
                structural,
                bch.CorrectedBitLocations,
                encoding,
                storageMinor);
        }

        if (!HasValidChecksum(candidate, encoding, storageMinor))
        {
            return Failure(
                V3PageStatus.CrcFailure,
                raw,
                candidate,
                false,
                "Page checksum is invalid after BCH decoding.",
                bch.CorrectedBitLocations,
                encoding,
                storageMinor);
        }

        return new V3PageDecodeResult(
            bch.CorrectedBitLocations.Count == 0
                ? V3PageStatus.Ok
                : V3PageStatus.Corrected,
            raw,
            candidate,
            envelope,
            bch.CorrectedBitLocations,
            true,
            null,
            encoding,
            storageMinor);
    }

    private static bool HasValidChecksum(
        ReadOnlySpan<byte> page,
        V3DataEncoding? encoding,
        byte storageMinor)
    {
        if (encoding == V3DataEncoding.CompactCrc32C ||
            (encoding is null && storageMinor == 1))
        {
            return Crc32C.Compute(page[..CompactCrcOffset]) ==
                BinaryPrimitives.ReadUInt32LittleEndian(
                    page[CompactCrcOffset..EnvelopeBytes]);
        }

        return Crc64Ecma.Compute(page[..LegacyCrcOffset]) ==
            BinaryPrimitives.ReadUInt64LittleEndian(
                page[LegacyCrcOffset..EnvelopeBytes]);
    }

    private static string? ValidateAndParse(
        ReadOnlySpan<byte> page,
        out V3Envelope? envelope,
        out V3DataEncoding? encoding,
        out byte storageMinor,
        out bool unsupported)
    {
        envelope = null;
        encoding = null;
        storageMinor = 0;
        unsupported = false;

        if (page[..4].SequenceEqual("MG3D"u8))
        {
            return ValidateCompactData(
                page,
                out envelope,
                out encoding,
                out storageMinor,
                out unsupported);
        }

        if (!page[..4].SequenceEqual("MG3P"u8))
        {
            return "Page magic is neither MG3P nor MG3D.";
        }

        if (page[4] != 3)
        {
            unsupported = true;
            return $"Storage major version {page[4]} is unsupported.";
        }

        storageMinor = page[5];
        if (storageMinor is not (0 or 1 or 2))
        {
            unsupported = true;
            return $"Storage minor version {storageMinor} is unsupported.";
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

        var paddingEnd = storageMinor == 1 ? CompactCrcOffset : LegacyCrcOffset;
        if (page.Slice(
                PayloadOffset + payloadLength,
                paddingEnd - PayloadOffset - payloadLength)
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
        if (envelope.Type == V3PageType.Data)
        {
            if (storageMinor != 0)
            {
                unsupported = true;
                return $"Generic MG3P minor {storageMinor} DATA pages are not a documented encoding.";
            }

            encoding = V3DataEncoding.LegacyCrc64;
        }

        return null;
    }

    private static string? ValidateCompactData(
        ReadOnlySpan<byte> page,
        out V3Envelope? envelope,
        out V3DataEncoding? encoding,
        out byte storageMinor,
        out bool unsupported)
    {
        envelope = null;
        unsupported = false;
        encoding = page[4] switch
        {
            1 => V3DataEncoding.CompactCrc32C,
            2 => V3DataEncoding.CompactCrc64Fallback,
            _ => null
        };
        storageMinor = page[4] switch
        {
            1 => (byte)1,
            2 => (byte)2,
            _ => (byte)0
        };
        if (encoding is null)
        {
            unsupported = true;
            return $"MG3D encoding ID {page[4]} is unsupported.";
        }

        if (page[^1] != 0xFF)
        {
            return "Reserved physical byte is not erased.";
        }

        var maximumSlots = encoding == V3DataEncoding.CompactCrc32C ? 33 : 32;
        var slotCount = page[5];
        if (slotCount == 0 || slotCount > maximumSlots)
        {
            return $"Compact DATA slot count {slotCount} is invalid.";
        }

        var fileId = BinaryPrimitives.ReadUInt32LittleEndian(page[6..10]);
        var interval = BinaryPrimitives.ReadUInt16LittleEndian(page[24..26]);
        var phase = BinaryPrimitives.ReadUInt16LittleEndian(page[22..24]);
        if (fileId == 0 || interval == 0)
        {
            return "Compact DATA identity, interval, or Timer1 phase is invalid.";
        }

        var bitmapBytes = encoding == V3DataEncoding.CompactCrc32C ? 5 : 4;
        var recordsOffset = encoding == V3DataEncoding.CompactCrc32C ? 31 : 30;
        var maximumRecords = maximumSlots;
        for (var slot = 0; slot < maximumRecords; slot++)
        {
            var valid = (page[26 + (slot / 8)] & (1 << (slot % 8))) != 0;
            var record = page.Slice(recordsOffset + (slot * 6), 6);
            if (slot >= slotCount)
            {
                if (valid || record.IndexOfAnyExcept((byte)0) >= 0)
                {
                    return "Compact DATA has non-zero bitmap bits or records beyond slot count.";
                }
            }
            else if (!valid && record.IndexOfAnyExcept((byte)0) >= 0)
            {
                return $"Compact DATA null slot {slot} has non-zero record bytes.";
            }
        }

        var unusedBitmapMask = encoding == V3DataEncoding.CompactCrc32C
            ? (byte)0xFE
            : (byte)0x00;
        if ((page[26 + bitmapBytes - 1] & unusedBitmapMask) != 0)
        {
            return "Compact DATA has reserved validity bitmap bits set.";
        }

        if (encoding == V3DataEncoding.CompactCrc64Fallback &&
            page[222..225].IndexOfAnyExcept((byte)0xFF) >= 0)
        {
            return "Compact CRC64 DATA padding is not erased.";
        }

        envelope = new V3Envelope(
            V3PageType.Data,
            fileId,
            BinaryPrimitives.ReadUInt32LittleEndian(page[10..14]),
            BinaryPrimitives.ReadUInt32LittleEndian(page[14..18]),
            BinaryPrimitives.ReadUInt32LittleEndian(page[18..22]),
            interval,
            slotCount,
            0,
            checked((ushort)(slotCount * 6)),
            page[22..26].ToArray(),
            page.Slice(recordsOffset, maximumRecords * 6).ToArray());
        return null;
    }

    private static V3PageDecodeResult Failure(
        V3PageStatus status,
        byte[] raw,
        byte[]? decoded,
        bool crcValid,
        string reason,
        IReadOnlyList<int>? corrections = null,
        V3DataEncoding? encoding = null,
        byte storageMinor = 0) =>
        new(
            status,
            raw,
            decoded,
            null,
            corrections ?? [],
            crcValid,
            reason,
            encoding,
            storageMinor);
}

public sealed record V3DataSample(
    uint SampleSequence,
    uint Timestamp,
    uint? PressureCounts,
    uint? TemperatureCounts,
    byte SensorIteration,
    byte QualityFlags,
    double ExactTimestampSeconds = double.NaN)
{
    public bool IsMissing => PressureCounts is null || TemperatureCounts is null;
}

public sealed record V3DataPage(
    V3PageDecodeResult Page,
    uint FileId,
    uint PageSequence,
    uint FirstSampleSequence,
    uint FirstTimestamp,
    uint NominalInterval,
    byte PageFlags,
    IReadOnlyList<V3DataSample> Samples,
    ushort FirstTimer1Phase = 0,
    V3DataEncoding Encoding = V3DataEncoding.LegacyCrc64);

public static class V3DataDecoder
{
    public static V3DataPage DecodePage(ReadOnlySpan<byte> physical)
    {
        var page = V3PageCodec.Decode(physical);
        if (!page.IsAccepted)
        {
            throw new InvalidDataException(
                page.StructuralFailure ?? $"V3 page status is {page.Status}.");
        }

        return page.DataEncoding switch
        {
            V3DataEncoding.CompactCrc32C => DecodeCompact(page, 33, 5, 31),
            V3DataEncoding.CompactCrc64Fallback => page.DecodedBytes![..4]
                .AsSpan().SequenceEqual("MG3D"u8)
                    ? DecodeCompact(page, 32, 4, 30)
                    : DecodeLegacy(page),
            V3DataEncoding.LegacyCrc64 => DecodeLegacy(page),
            _ => throw new InvalidDataException(
                "DATA page has no supported explicit encoding.")
        };
    }

    public static IReadOnlyList<V3DataPage> DecodeSequence(
        ReadOnlySpan<byte> bytes,
        uint? expectedFileId = null)
    {
        if (bytes.Length % V3PageCodec.PhysicalBytes != 0)
        {
            throw new InvalidDataException(
                "V3 data length is not a whole number of physical pages.");
        }

        var pages = new List<V3DataPage>();
        uint? nextPage = null;
        uint? nextSample = null;
        double? lastTimestamp = null;
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
                throw new InvalidDataException(
                    "V3 file/page/slot sequence is non-monotonic or contains an implicit gap.");
            }

            if (lastTimestamp is not null &&
                page.Samples.Count != 0 &&
                page.Samples[0].ExactTimestampSeconds < lastTimestamp)
            {
                throw new InvalidDataException(
                    "V3 timestamps are not monotonic across pages.");
            }

            pages.Add(page);
            nextPage++;
            nextSample += (uint)page.Samples.Count;
            if (page.Samples.Count != 0)
            {
                lastTimestamp = page.Samples[^1].ExactTimestampSeconds;
            }
        }

        return pages;
    }

    private static V3DataPage DecodeCompact(
        V3PageDecodeResult page,
        int maximumSlots,
        int bitmapBytes,
        int recordsOffset)
    {
        var bytes = page.DecodedBytes!;
        var slotCount = bytes[5];
        var firstSequence = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(14, 4));
        var firstSeconds = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(18, 4));
        var phase = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22, 2));
        var interval = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(24, 2));
        var anchor = firstSeconds + (phase / 32768.0);
        var samples = new List<V3DataSample>(slotCount);
        for (var index = 0; index < slotCount; index++)
        {
            if (firstSequence > uint.MaxValue - (uint)index ||
                firstSeconds > uint.MaxValue - ((uint)index * interval))
            {
                throw new InvalidDataException(
                    "Compact DATA slot sequence or timestamp overflows.");
            }

            var valid = (bytes[26 + (index / 8)] & (1 << (index % 8))) != 0;
            var record = bytes.AsSpan(recordsOffset + (index * 6), 6);
            samples.Add(new V3DataSample(
                firstSequence + (uint)index,
                firstSeconds + ((uint)index * interval),
                valid ? ReadUInt24LittleEndian(record[..3]) : null,
                valid ? ReadUInt24LittleEndian(record[3..]) : null,
                0,
                valid ? (byte)0 : (byte)1,
                anchor + (index * (double)interval)));
        }

        _ = maximumSlots;
        _ = bitmapBytes;
        return new V3DataPage(
            page,
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(6, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(10, 4)),
            firstSequence,
            firstSeconds,
            interval,
            0,
            samples,
            phase,
            page.DataEncoding!.Value);
    }

    private static V3DataPage DecodeLegacy(V3PageDecodeResult page)
    {
        var envelope = page.Envelope!;
        if (envelope.Type != V3PageType.Data)
        {
            throw new InvalidDataException(
                $"Expected a DATA page, found {envelope.Type}.");
        }

        if (envelope.FixedMetadata.AsSpan().IndexOfAnyExcept((byte)0) >= 0 ||
            envelope.SampleCount is 0 or > V3PageCodec.LegacyMaximumSamples ||
            envelope.PayloadLength != envelope.SampleCount * 10)
        {
            throw new InvalidDataException(
                "Legacy DATA metadata, sample count, or payload length is inconsistent.");
        }

        var samples = new List<V3DataSample>(envelope.SampleCount);
        uint previousTimestamp = envelope.FirstTimestamp;
        for (var index = 0; index < envelope.SampleCount; index++)
        {
            var entry = envelope.Payload.AsSpan(index * 10, 10);
            var delta = BinaryPrimitives.ReadUInt16LittleEndian(entry[6..8]);
            if (envelope.FirstTimestamp > uint.MaxValue - delta ||
                envelope.FirstSampleSequence > uint.MaxValue - (uint)index)
            {
                throw new InvalidDataException(
                    "Legacy DATA timestamp or sequence overflows.");
            }

            var timestamp = envelope.FirstTimestamp + delta;
            if (index != 0 && timestamp < previousTimestamp)
            {
                throw new InvalidDataException(
                    "Legacy DATA timestamps are not monotonic.");
            }

            samples.Add(new V3DataSample(
                envelope.FirstSampleSequence + (uint)index,
                timestamp,
                ReadUInt24LittleEndian(entry[..3]),
                ReadUInt24LittleEndian(entry[3..6]),
                entry[8],
                entry[9],
                timestamp));
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

    private static uint ReadUInt24LittleEndian(ReadOnlySpan<byte> bytes) =>
        (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16));
}
