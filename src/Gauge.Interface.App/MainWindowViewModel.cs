using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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
    private const int SmallFileSampleThreshold = 10;
    private const int WakeBaud = 57600;
    private const int FastBaud = 460800;
    private const int WakeTransactionTimeoutMs = 250;
    private const int WakePollIntervalMs = 100;
    private const int WakeScanTimeoutMs = 30000;
    private const int BackgroundWakeScanTimeoutMs = 1500;
    private static readonly TimeSpan FastVerifyDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AppPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Northstar",
        "GaugeInterface",
        "settings.json");
    private readonly CancellationTokenSource _pollingCancellation = new();
    private readonly SemaphoreSlim _serialGate = new(1, 1);

    private GaugeFileTable? _fileTable;
    private SensorCalibrationBundle? _calibration;
    private DeviceData? _connectedDevice;
    private CancellationTokenSource? _backgroundDownloadCancellation;
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
    private string _downloadProgressText = "";
    private IBrush _connectionBrush = new SolidColorBrush(Color.Parse("#CE0E2D"));
    private GaugeFileRowViewModel? _selectedFile;
    private double _downloadProgressPercent;
    private bool _isPortConfigured;
    private bool _isGaugeConnected;
    private bool _isGraphVisible;
    private bool _showDeviceDetails;
    private bool _ignoreSmallFiles = true;
    private bool _isBusy;
    private bool _isInitialising = true;
    private DateTime _statusProtectedUntilUtc = DateTime.MinValue;

    public MainWindowViewModel()
    {
        _settings = LoadSettings();
        _outputDirectory = string.IsNullOrWhiteSpace(_settings.OutputDirectory)
            ? Path.Combine(Environment.CurrentDirectory, "artifacts", "desktop-downloads")
            : _settings.OutputDirectory;
        RefreshPortsCommand = new RelayCommand(RefreshPortsAsync);
        StartCommand = new RelayCommand(StartAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(SelectedPort));
        ReadFilesCommand = new RelayCommand(ReadFilesAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(SelectedPort));
        ShowGraphCommand = new RelayCommand(ShowGraphAsync, () => SelectedFile?.Samples is { Count: > 0 } || ChartSamples.Count > 0);
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

    public double DownloadProgressPercent
    {
        get => _downloadProgressPercent;
        set => SetField(ref _downloadProgressPercent, value);
    }

    public string DownloadProgressText
    {
        get => _downloadProgressText;
        set => SetField(ref _downloadProgressText, value);
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

    public bool IgnoreSmallFiles
    {
        get => _ignoreSmallFiles;
        set
        {
            if (SetField(ref _ignoreSmallFiles, value) && _fileTable is not null)
            {
                CancelBackgroundDownloads();
                PopulateFiles(_fileTable);
                StartBackgroundDownloads();
            }
        }
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
        if (SelectedFile?.Samples is { Count: > 0 } samples)
        {
            ShowFileGraph(SelectedFile, samples);
        }
        else if (ChartSamples.Count > 0)
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
        CancelBackgroundDownloads();
        IsBusy = true;
        await _serialGate.WaitAsync().ConfigureAwait(true);
        Files.Clear();
        Samples.Clear();
        ChartSamples.Clear();
        SelectedFile = null;
        _calibration = null;
        _connectedDevice = null;
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
            _connectedDevice = device;
            DeviceSummary = DescribeGauge(device);
            DeviceDetails = BuildDeviceDetails(device, identity.Payload);
            IsGaugeConnected = true;
            ConnectionStatus = "Connected";
            ConnectionBrush = new SolidColorBrush(Color.Parse("#2DA55D"));

            Status = "Reading file table";
            _fileTable = await service.ReadFileTableAsync().ConfigureAwait(true);
            PopulateFiles(_fileTable);
            FileSummary = Files.Count == 0 ? "No valid files found" : string.Empty;
            Status = "Capturing sensor calibration";
            _calibration = await service.CaptureSensorCalibrationAsync().ConfigureAwait(true);
            Status = Files.Count == 0 ? "Gauge connected; no files found" : "Gauge connected; downloading files";
        }
        catch (Exception ex) when (IsExpectedUiFailure(ex))
        {
            SetDisconnected();
            Status = $"Connection failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _serialGate.Release();
        }

        if (IsGaugeConnected && _fileTable is not null && _calibration is not null)
        {
            StartBackgroundDownloads();
        }
    }

    public async Task DownloadSelectedAsync()
    {
        CancelBackgroundDownloads();
        if (IsBusy)
        {
            SetProtectedStatus("Already working. Please wait for the current operation to finish.");
            return;
        }

        SelectedFile ??= Files.FirstOrDefault(file => file.Suggestion == "Suggested")
            ?? Files.LastOrDefault();

        if (SelectedFile is null)
        {
            SetProtectedStatus("Select a file before downloading.");
            return;
        }

        if (_fileTable is null)
        {
            SetProtectedStatus("Read the gauge file table before downloading.");
            return;
        }

        IsBusy = true;
        Samples.Clear();
        ChartSamples.Clear();
        LastCsvPath = string.Empty;
        DownloadProgressPercent = 0;
        DownloadProgressText = "Preparing download";
        ResetReview();

        try
        {
            await EnsureCalibrationAsync(CancellationToken.None).ConfigureAwait(true);
            var downloaded = await DownloadFileRowAsync(SelectedFile, manual: true, CancellationToken.None).ConfigureAwait(true);
            if (downloaded is not null)
            {
                ShowFileGraph(SelectedFile, downloaded.Samples);
                SetProtectedStatus($"Downloaded file {downloaded.Download.FileIndex} with {downloaded.Samples.Count} sample(s)", TimeSpan.FromSeconds(20));
            }
        }
        catch (Exception ex) when (IsExpectedUiFailure(ex))
        {
            SetProtectedStatus($"Download failed: {ex.Message}", TimeSpan.FromSeconds(20));
        }
        catch (Exception ex)
        {
            SetProtectedStatus($"Download failed unexpectedly: {ex.Message}", TimeSpan.FromSeconds(20));
        }
        finally
        {
            IsBusy = false;
            if (IsGaugeConnected)
            {
                StartBackgroundDownloads();
            }
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

                if (!await _serialGate.WaitAsync(0, cancellationToken).ConfigureAwait(true))
                {
                    continue;
                }

                try
                {
                    await PollConnectionOnceAsync().ConfigureAwait(true);
                }
                finally
                {
                    _serialGate.Release();
                }
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
            if (CanPollSetStatus())
            {
                Status = "Gauge connected";
            }
            return;
        }

        var slowIdentity = await WaitForIdentifyAsync(
            SelectedPort,
            WakeBaud,
            BackgroundWakeScanTimeoutMs,
            WakePollIntervalMs,
            WakeTransactionTimeoutMs).ConfigureAwait(true);
        if (slowIdentity is not null)
        {
            Status = $"Gauge woke at {WakeBaud}; reading files";
            await Task.Delay(FastVerifyDelay).ConfigureAwait(true);
            _ = ReadFilesAsync();
            return;
        }

        if (CanPollSetStatus())
        {
            Status = "Waiting for gauge";
        }
    }

    private void SetDisconnected()
    {
        CancelBackgroundDownloads();
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

        var slowIdentity = await WaitForIdentifyAsync(
            SelectedPort,
            WakeBaud,
            WakeScanTimeoutMs,
            WakePollIntervalMs,
            WakeTransactionTimeoutMs).ConfigureAwait(true);
        if (slowIdentity is not null)
        {
            Status = $"Gauge woke at {WakeBaud}; verifying fast link";
            await Task.Delay(FastVerifyDelay).ConfigureAwait(true);
            try
            {
                return await OpenIdentifiedTransportAsync(SelectedPort, FastBaud, 30000).ConfigureAwait(true);
            }
            catch (Exception ex) when (IsExpectedUiFailure(ex) || ex is ArgumentOutOfRangeException)
            {
                Status = $"Fast link did not verify; trying {WakeBaud} baud";
                return await OpenIdentifiedTransportAsync(SelectedPort, WakeBaud, 30000).ConfigureAwait(true);
            }
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

    private void StartBackgroundDownloads()
    {
        if (!IsGaugeConnected || _fileTable is null || _calibration is null || Files.Count == 0)
        {
            return;
        }

        if (_backgroundDownloadCancellation is { IsCancellationRequested: false })
        {
            return;
        }

        _backgroundDownloadCancellation?.Dispose();
        _backgroundDownloadCancellation = new CancellationTokenSource();
        _ = RunBackgroundDownloadsAsync(_backgroundDownloadCancellation.Token);
    }

    private void CancelBackgroundDownloads()
    {
        _backgroundDownloadCancellation?.Cancel();
    }

    private async Task RunBackgroundDownloadsAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var file in Files.Where(file => !file.IsDownloaded).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                await DownloadFileRowAsync(file, manual: false, cancellationToken).ConfigureAwait(true);
            }

            if (CanPollSetStatus())
            {
                Status = "Gauge connected; files ready";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (IsExpectedUiFailure(ex))
        {
            SetProtectedStatus($"Background download paused: {ex.Message}", TimeSpan.FromSeconds(15));
        }
        catch (Exception ex)
        {
            SetProtectedStatus($"Background download failed: {ex.Message}", TimeSpan.FromSeconds(15));
        }
        finally
        {
            if (_backgroundDownloadCancellation?.Token == cancellationToken)
            {
                _backgroundDownloadCancellation.Dispose();
                _backgroundDownloadCancellation = null;
            }
        }
    }

    private async Task EnsureCalibrationAsync(CancellationToken cancellationToken)
    {
        if (_calibration is not null)
        {
            return;
        }

        await _serialGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            SetProtectedStatus("Capturing sensor calibration", TimeSpan.FromSeconds(30));
            await using var connection = await OpenVerifiedConnectionAsync(preferFast: true).ConfigureAwait(true);
            var service = new GaugeJobService(new GaugeSession(connection.Transport));
            _calibration = await service.CaptureSensorCalibrationAsync(cancellationToken: cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _serialGate.Release();
        }
    }

    private async Task<DownloadedGaugeFile?> DownloadFileRowAsync(
        GaugeFileRowViewModel file,
        bool manual,
        CancellationToken cancellationToken)
    {
        if (_fileTable is null || _calibration is null)
        {
            return null;
        }

        if (file.Download is not null && file.Samples is not null)
        {
            return new DownloadedGaugeFile(file.Download, file.Samples);
        }

        await _serialGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            file.MarkDownloading();
            var label = manual ? "Downloading" : "Auto-downloading";
            SetProtectedStatus($"{label} file {file.Index}", TimeSpan.FromSeconds(30));
            await using var connection = await OpenVerifiedConnectionAsync(preferFast: true).ConfigureAwait(true);
            var service = new GaugeJobService(new GaugeSession(connection.Transport));
            var timer = Stopwatch.StartNew();
            var progress = new Progress<MemoryReadProgress>(progress =>
            {
                file.MarkProgress(progress, timer.Elapsed);
                if (manual)
                {
                    UpdateDownloadProgress(progress, timer.Elapsed);
                }
            });
            var download = await service.DownloadFileAsync(_fileTable, file.Index, progress: progress, cancellationToken: cancellationToken).ConfigureAwait(true);
            var samples = GaugeJobService.BuildCalibratedSamples(download, _calibration);
            file.MarkDownloaded(
                download,
                samples,
                hasWarnings: samples.Any(sample => sample.BatteryStatus != 0),
                hasErrors: !file.IsCrcValid || samples.Any(sample => sample.CrcError));
            return new DownloadedGaugeFile(download, samples);
        }
        catch (OperationCanceledException)
        {
            file.MarkQueued();
            throw;
        }
        catch (Exception ex) when (!manual && IsExpectedUiFailure(ex))
        {
            file.MarkError(ex.Message);
            return null;
        }
        finally
        {
            _serialGate.Release();
        }
    }

    private void ShowFileGraph(GaugeFileRowViewModel file, IReadOnlyList<CalibratedGaugeSample> samples)
    {
        Samples.Clear();
        ChartSamples.Clear();

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
        LastCsvPath = string.Empty;
        if (file.Download is not null)
        {
            UpdateReview(file.Download, samples);
        }

        IsGraphVisible = true;
        UpdateSelectedFileActions();
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
        var existingRows = Files.ToDictionary(file => file.Index);
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
            if (IgnoreSmallFiles && samples < SmallFileSampleThreshold)
            {
                continue;
            }

            var row = new GaugeFileRowViewModel(
                index,
                bytes,
                samples,
                FormatBytes(bytes),
                largest == 0 ? 0 : Math.Max(4, bytes * 100.0 / largest),
                index == recommendedIndex ? "Suggested" : string.Empty,
                record.IsCrcValid);
            Files.Add(existingRows.TryGetValue(index, out var existing) ? existing : row);
        }

        SelectedFile = Files.FirstOrDefault(file => file.Index == recommendedIndex)
            ?? Files.LastOrDefault();
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

    private void UpdateDownloadProgress(MemoryReadProgress progress, TimeSpan elapsed)
    {
        if (progress.TotalBytes <= 0)
        {
            DownloadProgressPercent = 0;
            DownloadProgressText = "Preparing download";
            return;
        }

        DownloadProgressPercent = Math.Clamp(progress.BytesRead * 100.0 / progress.TotalBytes, 0, 100);
        if (progress.BytesRead <= 0 || elapsed.TotalSeconds < 0.5)
        {
            DownloadProgressText = $"{DownloadProgressPercent:F0}%";
            return;
        }

        var bytesPerSecond = progress.BytesRead / elapsed.TotalSeconds;
        var remainingBytes = Math.Max(0, progress.TotalBytes - progress.BytesRead);
        var remaining = bytesPerSecond <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(remainingBytes / bytesPerSecond);

        DownloadProgressText = remainingBytes == 0
            ? "100% complete"
            : $"{DownloadProgressPercent:F0}% - about {FormatDuration(remaining)} remaining";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 1)
        {
            return "1 sec";
        }

        if (duration.TotalMinutes < 1)
        {
            return $"{Math.Ceiling(duration.TotalSeconds):F0} secs";
        }

        return $"{Math.Ceiling(duration.TotalMinutes):F0} mins";
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

    private void SetProtectedStatus(string value, TimeSpan? duration = null)
    {
        _statusProtectedUntilUtc = DateTime.UtcNow + (duration ?? TimeSpan.FromSeconds(10));
        Status = value;
    }

    private bool CanPollSetStatus()
    {
        return DateTime.UtcNow >= _statusProtectedUntilUtc;
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

public sealed record DownloadedGaugeFile(
    GaugeMemoryDownload Download,
    IReadOnlyList<CalibratedGaugeSample> Samples);

public sealed class GaugeFileRowViewModel : INotifyPropertyChanged
{
    private static readonly Geometry DownloadGeometry = Geometry.Parse("M19,9H15V3H9V9H5L12,16L19,9M5,18V20H19V18H5Z");
    private static readonly Geometry GraphGeometry = Geometry.Parse("M3,3V21H21V19H5V3H3M7,17L12,12L15,15L20,9L18.59,7.59L15,12L12,9L5.5,15.5L7,17Z");
    private static readonly IBrush ReadyBrush = new SolidColorBrush(Color.Parse("#2DA55D"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#D97706"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#CE0E2D"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#5D5D66"));

    private bool _hasErrors;
    private bool _hasWarnings;
    private bool _isSelected;
    private int _sampleCount;
    private double _progressPercent;
    private string _state = "Queued";
    private string _progressText = string.Empty;

    public GaugeFileRowViewModel(int index, int bytes, int estimatedSamples, string size, double sizePercent, string suggestion, bool isCrcValid)
    {
        Index = index;
        Bytes = bytes;
        EstimatedSamples = estimatedSamples;
        Size = size;
        SizePercent = sizePercent;
        Suggestion = suggestion;
        IsCrcValid = isCrcValid;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index { get; }

    public int Bytes { get; }

    public int EstimatedSamples { get; }

    public string Size { get; }

    public double SizePercent { get; }

    public string Suggestion { get; }

    public bool IsCrcValid { get; }

    public bool IsDownloaded => Download is not null;

    public GaugeMemoryDownload? Download { get; private set; }

    public byte[]? RawBytes { get; private set; }

    public IReadOnlyList<CalibratedGaugeSample>? Samples { get; private set; }

    public string State
    {
        get => _state;
        private set => SetField(ref _state, value);
    }

    public bool HasWarnings
    {
        get => _hasWarnings;
        private set => SetField(ref _hasWarnings, value);
    }

    public bool HasErrors
    {
        get => _hasErrors;
        private set => SetField(ref _hasErrors, value);
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

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetField(ref _progressPercent, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetField(ref _progressText, value);
    }

    public bool IsDownloading => State == "Downloading";

    public string RowStatus => State switch
    {
        "Downloading" => string.IsNullOrWhiteSpace(ProgressText) ? "Downloading" : $"Downloading {ProgressText}",
        "Downloaded" when HasErrors => "Ready - data errors",
        "Downloaded" when HasWarnings => "Ready - review warnings",
        "Downloaded" => "Ready",
        "Error" => "Download failed - select to retry",
        _ => "Waiting"
    };

    public Geometry ActionIcon => IsDownloaded ? GraphGeometry : DownloadGeometry;

    public IBrush ActionBrush => !IsDownloaded || HasErrors
        ? ErrorBrush
        : HasWarnings ? WarningBrush : ReadyBrush;

    public IBrush StatusBrush => State == "Error" || HasErrors
        ? ErrorBrush
        : HasWarnings ? WarningBrush : State == "Downloaded" ? ReadyBrush : MutedBrush;

    public string ActionToolTip => IsDownloaded ? "View pressure and temperature graph" : "Download this file";

    public void MarkDownloading()
    {
        State = "Downloading";
        ProgressPercent = 0;
        ProgressText = "0%";
    }

    public void MarkQueued()
    {
        State = "Queued";
        ProgressPercent = 0;
        ProgressText = string.Empty;
    }

    public void MarkProgress(MemoryReadProgress progress, TimeSpan elapsed)
    {
        if (progress.TotalBytes <= 0)
        {
            ProgressPercent = 0;
            ProgressText = "Preparing";
            return;
        }

        ProgressPercent = Math.Clamp(progress.BytesRead * 100.0 / progress.TotalBytes, 0, 100);
        if (progress.BytesRead <= 0 || elapsed.TotalSeconds < 0.5)
        {
            ProgressText = $"{ProgressPercent:F0}%";
            return;
        }

        var bytesPerSecond = progress.BytesRead / elapsed.TotalSeconds;
        var remainingBytes = Math.Max(0, progress.TotalBytes - progress.BytesRead);
        var remaining = bytesPerSecond <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(remainingBytes / bytesPerSecond);
        ProgressText = remainingBytes == 0
            ? "100%"
            : $"{ProgressPercent:F0}% - {FormatDuration(remaining)}";
    }

    public void MarkDownloaded(
        GaugeMemoryDownload download,
        IReadOnlyList<CalibratedGaugeSample> samples,
        bool hasWarnings,
        bool hasErrors)
    {
        Download = download;
        RawBytes = download.RawBytes;
        Samples = samples;
        SampleCount = samples.Count;
        HasWarnings = hasWarnings;
        HasErrors = hasErrors;
        ProgressPercent = 100;
        ProgressText = "100%";
        State = "Downloaded";
        OnPropertyChanged(nameof(IsDownloaded));
        RaisePresentationChanged();
    }

    public void MarkError(string message)
    {
        State = "Error";
        ProgressText = message;
        RaisePresentationChanged();
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 1)
        {
            return "1 sec";
        }

        if (duration.TotalMinutes < 1)
        {
            return $"{Math.Ceiling(duration.TotalSeconds):F0} secs";
        }

        return $"{Math.Ceiling(duration.TotalMinutes):F0} mins";
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        if (propertyName is nameof(State) or nameof(HasWarnings) or nameof(HasErrors) or nameof(ProgressText))
        {
            RaisePresentationChanged();
        }

        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void RaisePresentationChanged()
    {
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(RowStatus));
        OnPropertyChanged(nameof(ActionIcon));
        OnPropertyChanged(nameof(ActionBrush));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(ActionToolTip));
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
