using System.Globalization;

namespace Gauge.Core;

public static class CalibratedCsvExporter
{
    public const string Header = "P Counts,T Counts,Pressure,Temperature,Seq,Counter,Address,Timestamp,T Freq,P Freq,CRC ERR,Corrected,Batt Status";

    public static IReadOnlyList<string> BuildLines(IEnumerable<CalibratedGaugeSample> samples)
    {
        var lines = new List<string> { Header };
        lines.AddRange(samples.Select(BuildLine));
        return lines;
    }

    private static string BuildLine(CalibratedGaugeSample sample)
    {
        if (sample.IsMissing)
        {
            return string.Join(
                ',',
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                sample.Sequence.ToString(CultureInfo.InvariantCulture),
                sample.Counter.ToString(CultureInfo.InvariantCulture),
                sample.Address.ToString(CultureInfo.InvariantCulture),
                FormatTimestamp(sample),
                string.Empty,
                string.Empty,
                (sample.CrcError ? 1 : 0).ToString(CultureInfo.InvariantCulture),
                (sample.Corrected ? 1 : 0).ToString(CultureInfo.InvariantCulture),
                sample.BatteryStatus.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join(
            ',',
            sample.PressureCounts.ToString(CultureInfo.InvariantCulture),
            sample.TemperatureCounts.ToString(CultureInfo.InvariantCulture),
            FormatDouble(sample.Pressure),
            FormatDouble(sample.Temperature),
            sample.Sequence.ToString(CultureInfo.InvariantCulture),
            sample.Counter.ToString(CultureInfo.InvariantCulture),
            sample.Address.ToString(CultureInfo.InvariantCulture),
            FormatTimestamp(sample),
            FormatDouble(sample.TemperatureFrequency),
            FormatDouble(sample.PressureFrequency),
            (sample.CrcError ? 1 : 0).ToString(CultureInfo.InvariantCulture),
            (sample.Corrected ? 1 : 0).ToString(CultureInfo.InvariantCulture),
            sample.BatteryStatus.ToString(CultureInfo.InvariantCulture));
    }

    private static string FormatDouble(double value)
    {
        return value.ToString("G17", CultureInfo.InvariantCulture);
    }

    private static string FormatTimestamp(CalibratedGaugeSample sample) =>
        double.IsNaN(sample.ExactTimestampSeconds)
            ? sample.Timestamp.ToString(CultureInfo.InvariantCulture)
            : sample.ExactTimestampSeconds.ToString("G17", CultureInfo.InvariantCulture);
}
