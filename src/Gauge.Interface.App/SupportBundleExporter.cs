using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Gauge.Core;
using Gauge.Protocol;
using Gauge.Transport;

namespace Gauge.Interface.App;

internal static class SupportBundleExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static void Write(
        Stream output,
        GaugeSupportBundle diagnostics,
        IReadOnlyList<SupportCalibrationArtifact> calibrations)
    {
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        WriteTextEntry(
            archive,
            "diagnostics.json",
            JsonSerializer.Serialize(diagnostics, JsonOptions));
        if (diagnostics.V3Diagnostics?.CrashCapsule is { } crashCapsule)
        {
            WriteTextEntry(
                archive,
                "diagnostics/crash-capsule.json",
                JsonSerializer.Serialize(
                    new SupportCrashCapsuleReport(
                        diagnostics.GeneratedUtc,
                        diagnostics.Device?.DeviceType,
                        diagnostics.Device?.DeviceSerial,
                        diagnostics.Device?.FirmwareVersion,
                        crashCapsule),
                    JsonOptions));
        }

        foreach (var artifact in calibrations)
        {
            WriteBytesEntry(archive, $"{artifact.Directory}/sensor-serial.txt", artifact.Calibration.SensorSerial);
            WriteBytesEntry(archive, $"{artifact.Directory}/sensor-header.txt", artifact.Calibration.SensorHeader);
            WriteBytesEntry(archive, $"{artifact.Directory}/pressure-poly.txt", artifact.Calibration.PressurePolynomial);
            WriteBytesEntry(archive, $"{artifact.Directory}/temperature-poly.txt", artifact.Calibration.TemperaturePolynomial);
        }
    }

    private static void WriteTextEntry(ZipArchive archive, string path, string value)
    {
        WriteBytesEntry(archive, path, Encoding.UTF8.GetBytes(value));
    }

    private static void WriteBytesEntry(ZipArchive archive, string path, ReadOnlySpan<byte> value)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(value);
    }
}

internal sealed record GaugeSupportBundle(
    DateTimeOffset GeneratedUtc,
    string ApplicationVersion,
    string OperatingSystem,
    string Framework,
    SupportConnectionSnapshot Connection,
    DeviceData? Device,
    SupportMemorySnapshot Memory,
    SupportCalibrationSnapshot Calibration,
    IReadOnlyList<SupportFileSnapshot> Files,
    SupportV3DiagnosticSnapshot? V3Diagnostics,
    CommunicationSessionSummary CommunicationSummary,
    IReadOnlyList<CommunicationEventLogEntry> CommunicationEvents,
    SupportFirmwareSnapshot Firmware,
    string RawIdentity);

internal sealed record SupportFirmwareSnapshot(
    string ImageFileName,
    string? ImageSha256,
    string Status,
    double ProgressPercent,
    string Loader,
    bool IsUpdating,
    bool IsRecoveryRequired);

internal sealed record SupportConnectionSnapshot(
    string Port,
    string PortDescription,
    int WakeBaud,
    int DataBaud,
    bool IsConnected,
    string Status,
    bool IgnoreSmallFiles);

internal sealed record SupportMemorySnapshot(
    bool IsLoaded,
    string Format,
    int FileCount,
    string? EndOfFileAddress,
    int? CatalogRecordCount,
    int? RejectedCatalogRecordCount);

internal sealed record SupportCalibrationSnapshot(
    bool IsCaptured,
    string Source,
    int FileLocalCalibrationCount,
    string? SensorIdentity,
    double? ReferenceClock,
    int? SensorId,
    uint? CountBias,
    int? PressureStartupMilliseconds,
    uint? PllClock);

internal sealed record SupportFileSnapshot(
    int FileNumber,
    string Format,
    int? FileTableRecordIndex,
    string? FileIdentity,
    string DataAddress,
    int EstimatedBytes,
    int MeasurementIntervalSeconds,
    byte? ResetCause,
    bool? FileTableCrcValid,
    string DownloadState,
    string QualitySummary,
    int ConvertedSampleCount,
    int DataCrcErrors,
    int BatteryWarnings,
    int AcousticRecords,
    int AcousticDiagnosticRecords,
    int RawAcousticRecords,
    int TimestampRecords,
    int UnknownRecords,
    string DataQuality,
    bool? IsOpen,
    int CorrectedPages,
    int MissingSamples,
    int PageSequenceGaps,
    bool RequiresMemoryService);

internal sealed record SupportV3DiagnosticSnapshot(
    string Health,
    ushort LatestEventId,
    string LatestEvent,
    string LatestEventDetail,
    string Severity,
    bool ProtectedLoggingFaultReportAvailable,
    uint ProtectedLoggingFaultReportGeneration,
    uint BootId,
    uint JournalPageCount,
    byte PendingRamEventCount,
    byte FailedChipMask,
    byte DegradedReplicaMask,
    string Flags,
    SupportCrashCapsuleSnapshot? CrashCapsule,
    string CrashCapsuleReadStatus);

internal sealed record SupportCrashCapsuleSnapshot(
    byte SchemaVersion,
    uint Generation,
    uint BootId,
    ushort EventId,
    byte FaultId,
    byte ApplicationState,
    string ApplicationStateName,
    uint FileId,
    uint CommittedSampleCount,
    byte RawRcon);

internal sealed record SupportCrashCapsuleReport(
    DateTimeOffset ExportedUtc,
    uint? DeviceType,
    uint? DeviceSerial,
    string? FirmwareVersion,
    SupportCrashCapsuleSnapshot Capsule);

internal sealed record SupportCalibrationArtifact(
    string Directory,
    SensorCalibrationBundle Calibration);
