using System.Globalization;
using System.Text.RegularExpressions;

namespace Gauge.Protocol;

public sealed record SensorCalibrationHeader(
    double? ReferenceClock,
    int? SensorId,
    uint? CountBias,
    int? PressureStartupMilliseconds,
    uint? PllClock)
{
    private static readonly Regex TokenRegex = new(
        @"(?<key>RefClk|Id|Bias|PStartupMs|PLLClk)\s+(?<value>[^\s]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static SensorCalibrationHeader Parse(ReadOnlySpan<byte> payload)
    {
        return Parse(SensorAsciiData.DecodePayload(payload));
    }

    public static SensorCalibrationHeader Parse(string text)
    {
        double? referenceClock = null;
        int? sensorId = null;
        uint? countBias = null;
        int? pressureStartupMilliseconds = null;
        uint? pllClock = null;

        foreach (Match match in TokenRegex.Matches(text))
        {
            var value = match.Groups["value"].Value;
            switch (match.Groups["key"].Value)
            {
                case "RefClk":
                    referenceClock = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "Id":
                    sensorId = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "Bias":
                    countBias = uint.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "PStartupMs":
                    pressureStartupMilliseconds = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "PLLClk":
                    pllClock = uint.Parse(value, CultureInfo.InvariantCulture);
                    break;
            }
        }

        return new SensorCalibrationHeader(
            referenceClock,
            sensorId,
            countBias,
            pressureStartupMilliseconds,
            pllClock);
    }
}
