using System.Buffers.Binary;

namespace Gauge.Protocol;

public sealed record V3HeaderPage(
    V3PageDecodeResult Page,
    V3PageType Type,
    uint FileId,
    uint PageSequence,
    uint BodyPageIndex,
    uint BodyPageCount,
    ushort HeaderLength,
    byte[] Payload);

public sealed record V3FileHeader(
    uint FileId,
    uint MeasurementInterval,
    uint CreationBootId,
    byte[] SensorSerial,
    byte[] SensorHeader,
    byte[] PressurePolynomial,
    byte[] TemperaturePolynomial,
    byte[] RawHeaderStream,
    IReadOnlyList<V3HeaderPage> Pages,
    byte Schema = 1);

public static class V3HeaderDecoder
{
    public const int MaximumHeaderBytes = 2048;
    public const int MaximumBodyPages = 12;

    public static V3HeaderPage DecodePage(ReadOnlySpan<byte> physical)
    {
        var page = V3PageCodec.Decode(physical);
        if (!page.IsAccepted)
        {
            throw new InvalidDataException(page.StructuralFailure ?? $"Header page status is {page.Status}.");
        }

        var envelope = page.Envelope!;
        if (envelope.Type is not (V3PageType.HeaderBody or V3PageType.HeaderCommit))
        {
            throw new InvalidDataException($"Expected a header page, found {envelope.Type}.");
        }

        if (envelope.FirstSampleSequence != 0 ||
            envelope.FirstTimestamp != 0 ||
            envelope.NominalInterval != 0 ||
            envelope.SampleCount != 0 ||
            envelope.PageFlags != 0)
        {
            throw new InvalidDataException("Header page contains non-zero reserved envelope fields.");
        }

        var first = BinaryPrimitives.ReadUInt32LittleEndian(envelope.FixedMetadata.AsSpan(0, 4));
        var second = BinaryPrimitives.ReadUInt32LittleEndian(envelope.FixedMetadata.AsSpan(4, 4));
        if (envelope.Type == V3PageType.HeaderCommit && second > MaximumHeaderBytes)
        {
            throw new InvalidDataException("HEADER_COMMIT header length exceeds its fixed bound.");
        }

        return envelope.Type == V3PageType.HeaderBody
            ? new V3HeaderPage(page, envelope.Type, envelope.FileId, envelope.PageSequence, first, second, 0, envelope.Payload)
            : new V3HeaderPage(page, envelope.Type, envelope.FileId, envelope.PageSequence, 0, first, (ushort)second, envelope.Payload);
    }

    public static V3FileHeader Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < V3PageCodec.PhysicalBytes * 2 ||
            bytes.Length % V3PageCodec.PhysicalBytes != 0 ||
            bytes.Length > (MaximumBodyPages + 1) * V3PageCodec.PhysicalBytes)
        {
            throw new InvalidDataException("V3 header extent has an impossible length.");
        }

        var pages = new List<V3HeaderPage>();
        var stream = new List<byte>();
        uint? fileId = null;
        uint? bodyPageCount = null;
        V3HeaderPage? commit = null;

        for (var index = 0; index < bytes.Length / V3PageCodec.PhysicalBytes; index++)
        {
            var page = DecodePage(bytes.Slice(index * V3PageCodec.PhysicalBytes, V3PageCodec.PhysicalBytes));
            pages.Add(page);
            fileId ??= page.FileId;
            bodyPageCount ??= page.BodyPageCount;

            if (page.FileId != fileId || page.PageSequence != (uint)index)
            {
                throw new InvalidDataException("Header file/page sequence is non-monotonic.");
            }

            if (page.Type == V3PageType.HeaderBody)
            {
                if (commit is not null ||
                    page.BodyPageIndex != (uint)index ||
                    page.BodyPageCount != bodyPageCount ||
                    page.BodyPageCount is 0 or > MaximumBodyPages)
                {
                    throw new InvalidDataException("HEADER_BODY page metadata is inconsistent.");
                }

                if (stream.Count + page.Payload.Length > MaximumHeaderBytes)
                {
                    throw new InvalidDataException("Header stream exceeds its fixed bound.");
                }

                stream.AddRange(page.Payload);
                continue;
            }

            commit = page;
            if ((uint)index != bodyPageCount ||
                page.PageSequence != bodyPageCount ||
                page.BodyPageCount != bodyPageCount ||
                page.HeaderLength != stream.Count ||
                page.Payload.Length != 4)
            {
                throw new InvalidDataException("HEADER_COMMIT does not match its body pages.");
            }

            break;
        }

        if (commit is null)
        {
            throw new InvalidDataException("Header has no valid commit page.");
        }

        var rawStream = stream.ToArray();
        var committedCrc = BinaryPrimitives.ReadUInt32LittleEndian(commit.Payload);
        if (Crc32C.Compute(rawStream) != committedCrc)
        {
            throw new InvalidDataException("Committed header stream CRC32C is invalid.");
        }

        return DecodeStream(rawStream, pages);
    }

    public static V3FileHeader DecodeStream(
        ReadOnlySpan<byte> stream,
        IReadOnlyList<V3HeaderPage>? pages = null)
    {
        if (stream.Length >= 5 &&
            stream[..4].SequenceEqual("MG3H"u8) &&
            stream[4] == 2)
        {
            return DecodeSchema2(stream, pages);
        }

        if (stream.Length is < 24 or > MaximumHeaderBytes ||
            !stream[..4].SequenceEqual("MG3H"u8) ||
            BinaryPrimitives.ReadUInt16LittleEndian(stream[4..6]) != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(stream[6..8]) != stream.Length)
        {
            throw new InvalidDataException("V3 header stream prefix is invalid.");
        }

        var fileId = BinaryPrimitives.ReadUInt32LittleEndian(stream[8..12]);
        if (fileId == 0)
        {
            throw new InvalidDataException("V3 header file ID is zero.");
        }

        var storedTlvCrc = BinaryPrimitives.ReadUInt32LittleEndian(stream[20..24]);
        if (Crc32C.Compute(stream[24..]) != storedTlvCrc)
        {
            throw new InvalidDataException("V3 header TLV CRC32C is invalid.");
        }

        var required = new byte[4][];
        var offset = 24;
        while (offset < stream.Length)
        {
            if (stream.Length - offset < 6)
            {
                throw new InvalidDataException("V3 header contains a partial TLV.");
            }

            var type = BinaryPrimitives.ReadUInt16LittleEndian(stream[offset..(offset + 2)]);
            var flags = stream[offset + 2];
            var reserved = stream[offset + 3];
            var length = BinaryPrimitives.ReadUInt16LittleEndian(stream[(offset + 4)..(offset + 6)]);
            if (reserved != 0 || length < 4 || length > stream.Length - offset - 6)
            {
                throw new InvalidDataException("V3 header TLV has an impossible length or reserved field.");
            }

            var valueLength = length - 4;
            var value = stream.Slice(offset + 6, valueLength);
            var storedValueCrc = BinaryPrimitives.ReadUInt32LittleEndian(
                stream.Slice(offset + 6 + valueLength, 4));
            if (Crc32C.Compute(value) != storedValueCrc)
            {
                throw new InvalidDataException($"V3 header TLV {type} CRC32C is invalid.");
            }

            if (type is >= 1 and <= 4)
            {
                if (required[type - 1] is not null)
                {
                    throw new InvalidDataException($"Required V3 header TLV {type} is duplicated.");
                }

                required[type - 1] = value.ToArray();
            }
            else if ((flags & 1) != 0)
            {
                throw new InvalidDataException($"Unknown required V3 header TLV {type}.");
            }

            offset += 6 + length;
        }

        if (required.Any(value => value is null))
        {
            throw new InvalidDataException("V3 header is missing one or more required TLVs.");
        }

        return new V3FileHeader(
            fileId,
            BinaryPrimitives.ReadUInt32LittleEndian(stream[12..16]),
            BinaryPrimitives.ReadUInt32LittleEndian(stream[16..20]),
            required[0],
            required[1],
            required[2],
            required[3],
            stream.ToArray(),
            pages ?? []);
    }

    private static V3FileHeader DecodeSchema2(
        ReadOnlySpan<byte> stream,
        IReadOnlyList<V3HeaderPage>? pages)
    {
        const int prefixBytes = 21;
        if (stream.Length < prefixBytes ||
            stream.Length > MaximumHeaderBytes ||
            BinaryPrimitives.ReadUInt16LittleEndian(stream[5..7]) != stream.Length)
        {
            throw new InvalidDataException(
                "V3 schema-2 header stream prefix or total length is invalid.");
        }

        var fileId = BinaryPrimitives.ReadUInt32LittleEndian(stream[7..11]);
        var interval = BinaryPrimitives.ReadUInt16LittleEndian(stream[11..13]);
        if (fileId == 0 || interval == 0)
        {
            throw new InvalidDataException(
                "V3 schema-2 header identity or interval is invalid.");
        }

        var serialLength = BinaryPrimitives.ReadUInt16LittleEndian(stream[13..15]);
        var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(stream[15..17]);
        var pressureLength = BinaryPrimitives.ReadUInt16LittleEndian(stream[17..19]);
        var temperatureLength = BinaryPrimitives.ReadUInt16LittleEndian(stream[19..21]);
        var fieldsLength = checked(
            (int)serialLength + headerLength + pressureLength + temperatureLength);
        if (fieldsLength != stream.Length - prefixBytes)
        {
            throw new InvalidDataException(
                "V3 schema-2 calibration field lengths do not match the committed stream.");
        }

        var offset = prefixBytes;
        var serial = stream.Slice(offset, serialLength).ToArray();
        offset += serialLength;
        var header = stream.Slice(offset, headerLength).ToArray();
        offset += headerLength;
        var pressure = stream.Slice(offset, pressureLength).ToArray();
        offset += pressureLength;
        var temperature = stream.Slice(offset, temperatureLength).ToArray();

        return new V3FileHeader(
            fileId,
            interval,
            0,
            serial,
            header,
            pressure,
            temperature,
            stream.ToArray(),
            pages ?? [],
            2);
    }
}
