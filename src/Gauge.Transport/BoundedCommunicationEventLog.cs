namespace Gauge.Transport;

public sealed class BoundedCommunicationEventLog
{
    private const int MaximumEntries = 100;
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(5);
    private readonly Lock _gate = new();
    private readonly List<CommunicationEventLogEntry> _entries = [];
    private bool _hasSession;
    private bool _isActive;
    private string _port = string.Empty;
    private DateTimeOffset? _startedUtc;
    private DateTimeOffset? _endedUtc;
    private int _transactions;
    private int _retryAttempts;
    private int _crcErrors;
    private int _timeoutErrors;
    private int _ioErrors;
    private int _protocolErrors;
    private int _portAccessErrors;
    private int _otherErrors;
    private int _recoveredTransactions;
    private int _failedTransactions;
    private int _openFailures;
    private CommunicationEventLogEntry? _lastIssue;

    public void StartSession(string port, DateTimeOffset? timestampUtc = null)
    {
        lock (_gate)
        {
            _entries.Clear();
            _hasSession = true;
            _isActive = true;
            _port = port;
            _startedUtc = timestampUtc ?? DateTimeOffset.UtcNow;
            _endedUtc = null;
            _transactions = 0;
            _retryAttempts = 0;
            _crcErrors = 0;
            _timeoutErrors = 0;
            _ioErrors = 0;
            _protocolErrors = 0;
            _portAccessErrors = 0;
            _otherErrors = 0;
            _recoveredTransactions = 0;
            _failedTransactions = 0;
            _openFailures = 0;
            _lastIssue = null;
        }
    }

    public void EndSession(DateTimeOffset? timestampUtc = null)
    {
        lock (_gate)
        {
            if (!_isActive)
            {
                return;
            }

            _isActive = false;
            _endedUtc = timestampUtc ?? DateTimeOffset.UtcNow;
        }
    }

    public void Record(SerialGaugeTransportEvent value)
    {
        lock (_gate)
        {
            if (!_isActive)
            {
                return;
            }

            switch (value.Kind)
            {
                case SerialGaugeTransportEventKind.Succeeded:
                    _transactions++;
                    return;
                case SerialGaugeTransportEventKind.Retry:
                    _retryAttempts++;
                    CountFailure(value.FailureKind);
                    break;
                case SerialGaugeTransportEventKind.Recovered:
                    _recoveredTransactions++;
                    break;
                case SerialGaugeTransportEventKind.Failed:
                    _failedTransactions++;
                    CountFailure(value.FailureKind);
                    break;
                case SerialGaugeTransportEventKind.OpenFailed:
                    _openFailures++;
                    CountFailure(value.FailureKind);
                    break;
            }

            var entry = new CommunicationEventLogEntry(
                value.TimestampUtc,
                value.TimestampUtc,
                value.Kind.ToString(),
                value.PortName,
                value.BaudRate,
                value.Command?.ToString(),
                value.Attempt,
                value.MaximumAttempts,
                value.FailureKind?.ToString(),
                value.ErrorType,
                value.Message,
                1);

            if (value.Kind is not SerialGaugeTransportEventKind.Recovered)
            {
                _lastIssue = entry;
            }

            for (var index = _entries.Count - 1; index >= 0; index--)
            {
                var previous = _entries[index];
                if (entry.FirstTimestampUtc - previous.LastTimestampUtc > CoalesceWindow)
                {
                    break;
                }

                if (CanCoalesce(previous, entry))
                {
                    var updated = previous with
                    {
                        LastTimestampUtc = entry.LastTimestampUtc,
                        Occurrences = previous.Occurrences + 1
                    };
                    _entries[index] = updated;
                    if (ReferenceEquals(_lastIssue, entry))
                    {
                        _lastIssue = updated;
                    }
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

    public CommunicationSessionSummary Summary()
    {
        lock (_gate)
        {
            return new CommunicationSessionSummary(
                _hasSession,
                _isActive,
                _port,
                _startedUtc,
                _endedUtc,
                _transactions,
                _retryAttempts,
                _crcErrors,
                _timeoutErrors,
                _ioErrors,
                _protocolErrors,
                _portAccessErrors,
                _otherErrors,
                _recoveredTransactions,
                _failedTransactions,
                _openFailures,
                _lastIssue);
        }
    }

    private void CountFailure(SerialGaugeTransportFailureKind? failureKind)
    {
        switch (failureKind)
        {
            case SerialGaugeTransportFailureKind.Crc:
                _crcErrors++;
                break;
            case SerialGaugeTransportFailureKind.Timeout:
                _timeoutErrors++;
                break;
            case SerialGaugeTransportFailureKind.Io:
                _ioErrors++;
                break;
            case SerialGaugeTransportFailureKind.Protocol:
                _protocolErrors++;
                break;
            case SerialGaugeTransportFailureKind.PortAccess:
                _portAccessErrors++;
                break;
            case SerialGaugeTransportFailureKind.Other:
                _otherErrors++;
                break;
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
            && previous.FailureKind == current.FailureKind
            && previous.ErrorType == current.ErrorType
            && previous.Message == current.Message;
    }
}

public sealed record CommunicationSessionSummary(
    bool HasSession,
    bool IsActive,
    string Port,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? EndedUtc,
    int Transactions,
    int RetryAttempts,
    int CrcErrors,
    int TimeoutErrors,
    int IoErrors,
    int ProtocolErrors,
    int PortAccessErrors,
    int OtherErrors,
    int RecoveredTransactions,
    int FailedTransactions,
    int OpenFailures,
    CommunicationEventLogEntry? LastIssue);

public sealed record CommunicationEventLogEntry(
    DateTimeOffset FirstTimestampUtc,
    DateTimeOffset LastTimestampUtc,
    string Kind,
    string Port,
    int BaudRate,
    string? Command,
    int Attempt,
    int MaximumAttempts,
    string? FailureKind,
    string? ErrorType,
    string? Message,
    int Occurrences);
