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
    private string _selectedPort = string.Empty;
    private string _outputDirectory;
    private string _status = "Ready";
    private string _latestTemperature = "--";
    private string _latestPressure = "--";
    private string _deviceSummary = "No gauge connected";
    private string _lastCsvPath = string.Empty;
    private bool _isBusy;

    public MainWindowViewModel()
    {
        _outputDirectory = Path.Combine(Environment.CurrentDirectory, "artifacts", "desktop-latest");
        RefreshPortsCommand = new RelayCommand(RefreshPortsAsync);
        DownloadLatestCommand = new RelayCommand(DownloadLatestAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(SelectedPort));
        RefreshPorts();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<string> Ports { get; } = [];

    public ObservableCollection<SampleRowViewModel> Samples { get; } = [];

    public ICommand RefreshPortsCommand { get; }

    public ICommand DownloadLatestCommand { get; }

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

    public string LastCsvPath
    {
        get => _lastCsvPath;
        set => SetField(ref _lastCsvPath, value);
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

    private async Task DownloadLatestAsync()
    {
        IsBusy = true;
        Samples.Clear();
        LastCsvPath = string.Empty;

        try
        {
            Directory.CreateDirectory(OutputDirectory);
            Status = $"Opening {SelectedPort} at 460800 baud";

            await using var transport = new SerialGaugeTransport(new SerialGaugeTransportOptions(
                SelectedPort,
                460800,
                ReadTimeoutMs: 30000,
                WriteTimeoutMs: 30000));
            await transport.OpenAsync().ConfigureAwait(true);

            var session = new GaugeSession(transport);
            var service = new GaugeJobService(session);

            Status = "Identifying gauge";
            var identity = await session.IdentifyAsync().ConfigureAwait(true);
            DeviceSummary = DescribeGauge(identity.Payload);

            Status = "Capturing sensor calibration";
            var calibration = await service.CaptureSensorCalibrationAsync().ConfigureAwait(true);
            await WriteCalibrationBundleAsync(OutputDirectory, calibration).ConfigureAwait(true);

            Status = "Downloading latest memory file";
            var download = await service.DownloadLatestFileAsync().ConfigureAwait(true);
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
        catch (Exception ex) when (ex is TimeoutException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException
            or GaugeProtocolException)
        {
            Status = $"Download failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static async Task WriteCalibrationBundleAsync(string outputDirectory, SensorCalibrationBundle calibration)
    {
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "sensor-serial.txt"), calibration.SensorSerial).ConfigureAwait(true);
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "sensor-header.txt"), calibration.SensorHeader).ConfigureAwait(true);
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "pressure-poly.txt"), calibration.PressurePolynomial).ConfigureAwait(true);
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "temperature-poly.txt"), calibration.TemperaturePolynomial).ConfigureAwait(true);
    }

    private static string DescribeGauge(byte[] payload)
    {
        if (payload.Length < 22)
        {
            return "Gauge identified";
        }

        var device = DeviceData.DecodeMemoryGauge(payload);
        var builder = new StringBuilder();
        builder.Append($"Device {device.DeviceSerial}");
        builder.Append($" | Firmware {device.FirmwareMajor}.{device.FirmwareMinor}");
        builder.Append($" | Interval {device.MeasurementInterval}s");
        builder.Append($" | Memory mode {device.MemoryMode}");
        return builder.ToString();
    }

    private void RaiseCommandStates()
    {
        if (DownloadLatestCommand is RelayCommand download)
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
