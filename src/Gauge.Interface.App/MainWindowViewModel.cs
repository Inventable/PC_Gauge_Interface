using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Media;
using Gauge.Core;
using Gauge.Protocol;
using Gauge.Transport;

namespace Gauge.Interface.App;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private const int WakeBaud = 57600;
    private const int FastBaud = 460800;
    private static readonly TimeSpan FastVerifyDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AppPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Northstar",
        "GaugeInterface",
        "settings.json");
    private readonly CancellationTokenSource _pollingCancellation = new();

    private GaugeFileTable? _fileTable;
    private AppSettings _settings;
    private SerialPortOption? _selectedPortOption;
    private string _selectedPort = string.Empty;
    private string _outputDirectory;
    private string _jobName = "Gauge Job";
    private string _status = "Select serial port";
    private string _connectionStatus = "Setup";
    private string _latestTemperature = "--";
    private string _latestPressure = "--";
    private string _deviceSummary = "No gauge connected";
    private string _deviceDetails = string.Empty;
    private string _lastCsvPath = string.Empty;
    private string _fileSummary = "No file table loaded";
    private string _reviewSummary = "No downloaded job";
    private string _pressureRange = "--";
    private string _temperatureRange = "--";
    private string _jobDuration = "--";
    private IBrush _connectionBrush = new SolidColorBrush(Color.Parse("#CE0E2D"));
    private GaugeFileRowViewModel? _selectedFile;
    private bool _isPortConfigured;
    private bool _isGaugeConnected;
    private bool _isGraphVisible;
    private bool _showDeviceDetails;
    private bool _isBusy;
    private bool _isInitialising = true;

    public MainWindowViewModel()
    {
        _settings = LoadSettings();
        _outputDirectory = string.IsNullOrWhiteSpace(_settings.OutputDirectory)
            ? Path.Combine(Environment.CurrentDirectory, "artifacts", "desktop-downloads")
            : _settings.OutputDirectory;
        RefreshPortsCommand = new RelayCommand(RefreshPortsAsync);
        StartCommand = new RelayCommand(StartAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(SelectedPort));
        ReadFilesCommand = new RelayCommand(ReadFilesAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(SelectedPort));
        DownloadSelectedCommand = new RelayCommand(DownloadSelectedAsync, () => !IsBusy && SelectedFile is not null);
        ShowGraphCommand = new RelayCommand(ShowGraphAsync, () => ChartSamples.Count > 0);
        BackToFilesCommand = new RelayCommand(BackToFilesAsync);
        OpenSettingsCommand = new RelayCommand(OpenSettingsAsync);
        ToggleDeviceDetailsCommand = new RelayCommand(ToggleDeviceDetailsAsync);
        RefreshPorts();
        _isInitialising = false;
        _ = PollGaugeAsync(_pollingCancellation.Token);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<SerialPortOption> Ports { get; } = [];

    public ObservableCollection<GaugeFileRowViewModel> Files { get; } = [];

    public ObservableCollection<SampleRowViewModel> Samples { get; } = [];

    public ObservableCollection<ChartSampleViewModel> ChartSamples { get; } = [];

    public ICommand RefreshPortsCommand { get; }

    public ICommand StartCommand { get; }

    public ICommand ReadFilesCommand { get; }

    public ICommand DownloadSelectedCommand { get; }

    public ICommand ShowGraphCommand { get; }

    public ICommand BackToFilesCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ICommand ToggleDeviceDetailsCommand { get; }

    public string SelectedPort
    {
        get => _selectedPort;
        set
        {
            if (SetField(ref _selectedPort, value))
            {
                if (!_isInitialising)
                {
                    _settings = _settings with { LastPort = value };
                    SaveSettings();
                }

                RaiseCommandStates();
            }
        }
    }

    public SerialPortOption? SelectedPortOption
    {
        get => _selectedPortOption;
        set
        {
            if (SetField(ref _selectedPortOption, value))
            {
                SelectedPort = value?.Name ?? string.Empty;
            }
        }
    }

    public string OutputDirectory
    {
        get => _outputDirectory;
        set
        {
            if (SetField(ref _outputDirectory, value) && !_isInitialising)
            {
                _settings = _settings with { OutputDirectory = value };
                SaveSettings();
            }
        }
    }

    public string JobName
    {
        get => _jobName;
        set => SetField(ref _jobName, value);
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        set => SetField(ref _connectionStatus, value);
    }

    public IBrush ConnectionBrush
    {
        get => _connectionBrush;
        set => SetField(ref _connectionBrush, value);
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

    public string ReviewSummary
    {
        get => _reviewSummary;
        set => SetField(ref _reviewSummary, value);
    }

    public string PressureRange
    {
        get => _pressureRange;
        set => SetField(ref _pressureRange, value);
    }

    public string TemperatureRange
    {
        get => _temperatureRange;
        set => SetField(ref _temperatureRange, value);
    }

    public string JobDuration
    {
        get => _jobDuration;
        set => SetField(ref _jobDuration, value);
    }

    public GaugeFileRowViewModel? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (SetField(ref _selectedFile, value))
            {
                UpdateSelectedFileActions();
                RaiseCommandStates();
            }
        }
    }

    public bool IsPortConfigured
    {
        get => _isPortConfigured;
        set
        {
            if (SetField(ref _isPortConfigured, value))
            {
                OnPropertyChanged(nameof(IsSetupVisible));
                OnPropertyChanged(nameof(IsMainVisible));
            }
        }
    }

    public bool IsSetupVisible => !IsPortConfigured;

    public bool IsMainVisible => IsPortConfigured;

    public bool IsGaugeConnected
    {
        get => _isGaugeConnected;
        set
        {
            if (SetField(ref _isGaugeConnected, value))
            {
                OnPropertyChanged(nameof(IsDisconnectedVisible));
                OnPropertyChanged(nameof(IsFileTableVisible));
            }
        }
    }

    public bool IsDisconnectedVisible => IsPortConfigured && !IsGaugeConnected;

    public bool IsGraphVisible
    {
        get => _isGraphVisible;
        set
        {
            if (SetField(ref _isGraphVisible, value))
            {
                OnPropertyChanged(nameof(IsFileTableVisible));
            }
        }
    }

    public bool IsFileTableVisible => IsPortConfigured && IsGaugeConnected && !IsGraphVisible;

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
                OnPropertyChanged(nameof(DownloadButtonText));
                RaiseCommandStates();
            }
        }
    }

    public string DownloadButtonText => IsBusy ? "Working..." : "Download Selected";

    private Task RefreshPortsAsync()
    {
        RefreshPorts();
        return Task.CompletedTask;
    }

    private async Task StartAsync()
    {
        _settings = _settings with { LastPort = SelectedPort, OutputDirectory = OutputDirectory };
        SaveSettings();
        IsPortConfigured = true;
        IsGraphVisible = false;
        Status = $"Checking {SelectedPort}";
        await ReadFilesAsync().ConfigureAwait(true);
    }

    private Task OpenSettingsAsync()
    {
        IsPortConfigured = false;
        IsGraphVisible = false;
        Status = "Select serial port";
        return Task.CompletedTask;
    }

    private Task ShowGraphAsync()
    {
        if (ChartSamples.Count > 0)
        {
            IsGraphVisible = true;
        }

        return Task.CompletedTask;
    }

    private Task BackToFilesAsync()
    {
        IsGraphVisible = false;
        return Task.CompletedTask;
    }

    private Task ToggleDeviceDetailsAsync()
    {
        ShowDeviceDetails = !ShowDeviceDetails;
        return Task.CompletedTask;
    }

    private void RefreshPorts()
    {
        var previous = string.IsNullOrWhiteSpace(SelectedPort) ? _settings.LastPort : SelectedPort;
        Ports.Clear();

        foreach (var port in SerialPortDiscovery.GetPorts())
        {
            Ports.Add(new SerialPortOption(port.Name, port.DisplayName, port.IsLikelyUsbSerial));
        }

        SelectedPortOption = ChoosePort(previous);
        SelectedPort = SelectedPortOption?.Name ?? string.Empty;

        if (!IsPortConfigured)
        {
            Status = Ports.Count == 0
                ? "No serial ports found"
                : $"Selected {SelectedPortOption?.DisplayName ?? SelectedPort}";
        }
    }

    private async Task ReadFilesAsync()
    {
        IsBusy = true;
        Files.Clear();
        Samples.Clear();
        ChartSamples.Clear();
        SelectedFile = null;
        LatestTemperature = "--";
        LatestPressure = "--";
        LastCsvPath = string.Empty;
        ResetReview();

        try
        {
            Status = $"Waking gauge on {SelectedPort}";
            await using var connection = await OpenVerifiedConnectionAsync(preferFast: IsGaugeConnected).ConfigureAwait(true);
            var session = new GaugeSession(connection.Transport);
            var service = new GaugeJobService(session);

            var identity = connection.Identity;
            var device = DecodeDevice(identity.Payload);
            DeviceSummary = DescribeGauge(device);
            DeviceDetails = BuildDeviceDetails(device, identity.Payload);
            IsGaugeConnected = true;
            ConnectionStatus = "Connected";
            ConnectionBrush = new SolidColorBrush(Color.Parse("#2DA55D"));

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
            SetDisconnected();
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
        ChartSamples.Clear();
        LastCsvPath = string.Empty;
        ResetReview();

        try
        {
            var jobDirectory = BuildJobDirectory();
            Directory.CreateDirectory(jobDirectory);
            Status = $"Verifying gauge on {SelectedPort}";
            await using var connection = await OpenVerifiedConnectionAsync(preferFast: true).ConfigureAwait(true);
            var session = new GaugeSession(connection.Transport);
            var service = new GaugeJobService(session);

            Status = "Capturing sensor calibration";
            var calibration = await service.CaptureSensorCalibrationAsync().ConfigureAwait(true);
            await WriteCalibrationBundleAsync(jobDirectory, calibration).ConfigureAwait(true);

            Status = $"Downloading file {SelectedFile.Index}";
            var download = await service.DownloadFileAsync(_fileTable, SelectedFile.Index).ConfigureAwait(true);
            var rawPath = Path.Combine(jobDirectory, $"gauge-file-{download.FileIndex:000}.rawbin");
            await File.WriteAllBytesAsync(rawPath, download.RawBytes).ConfigureAwait(true);

            var samples = GaugeJobService.BuildCalibratedSamples(download, calibration);
            var csvPath = Path.Combine(jobDirectory, $"gauge-file-{download.FileIndex:000}-calibrated.csv");
            await File.WriteAllLinesAsync(csvPath, CalibratedCsvExporter.BuildLines(samples)).ConfigureAwait(true);

            foreach (var sample in samples.TakeLast(25))
            {
                Samples.Add(SampleRowViewModelFactory.FromSample(sample));
            }

            foreach (var sample in samples)
            {
                ChartSamples.Add(ChartSampleViewModelFactory.FromSample(sample));
            }

            var latest = samples[^1];
            LatestTemperature = $"{latest.Temperature:F2} C";
            LatestPressure = $"{latest.Pressure:F2} psi";
            LastCsvPath = csvPath;
            UpdateReview(download, samples);
            if (SelectedFile is not null)
            {
                SelectedFile.MarkDownloaded(sampleCount: samples.Count, hasWarnings: samples.Any(sample => sample.CrcError));
                UpdateSelectedFileActions();
            }

            IsGraphVisible = true;
            Status = $"Downloaded file {download.FileIndex} with {samples.Count} sample(s)";
        }
        catch (Exception ex) when (IsExpectedUiFailure(ex))
        {
            Status = $"Download failed: {ex.Message}";
        }
        catch (Exception ex)
        {
            Status = $"Download failed unexpectedly: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task PollGaugeAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(AppPollInterval);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(true);
                if (!IsPortConfigured || IsBusy || string.IsNullOrWhiteSpace(SelectedPort))
                {
                    continue;
                }

                await PollConnectionOnceAsync().ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollConnectionOnceAsync()
    {
        if (IsGaugeConnected)
        {
            var identity = await TryIdentifyAsync(SelectedPort, FastBaud, 350).ConfigureAwait(true);
            if (identity is null)
            {
                SetDisconnected();
                return;
            }

            var device = DecodeDevice(identity.Payload);
            DeviceSummary = DescribeGauge(device);
            DeviceDetails = BuildDeviceDetails(device, identity.Payload);
            ConnectionStatus = "Connected";
            ConnectionBrush = new SolidColorBrush(Color.Parse("#2DA55D"));
            Status = "Gauge connected";
            return;
        }

        var slowIdentity = await WaitForIdentifyAsync(SelectedPort, WakeBaud, 2000, 20, 80).ConfigureAwait(true);
        if (slowIdentity is not null)
        {
            Status = $"Gauge woke at {WakeBaud}; reading files";
            await Task.Delay(FastVerifyDelay).ConfigureAwait(true);
            await ReadFilesAsync().ConfigureAwait(true);
            return;
        }

        Status = "Waiting for gauge";
    }

    private void SetDisconnected()
    {
        IsGaugeConnected = false;
        IsGraphVisible = false;
        DeviceSummary = "No gauge connected";
        DeviceDetails = string.Empty;
        ConnectionStatus = "Disconnected";
        ConnectionBrush = new SolidColorBrush(Color.Parse("#CE0E2D"));
        Status = "Waiting for gauge";
        Files.Clear();
        SelectedFile = null;
        _fileTable = null;
        FileSummary = "No file table loaded";
    }

    private async Task<VerifiedGaugeConnection> OpenVerifiedConnectionAsync(bool preferFast)
    {
        if (preferFast)
        {
            var fastIdentity = await TryIdentifyAsync(SelectedPort, FastBaud, 1000).ConfigureAwait(true);
            if (fastIdentity is not null)
            {
                return await OpenIdentifiedTransportAsync(SelectedPort, FastBaud, 30000).ConfigureAwait(true);
            }
        }

        var slowIdentity = await WaitForIdentifyAsync(SelectedPort, WakeBaud, 5000, 100, 250).ConfigureAwait(true);
        if (slowIdentity is not null)
        {
            Status = $"Gauge woke at {WakeBaud}; verifying fast link";
            await Task.Delay(FastVerifyDelay).ConfigureAwait(true);
            var verified = await OpenIdentifiedTransportAsync(SelectedPort, FastBaud, 30000).ConfigureAwait(true);
            return verified;
        }

        Status = $"No slow response; checking {FastBaud} baud";
        return await OpenIdentifiedTransportAsync(SelectedPort, FastBaud, 30000).ConfigureAwait(true);
    }

    private SerialPortOption? ChoosePort(string previous)
    {
        if (!string.IsNullOrWhiteSpace(previous))
        {
            var remembered = Ports.FirstOrDefault(port => string.Equals(port.Name, previous, StringComparison.OrdinalIgnoreCase));
            if (remembered is not null)
            {
                return remembered;
            }
        }

        return Ports.FirstOrDefault(port => port.IsLikelyTarget)
            ?? Ports.FirstOrDefault();
    }

    private static async Task<GaugeFrame?> TryIdentifyAsync(string portName, int baudRate, int timeoutMs)
    {
        try
        {
            await using var transport = CreateTransport(portName, baudRate, timeoutMs);
            await transport.OpenAsync().ConfigureAwait(false);
            var session = new GaugeSession(transport);
            return await session.IdentifyAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpectedUiFailure(ex) || ex is ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static async Task<GaugeFrame?> WaitForIdentifyAsync(
        string portName,
        int baudRate,
        int timeoutMs,
        int intervalMs,
        int transactionTimeoutMs)
    {
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
        try
        {
            await using var transport = CreateTransport(portName, baudRate, transactionTimeoutMs);
            await transport.OpenAsync(timeoutSource.Token).ConfigureAwait(false);

            while (!timeoutSource.IsCancellationRequested)
            {
                var result = await TryIdentifyOpenTransportAsync(transport, timeoutSource.Token).ConfigureAwait(false);
                if (result is not null)
                {
                    return result;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(intervalMs), timeoutSource.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (IsExpectedUiFailure(ex) || ex is ArgumentOutOfRangeException)
        {
            return null;
        }

        return null;
    }

    private static async Task<VerifiedGaugeConnection> OpenIdentifiedTransportAsync(string portName, int baudRate, int timeoutMs)
    {
        var transport = CreateTransport(portName, baudRate, timeoutMs);
        try
        {
            await transport.OpenAsync().ConfigureAwait(false);
            var session = new GaugeSession(transport);
            var identity = await session.IdentifyAsync().ConfigureAwait(false);
            return new VerifiedGaugeConnection(transport, identity);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<GaugeFrame?> TryIdentifyOpenTransportAsync(SerialGaugeTransport transport, CancellationToken cancellationToken)
    {
        try
        {
            var session = new GaugeSession(transport);
            return await session.IdentifyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpectedUiFailure(ex) || ex is ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static SerialGaugeTransport CreateTransport(string portName, int baudRate, int timeoutMs)
    {
        return new SerialGaugeTransport(new SerialGaugeTransportOptions(
            portName,
            baudRate,
            ReadTimeoutMs: timeoutMs,
            WriteTimeoutMs: timeoutMs));
    }

    private string BuildJobDirectory()
    {
        var selected = SelectedFile is null ? "file" : $"file-{SelectedFile.Index:000}";
        var baseName = string.IsNullOrWhiteSpace(JobName) ? "gauge-job" : JobName.Trim();
        var folderName = $"{SanitizePathSegment(baseName)}-{selected}-{DateTime.Now:yyyyMMdd-HHmmss}";
        return Path.Combine(OutputDirectory, folderName);
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalid.Contains(character) ? '-' : character);
        }

        var sanitized = builder.ToString().Trim(' ', '.');
        return string.IsNullOrWhiteSpace(sanitized) ? "gauge-job" : sanitized;
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
                index,
                bytes,
                FormatBytes(bytes),
                largest == 0 ? 0 : Math.Max(4, bytes * 100.0 / largest),
                index == recommendedIndex ? "Suggested" : string.Empty,
                record.IsCrcValid);
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

    private void ResetReview()
    {
        ReviewSummary = "No downloaded job";
        PressureRange = "--";
        TemperatureRange = "--";
        JobDuration = "--";
    }

    private void UpdateReview(GaugeMemoryDownload download, IReadOnlyList<CalibratedGaugeSample> samples)
    {
        var pressureMin = samples.Min(sample => sample.Pressure);
        var pressureMax = samples.Max(sample => sample.Pressure);
        var temperatureMin = samples.Min(sample => sample.Temperature);
        var temperatureMax = samples.Max(sample => sample.Temperature);
        var durationSeconds = samples.Count == 0 ? 0 : samples[^1].Timestamp;
        var duration = TimeSpan.FromSeconds(durationSeconds);

        ReviewSummary = $"File {download.FileIndex} | {samples.Count} sample(s)";
        PressureRange = $"{pressureMin:F2} to {pressureMax:F2} psi";
        TemperatureRange = $"{temperatureMin:F2} to {temperatureMax:F2} C";
        JobDuration = duration.TotalHours >= 1
            ? $"{duration.TotalHours:F1} h"
            : $"{duration.TotalMinutes:F1} min";
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
        if (StartCommand is RelayCommand start)
        {
            start.RaiseCanExecuteChanged();
        }

        if (ReadFilesCommand is RelayCommand readFiles)
        {
            readFiles.RaiseCanExecuteChanged();
        }

        if (DownloadSelectedCommand is RelayCommand download)
        {
            download.RaiseCanExecuteChanged();
        }

        if (ShowGraphCommand is RelayCommand showGraph)
        {
            showGraph.RaiseCanExecuteChanged();
        }
    }

    private void UpdateSelectedFileActions()
    {
        foreach (var file in Files)
        {
            file.IsSelected = ReferenceEquals(file, SelectedFile);
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Settings could not be saved: {ex.Message}";
        }
    }
}

public sealed record AppSettings(
    string LastPort = "",
    string OutputDirectory = "");

public sealed record SerialPortOption(
    string Name,
    string DisplayName,
    bool IsLikelyTarget)
{
    public override string ToString()
    {
        return IsLikelyTarget ? $"{DisplayName} (likely)" : DisplayName;
    }
}

public sealed class GaugeFileRowViewModel : INotifyPropertyChanged
{
    private bool _isDownloaded;
    private bool _hasWarnings;
    private bool _isSelected;
    private int _sampleCount;

    public GaugeFileRowViewModel(int index, int bytes, string size, double sizePercent, string suggestion, bool isCrcValid)
    {
        Index = index;
        Bytes = bytes;
        Size = size;
        SizePercent = sizePercent;
        Suggestion = suggestion;
        IsCrcValid = isCrcValid;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index { get; }

    public int Bytes { get; }

    public string Size { get; }

    public double SizePercent { get; }

    public string Suggestion { get; }

    public bool IsCrcValid { get; }

    public bool IsDownloaded
    {
        get => _isDownloaded;
        private set => SetField(ref _isDownloaded, value);
    }

    public bool HasWarnings
    {
        get => _hasWarnings;
        private set => SetField(ref _hasWarnings, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public int SampleCount
    {
        get => _sampleCount;
        private set => SetField(ref _sampleCount, value);
    }

    public string DownloadStatus => IsDownloaded
        ? HasWarnings ? "Warnings" : "Downloaded"
        : "Not downloaded";

    public string GraphStatus => IsDownloaded
        ? HasWarnings ? "!" : "OK"
        : "--";

    public void MarkDownloaded(int sampleCount, bool hasWarnings)
    {
        SampleCount = sampleCount;
        HasWarnings = hasWarnings;
        IsDownloaded = true;
        OnPropertyChanged(nameof(DownloadStatus));
        OnPropertyChanged(nameof(GraphStatus));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        if (propertyName is nameof(IsDownloaded) or nameof(HasWarnings))
        {
            OnPropertyChanged(nameof(DownloadStatus));
            OnPropertyChanged(nameof(GraphStatus));
        }

        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record VerifiedGaugeConnection(
    SerialGaugeTransport Transport,
    GaugeFrame Identity) : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        return Transport.DisposeAsync();
    }
}

public sealed record SampleRowViewModel(
    int Sequence,
    string Pressure,
    string Temperature,
    string Timestamp,
    string Crc);

public sealed record ChartSampleViewModel(
    double Pressure,
    double Temperature);

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

public static class ChartSampleViewModelFactory
{
    public static ChartSampleViewModel FromSample(CalibratedGaugeSample sample)
    {
        return new ChartSampleViewModel(sample.Pressure, sample.Temperature);
    }
}
