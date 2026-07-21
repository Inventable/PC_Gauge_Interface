using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Gauge.Core;
using Gauge.Protocol;
using Gauge.Transport;

namespace Gauge.Interface.App;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private const int SmallFileSampleThreshold = 10;
    private const int WakeBaud = 57600;
    private const int FastBaud = 460800;
    private const int WakeTransactionTimeoutMs = 250;
    private const int WakePollIntervalMs = 100;
    private const int WakeScanTimeoutMs = 30000;
    private const int BackgroundWakeScanTimeoutMs = 1500;
    private const int ConnectedPollTransactionTimeoutMs = 100;
    private const int DataTransactionTimeoutMs = 100;
    private const int DataTransactionDeadlineMs = 500;
    private const int WakeTransactionDeadlineMs = 1000;
    private const int SensorTransactionTimeoutMs = 2000;
    private const int SensorTransactionDeadlineMs = 7000;
    private const int BootloaderBaud = 115200;
    private const uint MemoryGaugeDeviceType = 100230;
    private const ushort Pic18F26K80DeviceId = 0x6126;
    private static readonly TimeSpan LiveChartRefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FastVerifyDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AppPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan ConnectedPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SensorCalibrationDeadline = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReconnectRetentionWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DownloadRecoveryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly Geometry SortAscendingGeometry = Geometry.Parse("M7,15L12,10L17,15H7Z");
    private static readonly Geometry SortDescendingGeometry = Geometry.Parse("M7,9L12,14L17,9H7Z");
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Northstar",
        "GaugeInterface",
        "settings.json");
    private readonly CancellationTokenSource _pollingCancellation = new();
    private readonly SemaphoreSlim _serialGate = new(1, 1);
    private readonly BoundedCommunicationEventLog _communicationEvents = new();
    private int _communicationRefreshPending;
    private readonly Task _pollingTask;

    private GaugeFileTable? _fileTable;
    private SensorCalibrationBundle? _calibration;
    private DeviceData? _connectedDevice;
    private CancellationTokenSource? _backgroundDownloadCancellation;
    private CancellationTokenSource? _manualDownloadCancellation;
    private CancellationTokenSource? _foregroundOperationCancellation;
    private Task? _foregroundOperationTask;
    private Task? _backgroundDownloadTask;
    private GaugeFileRowViewModel? _activeDownload;
    private AppSettings _settings;
    private SerialPortOption? _selectedPortOption;
    private string _selectedPort = string.Empty;
    private string _outputDirectory;
    private string _jobName = "Gauge Job";
    private string _status = "Select serial port";
    private string _connectionStatus = "Setup";
    private string _deviceSummary = "No gauge connected";
    private string _deviceDetails = string.Empty;
    private string _fileSummary = "No file table loaded";
    private string _reviewFile = "--";
    private string _reviewSampleCount = "--";
    private string _cursorSample = "--";
    private string _cursorElapsed = "--";
    private string _cursorPressure = "--";
    private string _cursorTemperature = "--";
    private string _pressureMinimum = "--";
    private string _pressureMaximum = "--";
    private string _temperatureMinimum = "--";
    private string _temperatureMaximum = "--";
    private string _jobDuration = "--";
    private string _downloadProgressText = "";
    private ChartDataSet _chartData = ChartDataSet.Empty;
    private IBrush _connectionBrush = new SolidColorBrush(Color.Parse("#CE0E2D"));
    private GaugeFileRowViewModel? _selectedFile;
    private double _downloadProgressPercent;
    private bool _isPortConfigured;
    private bool _isGaugeConnected;
    private bool _isGraphVisible;
    private bool _showDeviceDetails;
    private bool _isAppSettingsVisible;
    private bool _isGaugeSettingsVisible;
    private bool _isEngineeringModeVisible;
    private bool _ignoreSmallFiles = true;
    private bool _isBusy;
    private bool _isInitialising = true;
    private bool _autoDownloadsPaused;
    private DateTime _statusProtectedUntilUtc = DateTime.MinValue;
    private DateTime _nextConnectedPollUtc = DateTime.MinValue;
    private FileListSortColumn _fileSortColumn = FileListSortColumn.FileNumber;
    private bool _fileSortDescending = true;
    private BootloaderApplicationImage? _firmwareImage;
    private FirmwareAction _pendingFirmwareAction;
    private string _firmwareImageName = "No image selected";
    private string _firmwareImageSummary = "Select an Offset production HEX file";
    private string _firmwareStatus = "Ready";
    private string _firmwareConfirmationText = string.Empty;
    private string _firmwareLoaderDetails = "Not connected";
    private double _firmwareProgressPercent;
    private bool _isFirmwareUpdating;
    private bool _isFirmwareConfirmationVisible;
    private bool _isFirmwareRecoveryRequired;
    private bool _isShuttingDown;
    private DateTime _retainedSessionUntilUtc = DateTime.MinValue;
    private uint? _retainedDeviceSerial;
    private string _retainedPort = string.Empty;
    private string? _calibrationFailure;

    public MainWindowViewModel()
    {
        _settings = LoadSettings();
        _outputDirectory = string.IsNullOrWhiteSpace(_settings.OutputDirectory)
            ? Path.Combine(Environment.CurrentDirectory, "artifacts", "desktop-downloads")
            : _settings.OutputDirectory;
        RefreshPortsCommand = new RelayCommand(RefreshPortsAsync);
        StartCommand = new RelayCommand(StartAsync, () => !IsFirmwareUpdating && !string.IsNullOrWhiteSpace(SelectedPort));
        ReadFilesCommand = new RelayCommand(ReadFilesAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(SelectedPort));
        ShowGraphCommand = new RelayCommand(ShowGraphAsync, () => SelectedFile?.HasPlotData == true || ChartData.Count > 0);
        BackToFilesCommand = new RelayCommand(BackToFilesAsync);
        OpenSettingsCommand = new RelayCommand(OpenSettingsAsync, () => !IsFirmwareUpdating);
        OpenAppSettingsCommand = new RelayCommand(OpenAppSettingsAsync, () => !IsFirmwareUpdating);
        OpenGaugeSettingsCommand = new RelayCommand(OpenGaugeSettingsAsync);
        OpenEngineeringModeCommand = new RelayCommand(OpenEngineeringModeAsync);
        CloseSettingsOverlayCommand = new RelayCommand(CloseSettingsOverlayAsync, () => !IsFirmwareUpdating);
        ToggleDeviceDetailsCommand = new RelayCommand(ToggleDeviceDetailsAsync);
        CancelOperationCommand = new RelayCommand(CancelOperationAsync, () => CanCancelOperation);
        BeginFirmwareProgramCommand = new RelayCommand(BeginFirmwareProgramAsync, CanBeginFirmwareProgram);
        BeginFirmwareRecoveryCommand = new RelayCommand(BeginFirmwareRecoveryAsync, CanBeginFirmwareRecovery);
        ConfirmFirmwareActionCommand = new RelayCommand(ConfirmFirmwareActionAsync, CanConfirmFirmwareAction);
        CancelFirmwareConfirmationCommand = new RelayCommand(CancelFirmwareConfirmationAsync, () => !IsFirmwareUpdating);
        RefreshPorts();
        _isInitialising = false;
        _pollingTask = PollGaugeAsync(_pollingCancellation.Token);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<SerialPortOption> Ports { get; } = [];

    public ObservableCollection<GaugeFileRowViewModel> Files { get; } = [];

    public ObservableCollection<SampleRowViewModel> Samples { get; } = [];

    public bool IsFileNumberSortActive => _fileSortColumn == FileListSortColumn.FileNumber;

    public bool IsFileNumberSortInactive => !IsFileNumberSortActive;

    public bool IsFileSizeSortActive => _fileSortColumn == FileListSortColumn.Size;

    public bool IsFileSizeSortInactive => !IsFileSizeSortActive;

    public Geometry FileSortDirectionIcon => _fileSortDescending
        ? SortDescendingGeometry
        : SortAscendingGeometry;

    public ICommand RefreshPortsCommand { get; }

    public ICommand StartCommand { get; }

    public ICommand ReadFilesCommand { get; }

    public ICommand ShowGraphCommand { get; }

    public ICommand BackToFilesCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ICommand OpenAppSettingsCommand { get; }

    public ICommand OpenGaugeSettingsCommand { get; }

    public ICommand OpenEngineeringModeCommand { get; }

    public ICommand CloseSettingsOverlayCommand { get; }

    public ICommand ToggleDeviceDetailsCommand { get; }

    public ICommand CancelOperationCommand { get; }

    public ICommand BeginFirmwareProgramCommand { get; }

    public ICommand BeginFirmwareRecoveryCommand { get; }

    public ICommand ConfirmFirmwareActionCommand { get; }

    public ICommand CancelFirmwareConfirmationCommand { get; }

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

                OnPropertyChanged(nameof(EngineeringTransport));
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

    public string LastRecordExportDirectory => _settings.LastRecordExportDirectory;

    public string LastSupportBundleDirectory => _settings.LastSupportBundleDirectory;

    public string LastFirmwareDirectory => _settings.LastFirmwareDirectory;

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

    public string DeviceSummary
    {
        get => _deviceSummary;
        set => SetField(ref _deviceSummary, value);
    }

    public string DeviceDetails
    {
        get => _deviceDetails;
        set
        {
            if (SetField(ref _deviceDetails, value))
            {
                OnPropertyChanged(nameof(EngineeringDeviceDetails));
            }
        }
    }

    public string FileSummary
    {
        get => _fileSummary;
        set => SetField(ref _fileSummary, value);
    }

    public string ReviewFile
    {
        get => _reviewFile;
        set => SetField(ref _reviewFile, value);
    }

    public string ReviewSampleCount
    {
        get => _reviewSampleCount;
        set => SetField(ref _reviewSampleCount, value);
    }

    public string CursorSample
    {
        get => _cursorSample;
        private set => SetField(ref _cursorSample, value);
    }

    public string CursorElapsed
    {
        get => _cursorElapsed;
        private set => SetField(ref _cursorElapsed, value);
    }

    public string CursorPressure
    {
        get => _cursorPressure;
        private set => SetField(ref _cursorPressure, value);
    }

    public string CursorTemperature
    {
        get => _cursorTemperature;
        private set => SetField(ref _cursorTemperature, value);
    }

    public ChartDataSet ChartData
    {
        get => _chartData;
        private set => SetField(ref _chartData, value);
    }

    public string PressureMinimum
    {
        get => _pressureMinimum;
        private set => SetField(ref _pressureMinimum, value);
    }

    public string PressureMaximum
    {
        get => _pressureMaximum;
        private set => SetField(ref _pressureMaximum, value);
    }

    public string TemperatureMinimum
    {
        get => _temperatureMinimum;
        private set => SetField(ref _temperatureMinimum, value);
    }

    public string TemperatureMaximum
    {
        get => _temperatureMaximum;
        private set => SetField(ref _temperatureMaximum, value);
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
                OnPropertyChanged(nameof(IsDisconnectedVisible));
                OnPropertyChanged(nameof(IsFileTableVisible));
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
                RaiseFirmwareCommandStates();
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

    public bool IsAppSettingsVisible
    {
        get => _isAppSettingsVisible;
        private set
        {
            if (SetField(ref _isAppSettingsVisible, value))
            {
                OnPropertyChanged(nameof(IsSettingsOverlayVisible));
            }
        }
    }

    public bool IsGaugeSettingsVisible
    {
        get => _isGaugeSettingsVisible;
        private set
        {
            if (SetField(ref _isGaugeSettingsVisible, value))
            {
                OnPropertyChanged(nameof(IsSettingsOverlayVisible));
            }
        }
    }

    public bool IsEngineeringModeVisible
    {
        get => _isEngineeringModeVisible;
        private set
        {
            if (SetField(ref _isEngineeringModeVisible, value))
            {
                OnPropertyChanged(nameof(IsSettingsOverlayVisible));
            }
        }
    }

    public bool IsSettingsOverlayVisible => IsAppSettingsVisible || IsGaugeSettingsVisible || IsEngineeringModeVisible;

    public bool HasSetupMessage => !IsPortConfigured && Ports.Count == 0;

    public string SetupMessage => HasSetupMessage ? "No serial ports found" : string.Empty;

    public string GaugeDeviceType => _connectedDevice is null
        ? "--"
        : DescribeDeviceType(_connectedDevice.DeviceType);

    public string GaugeDeviceSerial => _connectedDevice?.DeviceSerial.ToString() ?? "--";

    public string GaugeFirmware => _connectedDevice is null
        ? "--"
        : $"{_connectedDevice.FirmwareMajor}.{_connectedDevice.FirmwareMinor}";

    public string GaugePcb => _connectedDevice is null
        ? "--"
        : $"{_connectedDevice.PcbType} / {_connectedDevice.PcbSerial}";

    public string GaugeMeasurementInterval => _connectedDevice is null
        ? "--"
        : $"{_connectedDevice.MeasurementInterval} sec";

    public string GaugeMemoryMode => _connectedDevice?.MemoryMode.ToString() ?? "--";

    public string GaugeEraseStatus => _connectedDevice?.EraseStatus?.ToString() ?? "--";

    public string EngineeringTransport => string.IsNullOrWhiteSpace(SelectedPort)
        ? "No serial port selected"
        : $"{SelectedPort} | wake {WakeBaud:N0} baud | data {FastBaud:N0} baud";

    public string EngineeringFileTable => _fileTable is null
        ? "Not loaded"
        : $"{_fileTable.Records.Count:N0} file record(s) | EOF 0x{_fileTable.EndOfFile.Value:X8}";

    public string EngineeringCalibration => _calibration is not null
        ? "Captured"
        : string.IsNullOrWhiteSpace(_calibrationFailure) ? "Not captured" : _calibrationFailure;

    public string EngineeringDeviceDetails => string.IsNullOrWhiteSpace(DeviceDetails)
        ? "No gauge identity available"
        : DeviceDetails.Trim();

    public string EngineeringCommunicationHealth
    {
        get
        {
            var summary = _communicationEvents.Summary();
            if (!summary.HasSession)
            {
                return "No session";
            }

            if (summary.FailedTransactions + summary.OpenFailures > 0)
            {
                return "Error";
            }

            return summary.RetryAttempts + summary.CrcErrors > 0 ? "Review" : "Good";
        }
    }

    public IBrush EngineeringCommunicationBrush => EngineeringCommunicationHealth switch
    {
        "Good" => new SolidColorBrush(Color.Parse("#2DA55D")),
        "Review" => new SolidColorBrush(Color.Parse("#D97706")),
        "Error" => new SolidColorBrush(Color.Parse("#CE0E2D")),
        _ => new SolidColorBrush(Color.Parse("#5D5D66"))
    };

    public string EngineeringCommunicationSession
    {
        get
        {
            var summary = _communicationEvents.Summary();
            if (!summary.HasSession || summary.StartedUtc is null)
            {
                return "Not started";
            }

            var started = summary.StartedUtc.Value.ToLocalTime();
            if (summary.IsActive)
            {
                return $"Active on {summary.Port} since {started:HH:mm:ss}";
            }

            var ended = summary.EndedUtc?.ToLocalTime();
            return ended is null
                ? $"Last session on {summary.Port}"
                : $"Last session {started:HH:mm:ss}-{ended:HH:mm:ss}";
        }
    }

    public string EngineeringCommunicationTransactions => _communicationEvents.Summary().Transactions.ToString("N0");

    public string EngineeringCommunicationRetries => _communicationEvents.Summary().RetryAttempts.ToString("N0");

    public string EngineeringCommunicationCrcErrors => _communicationEvents.Summary().CrcErrors.ToString("N0");

    public string EngineeringCommunicationRecovered => _communicationEvents.Summary().RecoveredTransactions.ToString("N0");

    public string EngineeringCommunicationFailures
    {
        get
        {
            var summary = _communicationEvents.Summary();
            return (summary.FailedTransactions + summary.OpenFailures).ToString("N0");
        }
    }

    public string EngineeringCommunicationLastIssue
    {
        get
        {
            var issue = _communicationEvents.Summary().LastIssue;
            if (issue is null)
            {
                return "None";
            }

            var target = issue.Command ?? "port open";
            return $"{issue.LastTimestampUtc.ToLocalTime():HH:mm:ss} {target}: {issue.Message}";
        }
    }

    public string FirmwareImageName
    {
        get => _firmwareImageName;
        private set => SetField(ref _firmwareImageName, value);
    }

    public string FirmwareImageSummary
    {
        get => _firmwareImageSummary;
        private set => SetField(ref _firmwareImageSummary, value);
    }

    public string FirmwareStatus
    {
        get => _firmwareStatus;
        private set
        {
            if (SetField(ref _firmwareStatus, value))
            {
                OnPropertyChanged(nameof(FirmwareStatusBrush));
            }
        }
    }

    public string FirmwareLoaderDetails
    {
        get => _firmwareLoaderDetails;
        private set => SetField(ref _firmwareLoaderDetails, value);
    }

    public double FirmwareProgressPercent
    {
        get => _firmwareProgressPercent;
        private set => SetField(ref _firmwareProgressPercent, value);
    }

    public string FirmwareConfirmationText
    {
        get => _firmwareConfirmationText;
        set
        {
            if (SetField(ref _firmwareConfirmationText, value))
            {
                RaiseFirmwareCommandStates();
            }
        }
    }

    public bool IsFirmwareUpdating
    {
        get => _isFirmwareUpdating;
        private set
        {
            if (SetField(ref _isFirmwareUpdating, value))
            {
                OnPropertyChanged(nameof(CanChooseFirmware));
                OnPropertyChanged(nameof(CanCancelOperation));
                RaiseCommandStates();
            }
        }
    }

    public bool IsFirmwareConfirmationVisible
    {
        get => _isFirmwareConfirmationVisible;
        private set => SetField(ref _isFirmwareConfirmationVisible, value);
    }

    public bool IsFirmwareRecoveryRequired
    {
        get => _isFirmwareRecoveryRequired;
        private set
        {
            if (SetField(ref _isFirmwareRecoveryRequired, value))
            {
                OnPropertyChanged(nameof(IsFirmwareNormalActionVisible));
                OnPropertyChanged(nameof(IsFirmwareRecoveryActionVisible));
                RaiseFirmwareCommandStates();
            }
        }
    }

    public bool IsFirmwareImageSelected => _firmwareImage is not null;

    public bool CanChooseFirmware => !IsFirmwareUpdating;

    public bool IsFirmwareNormalActionVisible => !IsFirmwareRecoveryRequired;

    public bool IsFirmwareRecoveryActionVisible => IsFirmwareRecoveryRequired;

    public string FirmwareConfirmationPrompt => _pendingFirmwareAction == FirmwareAction.Recover
        ? "Type RECOVER to rewrite the application while the gauge remains in bootloader mode."
        : $"Type device serial {GaugeDeviceSerial} to confirm this firmware update.";

    public string FirmwareConfirmationAction => _pendingFirmwareAction == FirmwareAction.Recover
        ? "Recover Firmware"
        : "Program Firmware";

    public IBrush FirmwareStatusBrush => IsFirmwareRecoveryRequired
        ? new SolidColorBrush(Color.Parse("#D97706"))
        : FirmwareStatus.StartsWith("Complete", StringComparison.OrdinalIgnoreCase)
            ? new SolidColorBrush(Color.Parse("#2DA55D"))
            : FirmwareStatus.StartsWith("Rejected", StringComparison.OrdinalIgnoreCase)
                || FirmwareStatus.StartsWith("Failed", StringComparison.OrdinalIgnoreCase)
                ? new SolidColorBrush(Color.Parse("#CE0E2D"))
                : new SolidColorBrush(Color.Parse("#5D5D66"));

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
                OnPropertyChanged(nameof(CanCancelOperation));
                RaiseCommandStates();
            }
        }
    }

    public bool CanCancelOperation => IsBusy && !IsFirmwareUpdating;

    private Task RefreshPortsAsync()
    {
        RefreshPorts();
        return Task.CompletedTask;
    }

    public void SortFiles(FileListSortColumn column)
    {
        if (_fileSortColumn == column)
        {
            _fileSortDescending = !_fileSortDescending;
        }
        else
        {
            _fileSortColumn = column;
            _fileSortDescending = true;
        }

        ApplyFileSort();
        OnPropertyChanged(nameof(IsFileNumberSortActive));
        OnPropertyChanged(nameof(IsFileNumberSortInactive));
        OnPropertyChanged(nameof(IsFileSizeSortActive));
        OnPropertyChanged(nameof(IsFileSizeSortInactive));
        OnPropertyChanged(nameof(FileSortDirectionIcon));
    }

    private async Task StartAsync()
    {
        await CancelAndAwaitActiveOperationsAsync().ConfigureAwait(true);
        ExpireRetainedSessionIfNeeded();
        StartCommunicationSession();
        _autoDownloadsPaused = false;
        _settings = _settings with { LastPort = SelectedPort, OutputDirectory = OutputDirectory };
        SaveSettings();
        IsPortConfigured = true;
        IsGraphVisible = false;
        Status = $"Resetting {SelectedPort}";
        await ResetSelectedPortAsync().ConfigureAwait(true);
        Status = $"Checking {SelectedPort}";
        await ReadFilesAsync().ConfigureAwait(true);
    }

    private Task CancelOperationAsync()
    {
        _autoDownloadsPaused = true;
        _foregroundOperationCancellation?.Cancel();
        _manualDownloadCancellation?.Cancel();
        CancelBackgroundDownloads();
        Status = "Cancelling current operation";
        return Task.CompletedTask;
    }

    private async Task OpenSettingsAsync()
    {
        Status = "Stopping current operation";
        await CancelAndAwaitActiveOperationsAsync().ConfigureAwait(true);
        EndCommunicationSession();
        _autoDownloadsPaused = true;
        CloseSettingsOverlay();
        IsPortConfigured = false;
        IsGraphVisible = false;
        IsBusy = false;
        RefreshPorts();
        Status = "Select serial port";
    }

    private Task OpenGaugeSettingsAsync()
    {
        IsAppSettingsVisible = false;
        IsEngineeringModeVisible = false;
        IsGaugeSettingsVisible = true;
        RaiseDeviceInformationChanged();
        return Task.CompletedTask;
    }

    private Task OpenEngineeringModeAsync()
    {
        IsAppSettingsVisible = false;
        IsGaugeSettingsVisible = false;
        IsEngineeringModeVisible = true;
        RaiseDeviceInformationChanged();
        return Task.CompletedTask;
    }

    private Task OpenAppSettingsAsync()
    {
        IsGaugeSettingsVisible = false;
        IsEngineeringModeVisible = false;
        IsAppSettingsVisible = true;
        return Task.CompletedTask;
    }

    public void SelectFirmwareImage(string path)
    {
        FirmwareProgressPercent = 0;
        FirmwareLoaderDetails = "Not connected";
        FirmwareImageName = Path.GetFileName(path);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            _settings = _settings with { LastFirmwareDirectory = directory };
            SaveSettings();
        }

        try
        {
            var image = BootloaderApplicationImage.LoadOffsetProduction(path);
            _firmwareImage = image;
            FirmwareImageSummary =
                $"0x{BootloaderApplicationImage.ApplicationStart:X4}-0x{image.HighestProgramAddress:X4} | " +
                $"{image.DataRows.Count + 1:N0} programmed rows | SHA-256 {image.Sha256[..12]}...";
            FirmwareStatus = "Validated Offset production image";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidDataException)
        {
            _firmwareImage = null;
            FirmwareImageSummary = ex.Message;
            FirmwareStatus = "Rejected firmware image";
        }

        OnPropertyChanged(nameof(IsFirmwareImageSelected));
        RaiseFirmwareCommandStates();
    }

    public void FirmwareImageSelectionFailed(string message)
    {
        FirmwareStatus = $"Failed to select image: {message}";
    }

    private Task BeginFirmwareProgramAsync()
    {
        _pendingFirmwareAction = FirmwareAction.Program;
        ShowFirmwareConfirmation();
        return Task.CompletedTask;
    }

    private Task BeginFirmwareRecoveryAsync()
    {
        _pendingFirmwareAction = FirmwareAction.Recover;
        ShowFirmwareConfirmation();
        return Task.CompletedTask;
    }

    private void ShowFirmwareConfirmation()
    {
        FirmwareConfirmationText = string.Empty;
        IsFirmwareConfirmationVisible = true;
        OnPropertyChanged(nameof(FirmwareConfirmationPrompt));
        OnPropertyChanged(nameof(FirmwareConfirmationAction));
        RaiseFirmwareCommandStates();
    }

    private Task CancelFirmwareConfirmationAsync()
    {
        IsFirmwareConfirmationVisible = false;
        FirmwareConfirmationText = string.Empty;
        return Task.CompletedTask;
    }

    private async Task ConfirmFirmwareActionAsync()
    {
        var recoveryMode = _pendingFirmwareAction == FirmwareAction.Recover;
        IsFirmwareConfirmationVisible = false;
        FirmwareConfirmationText = string.Empty;
        await ProgramFirmwareAsync(recoveryMode).ConfigureAwait(true);
    }

    private async Task ProgramFirmwareAsync(bool recoveryMode)
    {
        var image = _firmwareImage;
        if (image is null || string.IsNullOrWhiteSpace(SelectedPort))
        {
            FirmwareStatus = "Failed: select a validated firmware image and serial port";
            return;
        }

        var expectedSerial = recoveryMode ? null : _connectedDevice?.DeviceSerial;
        var enteredBootloader = recoveryMode || IsFirmwareRecoveryRequired;
        var updateSucceeded = false;

        CancelBackgroundDownloads();
        _manualDownloadCancellation?.Cancel();
        _autoDownloadsPaused = true;
        IsFirmwareUpdating = true;
        IsBusy = true;
        FirmwareProgressPercent = 0;
        FirmwareLoaderDetails = "Discovering loader";
        FirmwareStatus = recoveryMode ? "Starting firmware recovery" : "Verifying connected gauge";

        await _serialGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (!recoveryMode)
            {
                if (!expectedSerial.HasValue)
                {
                    throw new InvalidOperationException("No connected gauge identity is available.");
                }

                await using (var connection = await OpenVerifiedConnectionAsync(preferFast: true).ConfigureAwait(true))
                {
                    var device = DecodeDevice(connection.Identity.Payload)
                        ?? throw new InvalidDataException("The connected gauge returned an incomplete identity.");
                    if (device.DeviceType != MemoryGaugeDeviceType)
                    {
                        throw new InvalidOperationException(
                            $"Device type {device.DeviceType} is not the supported memory gauge type {MemoryGaugeDeviceType}.");
                    }

                    if (device.DeviceSerial != expectedSerial.Value)
                    {
                        throw new InvalidOperationException(
                            $"Connected serial {device.DeviceSerial} does not match confirmed serial {expectedSerial.Value}.");
                    }
                }

                FirmwareStatus = "Entering bootloader";
                await EnterBootloaderOnceAsync(SelectedPort, FastBaud).ConfigureAwait(true);
                enteredBootloader = true;
                IsGaugeConnected = false;
                ConnectionStatus = "Bootloader";
                ConnectionBrush = new SolidColorBrush(Color.Parse("#D97706"));
                await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(true);
            }

            FirmwareStatus = "Reading bootloader identity";
            FirmwareUpdateResult result;
            BootloaderVersion version;
            await using (var bootloader = new SerialBootloaderClient(SelectedPort, BootloaderBaud, timeoutMs: 2000))
            {
                await bootloader.OpenAsync().ConfigureAwait(true);
                version = await bootloader.ReadVersionAsync(maximumAttempts: 3).ConfigureAwait(true);
                FirmwareLoaderDetails =
                    $"Loader {version.Major}.{version.Minor} | PIC ID 0x{version.DeviceId:X4} | {BootloaderBaud:N0} baud";
                if (version.DeviceId != Pic18F26K80DeviceId)
                {
                    throw new InvalidOperationException(
                        $"Loader device ID 0x{version.DeviceId:X4} does not match PIC18F26K80 ID 0x{Pic18F26K80DeviceId:X4}.");
                }

                var progress = new Progress<FirmwareUpdateProgress>(UpdateFirmwareProgress);
                var updater = new GaugeFirmwareUpdater(bootloader, version);
                result = await updater.ProgramAsync(image, progress, CancellationToken.None).ConfigureAwait(true);

                FirmwareStatus = "Resetting to verified application";
                try
                {
                    await bootloader.ResetToApplicationAsync(CancellationToken.None).ConfigureAwait(true);
                }
                catch (Exception ex) when (IsExpectedUiFailure(ex))
                {
                    FirmwareStatus = $"Reset acknowledgement missed; checking application ({ex.Message})";
                }
            }

            FirmwareStatus = "Reacquiring application at 57,600 baud";
            var restoredIdentity = await WaitForIdentifyAsync(
                SelectedPort,
                WakeBaud,
                timeoutMs: 5000,
                intervalMs: WakePollIntervalMs,
                transactionTimeoutMs: 1000).ConfigureAwait(true);
            var restoredDevice = restoredIdentity is null
                ? null
                : DecodeDevice(restoredIdentity.Payload);
            if (restoredDevice is null)
            {
                throw new IOException("The programmed application was not reacquired after reset.");
            }

            if (restoredDevice.DeviceType != MemoryGaugeDeviceType
                || (expectedSerial.HasValue && restoredDevice.DeviceSerial != expectedSerial.Value))
            {
                throw new InvalidDataException("The application restarted with an unexpected device identity.");
            }

            _connectedDevice = restoredDevice;
            DeviceSummary = DescribeGauge(restoredDevice);
            DeviceDetails = BuildDeviceDetails(restoredDevice, restoredIdentity!.Payload);
            IsFirmwareRecoveryRequired = false;
            IsGaugeConnected = true;
            ConnectionStatus = "Connected";
            ConnectionBrush = new SolidColorBrush(Color.Parse("#2DA55D"));
            FirmwareProgressPercent = 100;
            FirmwareStatus = $"Complete | {result.ProgrammedRows:N0} rows programmed and verified";
            Status = $"Firmware updated on device {restoredDevice.DeviceSerial}";
            RaiseDeviceInformationChanged();
            updateSucceeded = true;
        }
        catch (Exception ex) when (IsExpectedUiFailure(ex) || ex is FormatException)
        {
            if (enteredBootloader)
            {
                EnterFirmwareRecoveryState(ex.Message);
            }
            else
            {
                FirmwareStatus = $"Failed before bootloader entry: {ex.Message}";
                Status = "Firmware update did not start";
            }
        }
        finally
        {
            _serialGate.Release();
            IsBusy = false;
            IsFirmwareUpdating = false;
        }

        if (updateSucceeded)
        {
            _autoDownloadsPaused = false;
            await Task.Delay(FastVerifyDelay).ConfigureAwait(true);
            await ReadFilesAsync().ConfigureAwait(true);
            return;
        }

        if (!IsFirmwareRecoveryRequired && IsGaugeConnected)
        {
            _autoDownloadsPaused = false;
            StartBackgroundDownloads();
        }
    }

    private async Task EnterBootloaderOnceAsync(string portName, int baudRate)
    {
        var options = new SerialGaugeTransportOptions(
            portName,
            baudRate,
            ReadTimeoutMs: 1000,
            WriteTimeoutMs: 1000,
            MaxAttempts: 1,
            EventSink: RecordCommunicationEvent);
        await using var transport = new SerialGaugeTransport(options);
        await transport.OpenAsync().ConfigureAwait(false);
        var response = await transport
            .TransactAsync(GaugeFrame.Create(GaugeCommand.Bootload), CancellationToken.None)
            .ConfigureAwait(false);
        if (response.Command != GaugeCommand.Bootload
            || response.Payload is not [BootloaderProtocolConstants.CommandSuccess])
        {
            throw new GaugeProtocolException("Gauge rejected the bootloader-entry command.");
        }
    }

    private void UpdateFirmwareProgress(FirmwareUpdateProgress progress)
    {
        FirmwareProgressPercent = progress.TotalOperations <= 0
            ? 0
            : Math.Clamp(progress.CompletedOperations * 100.0 / progress.TotalOperations, 0, 100);
        var phase = progress.Phase switch
        {
            FirmwareUpdatePhase.CommittingStartVector => "Committing application",
            FirmwareUpdatePhase.Complete => "Verifying complete",
            _ => progress.Phase.ToString()
        };
        FirmwareStatus = $"{phase} | 0x{progress.Address:X4} | {FirmwareProgressPercent:F0}%";
    }

    private void EnterFirmwareRecoveryState(string message)
    {
        CancelBackgroundDownloads();
        _autoDownloadsPaused = true;
        IsFirmwareRecoveryRequired = true;
        IsGaugeConnected = false;
        IsGraphVisible = false;
        ConnectionStatus = "Bootloader";
        ConnectionBrush = new SolidColorBrush(Color.Parse("#D97706"));
        DeviceSummary = _connectedDevice is null
            ? "Gauge in bootloader"
            : $"Bootloader | Device {_connectedDevice.DeviceSerial}";
        FirmwareStatus = $"Recovery required: {message}";
        Status = "Gauge remains in bootloader mode";
    }

    private bool CanBeginFirmwareProgram()
    {
        return !IsBusy
            && !IsFirmwareUpdating
            && !IsFirmwareRecoveryRequired
            && _firmwareImage is not null
            && _connectedDevice?.DeviceType == MemoryGaugeDeviceType;
    }

    private bool CanBeginFirmwareRecovery()
    {
        return !IsBusy
            && !IsFirmwareUpdating
            && IsFirmwareRecoveryRequired
            && _firmwareImage is not null
            && !string.IsNullOrWhiteSpace(SelectedPort);
    }

    private bool CanConfirmFirmwareAction()
    {
        if (!IsFirmwareConfirmationVisible || IsFirmwareUpdating)
        {
            return false;
        }

        var expected = _pendingFirmwareAction == FirmwareAction.Recover
            ? "RECOVER"
            : GaugeDeviceSerial;
        return FirmwareConfirmationText.Equals(expected, StringComparison.Ordinal);
    }

    private Task CloseSettingsOverlayAsync()
    {
        CloseSettingsOverlay();
        return Task.CompletedTask;
    }

    private void CloseSettingsOverlay()
    {
        if (IsFirmwareUpdating)
        {
            return;
        }

        IsFirmwareConfirmationVisible = false;
        FirmwareConfirmationText = string.Empty;
        IsAppSettingsVisible = false;
        IsGaugeSettingsVisible = false;
        IsEngineeringModeVisible = false;
    }

    private Task ShowGraphAsync()
    {
        if (SelectedFile is { HasPlotData: true, Samples: not null } file)
        {
            ShowFileGraph(file, file.Samples);
        }
        else if (ChartData.Count > 0)
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

    public string BuildRecordFileName(GaugeFileRowViewModel file)
    {
        var serial = _connectedDevice?.DeviceSerial.ToString() ?? "unknown";
        return $"gauge-{serial}-{DateTime.Now:yyyyMMdd}-file-{file.Index:000}.rec";
    }

    public string BuildRawFileName(GaugeFileRowViewModel file)
    {
        var serial = _connectedDevice?.DeviceSerial.ToString() ?? "unknown";
        return $"gauge-{serial}-{DateTime.Now:yyyyMMdd}-file-{file.Index:000}.raw";
    }

    public string BuildSupportBundleFileName()
    {
        var serial = _connectedDevice?.DeviceSerial.ToString() ?? "unknown";
        return $"gauge-{serial}-support-{DateTime.Now:yyyyMMdd-HHmmss}.zip";
    }

    internal void WriteSupportBundle(Stream output)
    {
        var files = _fileTable?.Records
            .Select((record, fileNumber) =>
            {
                var row = Files.FirstOrDefault(file => file.Index == fileNumber);
                return new SupportFileSnapshot(
                    fileNumber,
                    record.Index,
                    record.DataAddress.ToString(),
                    EstimateBytes(_fileTable, fileNumber),
                    record.MeasurementInterval,
                    record.ResetCause,
                    record.IsCrcValid,
                    row?.State ?? "Not downloaded",
                    row?.SampleCount ?? 0,
                    row?.CrcErrorCount ?? 0,
                    row?.BatteryWarningCount ?? 0,
                    row?.AcousticRecordCount ?? 0,
                    row?.AcousticDiagnosticRecordCount ?? 0,
                    row?.RawAcousticRecordCount ?? 0,
                    row?.TimestampRecordCount ?? 0,
                    row?.UnknownRecordCount ?? 0,
                    row?.DataQualityDetail ?? "Not inspected");
            })
            .ToArray() ?? [];

        SensorCalibrationHeader? header = null;
        if (_calibration is not null)
        {
            header = SensorCalibrationHeader.Parse(_calibration.SensorHeader);
        }

        var diagnostics = new GaugeSupportBundle(
            DateTimeOffset.UtcNow,
            typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString() ?? "unknown",
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription,
            new SupportConnectionSnapshot(
                SelectedPort,
                SelectedPortOption?.DisplayName ?? SelectedPort,
                WakeBaud,
                FastBaud,
                IsGaugeConnected,
                ConnectionStatus,
                IgnoreSmallFiles),
            _connectedDevice,
            new SupportMemorySnapshot(
                _fileTable is not null,
                _fileTable?.Records.Count ?? 0,
                _fileTable?.EndOfFile.ToString()),
            new SupportCalibrationSnapshot(
                _calibration is not null,
                _calibration is null ? null : SensorAsciiData.DecodePayload(_calibration.SensorSerial),
                header?.ReferenceClock,
                header?.SensorId,
                header?.CountBias,
                header?.PressureStartupMilliseconds,
                header?.PllClock),
            files,
            _communicationEvents.Summary(),
            _communicationEvents.Snapshot(),
            new SupportFirmwareSnapshot(
                FirmwareImageName,
                _firmwareImage?.Sha256,
                FirmwareStatus,
                FirmwareProgressPercent,
                FirmwareLoaderDetails,
                IsFirmwareUpdating,
                IsFirmwareRecoveryRequired),
            EngineeringDeviceDetails);

        SupportBundleExporter.Write(output, diagnostics, _calibration);
    }

    public LegacyRecordMetadata BuildLegacyRecordMetadata(GaugeFileRowViewModel file)
    {
        if (_connectedDevice is null || _calibration is null || file.Samples is not { Count: > 0 } samples)
        {
            throw new InvalidOperationException("Downloaded gauge data and calibration are required for record export.");
        }

        var sensorIdentity = SensorAsciiData.DecodePayload(_calibration.SensorSerial)
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sensorType = sensorIdentity.ElementAtOrDefault(0) ?? "Unknown";
        var sensorSerial = sensorIdentity.ElementAtOrDefault(1) ?? "Unknown";
        var startOfJob = DateTime.Now - TimeSpan.FromSeconds(samples[^1].Timestamp);

        return new LegacyRecordMetadata(
            startOfJob,
            DescribeDeviceType(_connectedDevice.DeviceType),
            _connectedDevice.DeviceType,
            _connectedDevice.DeviceSerial,
            _connectedDevice.FirmwareMajor,
            _connectedDevice.FirmwareMinor,
            sensorType,
            sensorSerial);
    }

    public void RecordExportSucceeded(GaugeFileRowViewModel file, string savedPath)
    {
        var directory = Path.GetDirectoryName(savedPath);
        if (!string.IsNullOrWhiteSpace(directory) &&
            !string.Equals(directory, _settings.LastRecordExportDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _settings = _settings with { LastRecordExportDirectory = directory };
            SaveSettings();
        }

        SetProtectedStatus($"Saved file {file.Index} as {Path.GetFileName(savedPath)}", TimeSpan.FromSeconds(20));
    }

    public void RecordExportFailed(GaugeFileRowViewModel file, string message)
    {
        SetProtectedStatus($"Could not save file {file.Index}: {message}", TimeSpan.FromSeconds(20));
    }

    public void SupportBundleExportSucceeded(string savedPath)
    {
        var directory = Path.GetDirectoryName(savedPath);
        if (!string.IsNullOrWhiteSpace(directory) &&
            !string.Equals(directory, _settings.LastSupportBundleDirectory, StringComparison.OrdinalIgnoreCase))
        {
            _settings = _settings with { LastSupportBundleDirectory = directory };
            SaveSettings();
        }

        SetProtectedStatus($"Saved support bundle as {Path.GetFileName(savedPath)}", TimeSpan.FromSeconds(20));
    }

    public void SupportBundleExportFailed(string message)
    {
        SetProtectedStatus($"Could not save support bundle: {message}", TimeSpan.FromSeconds(20));
    }

    public void UpdateGraphCursor(ChartCursorEventArgs cursor)
    {
        CursorSample = cursor.SampleIndex.ToString("N0");
        CursorElapsed = FormatElapsedTime(cursor.ElapsedSeconds);
        CursorPressure = $"{cursor.Pressure:F2} psi";
        CursorTemperature = $"{cursor.Temperature:F2} C";
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
        OnPropertyChanged(nameof(HasSetupMessage));
        OnPropertyChanged(nameof(SetupMessage));

        if (!IsPortConfigured)
        {
            Status = Ports.Count == 0
                ? "No serial ports found"
                : $"Selected {SelectedPortOption?.DisplayName ?? SelectedPort}";
        }
    }

    private async Task ReadFilesAsync()
    {
        await RunForegroundOperationAsync(ReadFilesCoreAsync).ConfigureAwait(true);
    }

    private async Task ReadFilesCoreAsync(CancellationToken cancellationToken)
    {
        CancelBackgroundDownloads();
        await AwaitBackgroundDownloadAsync().ConfigureAwait(true);
        _autoDownloadsPaused = false;
        IsBusy = true;
        await _serialGate.WaitAsync(cancellationToken).ConfigureAwait(true);

        try
        {
            Status = $"Waking gauge on {SelectedPort}";
            DeviceData device;
            GaugeFrame identity;
            GaugeFileTable table;
            await using (var connection = await OpenVerifiedConnectionAsync(
                preferFast: IsGaugeConnected,
                cancellationToken).ConfigureAwait(true))
            {
                var session = new GaugeSession(connection.Transport);
                var service = new GaugeJobService(session);
                identity = connection.Identity;
                device = DecodeDevice(identity.Payload)
                    ?? throw new GaugeProtocolException("Gauge returned an incomplete identity payload.");

                if (_connectedDevice is not null && _connectedDevice.DeviceSerial != device.DeviceSerial)
                {
                    ClearRetainedGaugeData();
                }

                _connectedDevice = device;
                DeviceSummary = DescribeGauge(device);
                DeviceDetails = BuildDeviceDetails(device, identity.Payload);
                IsGaugeConnected = true;
                _nextConnectedPollUtc = DateTime.UtcNow + ConnectedPollInterval;
                ConnectionStatus = "Connected";
                ConnectionBrush = new SolidColorBrush(Color.Parse("#2DA55D"));
                RaiseDeviceInformationChanged();

                Status = "Reading file table";
                table = await service.ReadFileTableAsync(cancellationToken: cancellationToken).ConfigureAwait(true);
            }

            _fileTable = table;
            PopulateFiles(table);
            FileSummary = Files.Count == 0 ? "No valid files found" : string.Empty;
            RaiseDeviceInformationChanged();

            if (_calibration is null)
            {
                Status = "Capturing sensor calibration";
                try
                {
                    _calibration = await CaptureCalibrationWithDeadlineAsync(cancellationToken).ConfigureAwait(true);
                    _calibrationFailure = null;
                    ConvertRawDownloads();
                }
                catch (SensorCommunicationException ex)
                {
                    _calibrationFailure = FormatCalibrationFailure(ex);
                    var stillConnected = await ProbeConnectedGaugeAsync(device.DeviceSerial, cancellationToken).ConfigureAwait(true);
                    if (!stillConnected)
                    {
                        TransitionToDisconnected($"Gauge disconnected during calibration: {ex.Message}");
                        return;
                    }

                    Status = $"Sensor unavailable ({_calibrationFailure}); downloading raw files";
                }
            }

            RaiseDeviceInformationChanged();
            Status = Files.Count == 0
                ? "Gauge connected; no files found"
                : _calibration is null
                    ? "Gauge connected; downloading raw files"
                    : "Gauge connected; downloading files";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "Operation cancelled";
        }
        catch (Exception ex) when (IsExpectedUiFailure(ex))
        {
            TransitionToDisconnected($"Connection failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            _serialGate.Release();
        }

        if (IsGaugeConnected && _fileTable is not null)
        {
            StartBackgroundDownloads();
        }
    }

    public async Task DownloadSelectedAsync()
    {
        CancelBackgroundDownloads();
        await AwaitBackgroundDownloadAsync().ConfigureAwait(true);
        if (IsBusy)
        {
            SetProtectedStatus("Already working. Please wait for the current operation to finish.");
            return;
        }

        SelectedFile ??= Files.OrderByDescending(file => file.Index).FirstOrDefault();

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

        var requestedFile = SelectedFile;
        await RunForegroundOperationAsync(
            cancellationToken => DownloadSelectedCoreAsync(requestedFile, cancellationToken)).ConfigureAwait(true);
    }

    private async Task DownloadSelectedCoreAsync(
        GaugeFileRowViewModel requestedFile,
        CancellationToken cancellationToken)
    {
        _autoDownloadsPaused = false;
        _manualDownloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellationToken = _manualDownloadCancellation.Token;
        IsBusy = true;
        Samples.Clear();
        ChartData = ChartDataSet.Empty;
        DownloadProgressPercent = 0;
        DownloadProgressText = "Preparing download";
        ResetReview();

        try
        {
            await TryEnsureCalibrationAsync(cancellationToken).ConfigureAwait(true);
            if (!IsGaugeConnected)
            {
                return;
            }

            var downloaded = await DownloadFileRowAsync(requestedFile, manual: true, cancellationToken).ConfigureAwait(true);
            if (downloaded is not null)
            {
                SelectedFile = requestedFile;
                if (downloaded.Samples.Count > 0)
                {
                    ShowFileGraph(requestedFile, downloaded.Samples);
                    SetProtectedStatus(
                        $"Downloaded file {downloaded.Download.FileIndex} with {downloaded.Samples.Count} sample(s)",
                        TimeSpan.FromSeconds(20));
                }
                else
                {
                    SetProtectedStatus(
                        $"Downloaded raw file {downloaded.Download.FileIndex}; sensor calibration is unavailable",
                        TimeSpan.FromSeconds(20));
                }
            }
        }
        catch (OperationCanceledException)
        {
            SetProtectedStatus($"Cancelled file {requestedFile.Index}; select retry to continue", TimeSpan.FromSeconds(20));
        }
        catch (Exception ex) when (IsExpectedUiFailure(ex))
        {
            await HandleDownloadFailureAsync(requestedFile, ex, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            await HandleDownloadFailureAsync(requestedFile, ex, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _manualDownloadCancellation?.Dispose();
            _manualDownloadCancellation = null;
            IsBusy = false;
            if (IsGaugeConnected && !_autoDownloadsPaused)
            {
                StartBackgroundDownloads();
            }
        }
    }

    public void CancelDownload(GaugeFileRowViewModel file)
    {
        if (!ReferenceEquals(_activeDownload, file) || !file.IsDownloading)
        {
            return;
        }

        _autoDownloadsPaused = true;
        _manualDownloadCancellation?.Cancel();
        _backgroundDownloadCancellation?.Cancel();
        SetProtectedStatus($"Cancelling file {file.Index}", TimeSpan.FromSeconds(20));
    }

    public async Task RetryDownloadAsync(GaugeFileRowViewModel file)
    {
        SelectedFile = file;
        await DownloadSelectedAsync().ConfigureAwait(true);
    }

    private async Task PollGaugeAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(AppPollInterval);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(true);
                if (!IsPortConfigured
                    || IsBusy
                    || IsFirmwareRecoveryRequired
                    || string.IsNullOrWhiteSpace(SelectedPort))
                {
                    continue;
                }

                if (!await _serialGate.WaitAsync(0, cancellationToken).ConfigureAwait(true))
                {
                    continue;
                }

                try
                {
                    await PollConnectionOnceAsync(cancellationToken).ConfigureAwait(true);
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

    private async Task PollConnectionOnceAsync(CancellationToken cancellationToken)
    {
        ExpireRetainedSessionIfNeeded();
        if (IsGaugeConnected)
        {
            if (_backgroundDownloadCancellation is { IsCancellationRequested: false } ||
                DateTime.UtcNow < _nextConnectedPollUtc)
            {
                return;
            }

            _nextConnectedPollUtc = DateTime.UtcNow + ConnectedPollInterval;
            var identity = await TryIdentifyAsync(
                SelectedPort,
                FastBaud,
                ConnectedPollTransactionTimeoutMs,
                cancellationToken).ConfigureAwait(true);
            if (identity is null)
            {
                TransitionToDisconnected("Gauge did not respond to connection check");
                return;
            }

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
            WakeTransactionTimeoutMs,
            cancellationToken).ConfigureAwait(true);
        if (slowIdentity is not null)
        {
            StartCommunicationSession();
            var device = DecodeDevice(slowIdentity.Payload);
            if (device is not null && CanRestoreRetainedSession(device))
            {
                RestoreRetainedSession(device, slowIdentity.Payload);
                Status = $"Gauge {device.DeviceSerial} reconnected; resuming downloads";
                await Task.Delay(FastVerifyDelay).ConfigureAwait(true);
                QueueReadFiles();
                return;
            }

            ClearRetainedGaugeData();
            Status = $"Gauge woke at {WakeBaud}; reading files";
            await Task.Delay(FastVerifyDelay).ConfigureAwait(true);
            QueueReadFiles();
            return;
        }

        if (CanPollSetStatus())
        {
            Status = "Waiting for gauge";
        }
    }

    private void TransitionToDisconnected(
        string reason,
        bool retainGaugeData = true,
        bool cancelActiveOperations = true)
    {
        EndCommunicationSession();
        if (cancelActiveOperations)
        {
            CancelBackgroundDownloads();
            _manualDownloadCancellation?.Cancel();
        }

        if (retainGaugeData && _connectedDevice is not null)
        {
            _retainedDeviceSerial = _connectedDevice.DeviceSerial;
            _retainedPort = SelectedPort;
            _retainedSessionUntilUtc = DateTime.UtcNow + ReconnectRetentionWindow;
        }
        else
        {
            ClearRetainedGaugeData();
        }

        IsGaugeConnected = false;
        _nextConnectedPollUtc = DateTime.MinValue;
        IsGraphVisible = false;
        DeviceSummary = _retainedDeviceSerial.HasValue
            ? $"Gauge {_retainedDeviceSerial.Value} disconnected"
            : "No gauge connected";
        ConnectionStatus = "Disconnected";
        ConnectionBrush = new SolidColorBrush(Color.Parse("#CE0E2D"));
        Status = _retainedDeviceSerial.HasValue
            ? $"{reason}. Reconnect within 10 seconds to resume"
            : $"{reason}. Waiting for gauge";
        RaiseDeviceInformationChanged();
    }

    private async Task<VerifiedGaugeConnection> OpenVerifiedConnectionAsync(
        bool preferFast,
        CancellationToken cancellationToken = default,
        int transactionTimeoutMs = DataTransactionTimeoutMs,
        int transactionDeadlineMs = DataTransactionDeadlineMs)
    {
        if (preferFast)
        {
            var fastIdentity = await TryIdentifyAsync(
                SelectedPort,
                FastBaud,
                transactionTimeoutMs,
                cancellationToken,
                transactionDeadlineMs).ConfigureAwait(true);
            if (fastIdentity is not null)
            {
                return await OpenIdentifiedTransportAsync(
                    SelectedPort,
                    FastBaud,
                    transactionTimeoutMs,
                    cancellationToken,
                    transactionDeadlineMs).ConfigureAwait(true);
            }
        }

        var slowIdentity = await WaitForIdentifyAsync(
            SelectedPort,
            WakeBaud,
            WakeScanTimeoutMs,
            WakePollIntervalMs,
            WakeTransactionTimeoutMs,
            cancellationToken).ConfigureAwait(true);
        if (slowIdentity is not null)
        {
            Status = $"Gauge woke at {WakeBaud}; verifying fast link";
            await Task.Delay(FastVerifyDelay).ConfigureAwait(true);
            try
            {
                return await OpenIdentifiedTransportAsync(
                    SelectedPort,
                    FastBaud,
                    transactionTimeoutMs,
                    cancellationToken,
                    transactionDeadlineMs).ConfigureAwait(true);
            }
            catch (Exception ex) when (IsExpectedUiFailure(ex) || ex is ArgumentOutOfRangeException)
            {
                Status = $"Fast link did not verify; trying {WakeBaud} baud";
                return await OpenIdentifiedTransportAsync(
                    SelectedPort,
                    WakeBaud,
                    transactionTimeoutMs,
                    cancellationToken,
                    transactionDeadlineMs).ConfigureAwait(true);
            }
        }

        Status = $"No slow response; checking {FastBaud} baud";
        return await OpenIdentifiedTransportAsync(
            SelectedPort,
            FastBaud,
            transactionTimeoutMs,
            cancellationToken,
            transactionDeadlineMs).ConfigureAwait(true);
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

    private async Task<GaugeFrame?> TryIdentifyAsync(
        string portName,
        int baudRate,
        int timeoutMs,
        CancellationToken cancellationToken = default,
        int transactionDeadlineMs = DataTransactionDeadlineMs)
    {
        try
        {
            await using var transport = CreateTransport(
                portName,
                baudRate,
                timeoutMs,
                transactionDeadlineMs);
            await transport.OpenAsync(cancellationToken).ConfigureAwait(false);
            var session = new GaugeSession(transport);
            return await session.IdentifyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpectedUiFailure(ex) || ex is ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private async Task<GaugeFrame?> WaitForIdentifyAsync(
        string portName,
        int baudRate,
        int timeoutMs,
        int intervalMs,
        int transactionTimeoutMs,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
        try
        {
            await using var transport = CreateTransport(
                portName,
                baudRate,
                transactionTimeoutMs,
                WakeTransactionDeadlineMs);
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (IsExpectedUiFailure(ex) || ex is ArgumentOutOfRangeException)
        {
            return null;
        }

        return null;
    }

    private async Task<VerifiedGaugeConnection> OpenIdentifiedTransportAsync(
        string portName,
        int baudRate,
        int timeoutMs,
        CancellationToken cancellationToken = default,
        int transactionDeadlineMs = DataTransactionDeadlineMs)
    {
        var transport = CreateTransport(portName, baudRate, timeoutMs, transactionDeadlineMs);
        try
        {
            await transport.OpenAsync(cancellationToken).ConfigureAwait(false);
            var session = new GaugeSession(transport);
            var identity = await session.IdentifyAsync(cancellationToken).ConfigureAwait(false);
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

    private SerialGaugeTransport CreateTransport(
        string portName,
        int baudRate,
        int timeoutMs,
        int transactionDeadlineMs = DataTransactionDeadlineMs)
    {
        return new SerialGaugeTransport(new SerialGaugeTransportOptions(
            portName,
            baudRate,
            ReadTimeoutMs: timeoutMs,
            WriteTimeoutMs: timeoutMs,
            TransactionTimeoutMs: transactionDeadlineMs,
            EventSink: RecordCommunicationEvent));
    }

    private void StartBackgroundDownloads()
    {
        if (_autoDownloadsPaused || !IsGaugeConnected || _fileTable is null || Files.Count == 0)
        {
            return;
        }

        if (_backgroundDownloadCancellation is { IsCancellationRequested: false })
        {
            return;
        }

        _backgroundDownloadCancellation?.Dispose();
        _backgroundDownloadCancellation = new CancellationTokenSource();
        _backgroundDownloadTask = RunBackgroundDownloadsAsync(_backgroundDownloadCancellation.Token);
    }

    private void CancelBackgroundDownloads()
    {
        _backgroundDownloadCancellation?.Cancel();
    }

    private async Task RunBackgroundDownloadsAsync(CancellationToken cancellationToken)
    {
        var failedFileCount = 0;
        try
        {
            foreach (var file in Files
                .Where(file => !file.IsDownloaded)
                .OrderByDescending(file => file.Index)
                .ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var recoveryAttempted = false;
                while (!file.IsDownloaded)
                {
                    try
                    {
                        await DownloadFileRowAsync(file, manual: false, cancellationToken).ConfigureAwait(true);
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        var stillConnected = await VerifyGaugeAfterDownloadFailureAsync(
                            file,
                            cancellationToken).ConfigureAwait(true);
                        if (!stillConnected)
                        {
                            return;
                        }

                        if (!recoveryAttempted)
                        {
                            recoveryAttempted = true;
                            file.MarkRetrying();
                            SetProtectedStatus(
                                $"File {file.Index} communication recovered; resuming from the last confirmed packet",
                                TimeSpan.FromSeconds(15));
                            await Task.Delay(DownloadRecoveryDelay, cancellationToken).ConfigureAwait(true);
                            continue;
                        }

                        failedFileCount++;
                        file.MarkError(ex.Message);
                        break;
                    }
                }
            }

            if (CanPollSetStatus())
            {
                Status = failedFileCount == 0
                    ? "Gauge connected; files ready"
                    : $"Gauge connected; {failedFileCount} file(s) need retry";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            var file = _activeDownload ?? Files.FirstOrDefault(row => !row.IsDownloaded);
            if (file is not null)
            {
                file.MarkInterrupted();
            }

            TransitionToDisconnected($"Background download stopped unexpectedly: {ex.Message}");
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

    private async Task AwaitBackgroundDownloadAsync()
    {
        var task = _backgroundDownloadTask;
        if (task is null || task.IsCompleted)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<bool> TryEnsureCalibrationAsync(CancellationToken cancellationToken)
    {
        if (_calibration is not null)
        {
            return true;
        }

        await _serialGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            SetProtectedStatus("Capturing sensor calibration", SensorCalibrationDeadline);
            try
            {
                _calibration = await CaptureCalibrationWithDeadlineAsync(cancellationToken).ConfigureAwait(true);
                _calibrationFailure = null;
                ConvertRawDownloads();
                RaiseDeviceInformationChanged();
                return true;
            }
            catch (SensorCommunicationException ex)
            {
                _calibrationFailure = FormatCalibrationFailure(ex);
                RaiseDeviceInformationChanged();
                var expectedSerial = _connectedDevice?.DeviceSerial;
                if (!expectedSerial.HasValue
                    || !await ProbeConnectedGaugeAsync(expectedSerial.Value, cancellationToken).ConfigureAwait(true))
                {
                    TransitionToDisconnected($"Gauge disconnected during calibration: {ex.Message}");
                    return false;
                }

                SetProtectedStatus(
                    $"Sensor unavailable ({_calibrationFailure}); raw download remains available",
                    TimeSpan.FromSeconds(20));
                return false;
            }
        }
        finally
        {
            _serialGate.Release();
        }
    }

    private async Task<SensorCalibrationBundle> CaptureCalibrationWithDeadlineAsync(
        CancellationToken cancellationToken)
    {
        using var deadlineSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadlineSource.CancelAfter(SensorCalibrationDeadline);
        try
        {
            await using var connection = await OpenVerifiedConnectionAsync(
                preferFast: true,
                cancellationToken: deadlineSource.Token,
                transactionTimeoutMs: SensorTransactionTimeoutMs,
                transactionDeadlineMs: SensorTransactionDeadlineMs).ConfigureAwait(true);
            var service = new GaugeJobService(new GaugeSession(connection.Transport));
            return await service.CaptureSensorCalibrationAsync(
                cancellationToken: deadlineSource.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SensorCommunicationException(
                SensorCommunicationFailure.Timeout,
                $"Sensor calibration exceeded its {SensorCalibrationDeadline.TotalSeconds:F0} second deadline.",
                ex);
        }
        catch (TimeoutException ex)
        {
            throw new SensorCommunicationException(
                SensorCommunicationFailure.Timeout,
                $"Sensor calibration timed out: {ex.Message}",
                ex);
        }
    }

    private void ConvertRawDownloads()
    {
        if (_calibration is null)
        {
            return;
        }

        foreach (var file in Files.Where(file => file.Download is not null && file.Samples is null))
        {
            var download = file.Download!;
            var samples = GaugeJobService.BuildCalibratedSamples(download, _calibration);
            var summary = MemoryGaugeRecordSummary.Analyze(
                download.RawBytes,
                download.FileRecord.DataAddress.Value);
            file.MarkDownloaded(
                download,
                samples,
                samples.Count(sample => sample.BatteryStatus != 0),
                summary);
        }
    }

    private async Task<DownloadedGaugeFile?> DownloadFileRowAsync(
        GaugeFileRowViewModel file,
        bool manual,
        CancellationToken cancellationToken)
    {
        if (_fileTable is null)
        {
            return null;
        }

        if (file.Download is not null)
        {
            if (file.Samples is null && _calibration is not null)
            {
                var converted = GaugeJobService.BuildCalibratedSamples(file.Download, _calibration);
                var summary = MemoryGaugeRecordSummary.Analyze(
                    file.Download.RawBytes,
                    file.Download.FileRecord.DataAddress.Value);
                file.MarkDownloaded(
                    file.Download,
                    converted,
                    converted.Count(sample => sample.BatteryStatus != 0),
                    summary);
            }

            return new DownloadedGaugeFile(file.Download, file.Samples ?? []);
        }

        await _serialGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            _activeDownload = file;
            file.MarkDownloading();
            var label = manual ? "Downloading" : "Auto-downloading";
            SetProtectedStatus($"{label} file {file.Index}", TimeSpan.FromSeconds(30));
            await using var connection = await OpenVerifiedConnectionAsync(
                preferFast: true,
                cancellationToken).ConfigureAwait(true);
            var service = new GaugeJobService(new GaugeSession(connection.Transport));
            var timer = Stopwatch.StartNew();
            var converter = _calibration is null
                ? null
                : GaugeJobService.CreateSampleConverter(_fileTable.Records[file.Index], _calibration);
            var streamingSamples = new List<CalibratedGaugeSample>(file.EstimatedSamples);
            var processedBytes = 0;
            var lastPreviewElapsed = TimeSpan.Zero;
            var batteryWarningCount = 0;
            var recordSummary = MemoryGaugeRecordSummary.Empty;
            var progress = new Progress<MemoryReadProgress>(progress =>
            {
                file.CapturePartialRaw(progress);
                if (!file.IsDownloading)
                {
                    return;
                }

                file.MarkProgress(progress, timer.Elapsed);
                if (manual)
                {
                    UpdateDownloadProgress(progress, timer.Elapsed);
                }

                if (converter is null)
                {
                    return;
                }

                var availableBytes = Math.Min(progress.BytesRead, progress.Buffer.Length);
                var completeBytes = availableBytes / MemoryGaugeDataRecord.Length * MemoryGaugeDataRecord.Length;
                var shouldRefresh = processedBytes == 0 ||
                    timer.Elapsed - lastPreviewElapsed >= LiveChartRefreshInterval ||
                    progress.BytesRead >= progress.TotalBytes;
                if (!shouldRefresh || completeBytes <= processedBytes)
                {
                    return;
                }

                var firstRecordIndex = processedBytes / MemoryGaugeDataRecord.Length;
                var batchBytes = progress.Buffer.Span.Slice(processedBytes, completeBytes - processedBytes);
                var batch = converter.Convert(
                    batchBytes,
                    firstRecordIndex,
                    streamingSamples.Count);
                recordSummary = recordSummary.Combine(MemoryGaugeRecordSummary.Analyze(batchBytes));
                streamingSamples.AddRange(batch);
                processedBytes = completeBytes;
                lastPreviewElapsed = timer.Elapsed;
                batteryWarningCount += batch.Count(sample => sample.BatteryStatus != 0);
                file.MarkPartialSamples(streamingSamples, batteryWarningCount, recordSummary);
                RaiseCommandStates();

                if (IsGraphVisible && ReferenceEquals(SelectedFile, file))
                {
                    RefreshFileGraph(file, streamingSamples);
                }
            });
            var download = await service.DownloadFileAsync(
                _fileTable,
                file.Index,
                progress: progress,
                cancellationToken: cancellationToken,
                existingBytes: file.PartialRawBytes).ConfigureAwait(true);
            var finalRecordSummary = MemoryGaugeRecordSummary.Analyze(download.RawBytes, download.FileRecord.DataAddress.Value);
            IReadOnlyList<CalibratedGaugeSample> samples;
            if (_calibration is null)
            {
                samples = [];
                file.MarkRawDownloaded(download, finalRecordSummary);
            }
            else
            {
                samples = GaugeJobService.BuildCalibratedSamples(download, _calibration);
                file.MarkDownloaded(
                    download,
                    samples,
                    batteryWarningCount: samples.Count(sample => sample.BatteryStatus != 0),
                    finalRecordSummary);
            }
            RaiseCommandStates();
            if (!manual && IsGraphVisible && ReferenceEquals(SelectedFile, file))
            {
                RefreshFileGraph(file, samples);
            }

            return new DownloadedGaugeFile(download, samples);
        }
        catch (OperationCanceledException)
        {
            file.MarkCancelled();
            throw;
        }
        catch (Exception ex) when (IsExpectedUiFailure(ex))
        {
            file.MarkError(ex.Message);
            throw;
        }
        finally
        {
            if (ReferenceEquals(_activeDownload, file))
            {
                _activeDownload = null;
            }

            _serialGate.Release();
        }
    }

    private void ShowFileGraph(GaugeFileRowViewModel file, IReadOnlyList<CalibratedGaugeSample> samples)
    {
        ResetCursorReadout();
        RefreshFileGraph(file, samples);
        IsGraphVisible = true;
        UpdateSelectedFileActions();
    }

    private void RefreshFileGraph(GaugeFileRowViewModel file, IReadOnlyList<CalibratedGaugeSample> samples)
    {
        Samples.Clear();

        foreach (var sample in samples.TakeLast(25))
        {
            Samples.Add(SampleRowViewModelFactory.FromSample(sample));
        }

        ChartData = ChartDataSet.FromSamples(samples);

        UpdateReview(file, samples);
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

        for (var index = 0; index < table.Records.Count; index++)
        {
            var record = table.Records[index];
            var bytes = sizes[index];
            var samples = bytes / MemoryGaugeDataRecord.Length * 2;
            if (IgnoreSmallFiles && samples < SmallFileSampleThreshold)
            {
                continue;
            }

            if (existingRows.TryGetValue(index, out var existing))
            {
                Files.Add(existing);
                continue;
            }

            Files.Add(new GaugeFileRowViewModel(
                index,
                bytes,
                samples,
                record.MeasurementInterval,
                (long)samples * record.MeasurementInterval,
                FormatBytes(bytes),
                largest == 0 ? 0 : Math.Max(4, bytes * 100.0 / largest),
                record.IsCrcValid));
        }

        ApplyFileSort();
        SelectedFile = Files.FirstOrDefault();
    }

    private void ApplyFileSort()
    {
        var selectedIndex = SelectedFile?.Index;
        var sorted = (_fileSortColumn, _fileSortDescending) switch
        {
            (FileListSortColumn.FileNumber, true) => Files.OrderByDescending(file => file.Index),
            (FileListSortColumn.FileNumber, false) => Files.OrderBy(file => file.Index),
            (FileListSortColumn.Size, true) => Files.OrderByDescending(file => file.Bytes).ThenByDescending(file => file.Index),
            _ => Files.OrderBy(file => file.Bytes).ThenByDescending(file => file.Index)
        };
        var rows = sorted.ToArray();

        Files.Clear();
        foreach (var row in rows)
        {
            Files.Add(row);
        }

        SelectedFile = selectedIndex is null
            ? null
            : Files.FirstOrDefault(file => file.Index == selectedIndex.Value);
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

    private async Task RunForegroundOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (_foregroundOperationTask is { IsCompleted: false })
        {
            return;
        }

        _foregroundOperationCancellation?.Dispose();
        var source = CancellationTokenSource.CreateLinkedTokenSource(_pollingCancellation.Token);
        _foregroundOperationCancellation = source;
        var task = operation(source.Token);
        _foregroundOperationTask = task;
        try
        {
            await task.ConfigureAwait(true);
        }
        finally
        {
            if (ReferenceEquals(_foregroundOperationTask, task))
            {
                _foregroundOperationTask = null;
                _foregroundOperationCancellation = null;
            }

            source.Dispose();
        }
    }

    private async Task CancelAndAwaitActiveOperationsAsync()
    {
        _foregroundOperationCancellation?.Cancel();
        _manualDownloadCancellation?.Cancel();
        CancelBackgroundDownloads();

        var tasks = new[] { _foregroundOperationTask, _backgroundDownloadTask }
            .Where(task => task is not null)
            .Cast<Task>()
            .Distinct()
            .ToArray();
        if (tasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (IsExpectedUiFailure(ex))
        {
            // The caller is deliberately abandoning the failed operation.
        }
    }

    public async Task ShutdownAsync()
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;
        _pollingCancellation.Cancel();
        await CancelAndAwaitActiveOperationsAsync().ConfigureAwait(true);
        try
        {
            await _pollingTask.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }

        _manualDownloadCancellation?.Dispose();
        _backgroundDownloadCancellation?.Dispose();
        _foregroundOperationCancellation?.Dispose();
        _pollingCancellation.Dispose();
        _serialGate.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(ShutdownAsync());
    }

    private async Task ResetSelectedPortAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedPort))
        {
            return;
        }

        await using var transport = CreateTransport(
            SelectedPort,
            IsGaugeConnected ? FastBaud : WakeBaud,
            WakeTransactionTimeoutMs);
        try
        {
            await transport.OpenAsync(_pollingCancellation.Token).ConfigureAwait(true);
            await transport.CloseAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex) when (IsExpectedUiFailure(ex) || ex is ArgumentOutOfRangeException)
        {
            Status = $"Resetting {SelectedPort}: {ex.Message}";
        }
    }

    private async Task<bool> ProbeConnectedGaugeAsync(
        uint expectedSerial,
        CancellationToken cancellationToken)
    {
        var identity = await TryIdentifyAsync(
            SelectedPort,
            FastBaud,
            ConnectedPollTransactionTimeoutMs,
            cancellationToken).ConfigureAwait(true);
        var device = identity is null ? null : DecodeDevice(identity.Payload);
        return device?.DeviceSerial == expectedSerial;
    }

    private async Task HandleDownloadFailureAsync(
        GaugeFileRowViewModel file,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var stillConnected = await VerifyGaugeAfterDownloadFailureAsync(
            file,
            cancellationToken).ConfigureAwait(true);
        if (!stillConnected)
        {
            return;
        }

        file.MarkError(exception.Message);
        SetProtectedStatus(
            $"File {file.Index} failed after retries; gauge remains connected. Select the file to retry",
            TimeSpan.FromSeconds(20));
    }

    private async Task<bool> VerifyGaugeAfterDownloadFailureAsync(
        GaugeFileRowViewModel file,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        var expectedSerial = _connectedDevice?.DeviceSerial;
        file.MarkInterrupted();
        TransitionToDisconnected(
            $"Gauge communication failed while downloading file {file.Index}",
            cancelActiveOperations: false);

        GaugeFrame? recoveredIdentity = null;
        DeviceData? recoveredDevice = null;
        if (expectedSerial.HasValue)
        {
            try
            {
                recoveredIdentity = await TryIdentifyAsync(
                    SelectedPort,
                    FastBaud,
                    ConnectedPollTransactionTimeoutMs,
                    cancellationToken).ConfigureAwait(true);
                recoveredDevice = recoveredIdentity is null
                    ? null
                    : DecodeDevice(recoveredIdentity.Payload);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception)
            {
                recoveredIdentity = null;
                recoveredDevice = null;
            }
        }

        if (recoveredIdentity is not null
            && recoveredDevice is not null
            && recoveredDevice.DeviceSerial == expectedSerial)
        {
            StartCommunicationSession();
            RestoreRetainedSession(recoveredDevice, recoveredIdentity.Payload);
            Status = $"Gauge communication recovered; resuming file {file.Index}";
            return true;
        }

        TransitionToDisconnected($"Gauge disconnected while downloading file {file.Index}");
        return false;
    }

    private static string FormatCalibrationFailure(SensorCommunicationException exception)
    {
        return exception.Failure switch
        {
            SensorCommunicationFailure.ErrorSensorComms => "ERROR_SENSOR_COMMS",
            SensorCommunicationFailure.Timeout => "Sensor timeout",
            SensorCommunicationFailure.InitialiseFailed => "Sensor did not initialise",
            _ => "Invalid sensor response"
        };
    }

    private bool CanRestoreRetainedSession(DeviceData device)
    {
        return DateTime.UtcNow <= _retainedSessionUntilUtc
            && _retainedDeviceSerial == device.DeviceSerial
            && string.Equals(_retainedPort, SelectedPort, StringComparison.OrdinalIgnoreCase)
            && _fileTable is not null;
    }

    private void RestoreRetainedSession(DeviceData device, byte[] identityPayload)
    {
        _connectedDevice = device;
        DeviceSummary = DescribeGauge(device);
        DeviceDetails = BuildDeviceDetails(device, identityPayload);
        IsGaugeConnected = true;
        ConnectionStatus = "Connected";
        ConnectionBrush = new SolidColorBrush(Color.Parse("#2DA55D"));
        _nextConnectedPollUtc = DateTime.UtcNow + ConnectedPollInterval;
        ClearRetentionMarker();
        RaiseDeviceInformationChanged();
    }

    private void ExpireRetainedSessionIfNeeded()
    {
        if (_retainedDeviceSerial.HasValue && DateTime.UtcNow > _retainedSessionUntilUtc)
        {
            ClearRetainedGaugeData();
            DeviceSummary = "No gauge connected";
            DeviceDetails = string.Empty;
            FileSummary = "No file table loaded";
            RaiseDeviceInformationChanged();
        }
    }

    private void ClearRetentionMarker()
    {
        _retainedDeviceSerial = null;
        _retainedPort = string.Empty;
        _retainedSessionUntilUtc = DateTime.MinValue;
    }

    private void ClearRetainedGaugeData()
    {
        ClearRetentionMarker();
        Files.Clear();
        Samples.Clear();
        ChartData = ChartDataSet.Empty;
        SelectedFile = null;
        _connectedDevice = null;
        _fileTable = null;
        _calibration = null;
        _calibrationFailure = null;
        ResetReview();
    }

    private void QueueReadFiles()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_isShuttingDown && _foregroundOperationTask is not { IsCompleted: false })
            {
                _ = ReadFilesAsync();
            }
        });
    }

    private void ResetReview()
    {
        ReviewFile = "--";
        ReviewSampleCount = "--";
        ResetCursorReadout();
        PressureMinimum = "--";
        PressureMaximum = "--";
        TemperatureMinimum = "--";
        TemperatureMaximum = "--";
        JobDuration = "--";
    }

    private void ResetCursorReadout()
    {
        CursorSample = "--";
        CursorElapsed = "--";
        CursorPressure = "--";
        CursorTemperature = "--";
    }

    private void UpdateReview(GaugeFileRowViewModel file, IReadOnlyList<CalibratedGaugeSample> samples)
    {
        var pressureMin = samples.Min(sample => sample.Pressure);
        var pressureMax = samples.Max(sample => sample.Pressure);
        var temperatureMin = samples.Min(sample => sample.Temperature);
        var temperatureMax = samples.Max(sample => sample.Temperature);
        var durationSeconds = samples.Count == 0 ? 0 : samples[^1].Timestamp - samples[0].Timestamp;
        var duration = TimeSpan.FromSeconds(durationSeconds);

        ReviewFile = $"File {file.Index}";
        ReviewSampleCount = samples.Count.ToString("N0");
        PressureMinimum = $"{pressureMin:F2} psi";
        PressureMaximum = $"{pressureMax:F2} psi";
        TemperatureMinimum = $"{temperatureMin:F2} C";
        TemperatureMaximum = $"{temperatureMax:F2} C";
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

    private static string DescribeDeviceType(uint deviceType)
    {
        return deviceType switch
        {
            100200 => "Northstar Acoustic Quartz Transducer",
            100230 => "Northstar 4000AH Quartz Transducer",
            _ => "Northstar Quartz Transducer"
        };
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

    private static string FormatElapsedTime(double elapsedSeconds)
    {
        var elapsed = TimeSpan.FromSeconds(Math.Max(0, elapsedSeconds));
        if (elapsed.TotalDays >= 1)
        {
            return $"{(int)elapsed.TotalDays} d {elapsed.Hours:00} h {elapsed.Minutes:00} min";
        }

        if (elapsed.TotalHours >= 1)
        {
            return $"{(int)elapsed.TotalHours} h {elapsed.Minutes:00} min {elapsed.Seconds:00} sec";
        }

        if (elapsed.TotalMinutes >= 1)
        {
            return $"{(int)elapsed.TotalMinutes} min {elapsed.Seconds:00} sec";
        }

        return $"{elapsed.TotalSeconds:F0} sec";
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

        if (OpenSettingsCommand is RelayCommand openSettings)
        {
            openSettings.RaiseCanExecuteChanged();
        }

        if (OpenAppSettingsCommand is RelayCommand openAppSettings)
        {
            openAppSettings.RaiseCanExecuteChanged();
        }

        if (CancelOperationCommand is RelayCommand cancelOperation)
        {
            cancelOperation.RaiseCanExecuteChanged();
        }

        if (ShowGraphCommand is RelayCommand showGraph)
        {
            showGraph.RaiseCanExecuteChanged();
        }

        if (CloseSettingsOverlayCommand is RelayCommand closeSettings)
        {
            closeSettings.RaiseCanExecuteChanged();
        }

        RaiseFirmwareCommandStates();
    }

    private void RaiseFirmwareCommandStates()
    {
        if (BeginFirmwareProgramCommand is RelayCommand program)
        {
            program.RaiseCanExecuteChanged();
        }

        if (BeginFirmwareRecoveryCommand is RelayCommand recover)
        {
            recover.RaiseCanExecuteChanged();
        }

        if (ConfirmFirmwareActionCommand is RelayCommand confirm)
        {
            confirm.RaiseCanExecuteChanged();
        }

        if (CancelFirmwareConfirmationCommand is RelayCommand cancel)
        {
            cancel.RaiseCanExecuteChanged();
        }
    }

    private void UpdateSelectedFileActions()
    {
        foreach (var file in Files)
        {
            file.IsSelected = ReferenceEquals(file, SelectedFile);
        }
    }

    private void RaiseDeviceInformationChanged()
    {
        OnPropertyChanged(nameof(GaugeDeviceType));
        OnPropertyChanged(nameof(GaugeDeviceSerial));
        OnPropertyChanged(nameof(GaugeFirmware));
        OnPropertyChanged(nameof(GaugePcb));
        OnPropertyChanged(nameof(GaugeMeasurementInterval));
        OnPropertyChanged(nameof(GaugeMemoryMode));
        OnPropertyChanged(nameof(GaugeEraseStatus));
        OnPropertyChanged(nameof(EngineeringTransport));
        OnPropertyChanged(nameof(EngineeringFileTable));
        OnPropertyChanged(nameof(EngineeringCalibration));
        OnPropertyChanged(nameof(EngineeringDeviceDetails));
        RaiseFirmwareCommandStates();
        RaiseEngineeringCommunicationChanged();
    }

    private void StartCommunicationSession()
    {
        _communicationEvents.StartSession(SelectedPort);
        RaiseEngineeringCommunicationChanged();
    }

    private void EndCommunicationSession()
    {
        _communicationEvents.EndSession();
        RaiseEngineeringCommunicationChanged();
    }

    private void RecordCommunicationEvent(SerialGaugeTransportEvent value)
    {
        _communicationEvents.Record(value);
        if (Interlocked.Exchange(ref _communicationRefreshPending, 1) == 0)
        {
            _ = RefreshEngineeringCommunicationAsync();
        }
    }

    private async Task RefreshEngineeringCommunicationAsync()
    {
        await Task.Delay(250).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _communicationRefreshPending, 0);
            if (IsEngineeringModeVisible)
            {
                RaiseEngineeringCommunicationChanged();
            }
        });
    }

    private void RaiseEngineeringCommunicationChanged()
    {
        OnPropertyChanged(nameof(EngineeringCommunicationHealth));
        OnPropertyChanged(nameof(EngineeringCommunicationBrush));
        OnPropertyChanged(nameof(EngineeringCommunicationSession));
        OnPropertyChanged(nameof(EngineeringCommunicationTransactions));
        OnPropertyChanged(nameof(EngineeringCommunicationRetries));
        OnPropertyChanged(nameof(EngineeringCommunicationCrcErrors));
        OnPropertyChanged(nameof(EngineeringCommunicationRecovered));
        OnPropertyChanged(nameof(EngineeringCommunicationFailures));
        OnPropertyChanged(nameof(EngineeringCommunicationLastIssue));
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
    string OutputDirectory = "",
    string LastRecordExportDirectory = "",
    string LastSupportBundleDirectory = "",
    string LastFirmwareDirectory = "");

internal enum FirmwareAction
{
    Program,
    Recover
}

public enum FileListSortColumn
{
    FileNumber,
    Size
}

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
    private int _batteryWarningCount;
    private int _crcErrorCount;
    private double _progressPercent;
    private string _duration;
    private string _state = "Queued";
    private string _progressText = string.Empty;
    private ReadOnlyMemory<byte> _partialRawBuffer;
    private int _partialRawByteCount;

    public GaugeFileRowViewModel(
        int index,
        int bytes,
        int estimatedSamples,
        ushort measurementInterval,
        long estimatedDurationSeconds,
        string size,
        double sizePercent,
        bool isCrcValid)
    {
        Index = index;
        Bytes = bytes;
        EstimatedSamples = estimatedSamples;
        MeasurementInterval = measurementInterval;
        EstimatedDurationSeconds = estimatedDurationSeconds;
        _duration = FormatFileDuration(estimatedDurationSeconds);
        Size = size;
        SizePercent = sizePercent;
        IsCrcValid = isCrcValid;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index { get; }

    public int Bytes { get; }

    public int EstimatedSamples { get; }

    public ushort MeasurementInterval { get; }

    public string Interval => MeasurementInterval == 1 ? "1 sec" : $"{MeasurementInterval} sec";

    public long EstimatedDurationSeconds { get; }

    public string Duration
    {
        get => _duration;
        private set => SetField(ref _duration, value);
    }

    public string Size { get; }

    public double SizePercent { get; }

    public bool IsCrcValid { get; }

    public bool IsDownloaded => Download is not null;

    public bool CanExportRecord => IsDownloaded && HasPlotData;

    public bool CanExportRaw => IsRawOnly;

    public bool IsRawOnly => Download is not null && Samples is null;

    public bool HasPlotData => Samples is { Count: >= 2 };

    public GaugeMemoryDownload? Download { get; private set; }

    public IReadOnlyList<CalibratedGaugeSample>? Samples { get; private set; }

    public ReadOnlyMemory<byte> PartialRawBytes => _partialRawBuffer[.._partialRawByteCount];

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

    public int BatteryWarningCount
    {
        get => _batteryWarningCount;
        private set => SetField(ref _batteryWarningCount, value);
    }

    public int CrcErrorCount
    {
        get => _crcErrorCount;
        private set => SetField(ref _crcErrorCount, value);
    }

    public int AcousticRecordCount { get; private set; }

    public int FailedAcousticRecordCount { get; private set; }

    public int AcousticDiagnosticRecordCount { get; private set; }

    public int RawAcousticRecordCount { get; private set; }

    public int TimestampRecordCount { get; private set; }

    public int AuxiliaryRecordCount { get; private set; }

    public int UnknownRecordCount { get; private set; }

    public int ExcludedRecordCount => AcousticRecordCount + AuxiliaryRecordCount + UnknownRecordCount;

    public bool ContainsAcousticData => AcousticRecordCount > 0 || AcousticDiagnosticRecordCount > 0;

    public bool ContainsRawAcousticData => RawAcousticRecordCount > 0;

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

    public bool IsRetryAvailable => State is "Cancelled" or "Error" or "Interrupted";

    public bool IsRowRetryVisible => IsRetryAvailable && HasPlotData;

    public bool CanFileAction => !IsRawOnly && (!IsDownloading || HasPlotData);

    public string RowStatus => State switch
    {
        "Downloading" => string.IsNullOrWhiteSpace(ProgressText) ? "Downloading" : $"Downloading {ProgressText}",
        "Downloaded" when HasErrors => "Ready - data errors",
        "Downloaded" when HasWarnings => "Ready - review warnings",
        "Downloaded" => "Ready",
        "Raw downloaded" => "Raw ready - sensor calibration unavailable",
        "Error" => "Download failed - select to retry",
        "Cancelled" => ProgressPercent > 0
            ? $"Cancelled at {ProgressPercent:F0}% - select to retry"
            : "Cancelled - select to retry",
        "Interrupted" => ProgressPercent > 0
            ? $"Connection lost at {ProgressPercent:F0}% - waiting to resume"
            : "Connection lost - waiting to resume",
        _ => "Waiting"
    };

    public string ReviewStatus => IsDownloading ? "Downloading" : RowStatus;

    public Geometry ActionIcon => HasPlotData ? GraphGeometry : DownloadGeometry;

    public IBrush ActionBrush => IsRawOnly
        ? WarningBrush
        : !HasPlotData || State == "Error" || HasErrors
            ? ErrorBrush
            : HasWarnings ? WarningBrush : ReadyBrush;

    public IBrush StatusBrush => State == "Error" || HasErrors
        ? ErrorBrush
        : HasWarnings ? WarningBrush : State == "Downloaded" ? ReadyBrush : MutedBrush;

    public IBrush DataQualityBrush => HasErrors
        ? ErrorBrush
        : HasWarnings ? WarningBrush : ReadyBrush;

    public string DataQualityDetail
    {
        get
        {
            var details = new List<string>();
            if (!IsCrcValid)
            {
                details.Add("File CRC error");
            }

            if (CrcErrorCount > 0)
            {
                details.Add($"{CrcErrorCount:N0} data CRC error{(CrcErrorCount == 1 ? string.Empty : "s")}");
            }

            if (BatteryWarningCount > 0)
            {
                details.Add($"{BatteryWarningCount:N0} battery warning{(BatteryWarningCount == 1 ? string.Empty : "s")}");
            }

            if (FailedAcousticRecordCount > 0)
            {
                details.Add($"{FailedAcousticRecordCount:N0} failed acoustic packet{(FailedAcousticRecordCount == 1 ? string.Empty : "s")}");
            }

            if (UnknownRecordCount > 0)
            {
                details.Add($"{UnknownRecordCount:N0} unknown record{(UnknownRecordCount == 1 ? string.Empty : "s")}");
            }

            if (details.Count > 0)
            {
                return string.Join(", ", details);
            }

            return "No warnings";
        }
    }

    public string ActionToolTip => HasPlotData
        ? "View pressure and temperature graph"
        : IsRawOnly
            ? "Raw data downloaded; sensor calibration is required for graphing"
            : IsRetryAvailable ? "Retry download" : IsDownloading ? "Download in progress" : "Download this file";

    public void MarkDownloading()
    {
        Download = null;
        Samples = null;
        SampleCount = 0;
        BatteryWarningCount = 0;
        CrcErrorCount = 0;
        HasWarnings = false;
        HasErrors = false;
        ResetRecordSummary();
        State = "Downloading";
        ProgressPercent = Bytes == 0 ? 0 : Math.Clamp(_partialRawByteCount * 100.0 / Bytes, 0, 100);
        ProgressText = $"{ProgressPercent:F0}%";
        OnPropertyChanged(nameof(IsDownloaded));
        OnPropertyChanged(nameof(IsRawOnly));
        OnPropertyChanged(nameof(CanExportRecord));
        OnPropertyChanged(nameof(CanExportRaw));
        OnPropertyChanged(nameof(HasPlotData));
        RaisePresentationChanged();
    }

    public void MarkQueued()
    {
        Download = null;
        Samples = null;
        SampleCount = 0;
        BatteryWarningCount = 0;
        CrcErrorCount = 0;
        HasWarnings = false;
        HasErrors = false;
        ResetRecordSummary();
        ClearPartialRaw();
        State = "Queued";
        ProgressPercent = 0;
        ProgressText = string.Empty;
        OnPropertyChanged(nameof(IsDownloaded));
        OnPropertyChanged(nameof(HasPlotData));
        RaisePresentationChanged();
    }

    public void MarkCancelled()
    {
        State = "Cancelled";
        RaisePresentationChanged();
    }

    public void MarkInterrupted()
    {
        State = "Interrupted";
        ProgressText = ProgressPercent > 0 ? $"{ProgressPercent:F0}% retained" : "Waiting for reconnect";
        RaisePresentationChanged();
    }

    public void MarkRetrying()
    {
        State = "Resuming";
        ProgressText = ProgressPercent > 0 ? $"{ProgressPercent:F0}% retained" : "Retrying";
        RaisePresentationChanged();
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

    public void CapturePartialRaw(MemoryReadProgress progress)
    {
        var bytesRead = Math.Clamp(progress.BytesRead, 0, progress.Buffer.Length);
        if (bytesRead < _partialRawByteCount)
        {
            return;
        }

        _partialRawBuffer = progress.Buffer;
        _partialRawByteCount = bytesRead;
    }

    public void MarkPartialSamples(
        IReadOnlyList<CalibratedGaugeSample> samples,
        int batteryWarningCount,
        MemoryGaugeRecordSummary recordSummary)
    {
        Samples = samples;
        SampleCount = samples.Count;
        BatteryWarningCount = batteryWarningCount;
        ApplyRecordSummary(recordSummary);
        HasWarnings = batteryWarningCount > 0 || recordSummary.FailedAcousticRecordCount > 0;
        HasErrors = !IsCrcValid || recordSummary.CrcErrorCount > 0 || recordSummary.UnknownRecordCount > 0;
        OnPropertyChanged(nameof(Samples));
        OnPropertyChanged(nameof(HasPlotData));
        RaisePresentationChanged();
    }

    public void MarkDownloaded(
        GaugeMemoryDownload download,
        IReadOnlyList<CalibratedGaugeSample> samples,
        int batteryWarningCount,
        MemoryGaugeRecordSummary recordSummary)
    {
        Download = download;
        Samples = samples;
        ClearPartialRaw();
        SampleCount = samples.Count;
        BatteryWarningCount = batteryWarningCount;
        ApplyRecordSummary(recordSummary);
        Duration = FormatFileDuration(samples.Count <= 1 ? 0 : (long)(samples.Count - 1) * MeasurementInterval);
        HasWarnings = batteryWarningCount > 0 || recordSummary.FailedAcousticRecordCount > 0;
        HasErrors = !IsCrcValid || recordSummary.CrcErrorCount > 0 || recordSummary.UnknownRecordCount > 0;
        ProgressPercent = 100;
        ProgressText = "100%";
        State = "Downloaded";
        OnPropertyChanged(nameof(IsDownloaded));
        OnPropertyChanged(nameof(IsRawOnly));
        OnPropertyChanged(nameof(CanExportRecord));
        OnPropertyChanged(nameof(CanExportRaw));
        OnPropertyChanged(nameof(Samples));
        OnPropertyChanged(nameof(HasPlotData));
        RaisePresentationChanged();
    }

    public void MarkRawDownloaded(
        GaugeMemoryDownload download,
        MemoryGaugeRecordSummary recordSummary)
    {
        Download = download;
        Samples = null;
        ClearPartialRaw();
        SampleCount = 0;
        BatteryWarningCount = 0;
        ApplyRecordSummary(recordSummary);
        HasWarnings = true;
        HasErrors = !IsCrcValid || recordSummary.CrcErrorCount > 0 || recordSummary.UnknownRecordCount > 0;
        ProgressPercent = 100;
        ProgressText = "100%";
        State = "Raw downloaded";
        OnPropertyChanged(nameof(IsDownloaded));
        OnPropertyChanged(nameof(IsRawOnly));
        OnPropertyChanged(nameof(CanExportRecord));
        OnPropertyChanged(nameof(CanExportRaw));
        OnPropertyChanged(nameof(Samples));
        OnPropertyChanged(nameof(HasPlotData));
        RaisePresentationChanged();
    }

    public void MarkError(string message)
    {
        State = "Error";
        ProgressText = message;
        RaisePresentationChanged();
    }

    private void ApplyRecordSummary(MemoryGaugeRecordSummary summary)
    {
        CrcErrorCount = summary.CrcErrorCount;
        AcousticRecordCount = summary.AcousticRecordCount;
        FailedAcousticRecordCount = summary.FailedAcousticRecordCount;
        AcousticDiagnosticRecordCount = summary.AcousticDiagnosticRecordCount;
        RawAcousticRecordCount = summary.RawAcousticRecordCount;
        TimestampRecordCount = summary.TimestampRecordCount;
        AuxiliaryRecordCount = summary.AuxiliaryRecordCount;
        UnknownRecordCount = summary.UnknownRecordCount;
        OnPropertyChanged(nameof(ExcludedRecordCount));
        OnPropertyChanged(nameof(ContainsAcousticData));
        OnPropertyChanged(nameof(ContainsRawAcousticData));
        RaisePresentationChanged();
    }

    private void ResetRecordSummary()
    {
        CrcErrorCount = 0;
        AcousticRecordCount = 0;
        FailedAcousticRecordCount = 0;
        AcousticDiagnosticRecordCount = 0;
        RawAcousticRecordCount = 0;
        TimestampRecordCount = 0;
        AuxiliaryRecordCount = 0;
        UnknownRecordCount = 0;
        OnPropertyChanged(nameof(ExcludedRecordCount));
        OnPropertyChanged(nameof(ContainsAcousticData));
        OnPropertyChanged(nameof(ContainsRawAcousticData));
    }

    private void ClearPartialRaw()
    {
        _partialRawBuffer = ReadOnlyMemory<byte>.Empty;
        _partialRawByteCount = 0;
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

    private static string FormatFileDuration(long durationSeconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, durationSeconds));
        if (duration.TotalMinutes < 1)
        {
            return $"{Math.Floor(duration.TotalSeconds):F0} sec";
        }

        if (duration.TotalHours < 1)
        {
            return $"{Math.Floor(duration.TotalMinutes):F0} min";
        }

        if (duration.TotalDays < 1)
        {
            return $"{Math.Floor(duration.TotalHours):F0} h {duration.Minutes:00} min";
        }

        return $"{Math.Floor(duration.TotalDays):F0} d {duration.Hours:00} h";
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        if (propertyName is nameof(State)
            or nameof(HasWarnings)
            or nameof(HasErrors)
            or nameof(BatteryWarningCount)
            or nameof(CrcErrorCount)
            or nameof(ProgressText))
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
        OnPropertyChanged(nameof(IsRetryAvailable));
        OnPropertyChanged(nameof(IsRowRetryVisible));
        OnPropertyChanged(nameof(CanFileAction));
        OnPropertyChanged(nameof(IsRawOnly));
        OnPropertyChanged(nameof(CanExportRecord));
        OnPropertyChanged(nameof(CanExportRaw));
        OnPropertyChanged(nameof(HasPlotData));
        OnPropertyChanged(nameof(RowStatus));
        OnPropertyChanged(nameof(ReviewStatus));
        OnPropertyChanged(nameof(ActionIcon));
        OnPropertyChanged(nameof(ActionBrush));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(DataQualityBrush));
        OnPropertyChanged(nameof(DataQualityDetail));
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

public sealed record ChartDataSet(
    double[] ElapsedSeconds,
    double[] Pressure,
    double[] Temperature)
{
    public static ChartDataSet Empty { get; } = new([], [], []);

    public int Count => Pressure.Length;

    public static ChartDataSet FromSamples(IReadOnlyList<CalibratedGaugeSample> samples)
    {
        if (samples.Count == 0)
        {
            return Empty;
        }

        var elapsedSeconds = new double[samples.Count];
        var pressure = new double[samples.Count];
        var temperature = new double[samples.Count];
        var startTimestamp = samples[0].Timestamp;

        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            elapsedSeconds[index] = sample.Timestamp - startTimestamp;
            pressure[index] = sample.Pressure;
            temperature[index] = sample.Temperature;
        }

        return new ChartDataSet(elapsedSeconds, pressure, temperature);
    }
}

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
