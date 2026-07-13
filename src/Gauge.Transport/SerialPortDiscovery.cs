using System.IO.Ports;
using System.Management;
using System.Runtime.Versioning;

namespace Gauge.Transport;

public static class SerialPortDiscovery
{
    public static IReadOnlyList<SerialPortInfo> GetPorts()
    {
        var ports = SerialPort.GetPortNames()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(port => new SerialPortInfo(port, null, null))
            .OrderBy(port => port.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return OperatingSystem.IsWindows()
            ? ApplyWindowsDescriptions(ports)
            : ports;
    }

    public static IReadOnlyList<string> GetPortNames()
    {
        return GetPorts().Select(port => port.Name).ToArray();
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<SerialPortInfo> ApplyWindowsDescriptions(IReadOnlyList<SerialPortInfo> ports)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name, Manufacturer FROM Win32_SerialPort");
            var byName = searcher.Get()
                .OfType<ManagementObject>()
                .Select(port => new SerialPortInfo(
                    Convert.ToString(port["DeviceID"]) ?? string.Empty,
                    Convert.ToString(port["Name"]),
                    Convert.ToString(port["Manufacturer"])))
                .Where(port => !string.IsNullOrWhiteSpace(port.Name))
                .ToDictionary(port => port.Name, StringComparer.OrdinalIgnoreCase);

            return ports
                .Select(port => byName.TryGetValue(port.Name, out var described) ? described : port)
                .ToArray();
        }
        catch (ManagementException)
        {
            return ports;
        }
        catch (UnauthorizedAccessException)
        {
            return ports;
        }
    }
}

public sealed record SerialPortInfo(
    string Name,
    string? Description,
    string? Manufacturer)
{
    public bool IsLikelyUsbSerial
    {
        get
        {
            var text = $"{Description} {Manufacturer}";
            return text.Contains("FTDI", StringComparison.OrdinalIgnoreCase)
                || text.Contains("USB Serial", StringComparison.OrdinalIgnoreCase)
                || text.Contains("USB-to-Serial", StringComparison.OrdinalIgnoreCase);
        }
    }

    public string DisplayName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Description) && string.IsNullOrWhiteSpace(Manufacturer))
            {
                return Name;
            }

            var detail = string.IsNullOrWhiteSpace(Description) ? Manufacturer : Description;
            return $"{Name} - {detail}";
        }
    }
}
