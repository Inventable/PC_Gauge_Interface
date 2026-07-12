using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Input;
using Gauge.Core;
using Gauge.Protocol;
using Gauge.Transport;

namespace Gauge.Interface.App;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private GaugeFileTable? _fileTable;
    private string _selectedPort = string.Empty;
    private string _outputDirectory;
    private string _status = "Ready";
    private string _latestTemperature = "--";
    private string _latestPressure = "--";
    private string _deviceSummary = "No gauge connected";
    private string _deviceDetails = string.Empty;
    private string _lastCsvPath = string.Empty;
    private string _fileSummary = "No file table loaded";
    private GaugeFileRowViewModel? _selectedFile;
    private bool _showDeviceDetails;
    private bool _isBusy;

    public MainWindowViewModel()
    {
        _outputDirectory = Path.Combine(Environment.CurrentDirectory, "artifacts", "desktop-downloads");
        RefreshPortsCommand = new RelayCommand(RefreshPortsAsync);
        ReadFilesCommand = new RelayCommand(ReadFilesAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(SelectedPort));
        DownloadSelectedCommand = new RelayCommand(DownloadSelectedAsync, () => !IsBusy && SelectedFile is not null);
        ToggleDeviceDetailsCommand = new RelayCommand(ToggleDeviceDetailsAsync);
        RefreshPorts();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<string> Ports { get; } = [];

    public ObservableCollection<GaugeFileRowViewModel> Files { get; } = [];

    public ObservableCollection<SampleRowViewModel> Samples { get; } = [];

    public ICommand RefreshPortsCommand { get; }

    public ICommand ReadFilesCommand { get; }

    public ICommand DownloadSelectedCommand { get; }

    public ICommand ToggleDeviceDetailsCommand { get; }

    public string SelectedPort
    {
        get => _selectedPort;
        set
        {
            if (SetField(ref _selectedPort, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string OutputDirectory
    {
        get => _outputDirectory;
        set => SetField(ref _outputDirectory, value);
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public string LatestTemperature
    {
        get => _latestTemperature;
        set => SetField(ref _latestTemperature, value);
    }

    public string LatestPressure
    {
        get => _latestPressure;
        set => SetField(ref _latestPressure, value);
    }

    public string DeviceSummary
    {
        get => _deviceSummary;
        set => SetField(ref _deviceSummary, value);
    }

    public string DeviceDetails
    {
        get => _deviceDetails;
        set => SetField(ref _deviceDetails, value);
    }

    public string LastCsvPath
    {
        get => _lastCsvPath;
        set => SetField(ref _lastCsvPath, value);
    }

    public string FileSummary
    {
        get => _fileSummary;
        set => SetField(ref _fileSummary, value);
    }

    public GaugeFileRowViewModel? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (SetField(ref _selectedFile, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool ShowDeviceDetails
    {
        get => _showDeviceDetails;
        set => SetField(ref _showDeviceDetails, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetField(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    private Task RefreshPortsAsync()
    {
        RefreshPorts();
        return Task.CompletedTask;
    }

    private Task ToggleDeviceDetailsAsync()
    {
        ShowDeviceDetails = !ShowDeviceDetails;
        return Task.CompletedTask;
    }

    private void RefreshPorts()
    {
        var previous = SelectedPort;
        Ports.Clear();

        foreach (var port in SerialPortDiscovery.GetPortNames())
        {
            Ports.Add(port);
        }

        SelectedPort = Ports.Contains(previous)
            ? previous
            : Ports.FirstOrDefault() ?? string.Empty;

        Status = Ports.Count == 0 ? "No serial ports found" : $"Found {Ports.Count} serial port(s)";
    }

    private async Task ReadFilesAsync()
    {
        IsBusy = true;
        Files.Clear();
        Samples.Clear();
        SelectedFile = null;
        LatestTemperature = "--";
        LatestPressure = "--";
        LastCsvPath = string.Empty;

        try
        {
            Status = $"Opening {SelectedPort} at 460800 baud";
            await using var transport = await OpenTransportAsync().ConfigureAwait(true);
            var session = new GaugeSession(transport);
            var service = new GaugeJobService(session);

            Status = "Identifying gauge";
            var identity = await session.IdentifyAsync().ConfigureAwait(true);
            var device = DecodeDevice(identity.Payload);
            DeviceSummary = DescribeGauge(device);
            DeviceDetails = BuildDeviceDetails(device, identity.Payload);

            Status = "Reading file table";
            _fileTable = await service.ReadFileTableAsync().ConfigureAwait(true);
            PopulateFiles(_fileTable);
            FileSummary = Files.Count == 0
                ? "No valid files found"
                : $"{Files.Count} file(s), EOF {_fileTable.EndOfFile}";
            Status = Files.Count == 0 ? "Gauge connected; no files found" : "Gauge connected";
        }
        catch (Exception ex) when (IsExpectedUiFailure(ex))
        {
            Status = $"Connection failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DownloadSelectedAsync()
    {
        if (SelectedFile is null || _fileTable is null)
        {
            return;
        }

        IsBusy = true;
        Samples.Clear();
        LastCsvPath = string.Empty;

        try
        {
            Directory.CreateDirectory(OutputDirectory);
            Status = $"Opening {SelectedPort} at 460800 baud";
            await using var transport = await OpenTransportAsync().ConfigureAwait(true);
            var session = new GaugeSession(transport);
            var service = new GaugeJobService(session);

            Status = "Capturing sensor calibration";
            var calibration = await service.CaptureSensorCalibrationAsync().ConfigureAwait(true);
            await WriteCalibrationBundleAsync(OutputDirectory, calibration).ConfigureAwait(true);

            Status = $"Downloading file {SelectedFile.Index}";
            var download = await service.DownloadFileAsync(_fileTable, SelectedFile.Index).ConfigureAwait(true);
            var rawPath = Path.Combine(OutputDirectory, $"gauge-file-{download.FileIndex:000}.rawbin");
            await File.WriteAllBytesAsync(rawPath, download.RawBytes).ConfigureAwait(true);

            var samples = GaugeJobService.BuildCalibratedSamples(download, calibration);
            var csvPath = Path.Combine(OutputDirectory, $"gauge-file-{download.FileIndex:000}-calibrated.csv");
            await File.WriteAllLinesAsync(csvPath, CalibratedCsvExporter.BuildLines(samples)).ConfigureAwait(true);

            foreach (var sample in samples.TakeLast(25))
            {
                Samples.Add(SampleRowViewModelFactory.FromSample(sample));
            }

            var latest = samples[^1];
            LatestTemperature = $"{latest.Temperature:F2} C";
            LatestPressure = $"{latest.Pressure:F2} psi";
            LastCsvPath = csvPath;
            Status = $"Downloaded file {download.FileIndex} with {samples.Count} sample(s)";
        }
        catch (Exception ex) when (IsExpectedUiFailure(ex))
        {
            Status = $"Download failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<SerialGaugeTransport> OpenTransportAsync()
    {
        var transport = new SerialGaugeTransport(new SerialGaugeTransportOptions(
            SelectedPort,
            460800,
            ReadTimeoutMs: 30000,
            WriteTimeoutMs: 30000));
        await transport.OpenAsync().ConfigureAwait(true);
        return transport;
    }

    private void PopulateFiles(GaugeFileTable table)
    {
        Files.Clear();
        var sizes = table.Records
            .Select((record, index) => EstimateBytes(table, index))
            .ToArray();
        var largest = sizes.Length == 0 ? 0 : sizes.Max();
        var recommendedIndex = ChooseRecommendedFileIndex(sizes);

        for (var index = 0; index < table.Records.Count; index++)
        {
            var record = table.Records[index];
            var bytes = sizes[index];
            var samples = bytes / MemoryGaugeDataRecord.Length * 2;
            var row = new GaugeFileRowViewModel(
                record.Index,
                record.DataAddress.ToString(),
                record.MeasurementInterval,
                bytes,
                samples,
                FormatBytes(bytes),
                largest == 0 ? 0 : Math.Max(4, bytes * 100.0 / largest),
                index == recommendedIndex ? "Suggested" : string.Empty,
                record.IsCrcValid ? "OK" : "Bad");
            Files.Add(row);
        }

        if (recommendedIndex >= 0 && recommendedIndex < Files.Count)
        {
            SelectedFile = Files[recommendedIndex];
        }
    }

    private static int ChooseRecommendedFileIndex(IReadOnlyList<int> sizes)
    {
        if (sizes.Count == 0)
        {
            return -1;
        }

        var largest = sizes.Max();
        var largeThreshold = Math.Max(MemoryGaugeDataRecord.Length * 10, largest / 4);
        for (var index = sizes.Count - 1; index >= 0; index--)
        {
            if (sizes[index] >= largeThreshold)
            {
                return index;
            }
        }

        var largestIndex = 0;
        for (var index = 1; index < sizes.Count; index++)
        {
            if (sizes[index] >= sizes[largestIndex])
            {
                largestIndex = index;
            }
        }

        return largestIndex;
    }

    private static int EstimateBytes(GaugeFileTable table, int index)
    {
        var record = table.Records[index];
        for (var next = index + 1; next < table.Records.Count; next++)
        {
            if (table.Records[next].DataAddress.Value > record.DataAddress.Value)
            {
                return checked((int)(table.Records[next].DataAddress.Value - record.DataAddress.Value));
            }
        }

        return table.EndOfFile.Value == 0
            ? 0
            : checked((int)(table.EndOfFile.Value + MemoryGaugeFileRecord.Length - record.DataAddress.Value));
    }

    private static async Task WriteCalibrationBundleAsync(string outputDirectory, SensorCalibrationBundle calibration)
    {
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "sensor-serial.txt"), calibration.SensorSerial).ConfigureAwait(true);
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "sensor-header.txt"), calibration.SensorHeader).ConfigureAwait(true);
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "pressure-poly.txt"), calibration.PressurePolynomial).ConfigureAwait(true);
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "temperature-poly.txt"), calibration.TemperaturePolynomial).ConfigureAwait(true);
    }

    private static DeviceData? DecodeDevice(byte[] payload)
    {
        if (payload.Length < 22)
        {
            return null;
        }

        return payload.Length >= 32
            ? DeviceData.DecodeAcousticGauge(payload)
            : DeviceData.DecodeMemoryGauge(payload);
    }

    private static string DescribeGauge(DeviceData? device)
    {
        if (device is null)
        {
            return "Gauge connected";
        }

        return $"Connected | Device {device.DeviceSerial} | Firmware {device.FirmwareMajor}.{device.FirmwareMinor}";
    }

    private static string BuildDeviceDetails(DeviceData? device, byte[] payload)
    {
        if (device is null)
        {
            return $"Identify payload bytes: {payload.Length}";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"Device type: {device.DeviceType}");
        builder.AppendLine($"Device serial: {device.DeviceSerial}");
        builder.AppendLine($"PCB type: {device.PcbType}");
        builder.AppendLine($"PCB serial: {device.PcbSerial}");
        builder.AppendLine($"Firmware: {device.FirmwareMajor}.{device.FirmwareMinor}");
        builder.AppendLine($"Measurement interval: {device.MeasurementInterval}");
        builder.AppendLine($"Memory mode: {device.MemoryMode}");
        builder.AppendLine($"Erase status: {device.EraseStatus}");
        return builder.ToString();
    }

    private static string FormatBytes(int bytes)
    {
        return bytes >= 1024
            ? $"{bytes / 1024.0:F1} KB"
            : $"{bytes} B";
    }

    private static bool IsExpectedUiFailure(Exception ex)
    {
        return ex is TimeoutException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or GaugeProtocolException;
    }

    private void RaiseCommandStates()
    {
        if (ReadFilesCommand is RelayCommand readFiles)
        {
            readFiles.RaiseCanExecuteChanged();
        }

        if (DownloadSelectedCommand is RelayCommand download)
        {
            download.RaiseCanExecuteChanged();
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed record GaugeFileRowViewModel(
    int Index,
    string Address,
    ushort Rate,
    int Bytes,
    int Samples,
    string Size,
    double SizePercent,
    string Suggestion,
    string Crc);

public sealed record SampleRowViewModel(
    int Sequence,
    string Pressure,
    string Temperature,
    string Timestamp,
    string Crc);

public static class SampleRowViewModelFactory
{
    public static SampleRowViewModel FromSample(CalibratedGaugeSample sample)
    {
        return new SampleRowViewModel(
            sample.Sequence,
            sample.Pressure.ToString("F3"),
            sample.Temperature.ToString("F3"),
            sample.Timestamp.ToString(),
            sample.CrcError ? "Bad" : "OK");
    }
}
