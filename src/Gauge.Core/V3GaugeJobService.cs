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
    IReadOnlyList<V3RejectedCatalogRecord> RejectedRecords,
    bool UsesMirror = true,
    int PreferredReplicaId = 0,
    V3DiagnosticStatus? DiagnosticStatus = null,
    bool RequiresMemoryService = false);

public sealed record V3RejectedCatalogRecord(
    V3CatalogRecord CatalogRecord,
    string Reason);

public sealed record V3ReplicaPageEvidence(
    uint Address,
    int SelectedReplicaId,
    V3PageDecodeResult Selected,
    V3PageDecodeResult? Replica0,
    V3PageDecodeResult? Replica1,
    bool IsDivergent,
    int PreferredReplicaId = 0)
{
    public bool MirrorWasInspected => Replica0 is not null && Replica1 is not null;

    public bool RecoveredFromAlternate =>
        SelectedReplicaId != PreferredReplicaId;
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
    IReadOnlyList<uint> SampleSequenceGaps,
    bool UsesMirror = true,
    int PreferredReplicaId = 0,
    bool RequiresMemoryService = false)
{
    public IReadOnlyList<V3DataSample> Samples => DataPages.SelectMany(page => page.Samples).ToArray();
    public int CorrectedPageCount => Pages.Count(page => page.Selected.Status == V3PageStatus.Corrected);
    public int UncorrectablePageCount => Pages.Count(page => !page.Selected.IsAccepted);
    public bool HasMirrorDivergence => Pages.Any(page => page.IsDivergent);
    public int MirrorPageReadCount => Pages.Count(page => page.MirrorWasInspected);
    public int AlternateRecoveryCount => Pages.Count(page => page.RecoveredFromAlternate);
    public int MissingSampleCount => Samples.Count(sample => sample.IsMissing);
    // An open V3 file is a valid normal ending: power removal is the usual
    // end-of-job transaction and no footer is required. Only a gap in the
    // committed page sequence makes the recovered file incomplete.
    public bool IsIncomplete => PageSequenceGaps.Count != 0;

    public byte[] SelectedRawBytes
    {
        get
        {
            var result = Enumerable.Repeat((byte)0xFF, Replica0RawBytes.Length).ToArray();
            foreach (var page in Pages)
            {
                var offset = checked((int)(page.Address - File.DataStart));
                var source = page.SelectedReplicaId == 0
                    ? Replica0RawBytes
                    : Replica1RawBytes;
                source.AsSpan(offset, V3PageCodec.PhysicalBytes)
                    .CopyTo(result.AsSpan(offset));
            }

            return result;
        }
    }
}

public sealed class V3GaugeJobService
{
    public const uint ReplicaAddressStride = 0x02000000;

    private readonly GaugeSession _session;
    private readonly bool _useMirror;
    private int _preferredReplicaId;
    private bool _probeBoth;

    public V3GaugeJobService(GaugeSession session, bool useMirror = true)
    {
        _session = session;
        _useMirror = useMirror;
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
            var evidence = download.Pages.First(candidate =>
                candidate.Selected.Envelope?.FileId == page.FileId &&
                candidate.Selected.Envelope.PageSequence == page.PageSequence);
            var corrected = evidence.Selected.Status == V3PageStatus.Corrected;
            for (var sampleIndex = 0; sampleIndex < page.Samples.Count; sampleIndex++)
            {
                var sample = page.Samples[sampleIndex];
                var recordOffset = page.Encoding switch
                {
                    V3DataEncoding.CompactCrc32C => 31,
                    V3DataEncoding.CompactCrc64Fallback => 30,
                    _ => V3PageCodec.PayloadOffset
                };
                var recordBytes = page.Encoding == V3DataEncoding.LegacyCrc64 ? 10 : 6;
                var address = checked(
                    evidence.Address +
                    (uint)recordOffset +
                    (uint)(sampleIndex * recordBytes));
                if (sample.IsMissing)
                {
                    samples.Add(new CalibratedGaugeSample(
                        0,
                        0,
                        double.NaN,
                        double.NaN,
                        checked((int)sample.SampleSequence),
                        checked((ushort)(sample.SampleSequence & ushort.MaxValue)),
                        address,
                        sample.Timestamp,
                        double.NaN,
                        double.NaN,
                        false,
                        corrected,
                        0,
                        true,
                        sample.ExactTimestampSeconds));
                    continue;
                }

                var pressureCounts = checked(
                    sample.PressureCounts!.Value + sensorHeader.CountBias.Value);
                var temperatureCounts = checked(
                    sample.TemperatureCounts!.Value + sensorHeader.CountBias.Value);
                var pressureFrequency = calibration.PressureFrequencyHz(pressureCounts);
                var temperatureFrequency = calibration.TemperatureFrequencyHz(temperatureCounts);

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
                    0,
                    false,
                    sample.ExactTimestampSeconds));
            }
        }

        return samples;
    }

    public async Task<V3GaugeCatalog?> DiscoverAsync(
        CancellationToken cancellationToken = default,
        IProgress<FileInfoReadProgress>? progress = null)
    {
        progress?.Report(new FileInfoReadProgress(0, "Reading storage capabilities"));
        var capabilities = await _session.ProbeV3CapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        if (capabilities is null)
        {
            return null;
        }

        progress?.Report(new FileInfoReadProgress(5, "Validating storage layout"));
        if (_useMirror != capabilities.UsesMirror)
        {
            throw new InvalidDataException(
                "IDENTIFY memory mode does not agree with the command-73 V3 storage layout.");
        }

        var diagnosticStatus = capabilities.IsLegacyLayout
            ? null
            : await _session
                .ReadV3DiagnosticStatusAsync(capabilities, cancellationToken)
                .ConfigureAwait(false);
        progress?.Report(new FileInfoReadProgress(10, "Reading file catalog"));
        _preferredReplicaId = _useMirror
            ? diagnosticStatus?.PreferredReplicaId ?? 0
            : 0;
        _probeBoth = _useMirror &&
            diagnosticStatus?.ProbeBothFirst == true;

        var chunk = GetReadChunk(capabilities);
        var maximumRecords = Math.Min(
            256,
            checked((int)(capabilities.CatalogLength / V3PageCodec.PhysicalBytes)));
        var preferredScan = await ScanCatalogReplicaAsync(
            _preferredReplicaId,
            GetReplicaAddress(capabilities.CatalogStart, _preferredReplicaId),
            maximumRecords,
            chunk,
            cancellationToken).ConfigureAwait(false);

        V3CatalogRecovery recovery;
        if (!_useMirror)
        {
            if (!preferredScan.Replica.IsValid)
            {
                throw new InvalidDataException(
                    "The V3 full-capacity catalog is not recoverable from its single copy.");
            }

            recovery = new V3CatalogRecovery(
                preferredScan.Replica.Records,
                [
                    preferredScan.Replica,
                    new V3CatalogReplica(1, [], null, true, null, WasInspected: false)
                ],
                0,
                false);
        }
        else
        {
            recovery = await RecoverMirrorCatalogAsync(
                preferredScan,
                capabilities,
                maximumRecords,
                chunk,
                diagnosticStatus?.ProbeBothFirst == true,
                cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new FileInfoReadProgress(25, "Validating file information"));
        var summary = CreateHostCatalogSummary(recovery);
        var requiresMemoryService =
            diagnosticStatus?.RequiresMemoryService == true ||
            recovery.HasMirrorDivergence ||
            recovery.Replicas.Any(replica => replica.WasInspected && !replica.IsValid);

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
                var header = await ReadHeaderAsync(
                    record,
                    capabilities,
                    cancellationToken).ConfigureAwait(false);
                requiresMemoryService |= header.RequiresMemoryService;

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

            progress?.Report(new FileInfoReadProgress(
                25 + (75 * (index + 1) / (double)recovery.Records.Count),
                "Validating file information"));
        }

        progress?.Report(new FileInfoReadProgress(100, "File information ready"));
        return new V3GaugeCatalog(
            capabilities,
            summary,
            recovery,
            files,
            rejectedRecords,
            _useMirror,
            _preferredReplicaId,
            diagnosticStatus,
            requiresMemoryService);
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

        if (catalog.UsesMirror != _useMirror)
        {
            throw new InvalidOperationException(
                "V3 download service mode does not match the discovered catalog.");
        }

        var file = catalog.Files[fileIndex];
        var end = file.DataEnd;
        if (end < file.DataStart || end > file.BoundEnd || end % V3PageCodec.PhysicalBytes != 0)
        {
            throw new InvalidDataException("Recovered V3 file end is outside its catalog bound.");
        }

        var length = checked((int)(end - file.DataStart));
        var chunk = GetReadChunk(catalog.Capabilities);
        var preferredReplicaId = catalog.UsesMirror
            ? catalog.PreferredReplicaId
            : 0;
        var probeBoth = catalog.UsesMirror &&
            catalog.DiagnosticStatus?.ProbeBothFirst == true;
        var replica0 = Enumerable.Repeat((byte)0xFF, length).ToArray();
        var replica1 = Enumerable.Repeat((byte)0xFF, length).ToArray();

        var evidence = new List<V3ReplicaPageEvidence>();
        var dataPages = new List<V3DataPage>();
        var pageGaps = new List<uint>();
        var sampleGaps = new List<uint>();
        uint expectedPage = 0;
        uint? expectedSample = 0;

        for (var offset = 0; offset < length; offset += V3PageCodec.PhysicalBytes)
        {
            var address = checked(file.DataStart + (uint)offset);
            var preferredRaw = await ReadPageAsync(
                GetReplicaAddress(address, preferredReplicaId),
                chunk,
                cancellationToken).ConfigureAwait(false);
            preferredRaw.CopyTo(
                preferredReplicaId == 0 ? replica0 : replica1,
                offset);
            progress?.Report(new MemoryReadProgress(
                offset + V3PageCodec.PhysicalBytes,
                length,
                preferredReplicaId == 0 ? replica0 : replica1));
            var decodedPreferred = V3PageCodec.Decode(preferredRaw);
            var preferredIsFooter = decodedPreferred.IsAccepted &&
                decodedPreferred.Envelope?.Type == V3PageType.Footer;
            var preferredIsData = TryDecodeExpectedDataPage(
                decodedPreferred,
                file.CatalogRecord.FileId,
                expectedPage,
                    expectedSample,
                    out var preferredDataPage);
            var preferredIsValid = preferredIsFooter || preferredIsData;
            var preferredIsClean =
                decodedPreferred.Status == V3PageStatus.Ok && preferredIsValid;

            var selectedReplicaId = preferredReplicaId;
            var selected = decodedPreferred;
            var selectedDataPage = preferredDataPage;
            V3PageDecodeResult? decodedAlternate = null;
            var divergent = false;

            if ((!preferredIsClean || probeBoth) && catalog.UsesMirror)
            {
                var alternateReplicaId = 1 - preferredReplicaId;
                var alternateRaw = await ReadPageAsync(
                    GetReplicaAddress(address, alternateReplicaId),
                    chunk,
                    cancellationToken).ConfigureAwait(false);
                alternateRaw.CopyTo(
                    alternateReplicaId == 0 ? replica0 : replica1,
                    offset);
                decodedAlternate = V3PageCodec.Decode(alternateRaw);
                var alternateIsFooter = decodedAlternate.IsAccepted &&
                    decodedAlternate.Envelope?.Type == V3PageType.Footer;
                var alternateIsData = TryDecodeExpectedDataPage(
                decodedAlternate,
                        file.CatalogRecord.FileId,
                        expectedPage,
                        expectedSample,
                        out var alternateDataPage);
                var alternateIsValid = alternateIsFooter || alternateIsData;

                if (alternateIsValid &&
                    (!preferredIsValid ||
                     (decodedAlternate.Status == V3PageStatus.Ok &&
                      decodedPreferred.Status != V3PageStatus.Ok)))
                {
                    selectedReplicaId = alternateReplicaId;
                    selected = decodedAlternate;
                    selectedDataPage = alternateDataPage;
                }

                if (preferredIsValid && alternateIsValid)
                {
                    divergent = !decodedPreferred.DecodedBytes!
                        .AsSpan()
                        .SequenceEqual(decodedAlternate.DecodedBytes);
                }
            }

            var selectedIsValid =
                selected.Envelope?.Type == V3PageType.Footer ||
                selectedDataPage is not null;
            var decoded0 = preferredReplicaId == 0
                ? decodedPreferred
                : decodedAlternate;
            var decoded1 = preferredReplicaId == 1
                ? decodedPreferred
                : decodedAlternate;
            evidence.Add(new V3ReplicaPageEvidence(
                address,
                selectedReplicaId,
                selected,
                decoded0,
                decoded1,
                divergent,
                preferredReplicaId));

            if (!selectedIsValid)
            {
                pageGaps.Add(expectedPage);
                if (expectedSample is not null)
                {
                    sampleGaps.Add(expectedSample.Value);
                }
                expectedPage++;
                expectedSample = null;
                continue;
            }

            if (selected.Envelope!.Type == V3PageType.Footer)
            {
                break;
            }

            dataPages.Add(selectedDataPage!);
            expectedPage++;
            expectedSample = checked(
                selectedDataPage!.FirstSampleSequence +
                (uint)selectedDataPage.Samples.Count);
        }

        var requiresMemoryService =
            catalog.RequiresMemoryService ||
            evidence.Any(page =>
                page.RecoveredFromAlternate || page.IsDivergent);
        return new V3GaugeDownload(
            file,
            end,
            replica0,
            replica1,
            evidence,
            dataPages,
            evidence.All(page => page.Selected.Envelope?.Type != V3PageType.Footer),
            pageGaps,
            sampleGaps,
            catalog.UsesMirror,
            preferredReplicaId,
            requiresMemoryService);
    }

    private async Task<(V3FileHeader Header, int ReplicaId, bool RequiresMemoryService)> ReadHeaderAsync(
        V3CatalogRecord record,
        V3Capabilities capabilities,
        CancellationToken cancellationToken)
    {
        var chunk = GetReadChunk(capabilities);
        var preferredReplicaId = _useMirror ? _preferredReplicaId : 0;
        V3FileHeader? correctedPreferred = null;
        InvalidDataException? preferredFailure = null;
        try
        {
            var preferred = await ReadHeaderReplicaAsync(
                record,
                GetReplicaAddress(record.FileStart, preferredReplicaId),
                chunk,
                cancellationToken).ConfigureAwait(false);
            if (preferred.Pages.All(page => page.Page.Status == V3PageStatus.Ok) &&
                !_probeBoth)
            {
                return (preferred, preferredReplicaId, false);
            }

            correctedPreferred = preferred;
        }
        catch (InvalidDataException ex)
        {
            preferredFailure = ex;
        }

        if (!_useMirror)
        {
            if (correctedPreferred is not null)
            {
                return (correctedPreferred, 0, false);
            }

            throw new InvalidDataException(
                $"File {record.FileId} has no valid committed header in full-capacity storage. " +
                preferredFailure!.Message);
        }

        var alternateReplicaId = 1 - preferredReplicaId;
        try
        {
            var alternate = await ReadHeaderReplicaAsync(
                record,
                GetReplicaAddress(record.FileStart, alternateReplicaId),
                chunk,
                cancellationToken).ConfigureAwait(false);
            var alternateIsClean = alternate.Pages.All(
                page => page.Page.Status == V3PageStatus.Ok);
            if (correctedPreferred is null ||
                (alternateIsClean &&
                 correctedPreferred.Pages.Any(
                     page => page.Page.Status != V3PageStatus.Ok)))
            {
                return (alternate, alternateReplicaId, true);
            }

            var divergent = !correctedPreferred.RawHeaderStream
                .AsSpan()
                .SequenceEqual(alternate.RawHeaderStream);
            return (correctedPreferred, preferredReplicaId, divergent);
        }
        catch (InvalidDataException alternateFailure)
        {
            if (correctedPreferred is not null)
            {
                return (correctedPreferred, preferredReplicaId, true);
            }

            throw new InvalidDataException(
                $"File {record.FileId} has no valid committed header. " +
                $"Preferred: {preferredFailure!.Message} Alternate: {alternateFailure.Message}");
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
            (header.Schema == 1 &&
             header.CreationBootId != record.CreationBootId) ||
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
            var preferredPage = await ReadPageAsync(
                GetReplicaAddress(address, _preferredReplicaId),
                chunk,
                cancellationToken).ConfigureAwait(false);
            var expectedPageSequence = checked((address - file.DataStart) / V3PageCodec.PhysicalBytes);
            var decodedPreferred = V3PageCodec.Decode(preferredPage);
            if (IsExpectedFilePage(
                decodedPreferred,
                file.CatalogRecord.FileId,
                expectedPageSequence))
            {
                low = middle + 1;
                continue;
            }

            if (!_useMirror)
            {
                high = middle;
                continue;
            }

            var alternatePage = await ReadPageAsync(
                GetReplicaAddress(address, 1 - _preferredReplicaId),
                chunk,
                cancellationToken).ConfigureAwait(false);
            var decodedAlternate = V3PageCodec.Decode(alternatePage);
            if (IsExpectedFilePage(
                decodedAlternate,
                file.CatalogRecord.FileId,
                expectedPageSequence))
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
        var preferredBytes = await _session.ReadExternalMemoryChunkedAsync(
            GetReplicaAddress(sectorStart, _preferredReplicaId),
            sectorBytes,
            chunk,
            GaugeCommand.ReadExternalEeprom,
            cancellationToken).ConfigureAwait(false);

        for (var offset = 0; offset < sectorBytes; offset += V3PageCodec.PhysicalBytes)
        {
            var preferred = preferredBytes.AsSpan(offset, V3PageCodec.PhysicalBytes);
            var address = checked(sectorStart + (uint)offset);
            var expectedPageSequence = checked((address - file.DataStart) / V3PageCodec.PhysicalBytes);
            var decodedPreferred = V3PageCodec.Decode(preferred);
            if (IsExpectedFilePage(
                decodedPreferred,
                file.CatalogRecord.FileId,
                expectedPageSequence))
            {
                if (decodedPreferred.Envelope?.Type == V3PageType.Footer)
                {
                    return checked(address + (uint)V3PageCodec.PhysicalBytes);
                }
                continue;
            }

            if (!_useMirror)
            {
                return address;
            }

            var alternate = await ReadPageAsync(
                GetReplicaAddress(address, 1 - _preferredReplicaId),
                chunk,
                cancellationToken).ConfigureAwait(false);
            var decodedAlternate = V3PageCodec.Decode(alternate);
            if (!IsExpectedFilePage(
                decodedAlternate,
                file.CatalogRecord.FileId,
                expectedPageSequence))
            {
                return address;
            }

            if (decodedAlternate.Envelope?.Type == V3PageType.Footer)
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

    private async Task<V3CatalogRecovery> RecoverMirrorCatalogAsync(
        (V3CatalogReplica Replica, byte[] Bytes) preferredScan,
        V3Capabilities capabilities,
        int maximumRecords,
        ushort chunk,
        bool forceBoth,
        CancellationToken cancellationToken)
    {
        var alternateReplicaId = 1 - _preferredReplicaId;
        var inspectAlternate =
            forceBoth ||
            !preferredScan.Replica.IsValid ||
            preferredScan.Replica.Records.Any(
                record => record.Page.Status != V3PageStatus.Ok);

        if (!inspectAlternate &&
            preferredScan.Replica.Records.Count < maximumRecords)
        {
            var nextSequence = preferredScan.Replica.Records.Count;
            var probeAddress = checked(
                GetReplicaAddress(capabilities.CatalogStart, alternateReplicaId) +
                ((uint)nextSequence * V3PageCodec.PhysicalBytes));
            var probe = await ReadPageAsync(
                probeAddress,
                chunk,
                cancellationToken).ConfigureAwait(false);
            var decoded = V3PageCodec.Decode(probe);
            inspectAlternate = decoded.Status != V3PageStatus.Erased;
        }

        if (!inspectAlternate)
        {
            var uninspected = new V3CatalogReplica(
                alternateReplicaId,
                [],
                null,
                true,
                null,
                WasInspected: false);
            var replicas = _preferredReplicaId == 0
                ? new[] { preferredScan.Replica, uninspected }
                : new[] { uninspected, preferredScan.Replica };
            return new V3CatalogRecovery(
                preferredScan.Replica.Records,
                replicas,
                _preferredReplicaId,
                false);
        }

        var alternateScan = await ScanCatalogReplicaAsync(
            alternateReplicaId,
            GetReplicaAddress(capabilities.CatalogStart, alternateReplicaId),
            maximumRecords,
            chunk,
            cancellationToken).ConfigureAwait(false);
        return _preferredReplicaId == 0
            ? V3CatalogDecoder.Recover(
                preferredScan.Bytes,
                alternateScan.Bytes,
                maximumRecords,
                _preferredReplicaId)
            : V3CatalogDecoder.Recover(
                alternateScan.Bytes,
                preferredScan.Bytes,
                maximumRecords,
                _preferredReplicaId);
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
        uint? expectedSampleSequence,
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
                (expectedSampleSequence is null ||
                 dataPage.FirstSampleSequence == expectedSampleSequence);
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

    private uint GetReplicaAddress(uint logicalAddress, int replicaId)
    {
        if (replicaId is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(replicaId));
        }

        if (!_useMirror)
        {
            if (replicaId != 0)
            {
                throw new InvalidOperationException(
                    "Full-capacity V3 storage does not have a mirror replica.");
            }

            return logicalAddress;
        }

        if (logicalAddress >= ReplicaAddressStride)
        {
            throw new InvalidDataException(
                $"Mirrored logical address 0x{logicalAddress:X8} is outside chip 1.");
        }

        return replicaId == 0
            ? logicalAddress
            : checked(ReplicaAddressStride + logicalAddress);
    }

    private static ushort GetReadChunk(V3Capabilities capabilities) =>
        (ushort)Math.Min((int)capabilities.MaximumSerialPayload, 792);
}
