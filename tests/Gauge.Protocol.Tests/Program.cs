using System.Buffers.Binary;
using System.Text;
using Gauge.Calibration;
using Gauge.Core;
using Gauge.Protocol;
using Gauge.Transport;

var tests = new (string Name, Action Run)[]
{
    ("CRC16 matches firmware IDENTIFY vector", Crc16MatchesIdentifyVector),
    ("CRC16 verifies appended high-low bytes to zero", Crc16VerifiesAppendedBytes),
    ("CRC8 matches firmware-style record vector", Crc8MatchesRecordVector),
    ("IDENTIFY frame encodes expected wire bytes", IdentifyFrameEncodesExpectedWireBytes),
    ("Read request encodes declared length without request payload", ReadRequestEncodesDeclaredLengthWithoutRequestPayload),
    ("Encoded frame decodes back to original values", EncodedFrameDecodesBack),
    ("Bad CRC is rejected", BadCrcIsRejected),
    ("Echo-only IDENTIFY is rejected", EchoOnlyIdentifyIsRejected),
    ("Echo-only FIND_EOF is rejected", EchoOnlyFindEofIsRejected),
    ("V3 probe echo is distinguished from invalid-command fallback", V3ProbeEchoIsDistinguishedFromFallback),
    ("Chunked memory read resumes after retained prefix", ChunkedMemoryReadResumesAfterPrefix),
    ("Interrupted memory read retains only confirmed packets", InterruptedMemoryReadRetainsConfirmedPackets),
    ("Bootloader read-version request matches firmware frame", BootloaderReadVersionRequestMatchesFirmwareFrame),
    ("Bootloader write request carries keys and 24-bit address", BootloaderWriteRequestCarriesKeysAndAddress),
    ("Bootloader version response decodes", BootloaderVersionResponseDecodes),
    ("Intel HEX validates checksums and extended addresses", IntelHexValidatesChecksumsAndExtendedAddresses),
    ("Bootloader image rejects application data below offset", BootloaderImageRejectsDataBelowOffset),
    ("Firmware updater erases start first and writes it last", FirmwareUpdaterErasesStartFirstAndWritesItLast),
    ("Memory gauge identify payload decodes", MemoryGaugeIdentifyPayloadDecodes),
    ("Firmware display reverses identity byte order", FirmwareDisplayReversesIdentityByteOrder),
    ("Gauge interval setting uses V2/V3 wire format and verifies IDENTIFY", GaugeIntervalSettingUsesCompatibleWireFormat),
    ("Gauge storage setting resolves a lost acknowledgement by IDENTIFY", GaugeStorageSettingResolvesLostAcknowledgement),
    ("Memory gauge file record parses and validates CRC", MemoryGaugeFileRecordParsesAndValidatesCrc),
    ("Memory gauge file table ignores continuation records", MemoryGaugeFileTableIgnoresContinuationRecords),
    ("Memory gauge data record parses counts and CRC", MemoryGaugeDataRecordParsesCountsAndCrc),
    ("Memory gauge data records preserve incremental indexes", MemoryGaugeDataRecordsPreserveIncrementalIndexes),
    ("Acoustic records are classified and excluded from P&T conversion", AcousticRecordsAreExcludedFromPressureTemperatureConversion),
    ("Sensor hex double coefficients parse", SensorHexDoubleCoefficientsParse),
    ("Sensor calibration header parses fields", SensorCalibrationHeaderParsesFields),
    ("Quartz calibration converts counts to frequencies", QuartzCalibrationConvertsCountsToFrequencies),
    ("Quartz calibration evaluates live gauge measurement", QuartzCalibrationEvaluatesLiveGaugeMeasurement),
    ("Sensor Live status and sample payloads decode", SensorLivePayloadsDecode),
    ("Sensor Live reads calibration without initialising the sensor", SensorLiveReadsCalibrationWithoutInitialise),
    ("Sensor Live raw counts use the V3 calibration pipeline", SensorLiveRawCountsCalibrate),
    ("Calibrated CSV exporter formats rows", CalibratedCsvExporterFormatsRows),
    ("Legacy record exporter writes ASCII format", LegacyRecordExporterWritesAsciiFormat),
    ("Communication session summary counts integrity events", CommunicationSessionSummaryCountsIntegrityEvents),
    ("Erase progress payload validates protocol state and bounds", ExternalEraseProgressPayloadValidates),
    ("Progress erase reports all 512 pairs and clears the interlock", ProgressEraseReportsEveryPair),
    ("Progress erase surfaces a lost gauge response immediately", ProgressEraseStopsOnLostResponse),
    ("Unavailable progress erase falls back to estimated V2 polling", ProgressEraseFallsBackToLegacyV2),
    ("V2 erase is not complete while its EEPROM interlock remains set", LegacyEraseRequiresClearedInterlock),
    ("Incomplete V3 erase resets and restarts progress from zero", IncompleteEraseRestartBeginsFresh),
    ("V3 capabilities parse and legacy invalid-command fallback works", V3CapabilitiesAndFallbackWork),
    ("V3 clean catalog discovery uses raw primary reads only", V3CatalogDiscoveryUsesPrimaryRawReadsOnly),
    ("V3 discovery resolves the latest file logical end", V3DiscoveryResolvesLatestLogicalEnd),
    ("V3 data download reads mirror only for a failed primary page", V3DataDownloadUsesLazyMirrorFallback),
    ("V3 clean and 16-bit corrected target pages decode", V3BchTargetFixturesDecode),
    ("V3 six-file target catalog decodes monotonically", V3CatalogTargetFixtureDecodes),
    ("V3 target header reassembles required calibration TLVs", V3HeaderTargetFixtureDecodes),
    ("V3 file-local header calibrates its downloaded samples", V3FileLocalHeaderCalibratesSamples),
    ("V3 open target file extracts committed samples without footer", V3OpenTargetFileDecodes),
    ("V3 unknown required fields and non-monotonic sequences are rejected", V3RequiredFieldsAndSequencesAreRejected),
    ("V3 malformed padding and over-limit BCH damage are rejected", V3MalformedPagesAreRejected)
};

var failures = 0;

foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

return failures == 0 ? 0 : 1;

static void Crc16MatchesIdentifyVector()
{
    var body = new byte[] { 0x0C, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
    AssertEqual((ushort)0x0CC0, Crc16.Compute(body));
}

static void Crc16VerifiesAppendedBytes()
{
    var bodyWithCrc = new byte[] { 0x0C, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0xC0 };
    AssertEqual((ushort)0x0000, Crc16.Compute(bodyWithCrc));
}

static void Crc8MatchesRecordVector()
{
    var recordPrefix = new byte[] { 2, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14 };
    AssertEqual((byte)0x52, Crc8.Compute(recordPrefix));
}

static void IdentifyFrameEncodesExpectedWireBytes()
{
    var frame = GaugeFrame.Create(GaugeCommand.Identify);
    var wire = GaugeFrameCodec.Encode(frame);
    AssertEqual("550C0000000000000CC0", Convert.ToHexString(wire));
}

static void ReadRequestEncodesDeclaredLengthWithoutRequestPayload()
{
    var frame = GaugeFrame.CreateReadRequest(GaugeCommand.ReadFileSector, 0x00123456, 1024);
    var wire = GaugeFrameCodec.Encode(frame);

    AssertEqual(10, wire.Length);
    AssertEqual((byte)GaugeProtocolConstants.StartByte, wire[0]);
    AssertEqual((byte)GaugeCommand.ReadFileSector, wire[1]);
    AssertEqual((byte)0x00, wire[2]);
    AssertEqual((byte)0x04, wire[3]);
    AssertEqual((byte)0x56, wire[4]);
    AssertEqual((byte)0x34, wire[5]);
    AssertEqual((byte)0x12, wire[6]);
    AssertEqual((byte)0x00, wire[7]);
    AssertEqual((ushort)0, Crc16.Compute(wire.AsSpan(1)));
}

static void EncodedFrameDecodesBack()
{
    var payload = new byte[] { 1, 2, 3 };
    var original = GaugeFrame.Create(GaugeCommand.WriteExternalEeprom, 0x12345678, payload);
    var decoded = GaugeFrameCodec.Decode(GaugeFrameCodec.Encode(original));

    AssertEqual(original.Command, decoded.Command);
    AssertEqual(original.DataLength, decoded.DataLength);
    AssertEqual(original.Address, decoded.Address);
    AssertSequenceEqual(original.Payload, decoded.Payload);
}

static void BadCrcIsRejected()
{
    var wire = GaugeFrameCodec.Encode(GaugeFrame.Create(GaugeCommand.Identify));
    wire[^1] ^= 0x01;

    try
    {
        _ = GaugeFrameCodec.Decode(wire);
    }
    catch (GaugeProtocolException)
    {
        return;
    }

    throw new InvalidOperationException("Expected bad CRC frame to be rejected.");
}

static void EchoOnlyIdentifyIsRejected()
{
    var transport = new DelegateGaugeTransport(request => request);
    var session = new GaugeSession(transport);
    try
    {
        _ = session.IdentifyAsync().GetAwaiter().GetResult();
    }
    catch (GaugeProtocolException ex) when (ex.Message.Contains("IDENTIFY", StringComparison.Ordinal))
    {
        return;
    }

    throw new InvalidOperationException("An echoed zero-payload IDENTIFY request was accepted as a device identity.");
}

static void EchoOnlyFindEofIsRejected()
{
    var transport = new DelegateGaugeTransport(request => request);
    var session = new GaugeSession(transport);
    try
    {
        _ = session.FindEndOfFileAsync().GetAwaiter().GetResult();
    }
    catch (GaugeProtocolException ex) when (ex.Message.Contains("FIND_EOF", StringComparison.Ordinal))
    {
        return;
    }

    throw new InvalidOperationException("An echoed zero-payload FIND_EOF request was accepted as an address.");
}

static void V3ProbeEchoIsDistinguishedFromFallback()
{
    var request = GaugeFrame.Create(GaugeCommand.V3Capabilities);
    var echo = GaugeFrameCodec.Decode(GaugeFrameCodec.Encode(request));
    var fallback = new GaugeFrame(GaugeCommand.V3Capabilities, 1, 0, [0xFF]);

    AssertEqual(true, GaugeFrameCodec.IsExactRequestEcho(request, echo));
    AssertEqual(false, GaugeFrameCodec.IsExactRequestEcho(request, fallback));

    var session = new GaugeSession(new DelegateGaugeTransport(_ => fallback));
    AssertEqual<V3Capabilities?>(null, session.ProbeV3CapabilitiesAsync().GetAwaiter().GetResult());
}

static void ChunkedMemoryReadResumesAfterPrefix()
{
    var addresses = new List<uint>();
    var transport = new DelegateGaugeTransport(request =>
    {
        addresses.Add(request.Address);
        var payload = Enumerable.Range(0, request.DataLength)
            .Select(index => (byte)(request.Address + (uint)index))
            .ToArray();
        return GaugeFrame.Create(request.Command, request.Address, payload);
    });
    var session = new GaugeSession(transport);
    var bytes = session.ReadExternalMemoryChunkedAsync(
        0x1000,
        8,
        chunkSize: 2,
        command: GaugeCommand.ReadRecordSector,
        existingPrefix: new byte[] { 1, 2, 3, 4 }).GetAwaiter().GetResult();

    AssertSequenceEqual(new byte[] { 1, 2, 3, 4, 4, 5, 6, 7 }, bytes);
    AssertEqual(2, addresses.Count);
    AssertEqual((uint)0x1004, addresses[0]);
    AssertEqual((uint)0x1006, addresses[1]);
}

static void InterruptedMemoryReadRetainsConfirmedPackets()
{
    byte[] retained = [];
    var firstTransport = new DelegateGaugeTransport(request =>
    {
        if (request.Address == 0x1004)
        {
            throw new TimeoutException("Simulated lost packet reply.");
        }

        var payload = Enumerable.Range(0, request.DataLength)
            .Select(index => (byte)(request.Address + (uint)index))
            .ToArray();
        return GaugeFrame.Create(request.Command, request.Address, payload);
    });
    var progress = new InlineProgress<MemoryReadProgress>(update =>
    {
        retained = update.Buffer.Span[..update.BytesRead].ToArray();
    });

    try
    {
        new GaugeSession(firstTransport).ReadExternalMemoryChunkedAsync(
            0x1000,
            6,
            chunkSize: 2,
            command: GaugeCommand.ReadRecordSector,
            progress: progress).GetAwaiter().GetResult();
        throw new InvalidOperationException("Expected the interrupted read to fail.");
    }
    catch (TimeoutException)
    {
    }

    AssertSequenceEqual(new byte[] { 0, 1, 2, 3 }, retained);

    var resumedAddresses = new List<uint>();
    var resumedTransport = new DelegateGaugeTransport(request =>
    {
        resumedAddresses.Add(request.Address);
        var payload = Enumerable.Range(0, request.DataLength)
            .Select(index => (byte)(request.Address + (uint)index))
            .ToArray();
        return GaugeFrame.Create(request.Command, request.Address, payload);
    });
    var completed = new GaugeSession(resumedTransport).ReadExternalMemoryChunkedAsync(
        0x1000,
        6,
        chunkSize: 2,
        command: GaugeCommand.ReadRecordSector,
        existingPrefix: retained).GetAwaiter().GetResult();

    AssertSequenceEqual(new byte[] { 0, 1, 2, 3, 4, 5 }, completed);
    AssertEqual(1, resumedAddresses.Count);
    AssertEqual((uint)0x1004, resumedAddresses[0]);
}

static void BootloaderReadVersionRequestMatchesFirmwareFrame()
{
    var frame = BootloaderFrame.Create(BootloaderCommand.ReadVersion);
    var wire = BootloaderFrameCodec.EncodeRequest(frame);

    AssertEqual("55000000000000000000", Convert.ToHexString(wire));
}

static void BootloaderWriteRequestCarriesKeysAndAddress()
{
    var frame = BootloaderFrame.Create(
        BootloaderCommand.WriteFlash,
        0x001234,
        new byte[] { 0xAA, 0xBB },
        key1: 0x55,
        key2: 0xAA);
    var wire = BootloaderFrameCodec.EncodeRequest(frame);

    AssertEqual("5502020055AA34120000AABB", Convert.ToHexString(wire));
}

static void BootloaderVersionResponseDecodes()
{
    byte[] wire =
    [
        0x55,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x03, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFE, 0x67,
        0x00, 0x00, 0x40, 0x40, 0xAA, 0xBB, 0xCC, 0xDD
    ];

    var frame = BootloaderFrameCodec.DecodeResponse(wire, BootloaderProtocolConstants.VersionPayloadLength);
    var version = BootloaderVersion.Decode(frame.Payload);

    AssertEqual(BootloaderCommand.ReadVersion, frame.Command);
    AssertEqual((byte)1, version.Major);
    AssertEqual((byte)3, version.Minor);
    AssertEqual((uint)256, version.MaximumPacketSize);
    AssertEqual((ushort)0x67FE, version.DeviceId);
    AssertEqual((byte)64, version.EraseBlockSize);
    AssertEqual((byte)64, version.WriteBlockSize);
    AssertSequenceEqual(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, version.ConfigurationBytes);
}

static void IntelHexValidatesChecksumsAndExtendedAddresses()
{
    var lines = new[]
    {
        BuildHexRecord(0x0800, 0x00, [0x54, 0xEF, 0x04, 0xF0]),
        BuildHexRecord(0x0840, 0x00, [0x12, 0x34]),
        BuildHexRecord(0x0000, 0x04, [0x00, 0x30]),
        BuildHexRecord(0x0000, 0x00, [0x18, 0x82, 0x79, 0x36]),
        BuildHexRecord(0x0000, 0x01, [])
    };

    var hex = IntelHexImage.Parse(lines);
    var image = BootloaderApplicationImage.Create("Memory_Gauge.X.production.hex", hex);

    AssertEqual((byte)0x54, hex.Bytes[0x0800]);
    AssertEqual((byte)0x18, hex.Bytes[0x300000]);
    AssertEqual(6, image.ExplicitProgramBytes);
    AssertEqual(4, image.MetadataBytes);
    AssertEqual(2, image.Rows.Count);
    AssertEqual((uint)0x0800, image.StartRow.Address);

    var badChecksum = lines[0][..^2] + "00";
    try
    {
        _ = IntelHexImage.Parse([badChecksum, BuildHexRecord(0, 1, [])]);
    }
    catch (FormatException)
    {
        return;
    }

    throw new InvalidOperationException("Expected Intel HEX checksum failure.");
}

static void BootloaderImageRejectsDataBelowOffset()
{
    var hex = IntelHexImage.Parse(
    [
        BuildHexRecord(0x0000, 0x00, [0x01]),
        BuildHexRecord(0x0000, 0x01, [])
    ]);

    try
    {
        _ = BootloaderApplicationImage.Create("standalone.hex", hex);
    }
    catch (InvalidDataException)
    {
        return;
    }

    throw new InvalidOperationException("Expected bootloader-address data to be rejected.");
}

static void FirmwareUpdaterErasesStartFirstAndWritesItLast()
{
    var hex = IntelHexImage.Parse(
    [
        BuildHexRecord(0x0800, 0x00, [0x54, 0xEF, 0x04, 0xF0]),
        BuildHexRecord(0x0840, 0x00, [0x11, 0x22]),
        BuildHexRecord(0x0900, 0x00, [0x33, 0x44]),
        BuildHexRecord(0x0000, 0x01, [])
    ]);
    var image = BootloaderApplicationImage.Create("offset.hex", hex);
    var bootloader = new FakeBootloaderClient { LoseFirstWriteAcknowledgement = true };
    var version = new BootloaderVersion(1, 3, 256, 0x6126, 64, 64, []);
    var updater = new GaugeFirmwareUpdater(bootloader, version);

    var result = updater.ProgramAsync(image).GetAwaiter().GetResult();

    AssertEqual("E:000800", bootloader.Mutations[0]);
    AssertEqual("W:000800", bootloader.Mutations[^1]);
    AssertEqual("000900,000840,000800", string.Join(',', bootloader.WrittenAddresses.Select(value => value.ToString("X6"))));
    AssertEqual(992, result.ErasedRows);
    AssertEqual(3, result.ProgrammedRows);
    if (bootloader.Mutations.Any(value => ParseMutationAddress(value) < BootloaderApplicationImage.ApplicationStart))
    {
        throw new InvalidOperationException("Updater touched the resident bootloader address range.");
    }
}

static void MemoryGaugeIdentifyPayloadDecodes()
{
    var payload = new byte[22];
    payload[0] = 20;
    payload[1] = 1;
    WriteUInt32LittleEndian(payload.AsSpan(2), 100200);
    WriteUInt32LittleEndian(payload.AsSpan(6), 1);
    WriteUInt32LittleEndian(payload.AsSpan(10), 100198);
    WriteUInt32LittleEndian(payload.AsSpan(14), 2);
    payload[18] = 5;
    payload[19] = 0;
    payload[20] = 1;
    payload[21] = 0;

    var device = DeviceData.DecodeMemoryGauge(payload);

    AssertEqual((byte)20, device.FirmwareMajor);
    AssertEqual((byte)1, device.FirmwareMinor);
    AssertEqual((uint)100200, device.DeviceType);
    AssertEqual((uint)1, device.DeviceSerial);
    AssertEqual((uint)100198, device.PcbType);
    AssertEqual((uint)2, device.PcbSerial);
    AssertEqual((ushort)5, device.MeasurementInterval);
    AssertEqual((byte)1, device.MemoryMode);
    AssertEqual((byte?)0, device.EraseStatus);
    AssertEqual("1.20", device.FirmwareVersion);
}

static void FirmwareDisplayReversesIdentityByteOrder()
{
    var device = new DeviceData(
        FirmwareMajor: 1,
        FirmwareMinor: 2,
        DeviceType: 100230,
        DeviceSerial: 1,
        PcbType: 100198,
        PcbSerial: 2,
        MeasurementInterval: 5,
        MemoryMode: 1,
        EraseStatus: 0);

    AssertEqual("2.1", device.FirmwareVersion);
}

static void GaugeIntervalSettingUsesCompatibleWireFormat()
{
    ushort measurementInterval = 5;
    var settingWrites = 0;
    var transport = new DelegateGaugeTransport(request =>
    {
        if (request.Command == GaugeCommand.Identify)
        {
            return GaugeFrame.Create(
                request.Command,
                payload: BuildMemoryIdentityPayload(
                    eraseStatus: 0,
                    measurementInterval: measurementInterval,
                    memoryMode: 1));
        }

        if (request.Command == GaugeCommand.SetMeasureRate)
        {
            settingWrites++;
            AssertSequenceEqual([0x2C, 0x01], request.Payload);
            measurementInterval = BinaryPrimitives.ReadUInt16LittleEndian(request.Payload);
            return GaugeFrame.Create(request.Command, payload: [0x01]);
        }

        throw new InvalidOperationException($"Unexpected command {request.Command}.");
    });
    var service = new GaugeConfigurationService(new GaugeSession(transport));

    var device = service
        .SetMeasurementIntervalAsync(300, 1234)
        .GetAwaiter()
        .GetResult();

    AssertEqual(1, settingWrites);
    AssertEqual((ushort)300, device.MeasurementInterval);
}

static void GaugeStorageSettingResolvesLostAcknowledgement()
{
    byte memoryMode = 0;
    var settingWrites = 0;
    var transport = new DelegateGaugeTransport(request =>
    {
        if (request.Command == GaugeCommand.Identify)
        {
            return GaugeFrame.Create(
                request.Command,
                payload: BuildMemoryIdentityPayload(
                    eraseStatus: 0,
                    measurementInterval: 5,
                    memoryMode: memoryMode));
        }

        if (request.Command == GaugeCommand.SetMemoryMode)
        {
            settingWrites++;
            AssertSequenceEqual([0x01], request.Payload);
            memoryMode = request.Payload[0];
            throw new TimeoutException("Simulated lost setting acknowledgement.");
        }

        throw new InvalidOperationException($"Unexpected command {request.Command}.");
    });
    var service = new GaugeConfigurationService(new GaugeSession(transport));

    var device = service
        .SetStorageModeAsync(GaugeStorageMode.Mirror, 1234)
        .GetAwaiter()
        .GetResult();

    AssertEqual(1, settingWrites);
    AssertEqual((byte)GaugeStorageMode.Mirror, device.MemoryMode);
}

static void MemoryGaugeFileRecordParsesAndValidatesCrc()
{
    var bytes = new byte[MemoryGaugeFileRecord.Length];
    bytes[0] = 0x00;
    bytes[1] = 0x40;
    bytes[2] = 0x00;
    bytes[3] = 0x00;
    bytes[4] = (byte)MemoryGaugeFileRecordType.Start;
    bytes[5] = 0x3C;
    bytes[6] = 0x00;
    bytes[8] = 0x12;
    bytes[15] = Crc8.Compute(bytes.AsSpan(0, 15));

    var record = MemoryGaugeFileRecord.Parse(7, bytes);

    AssertEqual(7, record.Index);
    AssertEqual((uint)0x00004000, record.DataAddress.Value);
    AssertEqual(MemoryGaugeFileRecordType.Start, record.RecordType);
    AssertEqual((ushort)60, record.MeasurementInterval);
    AssertEqual((byte)0x12, record.ResetCause);
    AssertEqual(true, record.IsCrcValid);
}

static void MemoryGaugeFileTableIgnoresContinuationRecords()
{
    var table = Enumerable.Repeat((byte)0xFF, MemoryGaugeFileRecord.Length * 4).ToArray();
    WriteFileRecord(table.AsSpan(0, MemoryGaugeFileRecord.Length), 0x00004000, MemoryGaugeFileRecordType.Start);
    WriteFileRecord(table.AsSpan(MemoryGaugeFileRecord.Length, MemoryGaugeFileRecord.Length), 0x00008000, MemoryGaugeFileRecordType.Continue);
    WriteFileRecord(table.AsSpan(MemoryGaugeFileRecord.Length * 2, MemoryGaugeFileRecord.Length), 0x0000C000, MemoryGaugeFileRecordType.Start);

    var records = MemoryGaugeFileRecord.ParseTable(table);

    AssertEqual(2, records.Count);
    AssertEqual(0, records[0].Index);
    AssertEqual((uint)0x00004000, records[0].DataAddress.Value);
    AssertEqual(2, records[1].Index);
    AssertEqual((uint)0x0000C000, records[1].DataAddress.Value);
}

static void MemoryGaugeDataRecordParsesCountsAndCrc()
{
    var bytes = new byte[MemoryGaugeDataRecord.Length];
    bytes[0] = (byte)MemoryGaugeDataRecordType.PressureTemperature;
    bytes[1] = 0x03;
    bytes[2] = 0x02;
    bytes[3] = 0x01;
    bytes[4] = 0x06;
    bytes[5] = 0x05;
    bytes[6] = 0x04;
    bytes[7] = 0x09;
    bytes[8] = 0x08;
    bytes[9] = 0x07;
    bytes[10] = 0x0C;
    bytes[11] = 0x0B;
    bytes[12] = 0x0A;
    bytes[13] = 0x34;
    bytes[14] = 0x12;
    bytes[15] = Crc8.Compute(bytes.AsSpan(0, 15));

    var record = MemoryGaugeDataRecord.Parse(4, 0x4000, bytes);

    AssertEqual((uint)0x010203, record.FirstSample.TemperatureCounts);
    AssertEqual((uint)0x040506, record.FirstSample.PressureCounts);
    AssertEqual((uint)0x070809, record.SecondSample.TemperatureCounts);
    AssertEqual((uint)0x0A0B0C, record.SecondSample.PressureCounts);
    AssertEqual((ushort)0x1234, record.Counter);
    AssertEqual((byte)0, record.BatteryStatus);
    AssertEqual(true, record.IsCrcValid);
}

static void MemoryGaugeDataRecordsPreserveIncrementalIndexes()
{
    var bytes = new byte[MemoryGaugeDataRecord.Length * 2];
    for (var offset = 0; offset < bytes.Length; offset += MemoryGaugeDataRecord.Length)
    {
        bytes[offset] = (byte)MemoryGaugeDataRecordType.PressureTemperature;
        bytes[offset + 15] = Crc8.Compute(bytes.AsSpan(offset, 15));
    }

    var records = MemoryGaugeDataRecord.ParseMany(0x4020, bytes, firstRecordIndex: 2);

    AssertEqual(2, records.Count);
    AssertEqual(2, records[0].Index);
    AssertEqual(4, records[0].FirstSample.SampleIndex);
    AssertEqual((uint)0x4020, records[0].Address);
    AssertEqual(3, records[1].Index);
    AssertEqual(7, records[1].SecondSample.SampleIndex);
    AssertEqual((uint)0x4030, records[1].Address);
}

static void AcousticRecordsAreExcludedFromPressureTemperatureConversion()
{
    var bytes = new byte[MemoryGaugeDataRecord.Length * 6];
    WriteDataRecord(bytes.AsSpan(0, MemoryGaugeDataRecord.Length), MemoryGaugeDataRecordType.PressureTemperature, 1000, 1001);
    WriteDataRecord(bytes.AsSpan(MemoryGaugeDataRecord.Length, MemoryGaugeDataRecord.Length), MemoryGaugeDataRecordType.AcousticSent, 0, 0);
    WriteDataRecord(bytes.AsSpan(MemoryGaugeDataRecord.Length * 2, MemoryGaugeDataRecord.Length), MemoryGaugeDataRecordType.AcousticReceiveFailed, 0, 0);
    WriteDataRecord(bytes.AsSpan(MemoryGaugeDataRecord.Length * 3, MemoryGaugeDataRecord.Length), MemoryGaugeDataRecordType.AcousticBitCountsLow, 0, 0);
    WriteDataRecord(bytes.AsSpan(MemoryGaugeDataRecord.Length * 4, MemoryGaugeDataRecord.Length), MemoryGaugeDataRecordType.AcousticAdc, 0, 0);
    WriteDataRecord(bytes.AsSpan(MemoryGaugeDataRecord.Length * 5, MemoryGaugeDataRecord.Length), MemoryGaugeDataRecordType.PressureTemperature, 1002, 1003);

    var summary = MemoryGaugeRecordSummary.Analyze(bytes, 0x4000);
    AssertEqual(6, summary.TotalRecordCount);
    AssertEqual(2, summary.PressureTemperatureRecordCount);
    AssertEqual(2, summary.AcousticRecordCount);
    AssertEqual(1, summary.FailedAcousticRecordCount);
    AssertEqual(1, summary.AcousticDiagnosticRecordCount);
    AssertEqual(1, summary.RawAcousticRecordCount);
    AssertEqual(4, summary.ExcludedRecordCount);
    AssertEqual(0, summary.CrcErrorCount);

    var converter = new GaugeSampleConverter(0x4000, 3, BuildFlatCalibrationBundle());
    var samples = converter.Convert(bytes);
    AssertEqual(4, samples.Count);
    AssertEqual(0, samples[0].Sequence);
    AssertEqual(1, samples[1].Sequence);
    AssertEqual((uint)6, samples[2].Timestamp);
    AssertEqual((uint)9, samples[3].Timestamp);
    AssertEqual((uint)0x4050, samples[2].Address);

    var firstBatch = converter.Convert(bytes.AsSpan(0, MemoryGaugeDataRecord.Length * 2), 0, 0);
    var secondBatch = converter.Convert(bytes.AsSpan(MemoryGaugeDataRecord.Length * 2), 2, firstBatch.Count);
    AssertEqual(2, firstBatch.Count);
    AssertEqual(2, secondBatch.Count);
    AssertEqual(2, secondBatch[0].Sequence);
    AssertEqual((uint)6, secondBatch[0].Timestamp);
}

static SensorCalibrationBundle BuildFlatCalibrationBundle()
{
    double[][] pressureRows =
    [
        [1, 2],
        [1, 2],
        [0, 0, 0, 0, 0],
        [0, 0, 0, 0, 0],
        [0, 0, 0, 0, 0],
        [0, 0, 0, 0, 0],
        [0, 0, 0, 0, 0]
    ];
    double[][] temperatureRows =
    [
        [1, 2],
        [0]
    ];

    return new SensorCalibrationBundle(
        [],
        "S: RefClk 0 Id 1 Bias 0 PStartupMs 0 PLLClk 1000\r\n=\r\n"u8.ToArray(),
        BuildSensorCoefficientPayload(pressureRows),
        BuildSensorCoefficientPayload(temperatureRows));
}

static byte[] BuildSensorCoefficientPayload(IReadOnlyList<IReadOnlyList<double>> rows)
{
    var text = string.Join("\r\n", rows.Select(row =>
        string.Join(',', row.Select(value => BitConverter.DoubleToInt64Bits(value).ToString("X16"))))) + "\r\n=\r\n";
    return Encoding.ASCII.GetBytes(text);
}

static void WriteDataRecord(Span<byte> bytes, MemoryGaugeDataRecordType type, uint firstCounts, uint secondCounts)
{
    bytes.Clear();
    bytes[0] = (byte)type;
    WriteUInt24LittleEndian(bytes[1..4], firstCounts);
    WriteUInt24LittleEndian(bytes[4..7], firstCounts);
    WriteUInt24LittleEndian(bytes[7..10], secondCounts);
    WriteUInt24LittleEndian(bytes[10..13], secondCounts);
    bytes[15] = Crc8.Compute(bytes[..15]);
}

static void WriteUInt24LittleEndian(Span<byte> bytes, uint value)
{
    bytes[0] = (byte)value;
    bytes[1] = (byte)(value >> 8);
    bytes[2] = (byte)(value >> 16);
}

static void WriteFileRecord(Span<byte> bytes, uint address, MemoryGaugeFileRecordType type)
{
    WriteUInt32LittleEndian(bytes[..4], address);
    bytes[4] = (byte)type;
    bytes[5] = 0x01;
    bytes[6] = 0x00;
    bytes[8] = 0x12;
    bytes[15] = Crc8.Compute(bytes[..15]);
}

static void SensorHexDoubleCoefficientsParse()
{
    var payload = "410FFE325BB968AB,411012D943EFA2F7\r\n=\r\n"u8.ToArray();
    var rows = SensorAsciiData.ParseHexDoubleRows(payload);

    AssertEqual(1, rows.Count);
    AssertEqual(2, rows[0].Count);
    AssertNear(262086.294787233, rows[0][0], 0.000000001);
    AssertNear(263350.316343829, rows[0][1], 0.000000001);
}

static void SensorCalibrationHeaderParsesFields()
{
    var payload = "S: RefClk .0 Id 1777 Bias 12053700 PStartupMs 5000 PLLClk 169750000\r\n=\r\n"u8.ToArray();
    var header = SensorCalibrationHeader.Parse(payload);

    AssertEqual(0.0, header.ReferenceClock);
    AssertEqual(1777, header.SensorId);
    AssertEqual((uint)12053700, header.CountBias);
    AssertEqual(5000, header.PressureStartupMilliseconds);
    AssertEqual((uint)169750000, header.PllClock);
}

static void QuartzCalibrationEvaluatesLiveGaugeMeasurement()
{
    var calibration = BuildLiveGaugeCalibration();

    var temperature = calibration.TemperatureCelsiusFromCounts(16964453);
    var pressure = calibration.PressurePsiFromCounts(16995857, 16964453);

    AssertNear(28.36388855138488, temperature, 0.0000001);
    AssertNear(16.22890203894386, pressure, 0.0000001);
}

static void QuartzCalibrationConvertsCountsToFrequencies()
{
    var calibration = BuildLiveGaugeCalibration();

    AssertNear(49938.64092878635, calibration.PressureFrequencyHz(16995857), 0.000000001);
    AssertNear(262162.88848216913, calibration.TemperatureFrequencyHz(16964453), 0.000000001);
}

static void SensorLivePayloadsDecode()
{
    var statusPayload = new byte[SensorLiveStatus.PayloadLength];
    statusPayload[0] = 1;
    statusPayload[1] = (byte)SensorLiveState.Running;
    statusPayload[2] = (byte)(
        SensorLiveStatusFlags.DataReady |
        SensorLiveStatusFlags.SensorInitialised |
        SensorLiveStatusFlags.CalibrationAvailable);
    BinaryPrimitives.WriteUInt32LittleEndian(statusPayload.AsSpan(4), 7);
    var status = SensorLiveStatus.Parse(statusPayload);
    AssertEqual(true, status.DataReady);
    AssertEqual((uint)7, status.LatestSequence);

    var samplePayload = BuildSensorLiveSamplePayload(
        sequence: 7,
        ticks: 123,
        pressureRaw: 4_942_157,
        temperatureRaw: 4_910_753);
    var sample = SensorLiveSample.Parse(samplePayload);
    AssertEqual((uint)7, sample.Sequence);
    AssertEqual((uint)123, sample.MonotonicTicks);
    AssertEqual((uint)4_942_157, sample.PressureRaw);
    AssertEqual((uint)4_910_753, sample.TemperatureRaw);
}

static void SensorLiveReadsCalibrationWithoutInitialise()
{
    var commands = new List<GaugeCommand>();
    var startInterval = 0;
    var transport = new DelegateGaugeTransport(request =>
    {
        commands.Add(request.Command);
        return request.Command switch
        {
            GaugeCommand.SensorLiveStatus => GaugeFrame.Create(
                request.Command,
                payload: BuildSensorLiveStatusPayload(
                    SensorLiveState.Idle,
                    SensorLiveStatusFlags.CalibrationAvailable)),
            GaugeCommand.ReadSensorSerial => GaugeFrame.Create(
                request.Command,
                payload: "SERIAL\r\n=\r\n"u8),
            GaugeCommand.ReadSensorCalibration => GaugeFrame.Create(
                request.Command,
                payload: "S: RefClk 0 Id 1 Bias 10 PStartupMs 0 PLLClk 1000\r\n=\r\n"u8),
            GaugeCommand.ReadSensorPressurePolynomial => GaugeFrame.Create(
                request.Command,
                payload: "PRESSURE\r\n=\r\n"u8),
            GaugeCommand.ReadSensorTemperaturePolynomial => GaugeFrame.Create(
                request.Command,
                payload: "TEMPERATURE\r\n=\r\n"u8),
            GaugeCommand.SensorLiveStart => AcceptSensorLiveStart(request),
            GaugeCommand.SensorLiveStop => GaugeFrame.Create(request.Command, payload: [0x01]),
            _ => throw new InvalidOperationException(
                $"Unexpected Sensor Live command {request.Command}.")
        };
    });
    var service = new SensorLiveService(new GaugeSession(transport));

    var probe = service.ProbeAsync().GetAwaiter().GetResult();
    var calibration = service.ReadCalibrationAsync().GetAwaiter().GetResult();
    var started = service.StartAsync().GetAwaiter().GetResult();
    service.StopAsync().GetAwaiter().GetResult();

    AssertEqual(SensorLiveState.Idle, probe!.State);
    AssertEqual(true, calibration.SensorSerial.Length > 0);
    AssertEqual(SensorLiveState.Running, started.State);
    AssertEqual(1, startInterval);
    AssertEqual(false, commands.Contains(GaugeCommand.InitialiseSensor));

    GaugeFrame AcceptSensorLiveStart(GaugeFrame request)
    {
        startInterval = BinaryPrimitives.ReadUInt16LittleEndian(request.Payload);
        return GaugeFrame.Create(
            request.Command,
            payload: BuildSensorLiveStatusPayload(
                SensorLiveState.Running,
                SensorLiveStatusFlags.SensorInitialised |
                SensorLiveStatusFlags.CalibrationAvailable));
    }
}

static void SensorLiveRawCountsCalibrate()
{
    var headerBytes = ReadV3Fixture("latest-header-replica-0.bin");
    var header = V3HeaderDecoder.Decode(
        headerBytes.AsSpan(0, 6 * V3PageCodec.PhysicalBytes));
    var decoder = new SensorLiveDecoder(
        V3GaugeJobService.GetCalibrationBundle(header));
    var reading = decoder.Decode(new SensorLiveSample(
        ProtocolVersion: 1,
        QualityFlags: 0,
        SensorIteration: 12,
        Sequence: 1,
        MonotonicTicks: 100,
        PressureRaw: 4_942_157,
        TemperatureRaw: 4_910_753));

    AssertNear(16.22890203894386, reading.Pressure, 0.0000001);
    AssertNear(28.36388855138488, reading.Temperature, 0.0000001);
    AssertEqual(true, reading.IsSensible);
}

static byte[] BuildSensorLiveStatusPayload(
    SensorLiveState state,
    SensorLiveStatusFlags flags,
    byte lastError = 0,
    uint latestSequence = 0)
{
    var payload = new byte[SensorLiveStatus.PayloadLength];
    payload[0] = 1;
    payload[1] = (byte)state;
    payload[2] = (byte)flags;
    payload[3] = lastError;
    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), latestSequence);
    return payload;
}

static byte[] BuildSensorLiveSamplePayload(
    uint sequence,
    uint ticks,
    uint pressureRaw,
    uint temperatureRaw)
{
    var payload = new byte[SensorLiveSample.PayloadLength];
    payload[0] = 1;
    payload[2] = 12;
    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), sequence);
    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), ticks);
    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), pressureRaw);
    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16), temperatureRaw);
    return payload;
}

static QuartzCalibration BuildLiveGaugeCalibration()
{
    double[][] pressure =
    [
        [46324.44450667226, 49941.02711187358],
        [262086.29478723308, 263350.3163438285],
        [5272.866950699565, -172.73869329558318, -25.605078878633538, -11.515525660127002, -8.368828955905649],
        [-5167.581961537286, 91.64409956378267, -28.612436936426217, -1.0941474986489719, 16.1928585184203],
        [-139.30752948484871, -10.57008476037704, -13.411000370798636, 7.0023223259134735, 9.10864285056903],
        [-7.016010393447254, -8.010832833036478, 5.352444473805683, 0.747159883066896, -6.5313647187756345],
        [-2.5255854982739834, 1.031321883485257, 10.161270054912805, -6.371974535669055, -13.044085999053394]
    ];
    double[][] temperature =
    [
        [262086.29478723308, 263350.3163438285],
        [86.8433423264149, 64.4893597572775, -1.8634567922893475, 0.5398946147181602]
    ];

    return new QuartzCalibration(169750000, pressure, temperature);
}

static void CalibratedCsvExporterFormatsRows()
{
    var rows = CalibratedCsvExporter.BuildLines(
    [
        new CalibratedGaugeSample(
            16995857,
            16964453,
            16.22890203894386,
            28.36388855138488,
            0,
            240,
            0x000097B0,
            0,
            262162.88848216913,
            49938.64092878635,
            false,
            false,
            0)
    ]);

    AssertEqual(CalibratedCsvExporter.Header, rows[0]);
    AssertEqual(
        "16995857,16964453,16.228902038943861,28.363888551384878,0,240,38832,0,262162.88848216913,49938.64092878635,0,0,0",
        rows[1]);
}

static void LegacyRecordExporterWritesAsciiFormat()
{
    var metadata = new LegacyRecordMetadata(
        new DateTime(2026, 7, 12, 17, 16, 8),
        "Northstar 4000AH Quartz Transducer",
        100230,
        3807522001,
        0,
        2,
        "XHTI-7-1000153",
        "2022-03-05T00:06:52");
    CalibratedGaugeSample[] samples =
    [
        new CalibratedGaugeSample(
            16995857,
            16964453,
            16.22890203894386,
            28.36388855138488,
            0,
            240,
            0x000097B0,
            0,
            262162.88848216913,
            49938.64092878635,
            false,
            false,
            0)
    ];

    using var output = new MemoryStream();
    LegacyRecordExporter.Write(output, metadata, samples);
    var bytes = output.ToArray();
    if (bytes.Any(value => value > 0x7F))
    {
        throw new InvalidOperationException("Legacy record output contains non-ASCII bytes.");
    }

    var text = Encoding.ASCII.GetString(bytes);
    if (!text.Contains("\r\n", StringComparison.Ordinal) || text.Contains('\uFEFF'))
    {
        throw new InvalidOperationException("Legacy record output must use CRLF without a BOM.");
    }

    var lines = text.Split("\r\n", StringSplitOptions.None);
    AssertEqual("Start of Job: 2026/07/12 17:16:08", lines[0]);
    AssertEqual("Device Type: Northstar 4000AH Quartz Transducer", lines[2]);
    AssertEqual("Firmware Version: 2.0", lines[5]);
    AssertEqual(LegacyRecordExporter.Header, lines[10]);
    AssertEqual(
        "16995857\t16964453\t16.228902\t28.363889\t     0\t   240\t38832\t0.00000000\t262162.888482\t0\t0\t0",
        lines[11]);
}

static void CommunicationSessionSummaryCountsIntegrityEvents()
{
    var log = new BoundedCommunicationEventLog();
    var timestamp = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
    log.StartSession("COM5", timestamp);
    var repeated = new SerialGaugeTransportEvent(
        timestamp,
        SerialGaugeTransportEventKind.Retry,
        "COM5",
        57600,
        GaugeCommand.Identify,
        1,
        3,
        SerialGaugeTransportFailureKind.Timeout,
        nameof(TimeoutException),
        "Timed out");

    log.Record(repeated);
    log.Record(repeated with { TimestampUtc = timestamp.AddSeconds(1) });
    var coalesced = log.Snapshot();
    AssertEqual(1, coalesced.Count);
    AssertEqual(2, coalesced[0].Occurrences);
    AssertEqual(timestamp.AddSeconds(1), coalesced[0].LastTimestampUtc);

    log.Record(repeated with
    {
        TimestampUtc = timestamp.AddSeconds(2),
        Kind = SerialGaugeTransportEventKind.Recovered,
        Attempt = 3
    });
    log.Record(repeated with
    {
        TimestampUtc = timestamp.AddSeconds(2),
        Kind = SerialGaugeTransportEventKind.Succeeded,
        Attempt = 3,
        FailureKind = null,
        ErrorType = null,
        Message = null
    });
    log.Record(repeated with
    {
        TimestampUtc = timestamp.AddSeconds(3),
        Kind = SerialGaugeTransportEventKind.Failed,
        Attempt = 3,
        FailureKind = SerialGaugeTransportFailureKind.Crc,
        ErrorType = nameof(GaugeProtocolException),
        Message = "Frame CRC16 check failed."
    });

    var summary = log.Summary();
    AssertEqual(true, summary.IsActive);
    AssertEqual(1, summary.Transactions);
    AssertEqual(2, summary.RetryAttempts);
    AssertEqual(2, summary.TimeoutErrors);
    AssertEqual(1, summary.CrcErrors);
    AssertEqual(1, summary.RecoveredTransactions);
    AssertEqual(1, summary.FailedTransactions);
    AssertEqual("Crc", summary.LastIssue?.FailureKind);

    log.EndSession(timestamp.AddSeconds(4));
    AssertEqual(false, log.Summary().IsActive);
    log.Record(repeated with { TimestampUtc = timestamp.AddSeconds(5) });
    AssertEqual(2, log.Summary().RetryAttempts);

    log.StartSession("COM5", timestamp.AddMinutes(1));

    for (var index = 0; index < 110; index++)
    {
        log.Record(repeated with
        {
            TimestampUtc = timestamp.AddMinutes(index + 1),
            Message = $"Failure {index}"
        });
    }

    AssertEqual(100, log.Snapshot().Count);
}

static void V3CapabilitiesAndFallbackWork()
{
    var payload = CreateV3CapabilitiesPayload();

    var capabilities = V3Capabilities.Parse(payload);
    AssertEqual((ushort)256, capabilities.PageBytes);
    AssertEqual((ushort)792, capabilities.MaximumSerialPayload);

    var v3Session = new GaugeSession(new DelegateGaugeTransport(
        request => new GaugeFrame(request.Command, 32, 0, payload)));
    AssertEqual((byte)3, V3Capabilities.StorageMajor);
    AssertEqual(true, v3Session.ProbeV3CapabilitiesAsync().GetAwaiter().GetResult() is not null);

    var v2Session = new GaugeSession(new DelegateGaugeTransport(
        request => new GaugeFrame(request.Command, 1, 0, [0xFF])));
    AssertEqual<V3Capabilities?>(null, v2Session.ProbeV3CapabilitiesAsync().GetAwaiter().GetResult());
}

static void ExternalEraseProgressPayloadValidates()
{
    var busy = ExternalEraseStatus.Parse(BuildEraseProgressPayload(
        ExternalEraseState.Busy,
        completed: 17,
        busyMask: 3,
        address: 0x00110000));
    AssertEqual((ushort)17, busy.Completed);
    AssertEqual((ushort)512, busy.Total);
    AssertEqual((byte)3, busy.BusyMask);

    var complete = ExternalEraseStatus.Parse(BuildEraseProgressPayload(
        ExternalEraseState.Complete,
        completed: 512,
        address: 0x02000000));
    AssertEqual(100.0, complete.Percent);

    var badVersion = BuildEraseProgressPayload(ExternalEraseState.Busy, 0);
    badVersion[0] = 2;
    AssertGaugeProtocol(() => ExternalEraseStatus.Parse(badVersion));

    var badMask = BuildEraseProgressPayload(ExternalEraseState.Busy, 0);
    badMask[2] = 0x80;
    AssertGaugeProtocol(() => ExternalEraseStatus.Parse(badMask));

    var badCount = BuildEraseProgressPayload(ExternalEraseState.Complete, 512);
    badCount[4] = 1;
    AssertGaugeProtocol(() => ExternalEraseStatus.Parse(badCount));
}

static void ProgressEraseReportsEveryPair()
{
    ushort completed = 0;
    var commands = new List<GaugeCommand>();
    var updates = new List<ExternalEraseProgress>();
    var transport = new DelegateGaugeTransport(request =>
    {
        commands.Add(request.Command);
        if (request.Command == GaugeCommand.StartProgressErase)
        {
            return GaugeFrame.Create(
                request.Command,
                payload: BuildEraseProgressPayload(
                    ExternalEraseState.Busy,
                    completed,
                    busyMask: 3));
        }

        if (request.Command == GaugeCommand.GetEraseProgress)
        {
            completed++;
            var state = completed == 512
                ? ExternalEraseState.Complete
                : ExternalEraseState.Busy;
            return GaugeFrame.Create(
                request.Command,
                payload: BuildEraseProgressPayload(
                    state,
                    completed,
                    busyMask: state == ExternalEraseState.Busy ? (byte)3 : (byte)0,
                    address: (uint)completed * 0x10000));
        }
        if (request.Command == GaugeCommand.EndMemoryErase)
        {
            return GaugeFrame.Create(request.Command, payload: [0x01]);
        }
        if (request.Command == GaugeCommand.Identify)
        {
            return GaugeFrame.Create(
                request.Command,
                payload: BuildMemoryIdentityPayload(eraseStatus: 0));
        }

        throw new InvalidOperationException($"Unexpected erase command {request.Command}.");
    });

    var result = new ExternalMemoryEraseService(new GaugeSession(transport))
        .EraseAsync(
            new InlineProgress<ExternalEraseProgress>(updates.Add),
            pollInterval: TimeSpan.Zero)
        .GetAwaiter()
        .GetResult();

    AssertEqual(ExternalEraseMode.Progress, result.Mode);
    AssertEqual((ushort)512, result.Completed);
    AssertEqual(513, updates.Count);
    AssertEqual(0.0, updates[0].Percent);
    AssertEqual(100.0, updates[^1].Percent);
    AssertEqual(1, commands.Count(command => command == GaugeCommand.EndMemoryErase));
    AssertEqual(1, commands.Count(command => command == GaugeCommand.Identify));
    AssertEqual((byte)54, (byte)GaugeCommand.SetTxInterval);
    AssertEqual((byte)63, (byte)GaugeCommand.StartSensorMeasurement);
}

static void ProgressEraseStopsOnLostResponse()
{
    var progressPolls = 0;
    var transport = new DelegateGaugeTransport(request =>
    {
        return request.Command switch
        {
            GaugeCommand.StartProgressErase => GaugeFrame.Create(
                request.Command,
                payload: BuildEraseProgressPayload(
                    ExternalEraseState.Busy,
                    completed: 0,
                    busyMask: 3)),
            GaugeCommand.GetEraseProgress => LoseProgressReply(),
            _ => throw new InvalidOperationException(
                $"Unexpected erase command {request.Command}.")
        };
    });

    var timedOut = false;
    try
    {
        new ExternalMemoryEraseService(new GaugeSession(transport))
            .EraseAsync(pollInterval: TimeSpan.Zero)
            .GetAwaiter()
            .GetResult();
    }
    catch (TimeoutException)
    {
        timedOut = true;
    }

    AssertEqual(true, timedOut);
    AssertEqual(1, progressPolls);

    GaugeFrame LoseProgressReply()
    {
        progressPolls++;
        throw new TimeoutException("Simulated disconnected gauge.");
    }
}

static void ProgressEraseFallsBackToLegacyV2()
{
    var memoryPolls = 0;
    var commands = new List<GaugeCommand>();
    var updates = new List<ExternalEraseProgress>();
    var transport = new DelegateGaugeTransport(request =>
    {
        commands.Add(request.Command);
        return request.Command switch
        {
            GaugeCommand.StartProgressErase => GaugeFrame.Create(request.Command, payload: [0xFF]),
            GaugeCommand.EraseExternalMemory => GaugeFrame.Create(request.Command, payload: [0x01]),
            GaugeCommand.MemoryStatus => GaugeFrame.Create(
                request.Command,
                payload: [++memoryPolls < 2 ? (byte)0x03 : (byte)0x00]),
            GaugeCommand.EndMemoryErase => GaugeFrame.Create(request.Command, payload: [0x01]),
            GaugeCommand.Identify => GaugeFrame.Create(
                request.Command,
                payload: BuildMemoryIdentityPayload(eraseStatus: 0)),
            _ => throw new InvalidOperationException($"Unexpected legacy erase command {request.Command}.")
        };
    });

    var result = new ExternalMemoryEraseService(new GaugeSession(transport))
        .EraseAsync(
            new InlineProgress<ExternalEraseProgress>(updates.Add),
            pollInterval: TimeSpan.Zero)
        .GetAwaiter()
        .GetResult();

    AssertEqual(ExternalEraseMode.LegacyEstimated, result.Mode);
    AssertEqual(true, updates.All(update => update.IsEstimated));
    AssertEqual(100.0, updates[^1].Percent);
    AssertEqual(1, commands.Count(command => command == GaugeCommand.EraseExternalMemory));
    AssertEqual(1, commands.Count(command => command == GaugeCommand.EndMemoryErase));
}

static void LegacyEraseRequiresClearedInterlock()
{
    var transport = new DelegateGaugeTransport(request =>
    {
        return request.Command switch
        {
            GaugeCommand.StartProgressErase => GaugeFrame.Create(request.Command, payload: [0xFF]),
            GaugeCommand.EraseExternalMemory => GaugeFrame.Create(request.Command, payload: [0x01]),
            GaugeCommand.MemoryStatus => GaugeFrame.Create(request.Command, payload: [0x00]),
            GaugeCommand.EndMemoryErase => GaugeFrame.Create(request.Command, payload: [0x01]),
            GaugeCommand.Identify => GaugeFrame.Create(
                request.Command,
                payload: BuildMemoryIdentityPayload(eraseStatus: 1)),
            _ => throw new InvalidOperationException(
                $"Unexpected legacy erase command {request.Command}.")
        };
    });

    var rejected = false;
    try
    {
        new ExternalMemoryEraseService(new GaugeSession(transport))
            .EraseAsync(pollInterval: TimeSpan.Zero)
            .GetAwaiter()
            .GetResult();
    }
    catch (InvalidDataException)
    {
        rejected = true;
    }

    AssertEqual(true, rejected);
}

static void IncompleteEraseRestartBeginsFresh()
{
    var existingBusyPolls = 0;
    var restartIssued = false;
    var progressPolls = 0;
    var commands = new List<GaugeCommand>();
    var updates = new List<ExternalEraseProgress>();
    var transport = new DelegateGaugeTransport(request =>
    {
        commands.Add(request.Command);
        return request.Command switch
        {
            GaugeCommand.MemoryStatus when !restartIssued => GaugeFrame.Create(
                request.Command,
                payload: [++existingBusyPolls < 3 ? (byte)0x03 : (byte)0x00]),
            GaugeCommand.ResetDevice when existingBusyPolls >= 3 =>
                AcceptRestart(request.Command),
            GaugeCommand.StartProgressErase when restartIssued => GaugeFrame.Create(
                request.Command,
                payload: BuildEraseProgressPayload(
                    ExternalEraseState.Busy,
                    completed: 0,
                    busyMask: 3)),
            GaugeCommand.GetEraseProgress when restartIssued => GaugeFrame.Create(
                request.Command,
                payload: BuildEraseProgressPayload(
                    ExternalEraseState.Complete,
                    completed: CompleteProgressPoll(),
                    address: 0x02000000)),
            GaugeCommand.EndMemoryErase => GaugeFrame.Create(request.Command, payload: [0x01]),
            GaugeCommand.Identify => GaugeFrame.Create(
                request.Command,
                payload: BuildMemoryIdentityPayload(eraseStatus: 0)),
            _ => throw new InvalidOperationException(
                $"Unexpected restart command {request.Command}.")
        };
    });

    var service = new ExternalMemoryEraseService(new GaugeSession(transport));
    service.PrepareRestartFromBeginningAsync(pollInterval: TimeSpan.Zero)
        .GetAwaiter()
        .GetResult();
    var result = service.EraseAsync(
        new InlineProgress<ExternalEraseProgress>(updates.Add),
        pollInterval: TimeSpan.Zero)
        .GetAwaiter()
        .GetResult();

    AssertEqual(ExternalEraseMode.Progress, result.Mode);
    AssertEqual(true, restartIssued);
    AssertEqual(1, commands.Count(command => command == GaugeCommand.StartProgressErase));
    AssertEqual(0, commands.Count(command => command == GaugeCommand.EraseExternalMemory));
    AssertEqual(1, commands.Count(command => command == GaugeCommand.EndMemoryErase));
    AssertEqual(1, commands.Count(command => command == GaugeCommand.ResetDevice));
    AssertEqual(100.0, updates[^1].Percent);

    GaugeFrame AcceptRestart(GaugeCommand command)
    {
        restartIssued = true;
        return GaugeFrame.Create(command, payload: [0x01]);
    }

    ushort CompleteProgressPoll()
    {
        progressPolls++;
        return 512;
    }
}

static byte[] BuildMemoryIdentityPayload(
    byte eraseStatus,
    ushort measurementInterval = 5,
    byte memoryMode = 1)
{
    var payload = new byte[22];
    payload[0] = 3;
    payload[1] = 0;
    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2, 4), 100230);
    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(6, 4), 1234);
    BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(18, 2), measurementInterval);
    payload[20] = memoryMode;
    payload[21] = eraseStatus;
    return payload;
}

static byte[] BuildEraseProgressPayload(
    ExternalEraseState state,
    ushort completed,
    byte busyMask = 0,
    byte errorMask = 0,
    uint address = 0)
{
    var payload = new byte[ExternalEraseStatus.PayloadLength];
    payload[0] = 1;
    payload[1] = (byte)state;
    payload[2] = busyMask;
    payload[3] = errorMask;
    payload[4] = (byte)completed;
    payload[5] = (byte)(completed >> 8);
    payload[6] = 0;
    payload[7] = 2;
    WriteUInt32LittleEndian(payload.AsSpan(8, 4), address);
    return payload;
}

static void V3CatalogDiscoveryUsesPrimaryRawReadsOnly()
{
    var commands = new List<GaugeFrame>();
    var capabilitiesPayload = CreateV3CapabilitiesPayload();
    var transport = new DelegateGaugeTransport(request =>
    {
        commands.Add(request);
        if (request.Command == GaugeCommand.V3Capabilities)
        {
            return GaugeFrame.Create(request.Command, 0, capabilitiesPayload);
        }

        if (request.Command == GaugeCommand.V3CatalogSummary)
        {
            throw new InvalidOperationException("Host must not ask the PIC to recover the V3 catalog.");
        }

        if (request.Command == GaugeCommand.ReadExternalEeprom &&
            request.Address < V3GaugeJobService.ReplicaAddressStride)
        {
            return GaugeFrame.Create(
                request.Command,
                request.Address,
                Enumerable.Repeat((byte)0xFF, request.DataLength).ToArray());
        }

        throw new InvalidOperationException($"Unexpected V3 discovery request {request.Command} at 0x{request.Address:X8}.");
    });

    var catalog = new V3GaugeJobService(new GaugeSession(transport))
        .DiscoverAsync()
        .GetAwaiter()
        .GetResult();

    AssertEqual(0, catalog!.Files.Count);
    AssertEqual(false, catalog.Recovery.Replicas[1].WasInspected);
    AssertEqual(false, commands.Any(request => request.Command == GaugeCommand.V3CatalogSummary));
    AssertEqual(false, commands.Any(request => request.Address >= V3GaugeJobService.ReplicaAddressStride));
}

static void V3DataDownloadUsesLazyMirrorFallback()
{
    var clean = ReadV3Fixture("bch-clean-data.bin");
    var damaged = clean.ToArray();
    for (var bit = 0; bit < 17; bit++)
    {
        damaged[bit / 8] ^= (byte)(0x80 >> (bit % 8));
    }

    var cleanMirrorReads = new List<GaugeFrame>();
    var cleanDownload = CreateV3DataService(clean, clean, cleanMirrorReads)
        .DownloadFileAsync(CreateV3DataCatalog(clean), 0)
        .GetAwaiter()
        .GetResult();
    AssertEqual(0, cleanDownload.MirrorPageReadCount);
    AssertEqual(0, cleanMirrorReads.Count);
    AssertEqual(false, cleanDownload.Pages.Any(page => page.MirrorWasInspected));

    var correctedPrimary = ReadV3Fixture("bch-16-corrected-data.bin");
    var correctedMirrorReads = new List<GaugeFrame>();
    var corrected = CreateV3DataService(correctedPrimary, clean, correctedMirrorReads)
        .DownloadFileAsync(CreateV3DataCatalog(clean), 0)
        .GetAwaiter()
        .GetResult();
    AssertEqual(1, corrected.MirrorPageReadCount);
    AssertEqual(1, correctedMirrorReads.Count);
    AssertEqual(1, corrected.Pages[0].SelectedReplicaId);
    AssertEqual(V3PageStatus.Ok, corrected.Pages[0].Selected.Status);

    var fallbackMirrorReads = new List<GaugeFrame>();
    var recovered = CreateV3DataService(damaged, clean, fallbackMirrorReads)
        .DownloadFileAsync(CreateV3DataCatalog(clean), 0)
        .GetAwaiter()
        .GetResult();
    AssertEqual(1, recovered.MirrorPageReadCount);
    AssertEqual(1, fallbackMirrorReads.Count);
    AssertEqual((uint)0x10000, fallbackMirrorReads[0].Address - V3GaugeJobService.ReplicaAddressStride);
    AssertEqual(1, recovered.Pages[0].SelectedReplicaId);
    AssertEqual(false, recovered.Pages[1].MirrorWasInspected);
}

static void V3DiscoveryResolvesLatestLogicalEnd()
{
    const uint latestFileStart = 98304;
    const uint latestDataStart = latestFileStart + 4096;
    const uint storageEnd = latestDataStart + (3 * 4096);
    var catalogBytes = ReadV3Fixture("catalog-six-files-replica-0.bin");
    var headerBytes = ReadV3Fixture("latest-header-replica-0.bin");
    var dataBytes = ReadV3Fixture("latest-data-replica-0.bin");
    var capabilities = CreateV3CapabilitiesPayload();
    WriteUInt32LittleEndian(capabilities.AsSpan(16, 4), storageEnd);

    var transport = new DelegateGaugeTransport(request =>
    {
        if (request.Command == GaugeCommand.V3Capabilities)
        {
            return GaugeFrame.Create(request.Command, 0, capabilities);
        }

        if (request.Command != GaugeCommand.ReadExternalEeprom)
        {
            throw new InvalidOperationException($"Unexpected V3 command {request.Command}.");
        }

        var logicalAddress = request.Address >= V3GaugeJobService.ReplicaAddressStride
            ? request.Address - V3GaugeJobService.ReplicaAddressStride
            : request.Address;
        var result = Enumerable.Repeat((byte)0xFF, request.DataLength).ToArray();
        CopyFixtureRange(catalogBytes, 0, logicalAddress, result);
        CopyFixtureRange(headerBytes, latestFileStart, logicalAddress, result);
        CopyFixtureRange(dataBytes, latestDataStart, logicalAddress, result);
        return GaugeFrame.Create(request.Command, request.Address, result);
    });

    var service = new V3GaugeJobService(new GaugeSession(transport));
    var catalog = service.DiscoverAsync().GetAwaiter().GetResult()!;
    AssertEqual(1, catalog.Files.Count);
    AssertEqual(5, catalog.RejectedRecords.Count);
    AssertEqual(latestDataStart + 512, catalog.Files[0].DataEnd);
    AssertEqual((uint)512, catalog.Files[0].DataLength);

    var download = service.DownloadFileAsync(catalog, 0).GetAwaiter().GetResult();
    AssertEqual(2, download.Pages.Count);
    AssertEqual(0, download.UncorrectablePageCount);
    AssertEqual(4, download.Samples.Count);
}

static void CopyFixtureRange(
    byte[] source,
    uint sourceAddress,
    uint requestAddress,
    byte[] destination)
{
    var requestEnd = checked(requestAddress + (uint)destination.Length);
    var sourceEnd = checked(sourceAddress + (uint)source.Length);
    var overlapStart = Math.Max(requestAddress, sourceAddress);
    var overlapEnd = Math.Min(requestEnd, sourceEnd);
    if (overlapStart >= overlapEnd)
    {
        return;
    }

    source.AsSpan(
            checked((int)(overlapStart - sourceAddress)),
            checked((int)(overlapEnd - overlapStart)))
        .CopyTo(destination.AsSpan(checked((int)(overlapStart - requestAddress))));
}

static V3GaugeJobService CreateV3DataService(
    byte[] primary,
    byte[] mirror,
    List<GaugeFrame> mirrorReads)
{
    var transport = new DelegateGaugeTransport(request =>
    {
        var isMirror = request.Address >= V3GaugeJobService.ReplicaAddressStride;
        var logicalAddress = isMirror
            ? request.Address - V3GaugeJobService.ReplicaAddressStride
            : request.Address;
        if (isMirror)
        {
            mirrorReads.Add(request);
        }

        var source = isMirror ? mirror : primary;
        var offset = checked((int)(logicalAddress - 0x10000));
        return GaugeFrame.Create(
            request.Command,
            request.Address,
            source.AsSpan(offset, request.DataLength).ToArray());
    });
    return new V3GaugeJobService(new GaugeSession(transport));
}

static V3GaugeCatalog CreateV3DataCatalog(byte[] clean)
{
    const uint fileId = 1211301889;
    var page = V3PageCodec.Decode(clean.AsSpan(0, V3PageCodec.PhysicalBytes));
    var record = new V3CatalogRecord(0, fileId, 0xF000, 1, 1, 0, page);
    var header = new V3FileHeader(fileId, 1, 1, [], [], [], [], [], []);
    var file = new V3GaugeFile(0, record, header, 0x10000, 0x10200, 0x11000, false, 0);
    var replica = new V3CatalogReplica(0, [record], null, true, null);
    return new V3GaugeCatalog(
        new V3Capabilities(
            0,
            V3CapabilityFlags.Mirror | V3CapabilityFlags.Catalog |
            V3CapabilityFlags.Bch | V3CapabilityFlags.IndependentCrc,
            0,
            0x10000,
            0x10000,
            0x20000,
            256,
            4096,
            18,
            16,
            1,
            792),
        new V3CatalogSummary(0, 1, 0, 1, 0, fileId, 0xF000, uint.MaxValue),
        new V3CatalogRecovery(
            [record],
            [replica, new V3CatalogReplica(1, [], null, true, null, WasInspected: false)],
            0,
            false),
        [file],
        []);
}

static byte[] CreateV3CapabilitiesPayload()
{
    var payload = new byte[32];
    payload[0] = 1;
    payload[1] = 3;
    payload[3] = 0x0F;
    WriteUInt32LittleEndian(payload.AsSpan(4, 4), 0);
    WriteUInt32LittleEndian(payload.AsSpan(8, 4), 0x10000);
    WriteUInt32LittleEndian(payload.AsSpan(12, 4), 0x10000);
    WriteUInt32LittleEndian(payload.AsSpan(16, 4), 0x02000000);
    payload[20] = 0;
    payload[21] = 1;
    payload[22] = 0;
    payload[23] = 0x10;
    payload[24] = 18;
    payload[25] = 16;
    payload[26] = 1;
    payload[27] = 0x18;
    payload[28] = 0x03;
    return payload;
}

static void V3BchTargetFixturesDecode()
{
    var clean = ReadV3Fixture("bch-clean-data.bin");
    var corrected = ReadV3Fixture("bch-16-corrected-data.bin");

    var cleanPage = V3DataDecoder.DecodePage(clean.AsSpan(0, 256));
    var correctedPage = V3DataDecoder.DecodePage(corrected.AsSpan(0, 256));
    AssertEqual(V3PageStatus.Ok, cleanPage.Page.Status);
    AssertEqual(V3PageStatus.Corrected, correctedPage.Page.Status);
    AssertEqual(16, correctedPage.Page.CorrectedBitCount);
    AssertEqual((uint)1211301889, correctedPage.FileId);
    AssertEqual((uint)1048576, correctedPage.Samples[0].PressureCounts);
    AssertEqual((uint)2097152, correctedPage.Samples[0].TemperatureCounts);
    AssertSequenceEqual(clean.AsSpan(0, 233), correctedPage.Page.DecodedBytes!.AsSpan(0, 233));

    var second = V3DataDecoder.DecodePage(corrected.AsSpan(256, 256));
    AssertEqual(V3PageStatus.Ok, second.Page.Status);
    AssertEqual((uint)1, second.PageSequence);
    AssertFixtureCsv("bch-clean-data.expected.csv", V3CsvInspector.InspectData(clean));
    AssertFixtureCsv("bch-16-corrected-data.expected.csv", V3CsvInspector.InspectData(corrected));
}

static void V3CatalogTargetFixtureDecodes()
{
    var bytes = ReadV3Fixture("catalog-six-files-replica-0.bin");
    var replica = V3CatalogDecoder.DecodeReplica(0, bytes);
    AssertEqual(true, replica.IsValid);
    AssertEqual(6, replica.Records.Count);
    AssertEqual((uint)0, replica.Records[0].CatalogSequence);
    AssertEqual((uint)1379074054, replica.Records[^1].FileId);
    AssertEqual((uint)98304, replica.Records[^1].FileStart);
    AssertEqual(V3PageStatus.Erased, replica.TerminalPage!.Status);
    AssertFixtureCsv("catalog-six-files.expected.csv", V3CsvInspector.InspectCatalog(bytes));
}

static void V3HeaderTargetFixtureDecodes()
{
    var bytes = ReadV3Fixture("latest-header-replica-0.bin");
    var header = V3HeaderDecoder.Decode(bytes.AsSpan(0, 6 * 256));
    AssertEqual((uint)1379074054, header.FileId);
    AssertEqual(6, header.Pages.Count);
    AssertEqual(774, header.RawHeaderStream.Length);
    AssertEqual(37, header.SensorSerial.Length);
    AssertEqual(69, header.SensorHeader.Length);
    AssertEqual(604, header.PressurePolynomial.Length + header.TemperaturePolynomial.Length);
    AssertFixtureCsv("latest-header.expected.csv", V3CsvInspector.InspectHeader(bytes));
}

static void V3FileLocalHeaderCalibratesSamples()
{
    var headerBytes = ReadV3Fixture("latest-header-replica-0.bin");
    var header = V3HeaderDecoder.Decode(headerBytes.AsSpan(0, 6 * V3PageCodec.PhysicalBytes));
    var rawData = ReadV3Fixture("latest-data-replica-0.bin");
    var dataBytes = rawData.AsSpan(0, 2 * V3PageCodec.PhysicalBytes).ToArray();
    var dataPages = V3DataDecoder.DecodeSequence(dataBytes, header.FileId);
    var record = new V3CatalogRecord(
        5,
        header.FileId,
        98304,
        header.CreationBootId,
        header.MeasurementInterval,
        0,
        V3PageCodec.Decode(ReadV3Fixture("catalog-six-files-replica-0.bin")
            .AsSpan(5 * V3PageCodec.PhysicalBytes, V3PageCodec.PhysicalBytes)));
    var file = new V3GaugeFile(
        0,
        record,
        header,
        98304 + 4096,
        98304 + 4096 + (uint)dataBytes.Length,
        98304 + 8192,
        true,
        0);
    var evidence = dataPages
        .Select((page, index) => new V3ReplicaPageEvidence(
            file.DataStart + (uint)(index * V3PageCodec.PhysicalBytes),
            0,
            page.Page,
            page.Page,
            null,
            false))
        .ToArray();
    var download = new V3GaugeDownload(
        file,
        file.DataStart + (uint)dataBytes.Length,
        dataBytes,
        Enumerable.Repeat((byte)0xFF, dataBytes.Length).ToArray(),
        evidence,
        dataPages,
        true,
        [],
        []);

    var samples = V3GaugeJobService.BuildCalibratedSamples(download);
    var calibrationHeader = SensorCalibrationHeader.Parse(header.SensorHeader);
    AssertEqual(4, samples.Count);
    AssertEqual(
        checked(dataPages[0].Samples[0].PressureCounts + calibrationHeader.CountBias!.Value),
        samples[0].PressureCounts);
    AssertEqual(
        checked(dataPages[0].Samples[0].TemperatureCounts + calibrationHeader.CountBias.Value),
        samples[0].TemperatureCounts);
    AssertEqual((uint)4, samples[^1].Timestamp);
    AssertEqual(false, double.IsNaN(samples[0].Pressure));
    AssertEqual(false, double.IsNaN(samples[0].Temperature));
    AssertEqual(false, samples[0].CrcError);

    var alternateBias = calibrationHeader.CountBias.Value + 1;
    var alternateHeaderText = System.Text.RegularExpressions.Regex.Replace(
        SensorAsciiData.DecodePayload(header.SensorHeader),
        @"Bias\s+\d+",
        $"Bias {alternateBias}");
    var alternateHeader = header with
    {
        SensorHeader = Encoding.ASCII.GetBytes(alternateHeaderText)
    };
    var alternateSamples = V3GaugeJobService.BuildCalibratedSamples(
        download with { File = file with { Header = alternateHeader } });
    AssertEqual(samples[0].PressureCounts + 1, alternateSamples[0].PressureCounts);
    AssertEqual(samples[0].TemperatureCounts + 1, alternateSamples[0].TemperatureCounts);
}

static void V3OpenTargetFileDecodes()
{
    var bytes = ReadV3Fixture("latest-data-replica-0.bin");
    var pages = V3DataDecoder.DecodeSequence(bytes, 1379074054);
    var samples = pages.SelectMany(page => page.Samples).ToArray();
    AssertEqual(2, pages.Count);
    AssertEqual(4, samples.Length);
    AssertEqual((uint)0, samples[0].SampleSequence);
    AssertEqual((uint)3, samples[^1].SampleSequence);
    AssertEqual((uint)4, samples[^1].Timestamp);
    AssertEqual((byte)5, samples[^1].SensorIteration);
    AssertFixtureCsv("latest-data.expected.csv", V3CsvInspector.InspectData(bytes));
}

static void V3MalformedPagesAreRejected()
{
    var clean = ReadV3Fixture("bch-clean-data.bin");
    var padding = clean.AsSpan(0, 256).ToArray();
    padding[224] = 0;
    var crc = Crc64Ecma.Compute(padding.AsSpan(0, 225));
    for (var index = 0; index < 8; index++)
    {
        padding[225 + index] = (byte)(crc >> (index * 8));
    }

    var paddingResult = V3PageCodec.Decode(padding);
    AssertEqual(V3PageStatus.StructuralFailure, paddingResult.Status);

    var overLimit = clean.AsSpan(0, 256).ToArray();
    for (var bit = 0; bit < 17; bit++)
    {
        overLimit[bit / 8] ^= (byte)(0x80 >> (bit % 8));
    }

    var overLimitResult = V3PageCodec.Decode(overLimit);
    AssertEqual(false, overLimitResult.IsAccepted);
}

static void V3RequiredFieldsAndSequencesAreRejected()
{
    var headerBytes = ReadV3Fixture("latest-header-replica-0.bin");
    var header = V3HeaderDecoder.Decode(headerBytes.AsSpan(0, 6 * 256));
    var unknownRequired = header.RawHeaderStream.ToArray();
    unknownRequired[24] = 99;
    unknownRequired[25] = 0;
    WriteUInt32LittleEndian(
        unknownRequired.AsSpan(20, 4),
        Crc32C.Compute(unknownRequired.AsSpan(24)));
    AssertInvalidData(() => V3HeaderDecoder.DecodeStream(unknownRequired));

    var nonMonotonic = ReadV3Fixture("bch-clean-data.bin");
    WriteUInt32LittleEndian(nonMonotonic.AsSpan(256 + 12, 4), 9);
    WriteUInt64LittleEndian(
        nonMonotonic.AsSpan(256 + 225, 8),
        Crc64Ecma.Compute(nonMonotonic.AsSpan(256, 225)));
    AssertInvalidData(() => V3DataDecoder.DecodeSequence(nonMonotonic));

    var unknownMajor = ReadV3Fixture("bch-clean-data.bin").AsSpan(0, 256).ToArray();
    unknownMajor[4] = 4;
    WriteUInt64LittleEndian(
        unknownMajor.AsSpan(225, 8),
        Crc64Ecma.Compute(unknownMajor.AsSpan(0, 225)));
    AssertEqual(V3PageStatus.Unsupported, V3PageCodec.Decode(unknownMajor).Status);
}

static byte[] ReadV3Fixture(string name) =>
    File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "v3_target", name));

static void AssertFixtureCsv(string name, IReadOnlyList<string> actual)
{
    var expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "v3_target", name));
    AssertEqual(string.Join('\n', expected), string.Join('\n', actual));
}

static void AssertInvalidData(Action action)
{
    try
    {
        action();
    }
    catch (InvalidDataException)
    {
        return;
    }

    throw new InvalidOperationException("Expected invalid V3 data to be rejected.");
}

static void AssertGaugeProtocol(Action action)
{
    try
    {
        action();
    }
    catch (GaugeProtocolException)
    {
        return;
    }

    throw new InvalidOperationException("Expected invalid protocol data to be rejected.");
}

static void WriteUInt32LittleEndian(Span<byte> target, uint value)
{
    target[0] = (byte)value;
    target[1] = (byte)(value >> 8);
    target[2] = (byte)(value >> 16);
    target[3] = (byte)(value >> 24);
}

static void WriteUInt64LittleEndian(Span<byte> target, ulong value)
{
    for (var index = 0; index < 8; index++)
    {
        target[index] = (byte)(value >> (index * 8));
    }
}

static string BuildHexRecord(ushort address, byte recordType, ReadOnlySpan<byte> data)
{
    if (data.Length > byte.MaxValue)
    {
        throw new ArgumentOutOfRangeException(nameof(data));
    }

    var record = new byte[data.Length + 5];
    record[0] = (byte)data.Length;
    record[1] = (byte)(address >> 8);
    record[2] = (byte)address;
    record[3] = recordType;
    data.CopyTo(record.AsSpan(4));

    var sum = 0;
    for (var index = 0; index < record.Length - 1; index++)
    {
        sum = (sum + record[index]) & 0xFF;
    }

    record[^1] = (byte)(-sum & 0xFF);
    return $":{Convert.ToHexString(record)}";
}

static uint ParseMutationAddress(string mutation)
{
    return Convert.ToUInt32(mutation[2..], 16);
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}

static void AssertSequenceEqual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
{
    if (!expected.SequenceEqual(actual))
    {
        throw new InvalidOperationException($"Expected {Convert.ToHexString(expected)}, got {Convert.ToHexString(actual)}.");
    }
}

static void AssertNear(double expected, double actual, double tolerance)
{
    if (Math.Abs(expected - actual) > tolerance)
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}

sealed class FakeBootloaderClient : IBootloaderClient
{
    private readonly Dictionary<uint, byte> _flash = [];
    private bool _writeAcknowledgementLost;

    public bool LoseFirstWriteAcknowledgement { get; init; }
    public List<string> Mutations { get; } = [];
    public List<uint> WrittenAddresses { get; } = [];

    public Task<byte[]> ReadFlashAsync(
        uint address,
        ushort length,
        int maximumAttempts = 3,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var data = new byte[length];
        for (var index = 0; index < data.Length; index++)
        {
            data[index] = _flash.GetValueOrDefault(address + (uint)index, (byte)0xFF);
        }

        return Task.FromResult(data);
    }

    public Task EraseFlashRowsOnceAsync(
        uint address,
        ushort rowCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        for (var row = 0; row < rowCount; row++)
        {
            var rowAddress = address + ((uint)row * BootloaderApplicationImage.RowSize);
            Mutations.Add($"E:{rowAddress:X6}");
            for (var index = 0; index < BootloaderApplicationImage.RowSize; index++)
            {
                _flash[rowAddress + (uint)index] = 0xFF;
            }
        }

        return Task.CompletedTask;
    }

    public Task WriteFlashOnceAsync(
        uint address,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Mutations.Add($"W:{address:X6}");
        WrittenAddresses.Add(address);
        for (var index = 0; index < data.Length; index++)
        {
            _flash[address + (uint)index] = data.Span[index];
        }

        if (LoseFirstWriteAcknowledgement && !_writeAcknowledgementLost)
        {
            _writeAcknowledgementLost = true;
            throw new TimeoutException("Simulated lost write acknowledgement.");
        }

        return Task.CompletedTask;
    }
}

sealed class DelegateGaugeTransport : IGaugeTransport
{
    private readonly Func<GaugeFrame, GaugeFrame> _responder;

    public DelegateGaugeTransport(Func<GaugeFrame, GaugeFrame> responder)
    {
        _responder = responder;
    }

    public string Name => "Test transport";

    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<GaugeFrame> TransactAsync(GaugeFrame request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_responder(request));
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _report;

    public InlineProgress(Action<T> report)
    {
        _report = report;
    }

    public void Report(T value)
    {
        _report(value);
    }
}
