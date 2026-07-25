namespace Gauge.Protocol;

public static class V3CsvInspector
{
    public static IReadOnlyList<string> InspectData(ReadOnlySpan<byte> bytes)
    {
        EnsureWholePages(bytes);
        var lines = new List<string>
        {
            "page,status,corrected,file_id,page_sequence,sample_sequence,timestamp,pressure,temperature,iteration,quality"
        };
        for (var index = 0; index < bytes.Length / V3PageCodec.PhysicalBytes; index++)
        {
            var physical = bytes.Slice(index * V3PageCodec.PhysicalBytes, V3PageCodec.PhysicalBytes);
            try
            {
                var page = V3DataDecoder.DecodePage(physical);
                foreach (var sample in page.Samples)
                {
                    lines.Add(FormattableString.Invariant(
                        $"{index},{StatusText(page.Page)},{page.Page.CorrectedBitCount},{page.FileId},{page.PageSequence},{sample.SampleSequence},{sample.Timestamp},{sample.PressureCounts},{sample.TemperatureCounts},{sample.SensorIteration},{sample.QualityFlags}"));
                }
            }
            catch (InvalidDataException)
            {
                var page = V3PageCodec.Decode(physical);
                lines.Add($"{index},error-{StatusCode(page.Status)},{page.CorrectedBitCount},,,,,,,,");
            }
        }

        return lines;
    }

    public static IReadOnlyList<string> InspectCatalog(ReadOnlySpan<byte> bytes)
    {
        EnsureWholePages(bytes);
        var lines = new List<string>
        {
            "page,status,corrected,catalog_sequence,file_id,file_start,creation_boot_id,nominal_interval,flags"
        };
        for (var index = 0; index < bytes.Length / V3PageCodec.PhysicalBytes; index++)
        {
            var physical = bytes.Slice(index * V3PageCodec.PhysicalBytes, V3PageCodec.PhysicalBytes);
            var decoded = V3PageCodec.Decode(physical);
            if (decoded.Status == V3PageStatus.Erased)
            {
                lines.Add($"{index},erased,,,,,,,");
                break;
            }

            try
            {
                var record = V3CatalogDecoder.DecodeRecord(physical);
                lines.Add(FormattableString.Invariant(
                    $"{index},{StatusText(record.Page)},{record.Page.CorrectedBitCount},{record.CatalogSequence},{record.FileId},{record.FileStart},{record.CreationBootId},{record.NominalInterval},{record.Flags}"));
            }
            catch (InvalidDataException)
            {
                lines.Add($"{index},error-{StatusCode(decoded.Status)},{decoded.CorrectedBitCount},,,,,,");
                break;
            }
        }

        return lines;
    }

    public static IReadOnlyList<string> InspectHeader(ReadOnlySpan<byte> bytes)
    {
        EnsureWholePages(bytes);
        var lines = new List<string>
        {
            "page,status,corrected,type,file_id,sequence,payload_length,body_pages,header_length"
        };
        var streamLength = 0;
        var pageCount = Math.Min(bytes.Length / V3PageCodec.PhysicalBytes, V3HeaderDecoder.MaximumBodyPages + 1);
        for (var index = 0; index < pageCount; index++)
        {
            var physical = bytes.Slice(index * V3PageCodec.PhysicalBytes, V3PageCodec.PhysicalBytes);
            V3HeaderPage page;
            try
            {
                page = V3HeaderDecoder.DecodePage(physical);
            }
            catch (InvalidDataException)
            {
                var decoded = V3PageCodec.Decode(physical);
                lines.Add($"{index},error-{StatusCode(decoded.Status)},{decoded.CorrectedBitCount},,,,,,");
                return lines;
            }

            lines.Add(FormattableString.Invariant(
                $"{index},{StatusText(page.Page)},{page.Page.CorrectedBitCount},{(byte)page.Type},{page.FileId},{page.PageSequence},{page.Payload.Length},{page.BodyPageCount},{page.HeaderLength}"));
            if (page.Type == V3PageType.HeaderBody)
            {
                streamLength += page.Payload.Length;
                continue;
            }

            var extent = bytes[..((index + 1) * V3PageCodec.PhysicalBytes)];
            try
            {
                var header = V3HeaderDecoder.Decode(extent);
                lines.Add(FormattableString.Invariant(
                    $"stream,ok,,,{header.FileId},,,{header.RawHeaderStream.Length},0"));
                lines.Add(FormattableString.Invariant(
                    $"calibration,ok,,,,,{header.SensorSerial.Length},{header.SensorHeader.Length},{header.PressurePolynomial.Length + header.TemperaturePolynomial.Length}"));
            }
            catch (InvalidDataException)
            {
                lines.Add($"stream,error,,,{page.FileId},,,{streamLength},6");
            }

            break;
        }

        return lines;
    }

    private static void EnsureWholePages(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length % V3PageCodec.PhysicalBytes != 0)
        {
            throw new InvalidDataException("Input is not a non-empty whole number of V3 physical pages.");
        }
    }

    private static string StatusText(V3PageDecodeResult page) =>
        page.Status == V3PageStatus.Corrected ? "corrected" : "ok";

    private static int StatusCode(V3PageStatus status) => status switch
    {
        V3PageStatus.CrcFailure => 5,
        V3PageStatus.Unsupported => 9,
        _ => 6
    };
}
