using System.Buffers.Binary;

namespace Gauge.Protocol;

public enum ExternalEraseState : byte
{
    Idle = 0,
    Busy = 1,
    Complete = 2,
    Error = 3
}

public sealed record ExternalEraseStatus(
    byte ProtocolVersion,
    ExternalEraseState State,
    byte BusyMask,
    byte ErrorMask,
    ushort Completed,
    ushort Total,
    uint Address)
{
    public const int PayloadLength = 12;
    public const ushort ExpectedTotal = 512;
    public const uint MaximumAddress = 0x04000000;

    public double Percent => Total == 0 ? 0 : Completed * 100.0 / Total;

    public static ExternalEraseStatus Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != PayloadLength)
        {
            throw new GaugeProtocolException(
                $"Erase progress returned {payload.Length} byte(s); expected {PayloadLength}.");
        }

        if (payload[0] != 1)
        {
            throw new GaugeProtocolException(
                $"Unsupported erase progress protocol version {payload[0]}.");
        }

        if (!Enum.IsDefined((ExternalEraseState)payload[1]))
        {
            throw new GaugeProtocolException($"Unsupported erase state {payload[1]}.");
        }

        if ((payload[2] & 0xFC) != 0 || (payload[3] & 0xFC) != 0)
        {
            throw new GaugeProtocolException("Erase progress contains reserved mask bits.");
        }

        var result = new ExternalEraseStatus(
            payload[0],
            (ExternalEraseState)payload[1],
            payload[2],
            payload[3],
            BinaryPrimitives.ReadUInt16LittleEndian(payload[4..6]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[6..8]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[8..12]));

        if (result.Total != ExpectedTotal ||
            result.Completed > result.Total ||
            result.Address > MaximumAddress ||
            (result.State == ExternalEraseState.Complete &&
             (result.Completed != result.Total || result.BusyMask != 0 || result.ErrorMask != 0)) ||
            (result.State == ExternalEraseState.Error && result.ErrorMask == 0) ||
            (result.State == ExternalEraseState.Busy && result.Completed >= result.Total))
        {
            throw new GaugeProtocolException("Erase progress contains inconsistent values.");
        }

        return result;
    }
}
