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
        return CreateSampleConverter(download.FileRecord, calibrationBundle)
            .Convert(download.RawBytes);
    }

    public static GaugeSampleConverter CreateSampleConverter(
        MemoryGaugeFileRecord fileRecord,
        SensorCalibrationBundle calibrationBundle)
    {
        return new GaugeSampleConverter(
            fileRecord.DataAddress.Value,
            fileRecord.MeasurementInterval,
            calibrationBundle);
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

public sealed class GaugeSampleConverter
{
    private readonly uint _fileStartAddress;
    private readonly ushort _measurementInterval;
    private readonly uint _countBias;
    private readonly QuartzCalibration _calibration;

    public GaugeSampleConverter(
        uint fileStartAddress,
        ushort measurementInterval,
        SensorCalibrationBundle calibrationBundle)
    {
        var header = SensorCalibrationHeader.Parse(calibrationBundle.SensorHeader);
        if (header.CountBias is null)
        {
            throw new InvalidOperationException("Sensor header does not contain Bias.");
        }

        _fileStartAddress = fileStartAddress;
        _measurementInterval = measurementInterval;
        _countBias = header.CountBias.Value;
        _calibration = QuartzCalibration.FromPayloads(
            header,
            calibrationBundle.PressurePolynomial,
            calibrationBundle.TemperaturePolynomial);
    }

    public IReadOnlyList<CalibratedGaugeSample> Convert(
        ReadOnlySpan<byte> bytes,
        int firstRecordIndex = 0,
        int firstSampleIndex = -1)
    {
        if (firstRecordIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(firstRecordIndex));
        }

        if (firstSampleIndex < -1)
        {
            throw new ArgumentOutOfRangeException(nameof(firstSampleIndex));
        }

        var completeLength = bytes.Length / MemoryGaugeDataRecord.Length * MemoryGaugeDataRecord.Length;
        var startAddress = _fileStartAddress + checked((uint)(firstRecordIndex * MemoryGaugeDataRecord.Length));
        var records = MemoryGaugeDataRecord.ParseMany(startAddress, bytes[..completeLength], firstRecordIndex);
        var samples = new List<CalibratedGaugeSample>(records.Count * 2);
        var sampleIndex = firstSampleIndex < 0 ? firstRecordIndex * 2 : firstSampleIndex;

        foreach (var record in records)
        {
            if (!record.IsPressureTemperature)
            {
                continue;
            }

            samples.Add(BuildCalibratedSample(record, record.FirstSample, sampleIndex++));
            samples.Add(BuildCalibratedSample(record, record.SecondSample, sampleIndex++));
        }

        return samples;
    }

    private CalibratedGaugeSample BuildCalibratedSample(
        MemoryGaugeDataRecord record,
        MemoryGaugeSample sample,
        int sampleIndex)
    {
        var pressureCounts = sample.PressureCounts + _countBias;
        var temperatureCounts = sample.TemperatureCounts + _countBias;
        var pressureFrequency = _calibration.PressureFrequencyHz(pressureCounts);
        var temperatureFrequency = _calibration.TemperatureFrequencyHz(temperatureCounts);

        return new CalibratedGaugeSample(
            pressureCounts,
            temperatureCounts,
            _calibration.PressurePsiFromFrequency(pressureFrequency, temperatureFrequency),
            _calibration.TemperatureCelsiusFromFrequency(temperatureFrequency),
            sampleIndex,
            record.Counter,
            record.Address,
            checked((uint)(sampleIndex * _measurementInterval)),
            temperatureFrequency,
            pressureFrequency,
            !record.IsCrcValid,
            false,
            record.BatteryStatus);
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
