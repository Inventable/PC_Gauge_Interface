namespace Gauge.Transport;

public sealed record SerialGaugeTransportOptions(
    string PortName,
    int BaudRate = 460800,
    int ReadTimeoutMs = 2000,
    int WriteTimeoutMs = 2000,
    int MaxAttempts = 3,
    int RetryDelayMs = 20,
    Action<SerialGaugeTransportEvent>? EventSink = null);

public enum SerialGaugeTransportEventKind
{
    OpenFailed,
    Retry,
    Recovered,
    Failed
}

public sealed record SerialGaugeTransportEvent(
    DateTimeOffset TimestampUtc,
    SerialGaugeTransportEventKind Kind,
    string PortName,
    int BaudRate,
    Gauge.Protocol.GaugeCommand? Command,
    int Attempt,
    int MaximumAttempts,
    string? ErrorType,
    string? Message);
