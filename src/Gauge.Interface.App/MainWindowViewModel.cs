using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
    private const int ConnectedPollTransactionTimeoutMs = 250;
    private const int ConnectedPollMissLimit = 3;
    private const int DataTransactionTimeoutMs = 250;
    private const int DataTransactionDeadlineMs = 1500;
    private const int WakeTransactionDeadlineMs = 1000;
    private const int SensorTransactionTimeoutMs = 2000;
    private const int SensorTransactionDeadlineMs = 7000;
    private const int EraseTransactionTimeoutMs = 500;
    private const int EraseTransactionDeadlineMs = 1000;
    private const int EraseRestartWakeScanTimeoutMs = 3000;
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
    private static readonly IReadOnlyList<SampleIntervalOption> SampleIntervalChoices =
    [
        .. Enumerable.Range(1, 10).Select(seconds =>
            new SampleIntervalOption($"{seconds} second{(seconds == 1 ? string.Empty : "s")}", (ushort)seconds)),
        new("20 seconds", 20),
        new("30 seconds", 30),
        new("1 minute", 60),
        new("2 minutes", 120),
        new("5 minutes", 300),
        new("10 minutes", 600),
        new("20 minutes", 1200),
        new("30 minutes", 1800),
        new("1 hour", 3600),
        new("Custom...", null)
    ];
    private static readonly IReadOnlyList<StorageModeOption> StorageModeChoices =
    [
        new("Full capacity (64 MiB)", GaugeStorageMode.Full),
        new("Mirrored (32 MiB, redundant)", GaugeStorageMode.Mirror)
    ];
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Northstar",
        "GaugeInterface",
        "settings.json");
    private static readonly string DiagnosticsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Northstar",
        "GaugeInterface",
        "communication-failures.log");
    private readonly CancellationTokenSource _pollingCancellation = new();
    private readonly SemaphoreSlim _serialGate = new(1, 1);
    private readonly BoundedCommunicationEventLog _communicationEvents = new();
    private readonly object _diagnosticsSync = new();
    private int _communicationRefreshPending;
    private int _connectedPollMisses;
    private readonly Task _pollingTask;

    private GaugeFileTable? _fileTable;
    private V3GaugeCatalog? _v3Catalog;
    private SensorCalibrationBundle? _calibration;
    private DeviceData? _connectedDevice;
    private CancellationTokenSource? _backgroundDownloadCancellation;
    private CancellationTokenSource? _manualDownloadCancellation;
    private CancellationTokenSource? _foregroundOperationCancellation;
    private CancellationTokenSource? _sensorLiveCancellation;
    private Task? _foregroundOperationTask;
    private Task? _backgroundDownloadTask;
    private Task? _sensorLiveTask;
    private GaugeFileRowViewModel? _activeDownload;
    private readonly List<SensorLivePlotPoint> _sensorLivePoints = [];
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
    private bool _isSensorLiveVisible;
    private bool _isSensorLiveRunning;
    private string _sensorLiveStatus = "Ready";
    private string _sensorLiveDetail = "Open Sensor Live to test the attached sensor.";
    private string _sensorLivePressure = "-- psi";
    private string _sensorLiveTemperature = "-- C";
    private string _sensorLiveLastReading = "No live reading";
    private string _sensorLiveSampleSummary = "0 readings in the last 60 seconds";
    private IBrush _sensorLiveStatusBrush = new SolidColorBrush(Color.Parse("#D97706"));
    private ChartDataSet _sensorLiveChartData = ChartDataSet.Empty;
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
    private bool _isErasePageVisible;
    private bool _isEraseConfirmationVisible;
    private bool _isErasingMemory;
    private bool _isEraseRecoveryRequired;
    private bool _eraseCompletedSuccessfully;
    private double _eraseProgressPercent;
    private string _eraseProgressText = "Preparing erase";
    private string _eraseTimingText = string.Empty;
    private string _eraseResultText = string.Empty;
    private ushort? _eraseInitialCompleted;
    private bool _isShuttingDown;
    private DateTime _retainedSessionUntilUtc = DateTime.MinValue;
    private uint? _retainedDeviceSerial;
    private string _retainedPort = string.Empty;
    private string? _calibrationFailure;
    private SampleIntervalOption _selectedSampleInterval = SampleIntervalChoices[0];
    private StorageModeOption _selectedStorageMode = StorageModeChoices[0];
    private string _customSampleIntervalSeconds = string.Empty;
    private string _gaugeSettingsStatus = string.Empty;
    private GaugeStorageMode? _pendingStorageMode;
    private bool _externalMemoryKnownEmpty;

    public MainWindowViewModel()
    {
        _settings = LoadSettings();
        _ignoreSmallFiles = _settings.IgnoreSmallFiles;
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
        ApplySampleIntervalCommand = new RelayCommand(
            ApplySampleIntervalAsync,
            CanApplySampleInterval);
        ChangeStorageModeCommand = new RelayCommand(
            ChangeStorageModeAsync,
            CanChangeStorageMode);
        OpenEngineeringModeCommand = new RelayCommand(OpenEngineeringModeAsync);
        OpenSensorLiveCommand = new RelayCommand(OpenSensorLiveAsync, () => CanOpenSensorLive);
        StartSensorLiveCommand = new RelayCommand(StartSensorLiveAsync, () => IsSensorLiveVisible && !IsSensorLiveRunning && IsGaugeConnected);
        StopSensorLiveCommand = new RelayCommand(StopSensorLiveAsync, () => IsSensorLiveVisible && IsSensorLiveRunning);
        CloseSettingsOverlayCommand = new RelayCommand(CloseSettingsOverlayAsync, () => !IsFirmwareUpdating);
        ToggleDeviceDetailsCommand = new RelayCommand(ToggleDeviceDetailsAsync);
        CancelOperationCommand = new RelayCommand(CancelOperationAsync, () => CanCancelOperation);
        BeginFirmwareProgramCommand = new RelayCommand(BeginFirmwareProgramAsync, CanBeginFirmwareProgram);
        BeginFirmwareRecoveryCommand = new RelayCommand(BeginFirmwareRecoveryAsync, CanBeginFirmwareRecovery);
        ConfirmFirmwareActionCommand = new RelayCommand(ConfirmFirmwareActionAsync, CanConfirmFirmwareAction);
        CancelFirmwareConfirmationCommand = new RelayCommand(CancelFirmwareConfirmationAsync, () => !IsFirmwareUpdating);
        BeginMemoryEraseCommand = new RelayCommand(BeginMemoryEraseAsync, CanBeginMemoryErase);
        ConfirmMemoryEraseCommand = new RelayCommand(
            ConfirmMemoryEraseAsync,
            () => IsEraseConfirmationVisible && !IsErasingMemory && !IsBusy && IsGaugeConnected);
        CancelMemoryEraseCommand = new RelayCommand(
            CancelMemoryEraseAsync,
            () => IsErasePageVisible &&
                  ((IsEraseConfirmationVisible && !IsEraseRecoveryRequired) || IsErasingMemory));
        CloseMemoryEraseCommand = new RelayCommand(CloseMemoryEraseAsync, () => IsErasePageVisible && !IsEraseConfirmationVisible && !IsErasingMemory);
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

    public ICommand ApplySampleIntervalCommand { get; }

    public ICommand ChangeStorageModeCommand { get; }

    public ICommand OpenEngineeringModeCommand { get; }

    public ICommand OpenSensorLiveCommand { get; }

    public ICommand StartSensorLiveCommand { get; }

    public ICommand StopSensorLiveCommand { get; }

    public ICommand CloseSettingsOverlayCommand { get; }

    public ICommand ToggleDeviceDetailsCommand { get; }

    public ICommand CancelOperationCommand { get; }

    public ICommand BeginFirmwareProgramCommand { get; }

    public ICommand BeginFirmwareRecoveryCommand { get; }

    public ICommand ConfirmFirmwareActionCommand { get; }

    public ICommand CancelFirmwareConfirmationCommand { get; }

    public ICommand BeginMemoryEraseCommand { get; }

    public ICommand ConfirmMemoryEraseCommand { get; }

    public ICommand CancelMemoryEraseCommand { get; }

    public ICommand CloseMemoryEraseCommand { get; }

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

    public IReadOnlyList<NorthstarActivitySpeed> DisconnectedAnimationSpeeds { get; } =
        [NorthstarActivitySpeed.Slow, NorthstarActivitySpeed.Fast];

    public NorthstarActivitySpeed DisconnectedAnimationSpeed
    {
        get => _settings.DisconnectedAnimationSpeed;
        set
        {
            if (_settings.DisconnectedAnimationSpeed == value)
            {
                return;
            }

            _settings = _settings with { DisconnectedAnimationSpeed = value };
            OnPropertyChanged();
            SaveSettings();
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
                OnPropertyChanged(nameof(IsConnectedHeaderVisible));
                OnPropertyChanged(nameof(IsDisconnectedVisible));
                OnPropertyChanged(nameof(IsFileTableVisible));
                RaiseFirmwareCommandStates();
                RaiseEraseCommandStates();
                RaiseGaugeConfigurationChanged();
            }
        }
    }

    public bool IsConnectedHeaderVisible => IsGaugeConnected && !IsErasePageVisible;

    public bool IsDisconnectedVisible =>
        IsPortConfigured && !IsGaugeConnected && !IsErasePageVisible;

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

    public bool IsFileTableVisible =>
        IsPortConfigured && IsGaugeConnected && !IsGraphVisible && !IsErasePageVisible;

    public bool IsErasePageVisible
    {
        get => _isErasePageVisible;
        private set
        {
            if (SetField(ref _isErasePageVisible, value))
            {
                OnPropertyChanged(nameof(IsConnectedHeaderVisible));
                OnPropertyChanged(nameof(IsDisconnectedVisible));
                OnPropertyChanged(nameof(IsFileTableVisible));
                OnPropertyChanged(nameof(IsEraseOperationVisible));
                OnPropertyChanged(nameof(IsEraseFinished));
                OnPropertyChanged(nameof(EraseTitle));
                RaiseEraseCommandStates();
            }
        }
    }

    public bool IsEraseConfirmationVisible
    {
        get => _isEraseConfirmationVisible;
        private set
        {
            if (SetField(ref _isEraseConfirmationVisible, value))
            {
                OnPropertyChanged(nameof(IsEraseOperationVisible));
                OnPropertyChanged(nameof(IsEraseFinished));
                OnPropertyChanged(nameof(EraseTitle));
                RaiseEraseCommandStates();
            }
        }
    }

    public bool IsEraseOperationVisible => IsErasePageVisible && !IsEraseConfirmationVisible;

    public bool IsEraseRecoveryRequired
    {
        get => _isEraseRecoveryRequired;
        private set
        {
            if (SetField(ref _isEraseRecoveryRequired, value))
            {
                OnPropertyChanged(nameof(EraseWarningTitle));
                OnPropertyChanged(nameof(EraseWarningText));
                OnPropertyChanged(nameof(EraseConfirmationActionText));
                OnPropertyChanged(nameof(CanDismissEraseConfirmation));
                RaiseEraseCommandStates();
            }
        }
    }

    public string EraseWarningTitle =>
        IsEraseRecoveryRequired
            ? "Erase cycle incomplete"
            : _pendingStorageMode.HasValue
                ? "Storage mode change requires erase"
            : "This operation is irreversible";

    public string EraseWarningText =>
        IsEraseRecoveryRequired
            ? "A previous external-memory erase did not complete. This gauge is not safe to deploy until a full erase completes. Restarting will erase both 32 MiB flash devices again from the beginning."
            : _pendingStorageMode.HasValue
                ? $"Changing to {DescribeStorageMode((byte)_pendingStorageMode.Value)} requires empty external memory. All recorded files will be permanently erased, then the new storage mode will be written and verified."
            : "All recorded files on both 32 MiB external flash devices will be permanently erased. The operation must not be started unless this data is no longer required.";

    public string EraseConfirmationActionText =>
        IsEraseRecoveryRequired
            ? "Restart Erase"
            : _pendingStorageMode.HasValue
                ? "Erase and Change Mode"
                : "OK, Erase Memory";

    public bool CanDismissEraseConfirmation => !IsEraseRecoveryRequired;

    public bool IsErasingMemory
    {
        get => _isErasingMemory;
        private set
        {
            if (SetField(ref _isErasingMemory, value))
            {
                OnPropertyChanged(nameof(IsEraseFinished));
                OnPropertyChanged(nameof(EraseTitle));
                RaiseEraseCommandStates();
            }
        }
    }

    public bool IsEraseFinished =>
        IsEraseOperationVisible && !IsErasingMemory && !string.IsNullOrWhiteSpace(EraseResultText);

    public string EraseTitle =>
        IsEraseConfirmationVisible
            ? "Erase Memory"
            : IsEraseFinished
                ? "Erase Complete"
                : "Erase Active";

    public double EraseProgressPercent
    {
        get => _eraseProgressPercent;
        private set => SetField(ref _eraseProgressPercent, value);
    }

    public string EraseProgressText
    {
        get => _eraseProgressText;
        private set => SetField(ref _eraseProgressText, value);
    }

    public string EraseTimingText
    {
        get => _eraseTimingText;
        private set => SetField(ref _eraseTimingText, value);
    }

    public string EraseResultText
    {
        get => _eraseResultText;
        private set
        {
            if (SetField(ref _eraseResultText, value))
            {
                OnPropertyChanged(nameof(IsEraseFinished));
                OnPropertyChanged(nameof(EraseTitle));
            }
        }
    }

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

    public bool IsSensorLiveVisible
    {
        get => _isSensorLiveVisible;
        private set
        {
            if (SetField(ref _isSensorLiveVisible, value))
            {
                OnPropertyChanged(nameof(IsSettingsOverlayVisible));
                RaiseSensorLiveCommandStates();
            }
        }
    }

    public bool IsSensorLiveRunning
    {
        get => _isSensorLiveRunning;
        private set
        {
            if (SetField(ref _isSensorLiveRunning, value))
            {
                OnPropertyChanged(nameof(IsSensorLiveStopped));
                RaiseSensorLiveCommandStates();
            }
        }
    }

    public bool IsSensorLiveStopped => !IsSensorLiveRunning;

    public bool CanOpenSensorLive =>
        IsGaugeConnected &&
        !IsBusy &&
        !IsFirmwareUpdating &&
        !IsErasePageVisible &&
        _connectedDevice?.DeviceType == MemoryGaugeDeviceType;

    public string SensorLiveStatus
    {
        get => _sensorLiveStatus;
        private set => SetField(ref _sensorLiveStatus, value);
    }

    public string SensorLiveDetail
    {
        get => _sensorLiveDetail;
        private set => SetField(ref _sensorLiveDetail, value);
    }

    public string SensorLivePressure
    {
        get => _sensorLivePressure;
        private set => SetField(ref _sensorLivePressure, value);
    }

    public string SensorLiveTemperature
    {
        get => _sensorLiveTemperature;
        private set => SetField(ref _sensorLiveTemperature, value);
    }

    public string SensorLiveLastReading
    {
        get => _sensorLiveLastReading;
        private set => SetField(ref _sensorLiveLastReading, value);
    }

    public string SensorLiveSampleSummary
    {
        get => _sensorLiveSampleSummary;
        private set => SetField(ref _sensorLiveSampleSummary, value);
    }

    public IBrush SensorLiveStatusBrush
    {
        get => _sensorLiveStatusBrush;
        private set => SetField(ref _sensorLiveStatusBrush, value);
    }

    public ChartDataSet SensorLiveChartData
    {
        get => _sensorLiveChartData;
        private set => SetField(ref _sensorLiveChartData, value);
    }

    public bool IsSettingsOverlayVisible =>
        IsAppSettingsVisible ||
        IsGaugeSettingsVisible ||
        IsEngineeringModeVisible ||
        IsSensorLiveVisible;

    public bool HasSetupMessage => !IsPortConfigured && Ports.Count == 0;

    public string SetupMessage => HasSetupMessage ? "No serial ports found" : string.Empty;

    public string GaugeDeviceType => _connectedDevice is null
        ? "--"
        : DescribeDeviceType(_connectedDevice.DeviceType);

    public string GaugeDeviceSerial => _connectedDevice?.DeviceSerial.ToString() ?? "--";

    public string GaugeFirmware => _connectedDevice is null
        ? "--"
        : _connectedDevice.FirmwareVersion;

    public string GaugePcb => _connectedDevice is null
        ? "--"
        : $"{_connectedDevice.PcbType} / {_connectedDevice.PcbSerial}";

    public string GaugeMeasurementInterval => _connectedDevice is null
        ? "--"
        : FormatSampleInterval(_connectedDevice.MeasurementInterval);

    public string GaugeMemoryMode => _connectedDevice is null
        ? "--"
        : DescribeStorageMode(_connectedDevice.MemoryMode);

    public string GaugeEraseStatus => _connectedDevice?.EraseStatus?.ToString() ?? "--";

    public IReadOnlyList<SampleIntervalOption> SampleIntervalOptions => SampleIntervalChoices;

    public SampleIntervalOption SelectedSampleInterval
    {
        get => _selectedSampleInterval;
        set
        {
            if (SetField(ref _selectedSampleInterval, value))
            {
                OnPropertyChanged(nameof(IsCustomSampleInterval));
                RaiseGaugeConfigurationChanged();
            }
        }
    }

    public bool IsCustomSampleInterval => SelectedSampleInterval.Seconds is null;

    public string CustomSampleIntervalSeconds
    {
        get => _customSampleIntervalSeconds;
        set
        {
            if (SetField(ref _customSampleIntervalSeconds, value))
            {
                RaiseGaugeConfigurationChanged();
            }
        }
    }

    public IReadOnlyList<StorageModeOption> StorageModeOptions => StorageModeChoices;

    public StorageModeOption SelectedStorageMode
    {
        get => _selectedStorageMode;
        set
        {
            if (SetField(ref _selectedStorageMode, value))
            {
                RaiseGaugeConfigurationChanged();
            }
        }
    }

    public string RecordingTimeEstimate => BuildRecordingTimeEstimate();

    public string StorageModeCompatibilityText =>
        _v3Catalog is not null
            ? "V3.0 logging supports mirrored storage. Full-capacity mode requires a firmware storage-format update."
            : "Changing storage mode requires empty memory. Recorded files will be erased before the new mode is applied.";

    public string GaugeSettingsStatus
    {
        get => _gaugeSettingsStatus;
        private set => SetField(ref _gaugeSettingsStatus, value);
    }

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
            if (!SetField(ref _ignoreSmallFiles, value))
            {
                return;
            }

            _settings = _settings with { IgnoreSmallFiles = value };
            SaveSettings();
            if (_fileTable is not null)
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
                RaiseGaugeConfigurationChanged();
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
        _sensorLiveCancellation?.Cancel();
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
        IsSensorLiveVisible = false;
        RefreshGaugeSettingSelections();
        GaugeSettingsStatus = string.Empty;
        IsGaugeSettingsVisible = true;
        RaiseDeviceInformationChanged();
        return Task.CompletedTask;
    }

    private void RefreshGaugeSettingSelections()
    {
        if (_connectedDevice is null)
        {
            return;
        }

        var interval = SampleIntervalChoices.FirstOrDefault(
            option => option.Seconds == _connectedDevice.MeasurementInterval);
        if (interval is null)
        {
            interval = SampleIntervalChoices[^1];
            CustomSampleIntervalSeconds =
                _connectedDevice.MeasurementInterval.ToString(CultureInfo.InvariantCulture);
        }

        SelectedSampleInterval = interval;
        SelectedStorageMode = StorageModeChoices.FirstOrDefault(
                option => (byte)option.Mode == _connectedDevice.MemoryMode)
            ?? StorageModeChoices[0];
        RaiseGaugeConfigurationChanged();
    }

    private bool CanApplySampleInterval()
    {
        return IsGaugeConfigurationAvailable() &&
            TryGetSelectedSampleInterval(out var seconds) &&
            seconds != _connectedDevice!.MeasurementInterval;
    }

    private async Task ApplySampleIntervalAsync()
    {
        if (!CanApplySampleInterval() ||
            !TryGetSelectedSampleInterval(out var seconds))
        {
            return;
        }

        var device = await ApplyGaugeConfigurationAsync(
            (service, serial, token) =>
                service.SetMeasurementIntervalAsync(seconds, serial, token),
            "changing the sample interval").ConfigureAwait(true);
        if (device is null)
        {
            return;
        }

        UpdateConfiguredDevice(device);
        GaugeSettingsStatus =
            $"Sample interval changed to {FormatSampleInterval(seconds)}. It will be used for the next recording.";
        Status = $"Gauge sample interval set to {seconds} second(s)";
        RefreshGaugeSettingSelections();
    }

    private bool CanChangeStorageMode()
    {
        return IsGaugeConfigurationAvailable() &&
            (byte)SelectedStorageMode.Mode != _connectedDevice!.MemoryMode &&
            !(_v3Catalog is not null && SelectedStorageMode.Mode == GaugeStorageMode.Full);
    }

    private async Task ChangeStorageModeAsync()
    {
        if (!CanChangeStorageMode())
        {
            if (_v3Catalog is not null &&
                SelectedStorageMode.Mode == GaugeStorageMode.Full)
            {
                GaugeSettingsStatus =
                    "Full-capacity mode is not available in V3.0 firmware because V3 currently writes every page to both flash devices.";
            }
            return;
        }

        var requestedMode = SelectedStorageMode.Mode;
        if (IsExternalMemoryEmpty())
        {
            var device = await ApplyGaugeConfigurationAsync(
                (service, serial, token) =>
                    service.SetStorageModeAsync(requestedMode, serial, token),
                "changing the storage mode").ConfigureAwait(true);
            if (device is null)
            {
                return;
            }

            UpdateConfiguredDevice(device);
            GaugeSettingsStatus =
                $"Storage mode changed to {DescribeStorageMode((byte)requestedMode)}.";
            Status = GaugeSettingsStatus;
            RefreshGaugeSettingSelections();
            return;
        }

        CancelBackgroundDownloads();
        await AwaitBackgroundDownloadAsync().ConfigureAwait(true);
        _autoDownloadsPaused = true;
        _pendingStorageMode = requestedMode;
        OnPropertyChanged(nameof(EraseWarningTitle));
        OnPropertyChanged(nameof(EraseWarningText));
        OnPropertyChanged(nameof(EraseConfirmationActionText));
        CloseSettingsOverlay();
        IsGraphVisible = false;
        PrepareErasePage(recoveryRequired: false);
        Status =
            $"External memory must be erased before changing to {DescribeStorageMode((byte)requestedMode)}";
    }

    private async Task<DeviceData?> ApplyGaugeConfigurationAsync(
        Func<GaugeConfigurationService, uint, CancellationToken, Task<DeviceData>> apply,
        string operation)
    {
        var expectedSerial = _connectedDevice?.DeviceSerial;
        if (!expectedSerial.HasValue)
        {
            return null;
        }

        CancelBackgroundDownloads();
        await AwaitBackgroundDownloadAsync().ConfigureAwait(true);
        _autoDownloadsPaused = true;
        IsBusy = true;
        await _serialGate.WaitAsync(_pollingCancellation.Token).ConfigureAwait(true);
        try
        {
            await using var connection = await OpenVerifiedConnectionAsync(
                preferFast: true,
                cancellationToken: _pollingCancellation.Token).ConfigureAwait(true);
            return await apply(
                new GaugeConfigurationService(new GaugeSession(connection.Transport)),
                expectedSerial.Value,
                _pollingCancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_pollingCancellation.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (IsGaugeConfigurationConnectionFailure(ex))
        {
            TransitionToDisconnected(
                $"Gauge stopped responding while {operation}: {ex.Message}");
            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException)
        {
            GaugeSettingsStatus = $"Could not apply setting: {ex.Message}";
            Status = GaugeSettingsStatus;
            return null;
        }
        finally
        {
            _serialGate.Release();
            IsBusy = false;
            RaiseGaugeConfigurationChanged();
        }
    }

    private bool IsGaugeConfigurationAvailable() =>
        !IsBusy &&
        !IsFirmwareUpdating &&
        !IsErasePageVisible &&
        IsGaugeConnected &&
        _connectedDevice?.DeviceType == MemoryGaugeDeviceType;

    private static bool IsGaugeConfigurationConnectionFailure(Exception exception) =>
        exception is TimeoutException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or GaugeProtocolException;

    private bool TryGetSelectedSampleInterval(out ushort seconds)
    {
        if (SelectedSampleInterval.Seconds is ushort preset)
        {
            seconds = preset;
            return true;
        }

        return ushort.TryParse(
                CustomSampleIntervalSeconds,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out seconds) &&
            seconds > 0;
    }

    private bool IsExternalMemoryEmpty()
    {
        if (_externalMemoryKnownEmpty)
        {
            return true;
        }

        if (_v3Catalog is not null)
        {
            return _v3Catalog.Recovery.Records.Count == 0 &&
                _v3Catalog.RejectedRecords.Count == 0;
        }

        return _fileTable is { Records.Count: 0 } table &&
            table.EndOfFile.Value <= 0x00004000U;
    }

    private string BuildRecordingTimeEstimate()
    {
        if (!TryGetSelectedSampleInterval(out var seconds))
        {
            return "Enter a whole-number interval from 1 to 65,535 seconds.";
        }

        var estimate = EstimateRemainingRecording(seconds, SelectedStorageMode.Mode);
        if (estimate is null)
        {
            return _v3Catalog is not null &&
                SelectedStorageMode.Mode == GaugeStorageMode.Full
                    ? "Recording estimate unavailable: V3.0 supports mirrored storage only."
                    : "Recording estimate unavailable until the file catalog has loaded.";
        }

        return $"Estimated recording time: {FormatEstimatedRecordingTime(estimate.Value.Seconds)} " +
            $"from {FormatBytes(checked((int)estimate.Value.RemainingBytes))} remaining.";
    }

    private (double Seconds, uint RemainingBytes)? EstimateRemainingRecording(
        ushort intervalSeconds,
        GaugeStorageMode selectedMode)
    {
        if (_v3Catalog is not null)
        {
            if (selectedMode != GaugeStorageMode.Mirror)
            {
                return null;
            }

            var capabilities = _v3Catalog.Capabilities;
            var nextFileStart = capabilities.DataStart;
            var latest = _v3Catalog.Files.LastOrDefault();
            if (latest is not null)
            {
                var occupiedEnd = Math.Max(latest.DataStart, latest.DataEnd);
                nextFileStart = AlignUp(occupiedEnd, capabilities.SectorBytes);
            }

            var nextDataStart = checked(nextFileStart + capabilities.SectorBytes);
            var remaining = nextDataStart >= capabilities.StorageEnd
                ? 0U
                : capabilities.StorageEnd - nextDataStart;
            var sampleCount =
                (remaining / V3PageCodec.PhysicalBytes) * V3PageCodec.MaximumSamples;
            return ((double)sampleCount * intervalSeconds, remaining);
        }

        if (_fileTable is null || _connectedDevice is null)
        {
            return null;
        }

        var capacity = selectedMode == GaugeStorageMode.Mirror
            ? 0x02000000U
            : 0x04000000U;
        var modeWillChange = (byte)selectedMode != _connectedDevice.MemoryMode;
        var usedEnd = modeWillChange || _fileTable.Records.Count == 0
            ? 0x00004000U
            : checked(_fileTable.EndOfFile.Value + (uint)MemoryGaugeFileRecord.Length);
        var remainingBytes = usedEnd >= capacity ? 0U : capacity - usedEnd;
        var sampleCountV2 = remainingBytes / 8U;
        return ((double)sampleCountV2 * intervalSeconds, remainingBytes);
    }

    private static uint AlignUp(uint value, ushort alignment)
    {
        var remainder = value % alignment;
        return remainder == 0
            ? value
            : checked(value + alignment - remainder);
    }

    private static string FormatEstimatedRecordingTime(double totalSeconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        if (duration.TotalDays >= 365.25)
        {
            return $"approximately {duration.TotalDays / 365.25:F1} years";
        }

        if (duration.TotalDays >= 1)
        {
            return $"approximately {duration.TotalDays:F1} days";
        }

        if (duration.TotalHours >= 1)
        {
            return $"approximately {duration.TotalHours:F1} hours";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"approximately {duration.TotalMinutes:F1} minutes";
        }

        return $"approximately {duration.TotalSeconds:F0} seconds";
    }

    private static string FormatSampleInterval(ushort seconds)
    {
        if (seconds % 3600 == 0)
        {
            var hours = seconds / 3600;
            return $"{hours} hour{(hours == 1 ? string.Empty : "s")}";
        }

        if (seconds % 60 == 0)
        {
            var minutes = seconds / 60;
            return $"{minutes} minute{(minutes == 1 ? string.Empty : "s")}";
        }

        return $"{seconds} second{(seconds == 1 ? string.Empty : "s")}";
    }

    private static string DescribeStorageMode(byte mode) => mode switch
    {
        (byte)GaugeStorageMode.Full => "Full capacity",
        (byte)GaugeStorageMode.Mirror => "Mirrored",
        _ => $"Unknown ({mode})"
    };

    private void UpdateConfiguredDevice(DeviceData device)
    {
        _connectedDevice = device;
        DeviceSummary = DescribeGauge(device);
        DeviceDetails = BuildDeviceDetails(device, []);
        _connectedPollMisses = 0;
        _nextConnectedPollUtc = DateTime.UtcNow + ConnectedPollInterval;
        RaiseDeviceInformationChanged();
    }

    private void RaiseGaugeConfigurationChanged()
    {
        OnPropertyChanged(nameof(RecordingTimeEstimate));
        OnPropertyChanged(nameof(StorageModeCompatibilityText));
        if (ApplySampleIntervalCommand is RelayCommand applyInterval)
        {
            applyInterval.RaiseCanExecuteChanged();
        }

        if (ChangeStorageModeCommand is RelayCommand changeMode)
        {
            changeMode.RaiseCanExecuteChanged();
        }
    }

    private async Task OpenSensorLiveAsync()
    {
        if (!CanOpenSensorLive)
        {
            return;
        }

        CancelBackgroundDownloads();
        await AwaitBackgroundDownloadAsync().ConfigureAwait(true);
        _autoDownloadsPaused = true;
        IsAppSettingsVisible = false;
        IsGaugeSettingsVisible = false;
        IsEngineeringModeVisible = false;
        IsSensorLiveVisible = true;
        ResetSensorLiveDisplay();
        await StartSensorLiveAsync().ConfigureAwait(true);
    }

    private Task StartSensorLiveAsync()
    {
        if (!IsSensorLiveVisible ||
            !IsGaugeConnected ||
            IsSensorLiveRunning ||
            _sensorLiveTask is { IsCompleted: false })
        {
            return Task.CompletedTask;
        }

        _sensorLiveCancellation?.Dispose();
        _sensorLiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _pollingCancellation.Token);
        IsSensorLiveRunning = true;
        SensorLiveStatus = "Starting sensor";
        SensorLiveDetail = "Checking firmware support and reading sensor calibration.";
        SensorLiveStatusBrush = new SolidColorBrush(Color.Parse("#D97706"));
        _sensorLiveTask = RunSensorLiveAsync(_sensorLiveCancellation.Token);
        return Task.CompletedTask;
    }

    private async Task StopSensorLiveAsync()
    {
        var task = _sensorLiveTask;
        _sensorLiveCancellation?.Cancel();
        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _sensorLiveCancellation?.Dispose();
        _sensorLiveCancellation = null;
        _sensorLiveTask = null;
        IsSensorLiveRunning = false;
        if (IsSensorLiveVisible &&
            SensorLiveStatus is not ("Firmware update required" or "Sensor error"))
        {
            SensorLiveStatus = "Sensor stopped";
            SensorLiveDetail = "Select Start Sensor to run the live test again.";
            SensorLiveStatusBrush = new SolidColorBrush(Color.Parse("#66737A"));
        }
    }

    private async Task RunSensorLiveAsync(CancellationToken cancellationToken)
    {
        var expectedSerial = _connectedDevice?.DeviceSerial
            ?? throw new InvalidOperationException("No connected gauge identity is available.");
        var gateHeld = false;
        var disconnected = false;
        IsBusy = true;
        try
        {
            await _serialGate.WaitAsync(cancellationToken).ConfigureAwait(true);
            gateHeld = true;
            await using var connection = await OpenVerifiedConnectionAsync(
                preferFast: true,
                cancellationToken: cancellationToken,
                transactionTimeoutMs: SensorTransactionTimeoutMs,
                transactionDeadlineMs: SensorTransactionDeadlineMs,
                wakeScanTimeoutMs: EraseRestartWakeScanTimeoutMs).ConfigureAwait(true);
            var device = ValidateEraseGauge(
                connection.Identity,
                expectedSerial,
                requireActiveInterlock: false);
            if (RequiresIncompleteEraseRecovery(device))
            {
                throw new InvalidOperationException(
                    "Sensor Live cannot run while the memory erase interlock is active.");
            }

            var service = new SensorLiveService(new GaugeSession(connection.Transport));
            var status = await service.ProbeAsync(cancellationToken).ConfigureAwait(true);
            if (status is null)
            {
                SensorLiveStatus = "Firmware update required";
                SensorLiveDetail =
                    "This gauge firmware does not implement Sensor Live commands 66-69.";
                SensorLiveStatusBrush = new SolidColorBrush(Color.Parse("#D97706"));
                return;
            }
            if (!status.Flags.HasFlag(SensorLiveStatusFlags.CalibrationAvailable))
            {
                throw new SensorCommunicationException(
                    SensorCommunicationFailure.InvalidResponse,
                    "Gauge reports that sensor calibration is unavailable.");
            }

            SensorLiveDetail = "Reading calibration directly from the attached sensor.";
            var calibration = await service.ReadCalibrationAsync(cancellationToken).ConfigureAwait(true);
            var decoder = new SensorLiveDecoder(calibration);
            status = await service.StartAsync(
                intervalSeconds: 1,
                cancellationToken).ConfigureAwait(true);
            var sensorStarted = true;
            var clock = Stopwatch.StartNew();
            uint lastSequence = 0;
            try
            {
                SensorLiveStatus = "Waiting for sensor";
                SensorLiveDetail = "Sensor started at a one-second interval.";
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (status.State == SensorLiveState.Fault)
                    {
                        throw new SensorCommunicationException(
                            SensorCommunicationFailure.InvalidResponse,
                            $"Gauge reported Sensor Live fault {status.LastError}.");
                    }
                    if (status.DataReady && status.LatestSequence != lastSequence)
                    {
                        var sample = await service
                            .ReadLatestAsync(cancellationToken)
                            .ConfigureAwait(true);
                        if (sample is not null && sample.Sequence != lastSequence)
                        {
                            var reading = decoder.Decode(sample);
                            lastSequence = sample.Sequence;
                            AddSensorLiveReading(reading, clock.Elapsed);
                        }
                    }

                    await Task.Delay(
                        SensorLiveService.DefaultPollInterval,
                        cancellationToken).ConfigureAwait(true);
                    status = await service
                        .ReadStatusAsync(cancellationToken)
                        .ConfigureAwait(true);
                }
            }
            finally
            {
                if (sensorStarted)
                {
                    using var stopDeadline = new CancellationTokenSource(
                        TimeSpan.FromSeconds(1));
                    try
                    {
                        await service.StopAsync(stopDeadline.Token).ConfigureAwait(true);
                    }
                    catch (Exception ex) when (
                        ex is OperationCanceledException or
                            TimeoutException or
                            IOException or
                            UnauthorizedAccessException or
                            GaugeProtocolException or
                            SensorCommunicationException)
                    {
                        // A disconnect or already-reset gauge cannot acknowledge stop.
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (NotSupportedException ex)
        {
            SensorLiveStatus = "Firmware update required";
            SensorLiveDetail = ex.Message;
            SensorLiveStatusBrush = new SolidColorBrush(Color.Parse("#D97706"));
        }
        catch (SensorCommunicationException ex)
        {
            SensorLiveStatus = "Sensor error";
            SensorLiveDetail = ex.Message;
            SensorLiveStatusBrush = new SolidColorBrush(Color.Parse("#CE0E2D"));
        }
        catch (Exception ex) when (IsSensorLiveConnectionFailure(ex))
        {
            disconnected = true;
            IsSensorLiveVisible = false;
            TransitionToDisconnected(
                $"Gauge stopped responding during Sensor Live: {ex.Message}",
                cancelActiveOperations: false);
        }
        catch (Exception ex)
        {
            SensorLiveStatus = "Sensor error";
            SensorLiveDetail = ex.Message;
            SensorLiveStatusBrush = new SolidColorBrush(Color.Parse("#CE0E2D"));
        }
        finally
        {
            if (gateHeld)
            {
                _serialGate.Release();
            }

            IsSensorLiveRunning = false;
            IsBusy = false;
            if (!disconnected && IsSensorLiveVisible &&
                cancellationToken.IsCancellationRequested)
            {
                SensorLiveStatus = "Sensor stopped";
                SensorLiveDetail = "Select Start Sensor to run the live test again.";
                SensorLiveStatusBrush = new SolidColorBrush(Color.Parse("#66737A"));
            }
        }
    }

    private void AddSensorLiveReading(
        DecodedSensorLiveReading reading,
        TimeSpan elapsed)
    {
        _sensorLivePoints.Add(new SensorLivePlotPoint(elapsed, reading));
        var cutoff = elapsed - TimeSpan.FromSeconds(60);
        _sensorLivePoints.RemoveAll(point => point.Elapsed < cutoff);

        SensorLivePressure = $"{reading.Pressure:F2} psi";
        SensorLiveTemperature = $"{reading.Temperature:F2} C";
        SensorLiveLastReading =
            $"Reading {reading.Sequence} at {DateTime.Now:HH:mm:ss}";
        SensorLiveSampleSummary =
            $"{_sensorLivePoints.Count} reading{(_sensorLivePoints.Count == 1 ? string.Empty : "s")} in the last 60 seconds";
        var origin = _sensorLivePoints[0].Elapsed.TotalSeconds;
        SensorLiveChartData = new ChartDataSet(
            _sensorLivePoints
                .Select(point => point.Elapsed.TotalSeconds - origin)
                .ToArray(),
            _sensorLivePoints
                .Select(point => point.Reading.Pressure)
                .ToArray(),
            _sensorLivePoints
                .Select(point => point.Reading.Temperature)
                .ToArray());

        if (reading.IsSensible)
        {
            SensorLiveStatus = "Sensor OK";
            SensorLiveDetail = "Sensor frames are valid and pressure and temperature decode to sensible values.";
            SensorLiveStatusBrush = new SolidColorBrush(Color.Parse("#2DA55D"));
        }
        else
        {
            SensorLiveStatus = "Check sensor";
            SensorLiveDetail = reading.QualityFlags == 0
                ? "The sensor frame decoded, but a reading is outside the expected test range."
                : $"The sensor reported quality flags 0x{reading.QualityFlags:X2}.";
            SensorLiveStatusBrush = new SolidColorBrush(Color.Parse("#D97706"));
        }
    }

    private void ResetSensorLiveDisplay()
    {
        _sensorLivePoints.Clear();
        SensorLiveChartData = ChartDataSet.Empty;
        SensorLiveStatus = "Starting sensor";
        SensorLiveDetail = "Checking firmware support and reading sensor calibration.";
        SensorLivePressure = "-- psi";
        SensorLiveTemperature = "-- C";
        SensorLiveLastReading = "No live reading";
        SensorLiveSampleSummary = "0 readings in the last 60 seconds";
        SensorLiveStatusBrush = new SolidColorBrush(Color.Parse("#D97706"));
    }

    private static bool IsSensorLiveConnectionFailure(Exception exception) =>
        exception is TimeoutException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or GaugeProtocolException;

    private async Task BeginMemoryEraseAsync()
    {
        if (!CanBeginMemoryErase())
        {
            return;
        }

        CancelBackgroundDownloads();
        await AwaitBackgroundDownloadAsync().ConfigureAwait(true);
        _autoDownloadsPaused = true;
        _pendingStorageMode = null;
        CloseSettingsOverlay();
        IsGraphVisible = false;
        PrepareErasePage(recoveryRequired: false);
    }

    private void PrepareErasePage(bool recoveryRequired)
    {
        IsEraseRecoveryRequired = recoveryRequired;
        OnPropertyChanged(nameof(EraseWarningTitle));
        OnPropertyChanged(nameof(EraseWarningText));
        OnPropertyChanged(nameof(EraseConfirmationActionText));
        EraseProgressPercent = 0;
        EraseProgressText = "0% complete | Calculating time remaining";
        EraseTimingText = string.Empty;
        EraseResultText = string.Empty;
        _eraseInitialCompleted = null;
        _eraseCompletedSuccessfully = false;
        IsEraseConfirmationVisible = true;
        IsErasePageVisible = true;
    }

    private async Task ConfirmMemoryEraseAsync()
    {
        if (!IsEraseConfirmationVisible || IsErasingMemory)
        {
            return;
        }

        IsEraseConfirmationVisible = false;
        await RunForegroundOperationAsync(EraseMemoryCoreAsync).ConfigureAwait(true);
    }

    private Task CancelMemoryEraseAsync()
    {
        if (IsEraseConfirmationVisible)
        {
            if (IsEraseRecoveryRequired)
            {
                return Task.CompletedTask;
            }

            IsEraseConfirmationVisible = false;
            IsErasePageVisible = false;
            _pendingStorageMode = null;
            _autoDownloadsPaused = false;
            StartBackgroundDownloads();
            return Task.CompletedTask;
        }

        if (IsErasingMemory)
        {
            _foregroundOperationCancellation?.Cancel();
            EraseResultText = "Cancelling polling; erase will be incomplete.";
            Status = "Cancelling external-memory erase";
        }

        return Task.CompletedTask;
    }

    private async Task CloseMemoryEraseAsync()
    {
        if (IsErasingMemory || IsEraseConfirmationVisible)
        {
            return;
        }

        if (!_eraseCompletedSuccessfully && IsGaugeConnected)
        {
            PrepareErasePage(recoveryRequired: true);
            Status = "External-memory erase remains incomplete; restart is required before deployment";
            return;
        }

        IsErasePageVisible = false;
        if (_eraseCompletedSuccessfully && IsGaugeConnected)
        {
            _autoDownloadsPaused = false;
            await ReadFilesAsync().ConfigureAwait(true);
        }
    }

    private async Task EraseMemoryCoreAsync(CancellationToken cancellationToken)
    {
        var expectedSerial = _connectedDevice?.DeviceSerial
            ?? throw new InvalidOperationException("No connected gauge identity is available.");
        var restartFromBeginning = IsEraseRecoveryRequired;
        var eraseCompleted = false;
        IsBusy = true;
        IsErasingMemory = true;
        EraseProgressPercent = 0;
        EraseProgressText = "0% complete | Calculating time remaining";
        EraseTimingText = string.Empty;
        EraseResultText = string.Empty;
        Status = "Erasing external memory";

        var gateHeld = false;
        try
        {
            await _serialGate.WaitAsync(cancellationToken).ConfigureAwait(true);
            gateHeld = true;

            if (restartFromBeginning)
            {
                await using (var restartConnection = await OpenVerifiedConnectionAsync(
                    preferFast: true,
                    cancellationToken: cancellationToken,
                    transactionTimeoutMs: EraseTransactionTimeoutMs,
                    transactionDeadlineMs: EraseTransactionDeadlineMs,
                    wakeScanTimeoutMs: EraseRestartWakeScanTimeoutMs).ConfigureAwait(true))
                {
                    ValidateEraseGauge(
                        restartConnection.Identity,
                        expectedSerial,
                        requireActiveInterlock: true);
                    await new ExternalMemoryEraseService(new GaugeSession(restartConnection.Transport))
                        .PrepareRestartFromBeginningAsync(cancellationToken)
                        .ConfigureAwait(true);
                }

                await Task.Delay(FastVerifyDelay, cancellationToken).ConfigureAwait(true);
            }

            var progress = new Progress<ExternalEraseProgress>(UpdateEraseProgress);
            ExternalEraseResult result;
            DeviceData device;
            await using (var eraseConnection = await OpenVerifiedConnectionAsync(
                preferFast: !restartFromBeginning,
                cancellationToken: cancellationToken,
                transactionTimeoutMs: EraseTransactionTimeoutMs,
                transactionDeadlineMs: EraseTransactionDeadlineMs,
                wakeScanTimeoutMs: EraseRestartWakeScanTimeoutMs).ConfigureAwait(true))
            {
                device = ValidateEraseGauge(
                    eraseConnection.Identity,
                    expectedSerial,
                    requireActiveInterlock: restartFromBeginning);
                result = await new ExternalMemoryEraseService(new GaugeSession(eraseConnection.Transport))
                    .EraseAsync(progress, cancellationToken)
                    .ConfigureAwait(true);
                eraseCompleted = true;

                if (_pendingStorageMode is GaugeStorageMode requestedMode)
                {
                    device = await new GaugeConfigurationService(
                            new GaugeSession(eraseConnection.Transport))
                        .SetStorageModeAsync(
                            requestedMode,
                            expectedSerial,
                            cancellationToken)
                        .ConfigureAwait(true);
                }
            }

            EraseProgressPercent = 100;
            EraseProgressText = "100% complete | 0 sec remaining";
            EraseTimingText = $"Completed in {FormatElapsedTime(result.Elapsed.TotalSeconds)}";
            EraseResultText = _pendingStorageMode is GaugeStorageMode changedMode
                ? $"External memory erased and storage mode changed to {DescribeStorageMode((byte)changedMode)}."
                : "External memory erased successfully.";
            _eraseCompletedSuccessfully = true;
            IsEraseRecoveryRequired = false;
            Status = _pendingStorageMode is GaugeStorageMode
                ? "External memory erase and storage-mode change complete"
                : "External memory erase complete";
            _pendingStorageMode = null;
            _externalMemoryKnownEmpty = true;
            _connectedDevice = device with { EraseStatus = 0 };
            DeviceSummary = DescribeGauge(_connectedDevice);
            DeviceDetails = BuildDeviceDetails(_connectedDevice, []);
            ConnectionStatus = "Connected";
            ConnectionBrush = new SolidColorBrush(Color.Parse("#2DA55D"));
            _connectedPollMisses = 0;
            _nextConnectedPollUtc = DateTime.UtcNow + ConnectedPollInterval;
            RaiseDeviceInformationChanged();

            Files.Clear();
            Samples.Clear();
            ChartData = ChartDataSet.Empty;
            SelectedFile = null;
            _fileTable = null;
            _v3Catalog = null;
            FileSummary = "No committed files found";
            ResetReview();
        }
        catch (OperationCanceledException) when (!_pollingCancellation.IsCancellationRequested)
        {
            LeaveEraseForDisconnectedState(
                "Gauge communication stopped during the external-memory erase");
        }
        catch (Exception ex) when (IsEraseConnectionFailure(ex))
        {
            LeaveEraseForDisconnectedState(
                $"Gauge stopped responding during the external-memory erase: {ex.Message}");
        }
        catch (Exception ex) when (eraseCompleted)
        {
            _pendingStorageMode = null;
            _externalMemoryKnownEmpty = true;
            _eraseCompletedSuccessfully = true;
            IsEraseRecoveryRequired = false;
            EraseProgressPercent = 100;
            EraseProgressText = "100% complete | 0 sec remaining";
            EraseResultText =
                $"External memory was erased, but the storage mode could not be changed: {ex.Message}";
            Status = "External memory erased; storage-mode change failed";

            Files.Clear();
            Samples.Clear();
            ChartData = ChartDataSet.Empty;
            SelectedFile = null;
            _fileTable = null;
            _v3Catalog = null;
            FileSummary = "No committed files found";
            ResetReview();
            RaiseGaugeConfigurationChanged();
        }
        catch (Exception ex)
        {
            IsEraseRecoveryRequired = true;
            PrepareErasePage(recoveryRequired: true);
            Status = $"External-memory erase remains incomplete: {ex.Message}";
        }
        finally
        {
            if (gateHeld)
            {
                _serialGate.Release();
            }

            IsErasingMemory = false;
            IsBusy = false;
        }
    }

    private void LeaveEraseForDisconnectedState(string reason)
    {
        _pendingStorageMode = null;
        _externalMemoryKnownEmpty = false;
        _eraseCompletedSuccessfully = false;
        IsEraseRecoveryRequired = false;
        IsEraseConfirmationVisible = false;
        EraseResultText = string.Empty;
        EraseTimingText = string.Empty;
        IsErasePageVisible = false;
        TransitionToDisconnected(reason, cancelActiveOperations: false);
    }

    private static bool IsEraseConnectionFailure(Exception exception) =>
        exception is TimeoutException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or GaugeProtocolException;

    private void UpdateEraseProgress(ExternalEraseProgress progress)
    {
        _eraseInitialCompleted ??= progress.Completed;
        EraseProgressPercent = Math.Clamp(progress.Percent, 0, 100);

        string remaining;
        if (progress.IsEstimated)
        {
            var estimate = ExternalMemoryEraseService.LegacyEstimatedDuration - progress.Elapsed;
            remaining = estimate > TimeSpan.Zero
                ? $"{FormatElapsedTime(estimate.TotalSeconds)} remaining"
                : "Waiting for gauge completion";
        }
        else
        {
            var completedThisRun = progress.Completed - _eraseInitialCompleted.Value;
            if (completedThisRun > 0 && progress.Completed < progress.Total)
            {
                var secondsPerPair = progress.Elapsed.TotalSeconds / completedThisRun;
                var estimate = TimeSpan.FromSeconds(
                    secondsPerPair * (progress.Total - progress.Completed));
                remaining = $"{FormatElapsedTime(estimate.TotalSeconds)} remaining";
            }
            else if (progress.Completed >= progress.Total)
            {
                remaining = "0 sec remaining";
            }
            else
            {
                remaining = "Calculating time remaining";
            }
        }

        EraseProgressText = progress.IsEstimated
            ? $"Approximately {progress.Percent:F0}% complete | {remaining}"
            : $"{progress.Percent:F0}% complete | {remaining}";
    }

    private bool CanBeginMemoryErase() =>
        !IsBusy &&
        !IsFirmwareUpdating &&
        IsGaugeConnected &&
        _connectedDevice?.DeviceType == MemoryGaugeDeviceType;

    private static bool RequiresIncompleteEraseRecovery(DeviceData device) =>
        device.DeviceType == MemoryGaugeDeviceType &&
        device.EraseStatus.GetValueOrDefault() != 0;

    private static DeviceData ValidateEraseGauge(
        GaugeFrame identity,
        uint expectedSerial,
        bool requireActiveInterlock)
    {
        var device = DecodeDevice(identity.Payload)
            ?? throw new GaugeProtocolException("Gauge returned an incomplete identity.");
        if (device.DeviceType != MemoryGaugeDeviceType ||
            device.DeviceSerial != expectedSerial)
        {
            throw new InvalidOperationException(
                "Connected gauge identity does not match the gauge selected for erase.");
        }
        if (requireActiveInterlock && !RequiresIncompleteEraseRecovery(device))
        {
            throw new InvalidDataException(
                "Gauge reset without preserving its incomplete-erase interlock.");
        }

        return device;
    }

    private void EnterIncompleteEraseRecovery(DeviceData device, byte[] identityPayload)
    {
        _pendingStorageMode = null;
        _externalMemoryKnownEmpty = false;
        CancelBackgroundDownloads();
        _autoDownloadsPaused = true;
        Files.Clear();
        Samples.Clear();
        ChartData = ChartDataSet.Empty;
        SelectedFile = null;
        _fileTable = null;
        _v3Catalog = null;
        _calibration = null;
        _calibrationFailure = null;
        ResetReview();

        _connectedDevice = device;
        DeviceSummary = DescribeGauge(device);
        DeviceDetails = BuildDeviceDetails(device, identityPayload);
        IsGaugeConnected = true;
        _connectedPollMisses = 0;
        _nextConnectedPollUtc = DateTime.UtcNow + ConnectedPollInterval;
        ConnectionStatus = "Erase required";
        ConnectionBrush = new SolidColorBrush(Color.Parse("#D97706"));
        IsGraphVisible = false;
        FileSummary = "File access locked until erase completes";
        PrepareErasePage(recoveryRequired: true);
        Status = "Incomplete external-memory erase detected; restart is required before deployment";
        RaiseDeviceInformationChanged();
    }

    private Task OpenEngineeringModeAsync()
    {
        IsAppSettingsVisible = false;
        IsGaugeSettingsVisible = false;
        IsSensorLiveVisible = false;
        IsEngineeringModeVisible = true;
        RaiseDeviceInformationChanged();
        return Task.CompletedTask;
    }

    private Task OpenAppSettingsAsync()
    {
        IsGaugeSettingsVisible = false;
        IsEngineeringModeVisible = false;
        IsSensorLiveVisible = false;
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

    private async Task CloseSettingsOverlayAsync()
    {
        if (IsSensorLiveVisible)
        {
            await StopSensorLiveAsync().ConfigureAwait(true);
        }

        CloseSettingsOverlay();
        _autoDownloadsPaused = false;
        if (IsGaugeConnected)
        {
            StartBackgroundDownloads();
        }
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
        IsSensorLiveVisible = false;
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
        var calibration = file.V3Calibration ?? _calibration;
        if (_connectedDevice is null || calibration is null || file.Samples is not { Count: > 0 } samples)
        {
            throw new InvalidOperationException("Downloaded gauge data and calibration are required for record export.");
        }

        var sensorIdentity = SensorAsciiData.DecodePayload(calibration.SensorSerial)
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
            GaugeFileTable? table = null;
            V3GaugeCatalog? v3Catalog = null;
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
                _connectedPollMisses = 0;
                _nextConnectedPollUtc = DateTime.UtcNow + ConnectedPollInterval;
                ConnectionStatus = "Connected";
                ConnectionBrush = new SolidColorBrush(Color.Parse("#2DA55D"));
                RaiseDeviceInformationChanged();

                if (RequiresIncompleteEraseRecovery(device))
                {
                    EnterIncompleteEraseRecovery(device, identity.Payload);
                    return;
                }

                Status = "Probing storage format";
                v3Catalog = await new V3GaugeJobService(session)
                    .DiscoverAsync(cancellationToken)
                    .ConfigureAwait(true);
                if (v3Catalog is null)
                {
                    Status = "Reading V2 file table";
                    table = await service.ReadFileTableAsync(cancellationToken: cancellationToken).ConfigureAwait(true);
                }
            }

            _fileTable = table;
            _v3Catalog = v3Catalog;
            _externalMemoryKnownEmpty = v3Catalog is not null
                ? v3Catalog.Recovery.Records.Count == 0 &&
                  v3Catalog.RejectedRecords.Count == 0
                : table is { Records.Count: 0 } &&
                  table.EndOfFile.Value <= 0x00004000U;
            if (v3Catalog is not null)
            {
                PopulateV3Files(v3Catalog);
            }
            else
            {
                PopulateFiles(table!);
            }
            FileSummary = Files.Count == 0
                ? "No committed files found"
                : v3Catalog?.RejectedRecords.Count > 0
                    ? $"{Files.Count} committed file(s); {v3Catalog.RejectedRecords.Count} uncommitted catalog reservation(s) ignored"
                    : string.Empty;
            RaiseDeviceInformationChanged();

            if (v3Catalog is null && _calibration is null)
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
                : v3Catalog is not null
                    ? "Gauge connected; V3 catalog and committed headers validated"
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

        if (IsGaugeConnected && (_fileTable is not null || _v3Catalog is not null))
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

        if (_fileTable is null && _v3Catalog is null)
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
            if (_v3Catalog is null)
            {
                await TryEnsureCalibrationAsync(cancellationToken).ConfigureAwait(true);
            }
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
                        $"Downloaded file {downloaded.FileIndex} with {downloaded.Samples.Count} sample(s)",
                        TimeSpan.FromSeconds(20));
                }
                else
                {
                    SetProtectedStatus(
                        $"Downloaded raw file {downloaded.FileIndex}; sensor calibration is unavailable",
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
                _connectedPollMisses++;
                if (_connectedPollMisses >= ConnectedPollMissLimit)
                {
                    TransitionToDisconnected(
                        $"Gauge did not respond to {ConnectedPollMissLimit} consecutive connection checks");
                }
                else if (CanPollSetStatus())
                {
                    Status = $"Gauge connection check delayed ({_connectedPollMisses}/{ConnectedPollMissLimit})";
                }
                return;
            }

            _connectedPollMisses = 0;
            var connectedDevice = DecodeDevice(identity.Payload);
            if (connectedDevice is not null && RequiresIncompleteEraseRecovery(connectedDevice))
            {
                EnterIncompleteEraseRecovery(connectedDevice, identity.Payload);
                return;
            }
            if (connectedDevice is not null)
            {
                _connectedDevice = connectedDevice;
                DeviceSummary = DescribeGauge(connectedDevice);
                DeviceDetails = BuildDeviceDetails(connectedDevice, identity.Payload);
                RaiseDeviceInformationChanged();
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
            if (device is not null && RequiresIncompleteEraseRecovery(device))
            {
                ClearRetainedGaugeData();
                await Task.Delay(FastVerifyDelay).ConfigureAwait(true);
                EnterIncompleteEraseRecovery(device, slowIdentity.Payload);
                return;
            }

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
            _sensorLiveCancellation?.Cancel();
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
        _connectedPollMisses = 0;
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
        int transactionDeadlineMs = DataTransactionDeadlineMs,
        int wakeScanTimeoutMs = WakeScanTimeoutMs)
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
            wakeScanTimeoutMs,
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
        int transactionDeadlineMs = DataTransactionDeadlineMs,
        int maximumAttempts = 3)
    {
        return new SerialGaugeTransport(new SerialGaugeTransportOptions(
            portName,
            baudRate,
            ReadTimeoutMs: timeoutMs,
            WriteTimeoutMs: timeoutMs,
            MaxAttempts: maximumAttempts,
            TransactionTimeoutMs: transactionDeadlineMs,
            EventSink: RecordCommunicationEvent));
    }

    private void StartBackgroundDownloads()
    {
        if (_autoDownloadsPaused ||
            !IsGaugeConnected ||
            (_fileTable is null && _v3Catalog is null) ||
            Files.Count == 0)
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
                        if (ex is InvalidDataException or FormatException or
                            InvalidOperationException or OverflowException)
                        {
                            failedFileCount++;
                            file.MarkError(ex.Message);
                            break;
                        }

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
        if (_v3Catalog is not null)
        {
            if (file.V3Download is not null && file.Samples is not null)
            {
                return new DownloadedGaugeFile(file.Index, file.Samples);
            }

            await _serialGate.WaitAsync(cancellationToken).ConfigureAwait(true);
            try
            {
                _activeDownload = file;
                file.MarkDownloading();
                var timer = Stopwatch.StartNew();
                var progress = new Progress<MemoryReadProgress>(value =>
                {
                    file.CapturePartialRaw(value);
                    file.MarkProgress(value, timer.Elapsed);
                    if (manual)
                    {
                        UpdateDownloadProgress(value, timer.Elapsed);
                    }
                });
                await using var connection = await OpenVerifiedConnectionAsync(
                    preferFast: true,
                    cancellationToken).ConfigureAwait(true);
                var download = await new V3GaugeJobService(new GaugeSession(connection.Transport))
                    .DownloadFileAsync(_v3Catalog, file.Index, cancellationToken, progress)
                    .ConfigureAwait(true);
                var samples = V3GaugeJobService.BuildCalibratedSamples(download);
                file.MarkV3Downloaded(download, samples);
                SetProtectedStatus(
                    $"V3 file {file.Index}: {samples.Count:N0} calibrated sample(s), {download.CorrectedPageCount:N0} corrected page(s)",
                    TimeSpan.FromSeconds(30));
                RaiseCommandStates();
                if (!manual && IsGraphVisible && ReferenceEquals(SelectedFile, file))
                {
                    RefreshFileGraph(file, samples);
                }

                return new DownloadedGaugeFile(file.Index, samples);
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

            return new DownloadedGaugeFile(file.Download.FileIndex, file.Samples ?? []);
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

            return new DownloadedGaugeFile(download.FileIndex, samples);
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

    private void PopulateV3Files(V3GaugeCatalog catalog)
    {
        Files.Clear();
        var sizes = catalog.Files
            .Select(file => checked((int)file.DataLength))
            .ToArray();
        var largest = sizes.Length == 0 ? 0 : sizes.Max();
        foreach (var file in catalog.Files)
        {
            var bytes = sizes[file.Index];
            var estimatedSamples = file.IsLatest &&
                !catalog.Summary.SampleCountRequiresRecovery
                    ? checked((int)catalog.Summary.SamplesCommitted)
                    : 0;
            var row = new GaugeFileRowViewModel(
                file.Index,
                bytes,
                estimatedSamples,
                file.CatalogRecord.NominalInterval,
                (long)estimatedSamples * file.CatalogRecord.NominalInterval,
                FormatBytes(bytes),
                largest == 0 ? 0 : Math.Max(4, bytes * 100.0 / largest),
                true);
            row.ConfigureV3(file, catalog.Recovery.SelectedReplicaId);
            Files.Add(row);
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
        _sensorLiveCancellation?.Cancel();
        CancelBackgroundDownloads();

        var tasks = new[]
            {
                _foregroundOperationTask,
                _backgroundDownloadTask,
                _sensorLiveTask
            }
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
        _sensorLiveCancellation?.Dispose();
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
            && (_fileTable is not null || _v3Catalog is not null);
    }

    private void RestoreRetainedSession(DeviceData device, byte[] identityPayload)
    {
        _connectedDevice = device;
        DeviceSummary = DescribeGauge(device);
        DeviceDetails = BuildDeviceDetails(device, identityPayload);
        IsGaugeConnected = true;
        _connectedPollMisses = 0;
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
        _v3Catalog = null;
        _externalMemoryKnownEmpty = false;
        _pendingStorageMode = null;
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

        return $"Connected | Device {device.DeviceSerial} | Firmware {device.FirmwareVersion}";
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
        builder.AppendLine($"Firmware: {device.FirmwareVersion}");
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
        OnPropertyChanged(nameof(CanOpenSensorLive));
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
        RaiseEraseCommandStates();
        RaiseSensorLiveCommandStates();
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

    private void RaiseEraseCommandStates()
    {
        if (BeginMemoryEraseCommand is RelayCommand begin)
        {
            begin.RaiseCanExecuteChanged();
        }

        if (ConfirmMemoryEraseCommand is RelayCommand confirm)
        {
            confirm.RaiseCanExecuteChanged();
        }

        if (CancelMemoryEraseCommand is RelayCommand cancel)
        {
            cancel.RaiseCanExecuteChanged();
        }

        if (CloseMemoryEraseCommand is RelayCommand close)
        {
            close.RaiseCanExecuteChanged();
        }
    }

    private void RaiseSensorLiveCommandStates()
    {
        if (OpenSensorLiveCommand is RelayCommand open)
        {
            open.RaiseCanExecuteChanged();
        }

        if (StartSensorLiveCommand is RelayCommand start)
        {
            start.RaiseCanExecuteChanged();
        }

        if (StopSensorLiveCommand is RelayCommand stop)
        {
            stop.RaiseCanExecuteChanged();
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
        RaiseGaugeConfigurationChanged();
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
        if (value.Kind is SerialGaugeTransportEventKind.Retry
            or SerialGaugeTransportEventKind.Failed
            or SerialGaugeTransportEventKind.OpenFailed)
        {
            try
            {
                lock (_diagnosticsSync)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(DiagnosticsPath)!);
                    File.AppendAllText(
                        DiagnosticsPath,
                        $"{value.TimestampUtc:O}\t{value.Kind}\t{value.PortName}\t{value.BaudRate}\t{value.Command}\t{value.Attempt}/{value.MaximumAttempts}\t{value.FailureKind}\t{value.ErrorType}\t{value.Message}{Environment.NewLine}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Diagnostics must never alter serial behaviour.
            }
        }

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

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(nameof(AppSettings.IgnoreSmallFiles), out _)
                ? settings
                : settings with { IgnoreSmallFiles = true };
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
    string LastFirmwareDirectory = "",
    NorthstarActivitySpeed DisconnectedAnimationSpeed = NorthstarActivitySpeed.Slow,
    bool IgnoreSmallFiles = true);

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
    int FileIndex,
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
        uint measurementInterval,
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

    public uint MeasurementInterval { get; }

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

    public bool IsDownloaded => Download is not null || V3Download is not null;

    public bool CanExportRecord => IsDownloaded && HasPlotData;

    public bool CanExportRaw => V3Download is not null || IsRawOnly;

    public bool IsRawOnly =>
        (V3Download is not null || Download is not null) &&
        Samples is null;

    public bool HasPlotData => Samples is { Count: >= 2 };

    public GaugeMemoryDownload? Download { get; private set; }

    public V3GaugeDownload? V3Download { get; private set; }

    public SensorCalibrationBundle? V3Calibration { get; private set; }

    public ReadOnlyMemory<byte> RawExportBytes =>
        V3Download?.Replica0RawBytes ?? Download?.RawBytes ?? [];

    public string V3FileIdentity { get; private set; } = string.Empty;

    public string V3CatalogReplica { get; private set; } = string.Empty;

    public string V3HeaderReplica { get; private set; } = string.Empty;

    public bool IsV3 { get; private set; }

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

            if (IsV3)
            {
                details.Add(
                    $"V3 file {V3FileIdentity}; header committed from replica {V3HeaderReplica}; " +
                    $"catalog replica {V3CatalogReplica}");
            }

            if (V3Download is not null)
            {
                details.Add(V3Download.IsOpen ? "Open file (healthy)" : "Footer committed");
                details.Add($"CRC64 valid on {V3Download.Pages.Count(page => page.Selected.IsCrcValid):N0}/{V3Download.Pages.Count:N0} inspected page(s)");
                details.Add($"{V3Download.CorrectedPageCount:N0} corrected page(s); raw primary bytes retained");
                details.Add(
                    V3Download.MirrorPageReadCount == 0
                        ? "Mirror not inspected (primary passed host validation)"
                        : $"Mirror read for {V3Download.MirrorPageReadCount:N0} failed primary page(s)");
                if (V3Download.HasMirrorDivergence)
                {
                    details.Add("mirror degraded/divergent");
                }

                foreach (var page in V3Download.Pages.Where(page => page.Selected.Status == V3PageStatus.Corrected))
                {
                    details.Add(
                        $"0x{page.Address:X8} replica {page.SelectedReplicaId} corrected bits [{string.Join(',', page.Selected.CorrectedBitLocations)}]");
                }

                if (V3Download.UncorrectablePageCount > 0)
                {
                    details.Add($"{V3Download.UncorrectablePageCount:N0} uncorrectable page(s)");
                }

                foreach (var page in V3Download.Pages.Where(page => !page.Selected.IsAccepted))
                {
                    details.Add(
                        $"0x{page.Address:X8} {page.Selected.Status}: {page.Selected.StructuralFailure}");
                }
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
        V3Download = null;
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
        V3Download = null;
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

    public void ConfigureV3(V3GaugeFile file, int selectedCatalogReplica)
    {
        IsV3 = true;
        V3FileIdentity = $"0x{file.CatalogRecord.FileId:X8}";
        V3CatalogReplica = selectedCatalogReplica.ToString();
        V3HeaderReplica = file.HeaderReplicaId.ToString();
        V3Calibration = V3GaugeJobService.GetCalibrationBundle(file.Header);
        OnPropertyChanged(nameof(V3FileIdentity));
        OnPropertyChanged(nameof(V3CatalogReplica));
        OnPropertyChanged(nameof(V3HeaderReplica));
        OnPropertyChanged(nameof(IsV3));
        RaisePresentationChanged();
    }

    public void MarkV3Downloaded(
        V3GaugeDownload download,
        IReadOnlyList<CalibratedGaugeSample> samples)
    {
        V3Download = download;
        Download = null;
        Samples = samples;
        ClearPartialRaw();
        SampleCount = samples.Count;
        CrcErrorCount = download.Pages.Count(page => !page.Selected.IsCrcValid);
        BatteryWarningCount = 0;
        HasWarnings = download.CorrectedPageCount > 0 || download.HasMirrorDivergence;
        HasErrors = download.UncorrectablePageCount > 0 || CrcErrorCount > 0;
        Duration = samples.Count <= 1
            ? FormatFileDuration(0)
            : FormatFileDuration(
                samples[^1].Timestamp - samples[0].Timestamp);
        ProgressPercent = 100;
        ProgressText = "100%";
        State = "Downloaded";
        OnPropertyChanged(nameof(IsDownloaded));
        OnPropertyChanged(nameof(IsRawOnly));
        OnPropertyChanged(nameof(CanExportRecord));
        OnPropertyChanged(nameof(CanExportRaw));
        OnPropertyChanged(nameof(RawExportBytes));
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

public sealed record SampleIntervalOption(string Label, ushort? Seconds)
{
    public override string ToString() => Label;
}

public sealed record StorageModeOption(string Label, GaugeStorageMode Mode)
{
    public override string ToString() => Label;
}

internal sealed record SensorLivePlotPoint(
    TimeSpan Elapsed,
    DecodedSensorLiveReading Reading);

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
