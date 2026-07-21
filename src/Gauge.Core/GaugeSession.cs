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
        if (device.DeviceType is not (100200 or 100230))
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
