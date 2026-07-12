namespace Gauge.Protocol;

public static class GaugeFrameCodec
{
    public static byte[] Encode(GaugeFrame frame)
    {
        if (frame.DataLength != frame.Payload.Length)
        {
            throw new ArgumentException("Frame data length does not match payload length.", nameof(frame));
        }

        var bodyLength = GaugeProtocolConstants.HeaderLength + frame.Payload.Length;
        var wire = new byte[1 + bodyLength + GaugeProtocolConstants.CrcLength];
        wire[0] = GaugeProtocolConstants.StartByte;

        WriteBody(frame, wire.AsSpan(1, bodyLength));

        var crc = Crc16.Compute(wire.AsSpan(1, bodyLength));
        wire[1 + bodyLength] = (byte)(crc >> 8);
        wire[1 + bodyLength + 1] = (byte)crc;

        return wire;
    }

    public static GaugeFrame Decode(ReadOnlySpan<byte> wire)
    {
        if (wire.Length < 1 + GaugeProtocolConstants.HeaderLength + GaugeProtocolConstants.CrcLength)
        {
            throw new GaugeProtocolException("Frame is too short.");
        }

        if (wire[0] != GaugeProtocolConstants.StartByte)
        {
            throw new GaugeProtocolException("Frame does not start with the gauge start byte.");
        }

        var body = wire[1..^2];
        var declaredLength = ReadUInt16LittleEndian(body[1], body[2]);
        var expectedBodyLength = GaugeProtocolConstants.HeaderLength + declaredLength;

        if (body.Length != expectedBodyLength)
        {
            throw new GaugeProtocolException($"Frame payload length mismatch. Expected body length {expectedBodyLength}, got {body.Length}.");
        }

        if (Crc16.Compute(wire[1..]) != 0)
        {
            throw new GaugeProtocolException("Frame CRC16 check failed.");
        }

        var address = ReadUInt32LittleEndian(body[3], body[4], body[5], body[6]);
        var payload = body[GaugeProtocolConstants.HeaderLength..].ToArray();

        return new GaugeFrame((GaugeCommand)body[0], declaredLength, address, payload);
    }

    public static bool TryDecode(ReadOnlySpan<byte> wire, out GaugeFrame? frame)
    {
        try
        {
            frame = Decode(wire);
            return true;
        }
        catch (GaugeProtocolException)
        {
            frame = null;
            return false;
        }
    }

    private static void WriteBody(GaugeFrame frame, Span<byte> body)
    {
        body[0] = (byte)frame.Command;
        body[1] = (byte)frame.DataLength;
        body[2] = (byte)(frame.DataLength >> 8);
        body[3] = (byte)frame.Address;
        body[4] = (byte)(frame.Address >> 8);
        body[5] = (byte)(frame.Address >> 16);
        body[6] = (byte)(frame.Address >> 24);
        frame.Payload.CopyTo(body[GaugeProtocolConstants.HeaderLength..]);
    }

    private static ushort ReadUInt16LittleEndian(byte low, byte high)
    {
        return (ushort)(low | (high << 8));
    }

    private static uint ReadUInt32LittleEndian(byte low, byte high, byte upper, byte extended)
    {
        return (uint)(low | (high << 8) | (upper << 16) | (extended << 24));
    }
}

