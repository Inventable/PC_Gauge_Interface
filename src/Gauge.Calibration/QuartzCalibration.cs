using Gauge.Protocol;

namespace Gauge.Calibration;

public sealed record QuartzCalibration(
    double PllClock,
    IReadOnlyList<IReadOnlyList<double>> PressurePolynomialRows,
    IReadOnlyList<IReadOnlyList<double>> TemperaturePolynomialRows)
{
    public const double PressureFrequencyScale = 5000.0;
    public const double TemperatureFrequencyScale = 26200.0;

    public static QuartzCalibration FromPayloads(
        SensorCalibrationHeader header,
        ReadOnlySpan<byte> pressurePolynomialPayload,
        ReadOnlySpan<byte> temperaturePolynomialPayload)
    {
        if (header.PllClock is null)
        {
            throw new ArgumentException("Sensor calibration header does not contain PLL clock.", nameof(header));
        }

        return new QuartzCalibration(
            header.PllClock.Value,
            SensorAsciiData.ParseHexDoubleRows(pressurePolynomialPayload),
            SensorAsciiData.ParseHexDoubleRows(temperaturePolynomialPayload));
    }

    public double PressureFrequencyHz(uint pressureCounts)
    {
        return PllClock * PressureFrequencyScale / pressureCounts;
    }

    public double TemperatureFrequencyHz(uint temperatureCounts)
    {
        return PllClock * TemperatureFrequencyScale / temperatureCounts;
    }

    public double TemperatureCelsiusFromCounts(uint temperatureCounts)
    {
        return TemperatureCelsiusFromFrequency(TemperatureFrequencyHz(temperatureCounts));
    }

    public double PressurePsiFromCounts(uint pressureCounts, uint temperatureCounts)
    {
        return PressurePsiFromFrequency(PressureFrequencyHz(pressureCounts), TemperatureFrequencyHz(temperatureCounts));
    }

    public double TemperatureCelsiusFromFrequency(double temperatureFrequencyHz)
    {
        if (TemperaturePolynomialRows.Count < 2 || TemperaturePolynomialRows[0].Count < 2)
        {
            throw new InvalidOperationException("Temperature polynomial must contain a domain row and coefficient row.");
        }

        var x = Normalize(
            temperatureFrequencyHz,
            TemperaturePolynomialRows[0][0],
            TemperaturePolynomialRows[0][1]);

        return EvaluatePolynomial(TemperaturePolynomialRows[1], x);
    }

    public double PressurePsiFromFrequency(double pressureFrequencyHz, double temperatureFrequencyHz)
    {
        if (PressurePolynomialRows.Count < 7 || PressurePolynomialRows[0].Count < 2 || PressurePolynomialRows[1].Count < 2)
        {
            throw new InvalidOperationException("Pressure polynomial must contain pressure and temperature domains plus five coefficient rows.");
        }

        var x = Normalize(pressureFrequencyHz, PressurePolynomialRows[0][0], PressurePolynomialRows[0][1]);
        var y = Normalize(temperatureFrequencyHz, PressurePolynomialRows[1][0], PressurePolynomialRows[1][1]);
        var pressure = 0.0;
        var yPower = 1.0;

        for (var yIndex = 0; yIndex < 5; yIndex++)
        {
            var xPower = 1.0;

            for (var xIndex = 0; xIndex < 5; xIndex++)
            {
                pressure += PressurePolynomialRows[xIndex + 2][yIndex] * xPower * yPower;
                xPower *= x;
            }

            yPower *= y;
        }

        return pressure;
    }

    private static double Normalize(double value, double minimum, double maximum)
    {
        return ((2.0 * value) - minimum - maximum) / (maximum - minimum);
    }

    private static double EvaluatePolynomial(IReadOnlyList<double> coefficients, double x)
    {
        var value = 0.0;

        for (var index = coefficients.Count - 1; index >= 0; index--)
        {
            value = (value * x) + coefficients[index];
        }

        return value;
    }
}
