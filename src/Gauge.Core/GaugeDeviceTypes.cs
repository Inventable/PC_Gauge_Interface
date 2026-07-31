namespace Gauge.Core;

public static class GaugeDeviceTypes
{
    public const uint ConstellationQ150Legacy = 100160;
    public const uint ConstellationAcousticLegacy = 100187;
    public const uint ConstellationQ177 = 100196;
    public const uint ConstellationAcoustic = 100200;
    public const uint ConstellationQ150 = 100230;

    public static IReadOnlyList<uint> Recognized { get; } =
    [
        ConstellationQ150Legacy,
        ConstellationAcousticLegacy,
        ConstellationQ177,
        ConstellationAcoustic,
        ConstellationQ150
    ];

    public static IReadOnlyList<uint> MemoryGaugeFamily { get; } =
    [
        ConstellationQ150Legacy,
        ConstellationQ177,
        ConstellationQ150
    ];

    public static bool IsRecognized(uint deviceType) =>
        Recognized.Contains(deviceType);

    public static bool IsMemoryGauge(uint deviceType) =>
        MemoryGaugeFamily.Contains(deviceType);

    public static bool IsFirmwareCompatible(uint requestedDeviceType, uint publishedDeviceType) =>
        requestedDeviceType == publishedDeviceType ||
        (IsMemoryGauge(requestedDeviceType) && IsMemoryGauge(publishedDeviceType));

    public static string Describe(uint deviceType) =>
        deviceType switch
        {
            ConstellationQ177 => "Constellation Q177",
            ConstellationQ150Legacy or ConstellationQ150 => "Constellation Q150",
            ConstellationAcousticLegacy or ConstellationAcoustic => "Constellation Acoustic Quartz Gauge",
            _ => $"Gauge Type {deviceType}"
        };
}
