using Gauge.Protocol;

var tests = new (string Name, Action Run)[]
{
    ("CRC16 matches firmware IDENTIFY vector", Crc16MatchesIdentifyVector),
    ("CRC16 verifies appended high-low bytes to zero", Crc16VerifiesAppendedBytes),
    ("CRC8 matches firmware-style record vector", Crc8MatchesRecordVector),
    ("IDENTIFY frame encodes expected wire bytes", IdentifyFrameEncodesExpectedWireBytes),
    ("Encoded frame decodes back to original values", EncodedFrameDecodesBack),
    ("Bad CRC is rejected", BadCrcIsRejected),
    ("Memory gauge identify payload decodes", MemoryGaugeIdentifyPayloadDecodes)
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
