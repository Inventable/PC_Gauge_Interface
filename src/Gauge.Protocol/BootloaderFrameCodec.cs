namespace Gauge.Protocol;

public static class BootloaderFrameCodec
{
    public static byte[] EncodeRequest(BootloaderFrame frame)
    {
        var wire = new byte[1 + BootloaderProtocolConstants.HeaderLength + frame.Payload.Length];
        wire[0] = BootloaderProtocolConstants.StartByte;
        WriteHeader(frame, wire.AsSpan(1, BootloaderProtocolConstants.HeaderLength));
        frame.Payload.CopyTo(wire.AsSpan(1 + BootloaderProtocolConstants.HeaderLength));
        return wire;
    }

    public static BootloaderFrame DecodeResponse(ReadOnlySpan<byte> wire, int expectedPayloadLength)
    {
        if (expectedPayloadLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedPayloadLength));
        }

        var expectedLength = 1 + BootloaderProtocolConstants.HeaderLength + expectedPayloadLength;
        if (wire.Length != expectedLength)
        {
            throw new GaugeProtocolException($"Bootloader response length mismatch. Expected {expectedLength}, got {wire.Length}.");
        }

        if (wire[0] != BootloaderProtocolConstants.StartByte)
        {
            throw new GaugeProtocolException("Bootloader response does not start with 0x55.");
        }

        var header = wire.Slice(1, BootloaderProtocolConstants.HeaderLength);
        var address = (uint)(header[5] | (header[6] << 8) | (header[7] << 16));
        return new BootloaderFrame(
            (BootloaderCommand)header[0],
            (ushort)(header[1] | (header[2] << 8)),
            header[3],
            header[4],
            address,
            wire.Slice(1 + BootloaderProtocolConstants.HeaderLength).ToArray());
    }

    private static void WriteHeader(BootloaderFrame frame, Span<byte> header)
    {
        header[0] = (byte)frame.Command;
        header[1] = (byte)frame.DataLength;
        header[2] = (byte)(frame.DataLength >> 8);
        header[3] = frame.Key1;
        header[4] = frame.Key2;
        header[5] = (byte)frame.Address;
        header[6] = (byte)(frame.Address >> 8);
        header[7] = (byte)(frame.Address >> 16);
        header[8] = 0;
    }
}
