using System.Globalization;
using System.Text;

namespace Gauge.Core;

public static class LegacyRecordExporter
{
    public const string Header = "P Counts\tT Counts\tPressure\tTemperature\tSeq\tCounter\tAddress\tTimestamp\tT Freq\tCRC ERR\tCorrected\tBatt Status";

    public static void Write(
        Stream output,
        LegacyRecordMetadata metadata,
        IEnumerable<CalibratedGaugeSample> samples)
    {
        using var writer = new StreamWriter(output, Encoding.ASCII, bufferSize: 65536, leaveOpen: true)
        {
            NewLine = "\r\n"
        };

        writer.WriteLine($"Start of Job: {metadata.StartOfJob:yyyy/MM/dd HH:mm:ss}");
        writer.WriteLine("Date Format: yyyy/MM/dd HH:mm:ss");
        writer.WriteLine($"Device Type: {metadata.DeviceDescription}");
        writer.WriteLine($"Type Number: {metadata.DeviceType}");
        writer.WriteLine($"Serial Number: {metadata.DeviceSerial}");
        writer.WriteLine($"Firmware Version: {metadata.FirmwareMajor}.{metadata.FirmwareMinor}");
        writer.WriteLine($"Sensor Type: {metadata.SensorType}");
        writer.WriteLine($"Sensor Serial: {metadata.SensorSerial}");
        writer.WriteLine($"Calibration Cert: {metadata.CalibrationCertificate}");
        writer.WriteLine(" ");
        writer.WriteLine(Header);

        foreach (var sample in samples)
        {
            writer.WriteLine(BuildSampleLine(sample));
        }
    }

    private static string BuildSampleLine(CalibratedGaugeSample sample)
    {
        return string.Join(
            '\t',
            sample.PressureCounts.ToString(CultureInfo.InvariantCulture),
            sample.TemperatureCounts.ToString(CultureInfo.InvariantCulture),
            sample.Pressure.ToString("F6", CultureInfo.InvariantCulture),
            sample.Temperature.ToString("F6", CultureInfo.InvariantCulture),
            sample.Sequence.ToString(CultureInfo.InvariantCulture).PadLeft(6),
            sample.Counter.ToString(CultureInfo.InvariantCulture).PadLeft(6),
            sample.Address.ToString(CultureInfo.InvariantCulture),
            sample.Timestamp.ToString("F8", CultureInfo.InvariantCulture),
            sample.TemperatureFrequency.ToString("F6", CultureInfo.InvariantCulture),
            sample.CrcError ? "1" : "0",
            sample.Corrected ? "1" : "0",
            sample.BatteryStatus.ToString(CultureInfo.InvariantCulture));
    }
}

public sealed record LegacyRecordMetadata(
    DateTime StartOfJob,
    string DeviceDescription,
    uint DeviceType,
    uint DeviceSerial,
    byte FirmwareMajor,
    byte FirmwareMinor,
    string SensorType,
    string SensorSerial,
    string CalibrationCertificate = "");
