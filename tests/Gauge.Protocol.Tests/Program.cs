using Gauge.Calibration;
using Gauge.Protocol;

var tests = new (string Name, Action Run)[]
{
    ("CRC16 matches firmware IDENTIFY vector", Crc16MatchesIdentifyVector),
    ("CRC16 verifies appended high-low bytes to zero", Crc16VerifiesAppendedBytes),
    ("CRC8 matches firmware-style record vector", Crc8MatchesRecordVector),
    ("IDENTIFY frame encodes expected wire bytes", IdentifyFrameEncodesExpectedWireBytes),
    ("Read request encodes declared length without request payload", ReadRequestEncodesDeclaredLengthWithoutRequestPayload),
    ("Encoded frame decodes back to original values", EncodedFrameDecodesBack),
    ("Bad CRC is rejected", BadCrcIsRejected),
    ("Memory gauge identify payload decodes", MemoryGaugeIdentifyPayloadDecodes),
    ("Memory gauge file record parses and validates CRC", MemoryGaugeFileRecordParsesAndValidatesCrc),
    ("Memory gauge data record parses counts and CRC", MemoryGaugeDataRecordParsesCountsAndCrc),
    ("Sensor hex double coefficients parse", SensorHexDoubleCoefficientsParse),
    ("Sensor calibration header parses fields", SensorCalibrationHeaderParsesFields),
    ("Quartz calibration evaluates report measurement", QuartzCalibrationEvaluatesReportMeasurement)
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

    AssertEqual((byte)20, device.FirmwareMinor);
    AssertEqual((byte)1, device.FirmwareMajor);
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

static void QuartzCalibrationEvaluatesReportMeasurement()
{
    var calibration = BuildReportCalibration();

    var temperature = calibration.TemperatureCelsiusFromFrequency(262037.3431970949);
    var pressure = calibration.PressurePsiFromFrequency(51290.0241121798, 262037.3431970949);

    AssertNear(14.9819772446, temperature, 0.0000001);
    AssertNear(16.0058758518, pressure, 0.0000001);
}

static QuartzCalibration BuildReportCalibration()
{
    double[][] pressure =
    [
        [50931.11051924353, 51290.02411217977],
        [262037.3100809597, 263385.11767060595],
        [574.2243948142991, -602.9156467214243, -5.643272450565251, -1.8236678268104058, -2.1685962221333286],
        [-107.82114791850289, 4.130031417845537, -6.6463502573076605, 4.103072488854925, 5.138222966532338],
        [-40.17680703198682, -4.110637621995555, -15.546620699523384, 4.731662740872568, 12.799932516583244],
        [-4.391831239917007, 5.0743832568239124, 5.858162386408311, -7.45101882228346, -5.5232144603518885],
        [-8.165749146445576, 3.1283182667300697, 14.331698067632992, -6.26667418065439, -13.922531327671203]
    ];
    double[][] temperature =
    [
        [262037.3100809597, 263385.11767060595],
        [84.39016012320367, 66.62699032132008, -1.9068843212100917, 0.8778991584520575]
    ];

    return new QuartzCalibration(169750000, pressure, temperature);
}

static void WriteUInt32LittleEndian(Span<byte> target, uint value)
{
    target[0] = (byte)value;
    target[1] = (byte)(value >> 8);
    target[2] = (byte)(value >> 16);
    target[3] = (byte)(value >> 24);
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
