using System.Buffers.Binary;

namespace Gauge.Protocol;

public enum SensorLiveState : byte
{
    Idle = 0,
    Starting = 1,
    Running = 2,
    Fault = 3
}

[Flags]
public enum SensorLiveStatusFlags : byte
{
    None = 0,
    DataReady = 1 << 0,
    SensorInitialised = 1 << 1,
    CalibrationAvailable = 1 << 2
}

public sealed record SensorLiveStatus(
    byte ProtocolVersion,
    SensorLiveState State,
    SensorLiveStatusFlags Flags,
    byte LastError,
    uint LatestSequence)
{
    public const byte CurrentProtocolVersion = 1;
    public const int PayloadLength = 8;

    public bool DataReady => Flags.HasFlag(SensorLiveStatusFlags.DataReady);

    public static SensorLiveStatus Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != PayloadLength)
        {
            throw new GaugeProtocolException(
                $"Sensor Live status returned {payload.Length} byte(s); expected {PayloadLength}.");
        }
        if (payload[0] != CurrentProtocolVersion)
        {
            throw new GaugeProtocolException(
                $"Unsupported Sensor Live protocol version {payload[0]}.");
        }
        if (!Enum.IsDefined((SensorLiveState)payload[1]))
        {
            throw new GaugeProtocolException($"Unsupported Sensor Live state {payload[1]}.");
        }
        if ((payload[2] & 0xF8) != 0)
        {
            throw new GaugeProtocolException("Sensor Live status contains reserved flag bits.");
        }

        var result = new SensorLiveStatus(
            payload[0],
            (SensorLiveState)payload[1],
            (SensorLiveStatusFlags)payload[2],
            payload[3],
            BinaryPrimitives.ReadUInt32LittleEndian(payload[4..8]));
        if ((result.State == SensorLiveState.Fault && result.LastError == 0) ||
            (result.State != SensorLiveState.Fault && result.LastError != 0) ||
            (result.LatestSequence == 0 && result.DataReady))
        {
            throw new GaugeProtocolException("Sensor Live status contains inconsistent values.");
        }

        return result;
    }
}

public sealed record SensorLiveSample(
    byte ProtocolVersion,
    byte QualityFlags,
    byte SensorIteration,
    uint Sequence,
    uint MonotonicTicks,
    uint PressureRaw,
    uint TemperatureRaw)
{
    public const byte CurrentProtocolVersion = 1;
    public const int PayloadLength = 20;
    public const uint MaximumRawCount = 0x00FFFFFF;
    public const byte KnownQualityFlags = 0x07;

    public static SensorLiveSample Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != PayloadLength)
        {
            throw new GaugeProtocolException(
                $"Sensor Live sample returned {payload.Length} byte(s); expected {PayloadLength}.");
        }
        if (payload[0] != CurrentProtocolVersion)
        {
            throw new GaugeProtocolException(
                $"Unsupported Sensor Live sample protocol version {payload[0]}.");
        }
        if ((payload[1] & ~KnownQualityFlags) != 0 || payload[3] != 0)
        {
            throw new GaugeProtocolException("Sensor Live sample contains reserved bits.");
        }

        var result = new SensorLiveSample(
            payload[0],
            payload[1],
            payload[2],
            BinaryPrimitives.ReadUInt32LittleEndian(payload[4..8]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[8..12]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[12..16]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[16..20]));
        if (result.Sequence == 0 ||
            result.PressureRaw > MaximumRawCount ||
            result.TemperatureRaw > MaximumRawCount)
        {
            throw new GaugeProtocolException("Sensor Live sample contains invalid measurement values.");
        }

        return result;
    }
}
