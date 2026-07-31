using System.Buffers.Binary;
using Gauge.Protocol;

namespace Gauge.Core;

public enum GaugeStorageMode : byte
{
    Full = 0,
    Mirror = 1
}

public sealed class GaugeConfigurationService
{
    private const byte CommandSuccess = 0x01;
    private readonly GaugeSession _session;

    public GaugeConfigurationService(GaugeSession session)
    {
        _session = session;
    }

    public async Task<DeviceData> SetMeasurementIntervalAsync(
        ushort seconds,
        uint expectedSerial,
        CancellationToken cancellationToken = default)
    {
        if (seconds == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(seconds),
                "Measurement interval must be at least one second.");
        }

        await RequireExpectedMemoryGaugeAsync(
            expectedSerial,
            requireEmptyEraseInterlock: true,
            cancellationToken).ConfigureAwait(false);

        var payload = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, seconds);
        return await WriteAndVerifyAsync(
            GaugeCommand.SetMeasureRate,
            payload,
            expectedSerial,
            device => device.MeasurementInterval == seconds,
            $"measurement interval {seconds} second(s)",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeviceData> SetStorageModeAsync(
        GaugeStorageMode mode,
        uint expectedSerial,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        await RequireExpectedMemoryGaugeAsync(
            expectedSerial,
            requireEmptyEraseInterlock: true,
            cancellationToken).ConfigureAwait(false);

        var device = await WriteAndVerifyAsync(
            GaugeCommand.SetMemoryMode,
            new byte[] { (byte)mode },
            expectedSerial,
            device => device.MemoryMode == (byte)mode,
            $"storage mode {mode}",
            cancellationToken).ConfigureAwait(false);
        var capabilities = await _session
            .ProbeV3CapabilitiesAsync(cancellationToken)
            .ConfigureAwait(false);
        if (capabilities is not null &&
            capabilities.MemoryMode != (V3MemoryMode)mode)
        {
            throw new InvalidDataException(
                "IDENTIFY accepted the storage mode, but command 73 reports a different V3 layout.");
        }

        return device;
    }

    private async Task<DeviceData> WriteAndVerifyAsync(
        GaugeCommand command,
        ReadOnlyMemory<byte> payload,
        uint expectedSerial,
        Func<DeviceData, bool> isApplied,
        string description,
        CancellationToken cancellationToken)
    {
        Exception? acknowledgementFailure = null;
        try
        {
            var reply = await _session
                .SendCommandAsync(command, payload, cancellationToken)
                .ConfigureAwait(false);
            if (reply.Payload is not [CommandSuccess])
            {
                throw new InvalidDataException(
                    $"Gauge rejected {description} with response {Convert.ToHexString(reply.Payload)}.");
            }
        }
        catch (Exception ex) when (
            ex is TimeoutException or IOException or GaugeProtocolException)
        {
            acknowledgementFailure = ex;
        }

        DeviceData device;
        try
        {
            device = await RequireExpectedMemoryGaugeAsync(
                expectedSerial,
                requireEmptyEraseInterlock: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception verificationFailure) when (acknowledgementFailure is not null)
        {
            throw new IOException(
                $"The gauge did not acknowledge {description}, and readback could not resolve whether it was applied.",
                new AggregateException(acknowledgementFailure, verificationFailure));
        }

        if (!isApplied(device))
        {
            throw new InvalidDataException(
                acknowledgementFailure is null
                    ? $"Gauge acknowledged {description}, but IDENTIFY did not report the requested value."
                    : $"Gauge did not acknowledge {description}, and IDENTIFY confirms it was not applied.",
                acknowledgementFailure);
        }

        return device;
    }

    private async Task<DeviceData> RequireExpectedMemoryGaugeAsync(
        uint expectedSerial,
        bool requireEmptyEraseInterlock,
        CancellationToken cancellationToken)
    {
        var identity = await _session.IdentifyAsync(cancellationToken).ConfigureAwait(false);
        var device = DeviceData.DecodeMemoryGauge(identity.Payload);
        if (!GaugeDeviceTypes.IsMemoryGauge(device.DeviceType) ||
            device.DeviceSerial != expectedSerial)
        {
            throw new InvalidOperationException(
                "Connected gauge identity does not match the gauge selected for configuration.");
        }

        if (requireEmptyEraseInterlock && device.EraseStatus.GetValueOrDefault() != 0)
        {
            throw new InvalidOperationException(
                "Gauge configuration is locked until the incomplete external-memory erase is restarted and completed.");
        }

        return device;
    }
}
