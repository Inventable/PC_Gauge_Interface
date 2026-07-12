namespace Gauge.Transport;

public sealed record SerialGaugeTransportOptions(
    string PortName,
    int BaudRate = 460800,
    int ReadTimeoutMs = 2000,
    int WriteTimeoutMs = 2000);

