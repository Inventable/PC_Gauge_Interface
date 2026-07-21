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
    ("Chunked memory read resumes after retained prefix", ChunkedMemoryReadResumesAfterPrefix),
    ("Bootloader read-version request matches firmware frame", BootloaderReadVersionRequestMatchesFirmwareFrame),
    ("Bootloader write request carries keys and 24-bit address", BootloaderWriteRequestCarriesKeysAndAddress),
    ("Bootloader version response decodes", BootloaderVersionResponseDecodes),
    ("Intel HEX validates checksums and extended addresses", IntelHexValidatesChecksumsAndExtendedAddresses),
    ("Bootloader image rejects application data below offset", BootloaderImageRejectsDataBelowOffset),
    ("Firmware updater erases start first and writes it last", FirmwareUpdaterErasesStartFirstAndWritesItLast),
    ("Memory gauge identify payload decodes", MemoryGaugeIdentifyPayloadDecodes),
    ("Memory gauge file record parses and validates CRC", MemoryGaugeFileRecordParsesAndValidatesCrc),
    ("Memory gauge file table ignores continuation records", MemoryGaugeFileTableIgnoresContinuationRecords),
    ("Memory gauge data record parses counts and CRC", MemoryGaugeDataRecordParsesCountsAndCrc),
    ("Memory gauge data records preserve incremental indexes", MemoryGaugeDataRecordsPreserveIncrementalIndexes),
    ("Acoustic records are classified and excluded from P&T conversion", AcousticRecordsAreExcludedFromPressureTemperatureConversion),
    ("Sensor hex double coefficients parse", SensorHexDoubleCoefficientsParse),
    ("Sensor calibration header parses fields", SensorCalibrationHeaderParsesFields),
    ("Quartz calibration converts counts to frequencies", QuartzCalibrationConvertsCountsToFrequencies),
    ("Quartz calibration evaluates live gauge measurement", QuartzCalibrationEvaluatesLiveGaugeMeasurement),
    ("Calibrated CSV exporter formats rows", CalibratedCsvExporterFormatsRows),
    ("Legacy record exporter writes ASCII format", LegacyRecordExporterWritesAsciiFormat),
    ("Communication session summary counts integrity events", CommunicationSessionSummaryCountsIntegrityEvents)
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

static void WriteUInt32LittleEndian(Span<byte> target, uint value)
{
    target[0] = (byte)value;
    target[1] = (byte)(value >> 8);
    target[2] = (byte)(value >> 16);
    target[3] = (byte)(value >> 24);
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
