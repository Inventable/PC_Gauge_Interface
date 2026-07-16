namespace Gauge.Transport;

public sealed class BoundedCommunicationEventLog
{
    private const int MaximumEntries = 100;
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(5);
    private readonly Lock _gate = new();
    private readonly List<CommunicationEventLogEntry> _entries = [];

    public void Record(SerialGaugeTransportEvent value)
    {
        var entry = new CommunicationEventLogEntry(
            value.TimestampUtc,
            value.TimestampUtc,
            value.Kind.ToString(),
            value.PortName,
            value.BaudRate,
            value.Command?.ToString(),
            value.Attempt,
            value.MaximumAttempts,
            value.ErrorType,
            value.Message,
            1);

        lock (_gate)
        {
            for (var index = _entries.Count - 1; index >= 0; index--)
            {
                var previous = _entries[index];
                if (entry.FirstTimestampUtc - previous.LastTimestampUtc > CoalesceWindow)
                {
                    break;
                }

                if (CanCoalesce(previous, entry))
                {
                    _entries[index] = previous with
                    {
                        LastTimestampUtc = entry.LastTimestampUtc,
                        Occurrences = previous.Occurrences + 1
                    };
                    return;
                }
            }

            _entries.Add(entry);
            if (_entries.Count > MaximumEntries)
            {
                _entries.RemoveRange(0, _entries.Count - MaximumEntries);
            }
        }
    }

    public IReadOnlyList<CommunicationEventLogEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    private static bool CanCoalesce(CommunicationEventLogEntry previous, CommunicationEventLogEntry current)
    {
        return previous.Kind == current.Kind
            && previous.Port == current.Port
            && previous.BaudRate == current.BaudRate
            && previous.Command == current.Command
            && previous.Attempt == current.Attempt
            && previous.MaximumAttempts == current.MaximumAttempts
            && previous.ErrorType == current.ErrorType
            && previous.Message == current.Message;
    }
}

public sealed record CommunicationEventLogEntry(
    DateTimeOffset FirstTimestampUtc,
    DateTimeOffset LastTimestampUtc,
    string Kind,
    string Port,
    int BaudRate,
    string? Command,
    int Attempt,
    int MaximumAttempts,
    string? ErrorType,
    string? Message,
    int Occurrences);
