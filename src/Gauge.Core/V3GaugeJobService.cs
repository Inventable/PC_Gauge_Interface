using Gauge.Calibration;
using Gauge.Protocol;

namespace Gauge.Core;

public sealed record V3GaugeFile(
    int Index,
    V3CatalogRecord CatalogRecord,
    V3FileHeader Header,
    uint DataStart,
    uint DataEnd,
    uint BoundEnd,
    bool IsLatest,
    int HeaderReplicaId)
{
    public uint DataLength => DataEnd - DataStart;
}

public sealed record V3GaugeCatalog(
    V3Capabilities Capabilities,
    V3CatalogSummary Summary,
    V3CatalogRecovery Recovery,
    IReadOnlyList<V3GaugeFile> Files,
    IReadOnlyList<V3RejectedCatalogRecord> RejectedRecords);

public sealed record V3RejectedCatalogRecord(
    V3CatalogRecord CatalogRecord,
    string Reason);

public sealed record V3ReplicaPageEvidence(
    uint Address,
    int SelectedReplicaId,
    V3PageDecodeResult Selected,
    V3PageDecodeResult Replica0,
    V3PageDecodeResult? Replica1,
    bool IsDivergent)
{
    public bool MirrorWasInspected => Replica1 is not null;
}

public sealed record V3GaugeDownload(
    V3GaugeFile File,
    uint EndAddress,
    byte[] Replica0RawBytes,
    byte[] Replica1RawBytes,
    IReadOnlyList<V3ReplicaPageEvidence> Pages,
    IReadOnlyList<V3DataPage> DataPages,
    bool IsOpen,
    IReadOnlyList<uint> PageSequenceGaps,
    IReadOnlyList<uint> SampleSequenceGaps)
{
    public IReadOnlyList<V3DataSample> Samples => DataPages.SelectMany(page => page.Samples).ToArray();
    public int CorrectedPageCount => Pages.Count(page => page.Selected.Status == V3PageStatus.Corrected);
    public int UncorrectablePageCount => Pages.Count(page => !page.Selected.IsAccepted);
    public bool HasMirrorDivergence => Pages.Any(page => page.IsDivergent);
    public int MirrorPageReadCount => Pages.Count(page => page.MirrorWasInspected);
}

public sealed class V3GaugeJobService
{
    public const uint ReplicaAddressStride = 0x02000000;

    private readonly GaugeSession _session;

    public V3GaugeJobService(GaugeSession session)
    {
        _session = session;
    }

    public static SensorCalibrationBundle GetCalibrationBundle(V3FileHeader header) =>
        new(
            header.SensorSerial.ToArray(),
            header.SensorHeader.ToArray(),
            header.PressurePolynomial.ToArray(),
            header.TemperaturePolynomial.ToArray());

    public static IReadOnlyList<CalibratedGaugeSample> BuildCalibratedSamples(
        V3GaugeDownload download)
    {
        var calibrationBundle = GetCalibrationBundle(download.File.Header);
        var sensorHeader = SensorCalibrationHeader.Parse(calibrationBundle.SensorHeader);
        if (sensorHeader.CountBias is null)
        {
            throw new InvalidDataException(
                $"V3 file {download.File.CatalogRecord.FileId} header does not contain Bias.");
        }

        QuartzCalibration calibration;
        try
        {
            calibration = QuartzCalibration.FromPayloads(
                sensorHeader,
                calibrationBundle.PressurePolynomial,
                calibrationBundle.TemperaturePolynomial);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException)
        {
            throw new InvalidDataException(
                $"V3 file {download.File.CatalogRecord.FileId} contains invalid calibration payloads.",
                ex);
        }

        var samples = new List<CalibratedGaugeSample>(download.Samples.Count);
        for (var pageIndex = 0; pageIndex < download.DataPages.Count; pageIndex++)
        {
            var page = download.DataPages[pageIndex];
            var evidence = download.Pages[pageIndex];
            var corrected = evidence.Selected.Status == V3PageStatus.Corrected;
            for (var sampleIndex = 0; sampleIndex < page.Samples.Count; sampleIndex++)
            {
                var sample = page.Samples[sampleIndex];
                var pressureCounts = checked(sample.PressureCounts + sensorHeader.CountBias.Value);
                var temperatureCounts = checked(sample.TemperatureCounts + sensorHeader.CountBias.Value);
                var pressureFrequency = calibration.PressureFrequencyHz(pressureCounts);
                var temperatureFrequency = calibration.TemperatureFrequencyHz(temperatureCounts);
                var address = checked(
                    evidence.Address +
                    (uint)V3PageCodec.PayloadOffset +
                    (uint)(sampleIndex * 10));

                samples.Add(new CalibratedGaugeSample(
                    pressureCounts,
                    temperatureCounts,
                    calibration.PressurePsiFromFrequency(pressureFrequency, temperatureFrequency),
                    calibration.TemperatureCelsiusFromFrequency(temperatureFrequency),
                    checked((int)sample.SampleSequence),
                    checked((ushort)(sample.SampleSequence & ushort.MaxValue)),
                    address,
                    sample.Timestamp,
                    temperatureFrequency,
                    pressureFrequency,
                    false,
                    corrected,
                    0));
            }
        }

        return samples;
    }

    public async Task<V3GaugeCatalog?> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var capabilities = await _session.ProbeV3CapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        if (capabilities is null)
        {
            return null;
        }

        var chunk = GetReadChunk(capabilities);
        var maximumRecords = Math.Min(
            256,
            checked((int)(capabilities.CatalogLength / V3PageCodec.PhysicalBytes)));
        var primaryScan = await ScanCatalogReplicaAsync(
            0,
            capabilities.CatalogStart,
            maximumRecords,
            chunk,
            cancellationToken).ConfigureAwait(false);

        V3CatalogRecovery recovery;
        if (primaryScan.Replica.IsValid &&
            primaryScan.Replica.Records.All(record => record.Page.Status == V3PageStatus.Ok))
        {
            recovery = new V3CatalogRecovery(
                primaryScan.Replica.Records,
                [
                    primaryScan.Replica,
                    new V3CatalogReplica(1, [], null, true, null, WasInspected: false)
                ],
                0,
                false);
        }
        else
        {
            var mirrorScan = await ScanCatalogReplicaAsync(
                1,
                checked(ReplicaAddressStride + capabilities.CatalogStart),
                maximumRecords,
                chunk,
                cancellationToken).ConfigureAwait(false);
            recovery = V3CatalogDecoder.Recover(
                primaryScan.Bytes,
                mirrorScan.Bytes,
                maximumRecords);
        }

        var summary = CreateHostCatalogSummary(recovery);

        var files = new List<V3GaugeFile>(recovery.Records.Count);
        var rejectedRecords = new List<V3RejectedCatalogRecord>();
        for (var index = 0; index < recovery.Records.Count; index++)
        {
            var record = recovery.Records[index];
            var nextStart = index + 1 < recovery.Records.Count
                ? recovery.Records[index + 1].FileStart
                : capabilities.StorageEnd;
            if (record.FileStart < capabilities.DataStart ||
                nextStart <= record.FileStart ||
                nextStart > capabilities.StorageEnd)
            {
                throw new InvalidDataException($"Catalog record {index} has an impossible file bound.");
            }

            try
            {
                var header = await ReadHeaderAsync(record, capabilities, cancellationToken).ConfigureAwait(false);

                var file = new V3GaugeFile(
                    files.Count,
                    record,
                    header.Header,
                    checked(record.FileStart + capabilities.SectorBytes),
                    checked(record.FileStart + capabilities.SectorBytes),
                    nextStart,
                    index == recovery.Records.Count - 1,
                    header.ReplicaId);
                var dataEnd = await FindDataEndAsync(
                    file,
                    capabilities,
                    cancellationToken).ConfigureAwait(false);
                files.Add(file with { DataEnd = dataEnd });
            }
            catch (InvalidDataException ex)
            {
                rejectedRecords.Add(new V3RejectedCatalogRecord(record, ex.Message));
            }
        }

        return new V3GaugeCatalog(capabilities, summary, recovery, files, rejectedRecords);
    }

    public async Task<V3GaugeDownload> DownloadFileAsync(
        V3GaugeCatalog catalog,
        int fileIndex,
        CancellationToken cancellationToken = default,
        IProgress<MemoryReadProgress>? progress = null)
    {
        if (fileIndex < 0 || fileIndex >= catalog.Files.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(fileIndex));
        }

        var file = catalog.Files[fileIndex];
        var end = file.DataEnd;
        if (end < file.DataStart || end > file.BoundEnd || end % V3PageCodec.PhysicalBytes != 0)
        {
            throw new InvalidDataException("Recovered V3 file end is outside its catalog bound.");
        }

        var length = checked((int)(end - file.DataStart));
        var chunk = GetReadChunk(catalog.Capabilities);
        var replica0 = await _session.ReadExternalMemoryChunkedAsync(
            file.DataStart,
            length,
            chunk,
            GaugeCommand.ReadExternalEeprom,
            cancellationToken,
            progress).ConfigureAwait(false);
        var replica1 = Enumerable.Repeat((byte)0xFF, length).ToArray();

        var evidence = new List<V3ReplicaPageEvidence>();
        var dataPages = new List<V3DataPage>();
        var pageGaps = new List<uint>();
        var sampleGaps = new List<uint>();
        uint expectedPage = 0;
        uint expectedSample = 0;

        for (var offset = 0; offset < length; offset += V3PageCodec.PhysicalBytes)
        {
            var raw0 = replica0.AsSpan(offset, V3PageCodec.PhysicalBytes);
            var decoded0 = V3PageCodec.Decode(raw0);
            var address = checked(file.DataStart + (uint)offset);
            var selectedReplicaId = 0;
            var selected = decoded0;
            V3PageDecodeResult? decoded1 = null;
            V3DataPage? dataPage = null;
            var primaryIsFooter = decoded0.IsAccepted &&
                decoded0.Envelope?.Type == V3PageType.Footer;
            var primaryIsData = TryDecodeExpectedDataPage(
                    decoded0,
                    file.CatalogRecord.FileId,
                    expectedPage,
                    expectedSample,
                    out var primaryDataPage);
            var primaryIsClean = decoded0.Status == V3PageStatus.Ok &&
                (primaryIsFooter || primaryIsData);

            if (primaryIsClean)
            {
                dataPage = primaryDataPage;
            }
            else
            {
                var raw1 = await ReadPageAsync(
                    checked(ReplicaAddressStride + address),
                    chunk,
                    cancellationToken).ConfigureAwait(false);
                raw1.CopyTo(replica1, offset);
                decoded1 = V3PageCodec.Decode(raw1);
                selectedReplicaId = 1;
                selected = decoded1;
                var mirrorIsFooter = decoded1.IsAccepted &&
                    decoded1.Envelope?.Type == V3PageType.Footer;
                var mirrorIsData = TryDecodeExpectedDataPage(
                        decoded1,
                        file.CatalogRecord.FileId,
                        expectedPage,
                        expectedSample,
                        out var mirrorDataPage);
                if (mirrorIsFooter || mirrorIsData)
                {
                    dataPage = mirrorDataPage;
                }
                else if (primaryIsFooter || primaryIsData)
                {
                    selectedReplicaId = 0;
                    selected = decoded0;
                    dataPage = primaryDataPage;
                }
                else
                {
                    evidence.Add(new V3ReplicaPageEvidence(
                        address,
                        selectedReplicaId,
                        selected,
                        decoded0,
                        decoded1,
                        true));
                    break;
                }
            }

            evidence.Add(new V3ReplicaPageEvidence(
                address,
                selectedReplicaId,
                selected,
                decoded0,
                decoded1,
                decoded1 is not null));

            if (selected.Envelope?.Type == V3PageType.Footer)
            {
                break;
            }

            dataPages.Add(dataPage!);
            expectedPage++;
            expectedSample += (uint)dataPage!.Samples.Count;
        }

        return new V3GaugeDownload(
            file,
            end,
            replica0,
            replica1,
            evidence,
            dataPages,
            evidence.All(page => page.Selected.Envelope?.Type != V3PageType.Footer),
            pageGaps,
            sampleGaps);
    }

    private async Task<(V3FileHeader Header, int ReplicaId)> ReadHeaderAsync(
        V3CatalogRecord record,
        V3Capabilities capabilities,
        CancellationToken cancellationToken)
    {
        var chunk = GetReadChunk(capabilities);
        V3FileHeader? correctedPrimary = null;
        InvalidDataException? primaryFailure = null;
        try
        {
            var primary = await ReadHeaderReplicaAsync(
                record,
                record.FileStart,
                chunk,
                cancellationToken).ConfigureAwait(false);
            if (primary.Pages.All(page => page.Page.Status == V3PageStatus.Ok))
            {
                return (primary, 0);
            }

            correctedPrimary = primary;
        }
        catch (InvalidDataException ex)
        {
            primaryFailure = ex;
        }

        try
        {
            var mirror = await ReadHeaderReplicaAsync(
                record,
                checked(ReplicaAddressStride + record.FileStart),
                chunk,
                cancellationToken).ConfigureAwait(false);
            return (mirror, 1);
        }
        catch (InvalidDataException mirrorFailure)
        {
            if (correctedPrimary is not null)
            {
                return (correctedPrimary, 0);
            }

            throw new InvalidDataException(
                $"File {record.FileId} has no valid committed header. " +
                $"Primary: {primaryFailure!.Message} Mirror: {mirrorFailure.Message}");
        }
    }

    private async Task<V3FileHeader> ReadHeaderReplicaAsync(
        V3CatalogRecord record,
        uint address,
        ushort chunk,
        CancellationToken cancellationToken)
    {
        const int initialPages = 3;
        var initialLength = initialPages * V3PageCodec.PhysicalBytes;
        var initial = await _session.ReadExternalMemoryChunkedAsync(
            address,
            initialLength,
            chunk,
            GaugeCommand.ReadExternalEeprom,
            cancellationToken).ConfigureAwait(false);
        var firstHeader = V3HeaderDecoder.DecodePage(
            initial.AsSpan(0, V3PageCodec.PhysicalBytes));
        if (firstHeader.Type != V3PageType.HeaderBody ||
            firstHeader.BodyPageIndex != 0 ||
            firstHeader.BodyPageCount is 0 or > V3HeaderDecoder.MaximumBodyPages)
        {
            throw new InvalidDataException($"File {record.FileId} has an invalid first header page.");
        }

        var extentLength = checked((int)(firstHeader.BodyPageCount + 1) * V3PageCodec.PhysicalBytes);
        byte[] extent;
        if (extentLength <= initial.Length)
        {
            extent = initial.AsSpan(0, extentLength).ToArray();
        }
        else
        {
            var remainder = await _session.ReadExternalMemoryChunkedAsync(
                checked(address + (uint)initial.Length),
                extentLength - initial.Length,
                chunk,
                GaugeCommand.ReadExternalEeprom,
                cancellationToken).ConfigureAwait(false);
            extent = new byte[extentLength];
            initial.CopyTo(extent, 0);
            remainder.CopyTo(extent, initial.Length);
        }

        var header = V3HeaderDecoder.Decode(extent);
        if (header.FileId != record.FileId ||
            header.CreationBootId != record.CreationBootId ||
            header.MeasurementInterval != record.NominalInterval)
        {
            throw new InvalidDataException("Committed header does not match its catalog record.");
        }

        return header;
    }

    private async Task<uint> FindDataEndAsync(
        V3GaugeFile file,
        V3Capabilities capabilities,
        CancellationToken cancellationToken)
    {
        if (file.BoundEnd == file.DataStart)
        {
            return file.DataStart;
        }

        if (!file.IsLatest)
        {
            return await FindEndWithinSectorAsync(
                file,
                checked(file.BoundEnd - capabilities.SectorBytes),
                capabilities,
                cancellationToken).ConfigureAwait(false);
        }

        var sectorBytes = capabilities.SectorBytes;
        var low = 0U;
        var high = (file.BoundEnd - file.DataStart) / sectorBytes;
        var chunk = GetReadChunk(capabilities);

        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            var address = checked(file.DataStart + middle * sectorBytes);
            var page0 = await ReadPageAsync(address, chunk, cancellationToken).ConfigureAwait(false);
            var expectedPageSequence = checked((address - file.DataStart) / V3PageCodec.PhysicalBytes);
            var decoded0 = V3PageCodec.Decode(page0);
            if (IsExpectedFilePage(decoded0, file.CatalogRecord.FileId, expectedPageSequence))
            {
                low = middle + 1;
                continue;
            }

            var page1 = await ReadPageAsync(
                checked(ReplicaAddressStride + address),
                chunk,
                cancellationToken).ConfigureAwait(false);
            var decoded1 = V3PageCodec.Decode(page1);
            if (IsExpectedFilePage(decoded1, file.CatalogRecord.FileId, expectedPageSequence))
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        if (low == 0)
        {
            return file.DataStart;
        }

        return await FindEndWithinSectorAsync(
            file,
            checked(file.DataStart + ((low - 1) * sectorBytes)),
            capabilities,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<uint> FindEndWithinSectorAsync(
        V3GaugeFile file,
        uint sectorStart,
        V3Capabilities capabilities,
        CancellationToken cancellationToken)
    {
        var sectorBytes = capabilities.SectorBytes;
        var chunk = GetReadChunk(capabilities);
        var replica0 = await _session.ReadExternalMemoryChunkedAsync(
            sectorStart,
            sectorBytes,
            chunk,
            GaugeCommand.ReadExternalEeprom,
            cancellationToken).ConfigureAwait(false);

        for (var offset = 0; offset < sectorBytes; offset += V3PageCodec.PhysicalBytes)
        {
            var primary = replica0.AsSpan(offset, V3PageCodec.PhysicalBytes);
            var address = checked(sectorStart + (uint)offset);
            var expectedPageSequence = checked((address - file.DataStart) / V3PageCodec.PhysicalBytes);
            var decoded0 = V3PageCodec.Decode(primary);
            if (IsExpectedFilePage(decoded0, file.CatalogRecord.FileId, expectedPageSequence))
            {
                if (decoded0.Envelope?.Type == V3PageType.Footer)
                {
                    return checked(address + (uint)V3PageCodec.PhysicalBytes);
                }
                continue;
            }

            var mirror = await ReadPageAsync(
                checked(ReplicaAddressStride + address),
                chunk,
                cancellationToken).ConfigureAwait(false);
            var decoded1 = V3PageCodec.Decode(mirror);
            if (!IsExpectedFilePage(decoded1, file.CatalogRecord.FileId, expectedPageSequence))
            {
                return address;
            }

            if (decoded1.Envelope?.Type == V3PageType.Footer)
            {
                return checked(address + (uint)V3PageCodec.PhysicalBytes);
            }
        }

        return checked(sectorStart + sectorBytes);
    }

    private async Task<(V3CatalogReplica Replica, byte[] Bytes)> ScanCatalogReplicaAsync(
        int replicaId,
        uint address,
        int maximumRecords,
        ushort chunk,
        CancellationToken cancellationToken)
    {
        const int pagesPerRead = 3;
        var bytes = new List<byte>(maximumRecords * V3PageCodec.PhysicalBytes);
        while (bytes.Count / V3PageCodec.PhysicalBytes < maximumRecords)
        {
            var remainingPages = maximumRecords - (bytes.Count / V3PageCodec.PhysicalBytes);
            var readPages = Math.Min(pagesPerRead, remainingPages);
            var next = await _session.ReadExternalMemoryChunkedAsync(
                checked(address + (uint)bytes.Count),
                readPages * V3PageCodec.PhysicalBytes,
                chunk,
                GaugeCommand.ReadExternalEeprom,
                cancellationToken).ConfigureAwait(false);
            bytes.AddRange(next);

            var replica = V3CatalogDecoder.DecodeReplica(
                replicaId,
                bytes.ToArray(),
                maximumRecords);
            if (replica.TerminalPage is not null)
            {
                return (replica, bytes.ToArray());
            }
        }

        var complete = bytes.ToArray();
        return (V3CatalogDecoder.DecodeReplica(replicaId, complete, maximumRecords), complete);
    }

    private static V3CatalogSummary CreateHostCatalogSummary(V3CatalogRecovery recovery)
    {
        var latest = recovery.Records.LastOrDefault();
        var inspected = recovery.Replicas.Where(replica => replica.WasInspected).ToArray();
        byte validMask = 0;
        byte degradedMask = 0;
        foreach (var replica in inspected)
        {
            var mask = (byte)(1 << replica.ReplicaId);
            if (replica.IsValid)
            {
                validMask |= mask;
            }
            else
            {
                degradedMask |= mask;
            }
        }

        return new V3CatalogSummary(
            recovery.SelectedReplicaId == 0 && degradedMask == 0 ? (byte)0 : (byte)1,
            validMask,
            degradedMask,
            checked((uint)recovery.Records.Count),
            latest?.CatalogSequence ?? 0,
            latest?.FileId ?? 0,
            latest?.FileStart ?? 0,
            uint.MaxValue);
    }

    private static bool TryDecodeExpectedDataPage(
        V3PageDecodeResult page,
        uint expectedFileId,
        uint expectedPageSequence,
        uint expectedSampleSequence,
        out V3DataPage? dataPage)
    {
        dataPage = null;
        if (!page.IsAccepted || page.Envelope?.Type != V3PageType.Data)
        {
            return false;
        }

        try
        {
            dataPage = V3DataDecoder.DecodePage(page.DecodedBytes!);
            return dataPage.FileId == expectedFileId &&
                dataPage.PageSequence == expectedPageSequence &&
                dataPage.FirstSampleSequence == expectedSampleSequence;
        }
        catch (InvalidDataException)
        {
            dataPage = null;
            return false;
        }
    }

    private static bool IsExpectedFilePage(
        V3PageDecodeResult page,
        uint fileId,
        uint pageSequence)
    {
        if (!page.IsAccepted ||
            page.Envelope?.FileId != fileId ||
            page.Envelope.PageSequence != pageSequence)
        {
            return false;
        }

        if (page.Envelope.Type == V3PageType.Footer)
        {
            return true;
        }

        if (page.Envelope.Type != V3PageType.Data)
        {
            return false;
        }

        try
        {
            _ = V3DataDecoder.DecodePage(page.DecodedBytes!);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private async Task<byte[]> ReadPageAsync(
        uint address,
        ushort chunk,
        CancellationToken cancellationToken) =>
        await _session.ReadExternalMemoryChunkedAsync(
            address,
            V3PageCodec.PhysicalBytes,
            chunk,
            GaugeCommand.ReadExternalEeprom,
            cancellationToken).ConfigureAwait(false);

    private static ushort GetReadChunk(V3Capabilities capabilities) =>
        (ushort)Math.Min((int)capabilities.MaximumSerialPayload, 792);
}
