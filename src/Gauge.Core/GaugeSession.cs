using Gauge.Protocol;
using Gauge.Transport;

namespace Gauge.Core;

public sealed class GaugeSession
{
    private readonly IGaugeTransport _transport;

    public GaugeSession(IGaugeTransport transport)
    {
        _transport = transport;
    }

    public async Task<GaugeFrame> IdentifyAsync(CancellationToken cancellationToken = default)
    {
        var request = GaugeFrame.Create(GaugeCommand.Identify);
        var reply = await _transport.TransactAsync(request, cancellationToken).ConfigureAwait(false);
        if (reply.Payload.Length == 22)
        {
            var device = DeviceData.DecodeMemoryGauge(reply.Payload);
            EnsureSupportedDevice(device);
            return reply;
        }

        if (reply.Payload.Length == 32)
        {
            var device = DeviceData.DecodeAcousticGauge(reply.Payload);
            EnsureSupportedDevice(device);
            return reply;
        }

        throw new GaugeProtocolException(
            $"IDENTIFY returned {reply.Payload.Length} byte(s); expected a complete 22-byte memory or 32-byte acoustic identity.");
    }

    public async Task<GaugeFrame> SendCommandAsync(GaugeCommand command, CancellationToken cancellationToken = default)
    {
        return await _transport
            .TransactAsync(GaugeFrame.Create(command), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<GaugeFrame> SendCommandAsync(
        GaugeCommand command,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        var request = GaugeFrame.Create(command, payload: payload.Span);
        return await _transport.TransactAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<V3Capabilities?> ProbeV3CapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        var reply = await _transport
            .TransactAsync(GaugeFrame.Create(GaugeCommand.V3Capabilities), cancellationToken)
            .ConfigureAwait(false);

        if (reply.Payload is [0xFF])
        {
            return null;
        }

        return V3Capabilities.Parse(reply.Payload);
    }

    public async Task<V3CatalogSummary> ReadV3CatalogSummaryAsync(CancellationToken cancellationToken = default)
    {
        var reply = await _transport
            .TransactAsync(GaugeFrame.Create(GaugeCommand.V3CatalogSummary), cancellationToken)
            .ConfigureAwait(false);
        return V3CatalogSummary.Parse(reply.Payload);
    }

    public async Task<V3DiagnosticStatus?> ReadV3DiagnosticStatusAsync(
        V3Capabilities capabilities,
        CancellationToken cancellationToken = default)
    {
        var reply = await _transport
            .TransactAsync(
                GaugeFrame.Create(GaugeCommand.V3DiagnosticStatus),
                cancellationToken)
            .ConfigureAwait(false);
        if (reply.Payload is [0xFF] && capabilities.IsLegacyLayout)
        {
            return null;
        }

        return V3DiagnosticStatus.Parse(reply.Payload, capabilities);
    }

    public async Task<CrashCapsuleReadResult> ReadV3CrashCapsuleAsync(
        CancellationToken cancellationToken = default)
    {
        var reply = await _transport
            .TransactAsync(
                GaugeFrame.Create(GaugeCommand.V3GetCrashCapsule),
                cancellationToken)
            .ConfigureAwait(false);

        return reply.Payload switch
        {
            [0xFC] => CrashCapsuleReadResult.NoLongerAvailable,
            [0xFF] => CrashCapsuleReadResult.Unsupported,
            _ => CrashCapsuleReadResult.Available(
                CrashCapsule.Parse(reply.Payload))
        };
    }

    public async Task<GaugeMemoryAddress> FindEndOfFileAsync(CancellationToken cancellationToken = default)
    {
        var reply = await _transport
            .TransactAsync(GaugeFrame.Create(GaugeCommand.FindEndOfFile), cancellationToken)
            .ConfigureAwait(false);

        if (reply.Payload.Length != 4)
        {
            throw new GaugeProtocolException($"FIND_EOF returned {reply.Payload.Length} byte(s); expected 4.");
        }

        return GaugeMemoryAddress.FromLittleEndian(reply.Payload);
    }

    public async Task<byte[]> ReadExternalMemoryAsync(
        uint address,
        ushort length,
        GaugeCommand command = GaugeCommand.ReadExternalEeprom,
        CancellationToken cancellationToken = default)
    {
        if (length == 0)
        {
            return [];
        }

        if (command is not (GaugeCommand.ReadExternalEeprom or GaugeCommand.ReadFileSector or GaugeCommand.ReadRecordSector))
        {
            throw new ArgumentOutOfRangeException(nameof(command), command, "Command is not an external memory read command.");
        }

        var requestEnd = checked((ulong)address + length);
        if (command == GaugeCommand.ReadExternalEeprom &&
            address < V3Capabilities.PhysicalChipBoundary &&
            requestEnd > V3Capabilities.PhysicalChipBoundary)
        {
            var firstLength = checked(
                (ushort)(V3Capabilities.PhysicalChipBoundary - address));
            var secondLength = checked((ushort)(length - firstLength));
            var first = await ReadExternalMemoryAsync(
                address,
                firstLength,
                command,
                cancellationToken).ConfigureAwait(false);
            var second = await ReadExternalMemoryAsync(
                V3Capabilities.PhysicalChipBoundary,
                secondLength,
                command,
                cancellationToken).ConfigureAwait(false);
            var combined = new byte[length];
            first.CopyTo(combined, 0);
            second.CopyTo(combined, first.Length);
            return combined;
        }

        var request = GaugeFrame.CreateReadRequest(command, address, length);
        var reply = await _transport.TransactAsync(request, cancellationToken).ConfigureAwait(false);

        if (reply.Payload.Length != length)
        {
            throw new GaugeProtocolException($"Memory read returned {reply.Payload.Length} byte(s); expected {length}.");
        }

        return reply.Payload;
    }

    private static void EnsureSupportedDevice(DeviceData device)
    {
        if (device.DeviceType is not (100160 or 100187 or 100196 or 100200 or 100230))
        {
            throw new GaugeProtocolException($"IDENTIFY returned unsupported device type {device.DeviceType}.");
        }
    }

    public async Task<byte[]> ReadSensorDataAsync(GaugeCommand command, CancellationToken cancellationToken = default)
    {
        if (command is not (GaugeCommand.ReadSensorSerial
            or GaugeCommand.ReadSensorCalibration
            or GaugeCommand.ReadSensorPressurePolynomial
            or GaugeCommand.ReadSensorTemperaturePolynomial))
        {
            throw new ArgumentOutOfRangeException(nameof(command), command, "Command is not a sensor data read command.");
        }

        var reply = await _transport
            .TransactAsync(GaugeFrame.Create(command), cancellationToken)
            .ConfigureAwait(false);

        return reply.Payload;
    }

    public async Task<byte[]> ReadExternalMemoryChunkedAsync(
        uint address,
        int length,
        ushort chunkSize = 1024,
        GaugeCommand command = GaugeCommand.ReadExternalEeprom,
        CancellationToken cancellationToken = default,
        IProgress<MemoryReadProgress>? progress = null,
        ReadOnlyMemory<byte> existingPrefix = default)
    {
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Length cannot be negative.");
        }

        if (chunkSize == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "Chunk size must be greater than zero.");
        }

        if (existingPrefix.Length > length)
        {
            throw new ArgumentException("Existing memory prefix is longer than the requested read.", nameof(existingPrefix));
        }

        var result = new byte[length];
        existingPrefix.CopyTo(result);
        var offset = existingPrefix.Length;
        progress?.Report(new MemoryReadProgress(offset, length, result));

        while (offset < length)
        {
            var bytesThisRead = (ushort)Math.Min(chunkSize, length - offset);
            var chunk = await ReadExternalMemoryAsync(
                address + (uint)offset,
                bytesThisRead,
                command,
                cancellationToken).ConfigureAwait(false);

            chunk.CopyTo(result.AsSpan(offset));
            offset += bytesThisRead;
            progress?.Report(new MemoryReadProgress(offset, length, result));
        }

        return result;
    }
}

public sealed record MemoryReadProgress(
    int BytesRead,
    int TotalBytes,
    ReadOnlyMemory<byte> Buffer = default);
