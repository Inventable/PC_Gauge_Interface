using System.IO.Ports;

namespace Gauge.Transport;

public static class SerialPortDiscovery
{
    public static IReadOnlyList<string> GetPortNames()
    {
        return SerialPort.GetPortNames()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
