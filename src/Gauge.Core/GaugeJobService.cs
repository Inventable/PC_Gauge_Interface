using Gauge.Calibration;
using Gauge.Protocol;

namespace Gauge.Core;

public sealed class GaugeJobService
{
    public const byte SensorCommsError = 0xFD;

    private readonly GaugeSession _session;

    public GaugeJobService(GaugeSession session)
    {
        _session = session;
    }

    public async Task<SensorCalibrationBundle> CaptureSensorCalibrationAsync(
        int initialiseAttempts = 3,
        TimeSpan? powerOffDelay = null,
        TimeSpan? retryDelay = null,
        CancellationToken cancellationToken = default)
    {
        if (initialiseAttempts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialiseAttempts), "Initialise attempts must be greater than zero.");
        }

        await _session.SendCommandAsync(GaugeCommand.PowerOffSensor, cancellationToken).ConfigureAwait(false);
        await Task.Delay(powerOffDelay ?? TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);

        for (var attempt = 1; attempt <= initialiseAttempts; attempt++)
        {
            var reply = await _session.SendCommandAsync(GaugeCommand.InitialiseSensor, cancellationToken).ConfigureAwait(false);
            if (reply.Payload is [0x01])
            {
                return new SensorCalibrationBundle(
                    await ReadRequiredSensorPayloadAsync(GaugeCommand.ReadSensorSerial, cancellationToken).ConfigureAwait(false),
                    await ReadRequiredSensorPayloadAsync(GaugeCommand.ReadSensorCalibration, cancellationToken).ConfigureAwait(false),
                    await ReadRequiredSensorPayloadAsync(GaugeCommand.ReadSensorPressurePolynomial, cancellationToken).ConfigureAwait(false),
                    await ReadRequiredSensorPayloadAsync(GaugeCommand.ReadSensorTemperaturePolynomial, cancellationToken).ConfigureAwait(false));
            }

            if (attempt < initialiseAttempts)
            {
                await Task.Delay(retryDelay ?? TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException($"Sensor initialise failed after {initialiseAttempts} attempt(s).");
    }

    public async Task<GaugeFileTable> ReadFileTableAsync(
        int tableBytes = 0x4000,
        ushort chunkBytes = 1024,
        CancellationToken cancellationToken = default)
    {
        var eof = await _session.FindEndOfFileAsync(cancellationToken).ConfigureAwait(false);
        var table = await _session
            .ReadExternalMemoryChunkedAsync(0, tableBytes, chunkBytes, GaugeCommand.ReadFileSector, cancellationToken)
            .ConfigureAwait(false);

        return new GaugeFileTable(eof, MemoryGaugeFileRecord.ParseTable(table));
    }

    public async Task<GaugeMemoryDownload> DownloadLatestFileAsync(
        ushort chunkBytes = 1024,
        CancellationToken cancellationToken = default)
    {
        var table = await ReadFileTableAsync(chunkBytes: chunkBytes, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (table.Records.Count == 0)
        {
            throw new InvalidOperationException("Gauge file table contains no valid file records.");
        }

        var latestIndex = table.Records.Count - 1;
        return await DownloadFileAsync(table, latestIndex, chunkBytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GaugeMemoryDownload> DownloadFileAsync(
        GaugeFileTable table,
        int fileIndex,
        ushort chunkBytes = 1024,
        CancellationToken cancellationToken = default,
        IProgress<MemoryReadProgress>? progress = null)
    {
        if (fileIndex < 0 || fileIndex >= table.Records.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(fileIndex), $"File index {fileIndex} is not valid.");
        }

        var record = table.Records[fileIndex];
        var nextAddress = GetNextDataAddress(table.Records, fileIndex, table.EndOfFile);
        if (nextAddress <= record.DataAddress.Value)
        {
            throw new InvalidOperationException($"File index {fileIndex} has no readable data range.");
        }

        var bytesToRead = checked((int)(nextAddress - record.DataAddress.Value));
        var bytes = await _session
            .ReadExternalMemoryChunkedAsync(record.DataAddress.Value, bytesToRead, chunkBytes, GaugeCommand.ReadRecordSector, cancellationToken, progress)
            .ConfigureAwait(false);

        return new GaugeMemoryDownload(fileIndex, record, table.EndOfFile, nextAddress, bytes);
    }

    public static IReadOnlyList<CalibratedGaugeSample> BuildCalibratedSamples(
        GaugeMemoryDownload download,
        SensorCalibrationBundle calibrationBundle)
    {
        var header = SensorCalibrationHeader.Parse(calibrationBundle.SensorHeader);
        if (header.CountBias is null)
        {
            throw new InvalidOperationException("Sensor header does not contain Bias.");
        }

        var calibration = QuartzCalibration.FromPayloads(
            header,
            calibrationBundle.PressurePolynomial,
            calibrationBundle.TemperaturePolynomial);
        var records = MemoryGaugeDataRecord.ParseMany(download.FileRecord.DataAddress.Value, download.RawBytes);
        var samples = new List<CalibratedGaugeSample>(records.Count * 2);

        foreach (var record in records)
        {
            samples.Add(BuildCalibratedSample(record, record.FirstSample, download.FileRecord.MeasurementInterval, header.CountBias.Value, calibration));
            samples.Add(BuildCalibratedSample(record, record.SecondSample, download.FileRecord.MeasurementInterval, header.CountBias.Value, calibration));
        }

        return samples;
    }

    private async Task<byte[]> ReadRequiredSensorPayloadAsync(GaugeCommand command, CancellationToken cancellationToken)
    {
        var payload = await _session.ReadSensorDataAsync(command, cancellationToken).ConfigureAwait(false);
        if (payload.Length == 0)
        {
            throw new InvalidOperationException($"{command} returned no payload.");
        }

        if (payload is [SensorCommsError])
        {
            throw new InvalidOperationException($"{command} returned ERROR_SENSOR_COMMS (0x{SensorCommsError:X2}).");
        }

        return payload;
    }

    private static CalibratedGaugeSample BuildCalibratedSample(
        MemoryGaugeDataRecord record,
        MemoryGaugeSample sample,
        ushort measurementInterval,
        uint countBias,
        QuartzCalibration calibration)
    {
        var pressureCounts = sample.PressureCounts + countBias;
        var temperatureCounts = sample.TemperatureCounts + countBias;
        var pressureFrequency = calibration.PressureFrequencyHz(pressureCounts);
        var temperatureFrequency = calibration.TemperatureFrequencyHz(temperatureCounts);

        return new CalibratedGaugeSample(
            pressureCounts,
            temperatureCounts,
            calibration.PressurePsiFromFrequency(pressureFrequency, temperatureFrequency),
            calibration.TemperatureCelsiusFromFrequency(temperatureFrequency),
            sample.SampleIndex,
            record.Counter,
            record.Address,
            checked((uint)(sample.SampleIndex * measurementInterval)),
            temperatureFrequency,
            pressureFrequency,
            !record.IsCrcValid,
            false,
            record.BatteryStatus);
    }

    private static uint GetNextDataAddress(IReadOnlyList<MemoryGaugeFileRecord> records, int index, GaugeMemoryAddress eof)
    {
        for (var next = index + 1; next < records.Count; next++)
        {
            if (records[next].DataAddress.Value > records[index].DataAddress.Value)
            {
                return records[next].DataAddress.Value;
            }
        }

        return eof.Value == 0 ? records[index].DataAddress.Value : eof.Value + MemoryGaugeFileRecord.Length;
    }
}

public sealed record GaugeFileTable(
    GaugeMemoryAddress EndOfFile,
    IReadOnlyList<MemoryGaugeFileRecord> Records);

public sealed record SensorCalibrationBundle(
    byte[] SensorSerial,
    byte[] SensorHeader,
    byte[] PressurePolynomial,
    byte[] TemperaturePolynomial);

public sealed record GaugeMemoryDownload(
    int FileIndex,
    MemoryGaugeFileRecord FileRecord,
    GaugeMemoryAddress EndOfFile,
    uint EndAddress,
    byte[] RawBytes);

public sealed record CalibratedGaugeSample(
    uint PressureCounts,
    uint TemperatureCounts,
    double Pressure,
    double Temperature,
    int Sequence,
    ushort Counter,
    uint Address,
    uint Timestamp,
    double TemperatureFrequency,
    double PressureFrequency,
    bool CrcError,
    bool Corrected,
    byte BatteryStatus);
