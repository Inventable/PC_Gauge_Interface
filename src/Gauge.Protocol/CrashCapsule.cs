using System.Buffers.Binary;

namespace Gauge.Protocol;

public sealed record CrashCapsule(
    byte SchemaVersion,
    byte RawRcon,
    byte ApplicationState,
    byte FaultId,
    uint Generation,
    uint BootId,
    ushort EventId,
    uint FileId,
    uint CommittedSampleCount)
{
    public const int PayloadLength = 32;
    public const byte CurrentSchemaVersion = 1;
    public const byte CommitMarker = 0xA5;

    private static ReadOnlySpan<byte> Magic => "MGCC"u8;

    public string ApplicationStateDisplay => ApplicationState switch
    {
        0 => "service window",
        1 => "start deployment",
        2 => "deployment recording",
        3 => "process PC command",
        4 => "Sensor Live",
        5 => "clock calibration",
        6 => "serial passthrough",
        7 => "safe shutdown",
        _ => $"unknown ({ApplicationState})"
    };

    public static CrashCapsule Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != PayloadLength)
        {
            throw new GaugeProtocolException(
                $"V3 crash capsule returned {payload.Length} byte(s); expected {PayloadLength}.");
        }

        if (!payload[..4].SequenceEqual(Magic))
        {
            throw new GaugeProtocolException("V3 crash capsule magic is invalid.");
        }

        if (payload[4] != CurrentSchemaVersion)
        {
            throw new GaugeProtocolException(
                $"Unsupported V3 crash capsule schema {payload[4]}.");
        }

        if (payload[26] != 0)
        {
            throw new GaugeProtocolException("V3 crash capsule reserved byte is non-zero.");
        }

        if (payload[31] != CommitMarker)
        {
            throw new GaugeProtocolException("V3 crash capsule commit marker is invalid.");
        }

        var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(payload[27..31]);
        var computedCrc = Crc32C.Compute(payload[..27]);
        if (storedCrc != computedCrc)
        {
            throw new GaugeProtocolException(
                $"V3 crash capsule CRC-32C is invalid (stored 0x{storedCrc:X8}, computed 0x{computedCrc:X8}).");
        }

        return new CrashCapsule(
            payload[4],
            payload[5],
            payload[6],
            payload[7],
            BinaryPrimitives.ReadUInt32LittleEndian(payload[8..12]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[12..16]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[16..18]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[18..22]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload[22..26]));
    }
}

public enum CrashCapsuleReadStatus
{
    Available,
    NoLongerAvailable,
    Unsupported
}

public sealed record CrashCapsuleReadResult(
    CrashCapsuleReadStatus Status,
    CrashCapsule? Capsule = null)
{
    public static CrashCapsuleReadResult Available(CrashCapsule capsule) =>
        new(CrashCapsuleReadStatus.Available, capsule);

    public static CrashCapsuleReadResult NoLongerAvailable { get; } =
        new(CrashCapsuleReadStatus.NoLongerAvailable);

    public static CrashCapsuleReadResult Unsupported { get; } =
        new(CrashCapsuleReadStatus.Unsupported);
}
