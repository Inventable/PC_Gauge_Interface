using System.Buffers.Binary;
using Gauge.Calibration;
using Gauge.Protocol;

namespace Gauge.Core;

public sealed class SensorLiveService
{
    public const byte CommandSuccess = 0x01;
    public const byte ErrorMemoryBusy = 0xFC;
    public const byte ErrorSensorComms = 0xFD;
    public const byte ErrorInvalidCommand = 0xFF;
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly GaugeSession _session;

    public SensorLiveService(GaugeSession session)
    {
        _session = session;
    }

    public async Task<SensorLiveStatus?> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var reply = await _session
            .SendCommandAsync(GaugeCommand.SensorLiveStatus, cancellationToken)
            .ConfigureAwait(false);
        return reply.Payload is [ErrorInvalidCommand]
            ? null
            : SensorLiveStatus.Parse(reply.Payload);
    }

    public Task<SensorCalibrationBundle> ReadCalibrationAsync(
        CancellationToken cancellationToken = default)
    {
        return new GaugeJobService(_session).ReadSensorCalibrationAsync(cancellationToken);
    }

    public async Task<SensorLiveStatus> StartAsync(
        ushort intervalSeconds = 1,
        CancellationToken cancellationToken = default)
    {
        if (intervalSeconds == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(intervalSeconds),
                "Sensor Live interval must be at least one second.");
        }

        var payload = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, intervalSeconds);
        var reply = await _session
            .SendCommandAsync(GaugeCommand.SensorLiveStart, payload, cancellationToken)
            .ConfigureAwait(false);
        if (reply.Payload is [ErrorInvalidCommand])
        {
            throw new NotSupportedException("Connected firmware does not support Sensor Live.");
        }
        if (reply.Payload is [ErrorSensorComms])
        {
            throw new SensorCommunicationException(
                SensorCommunicationFailure.ErrorSensorComms,
                "Gauge could not initialise or start the attached sensor.");
        }

        return SensorLiveStatus.Parse(reply.Payload);
    }

    public async Task<SensorLiveStatus> ReadStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var reply = await _session
            .SendCommandAsync(GaugeCommand.SensorLiveStatus, cancellationToken)
            .ConfigureAwait(false);
        return SensorLiveStatus.Parse(reply.Payload);
    }

    public async Task<SensorLiveSample?> ReadLatestAsync(
        CancellationToken cancellationToken = default)
    {
        var reply = await _session
            .SendCommandAsync(GaugeCommand.SensorLiveRead, cancellationToken)
            .ConfigureAwait(false);
        return reply.Payload is [ErrorMemoryBusy]
            ? null
            : SensorLiveSample.Parse(reply.Payload);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var reply = await _session
            .SendCommandAsync(GaugeCommand.SensorLiveStop, cancellationToken)
            .ConfigureAwait(false);
        if (reply.Payload is not [CommandSuccess])
        {
            throw new SensorCommunicationException(
                SensorCommunicationFailure.InvalidResponse,
                $"Sensor Live stop returned {Convert.ToHexString(reply.Payload)}.");
        }
    }
}

public sealed class SensorLiveDecoder
{
    public const double MinimumSensiblePressurePsi = -1000;
    public const double MaximumSensiblePressurePsi = 30000;
    public const double MinimumSensibleTemperatureCelsius = -100;
    public const double MaximumSensibleTemperatureCelsius = 250;

    private readonly uint _countBias;
    private readonly QuartzCalibration _calibration;

    public SensorLiveDecoder(SensorCalibrationBundle calibrationBundle)
    {
        var header = SensorCalibrationHeader.Parse(calibrationBundle.SensorHeader);
        _countBias = header.CountBias
            ?? throw new InvalidDataException("Sensor calibration header does not contain Bias.");
        _calibration = QuartzCalibration.FromPayloads(
            header,
            calibrationBundle.PressurePolynomial,
            calibrationBundle.TemperaturePolynomial);
    }

    public DecodedSensorLiveReading Decode(SensorLiveSample sample)
    {
        if (sample.PressureRaw == 0 || sample.TemperatureRaw == 0)
        {
            throw new InvalidDataException("Sensor returned a zero pressure or temperature count.");
        }

        var pressureCounts = checked(sample.PressureRaw + _countBias);
        var temperatureCounts = checked(sample.TemperatureRaw + _countBias);
        var pressureFrequency = _calibration.PressureFrequencyHz(pressureCounts);
        var temperatureFrequency = _calibration.TemperatureFrequencyHz(temperatureCounts);
        var pressure = _calibration.PressurePsiFromFrequency(
            pressureFrequency,
            temperatureFrequency);
        var temperature = _calibration.TemperatureCelsiusFromFrequency(
            temperatureFrequency);
        var sensible = sample.QualityFlags == 0 &&
                       double.IsFinite(pressure) &&
                       double.IsFinite(temperature) &&
                       pressure is >= MinimumSensiblePressurePsi and <= MaximumSensiblePressurePsi &&
                       temperature is >= MinimumSensibleTemperatureCelsius and <= MaximumSensibleTemperatureCelsius;

        return new DecodedSensorLiveReading(
            sample.Sequence,
            sample.MonotonicTicks,
            sample.SensorIteration,
            sample.QualityFlags,
            pressureCounts,
            temperatureCounts,
            pressureFrequency,
            temperatureFrequency,
            pressure,
            temperature,
            sensible);
    }
}

public sealed record DecodedSensorLiveReading(
    uint Sequence,
    uint MonotonicTicks,
    byte SensorIteration,
    byte QualityFlags,
    uint PressureCounts,
    uint TemperatureCounts,
    double PressureFrequency,
    double TemperatureFrequency,
    double Pressure,
    double Temperature,
    bool IsSensible);
