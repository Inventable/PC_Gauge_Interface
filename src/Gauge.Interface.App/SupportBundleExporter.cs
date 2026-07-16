using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Gauge.Core;
using Gauge.Protocol;

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
        SensorCalibrationBundle? calibration)
    {
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        WriteTextEntry(
            archive,
            "diagnostics.json",
            JsonSerializer.Serialize(diagnostics, JsonOptions));

        if (calibration is null)
        {
            return;
        }

        WriteBytesEntry(archive, "calibration/sensor-serial.txt", calibration.SensorSerial);
        WriteBytesEntry(archive, "calibration/sensor-header.txt", calibration.SensorHeader);
        WriteBytesEntry(archive, "calibration/pressure-poly.txt", calibration.PressurePolynomial);
        WriteBytesEntry(archive, "calibration/temperature-poly.txt", calibration.TemperaturePolynomial);
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
    string RawIdentity);

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
    int FileCount,
    string? EndOfFileAddress);

internal sealed record SupportCalibrationSnapshot(
    bool IsCaptured,
    string? SensorIdentity,
    double? ReferenceClock,
    int? SensorId,
    uint? CountBias,
    int? PressureStartupMilliseconds,
    uint? PllClock);

internal sealed record SupportFileSnapshot(
    int FileNumber,
    int FileTableRecordIndex,
    string DataAddress,
    int EstimatedBytes,
    int MeasurementIntervalSeconds,
    byte ResetCause,
    bool FileTableCrcValid,
    string DownloadState,
    int ConvertedSampleCount,
    int DataCrcErrors,
    int BatteryWarnings,
    int AcousticRecords,
    int AcousticDiagnosticRecords,
    int RawAcousticRecords,
    int TimestampRecords,
    int UnknownRecords,
    string DataQuality);
