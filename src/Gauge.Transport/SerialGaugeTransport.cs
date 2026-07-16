using System.IO.Ports;
using System.Runtime.ExceptionServices;
using Gauge.Protocol;

namespace Gauge.Transport;

public sealed class SerialGaugeTransport : IGaugeTransport
{
    private readonly SerialGaugeTransportOptions _options;
    private readonly SemaphoreSlim _transactionLock = new(1, 1);
    private SerialPort? _port;

    public SerialGaugeTransport(SerialGaugeTransportOptions options)
    {
        _options = options;
    }

    public string Name => $"{_options.PortName} @ {_options.BaudRate}";

    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_port?.IsOpen == true)
        {
            return Task.CompletedTask;
        }

        _port = new SerialPort(_options.PortName, _options.BaudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = _options.ReadTimeoutMs,
            WriteTimeout = _options.WriteTimeoutMs
        };

        try
        {
            _port.Open();
            _port.DiscardInBuffer();
            _port.DiscardOutBuffer();
        }
        catch (Exception ex)
        {
            Report(SerialGaugeTransportEventKind.OpenFailed, null, 1, 1, ex);
            throw;
        }

        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _port?.Close();
        return Task.CompletedTask;
    }

    public async Task<GaugeFrame> TransactAsync(GaugeFrame request, CancellationToken cancellationToken = default)
    {
        var port = _port;
        if (port is null || !port.IsOpen)
        {
            throw new InvalidOperationException("Serial transport is not open.");
        }

        await _transactionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () =>
                {
                    return TransactWithRetries(port, request, cancellationToken);
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transactionLock.Release();
        }
    }

    private GaugeFrame TransactWithRetries(SerialPort port, GaugeFrame request, CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        var attempts = Math.Max(1, _options.MaxAttempts);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var reply = TransactOnce(port, request, cancellationToken);
                if (attempt > 1)
                {
                    Report(SerialGaugeTransportEventKind.Recovered, request.Command, attempt, attempts, lastFailure);
                }

                Report(SerialGaugeTransportEventKind.Succeeded, request.Command, attempt, attempts, null);
                return reply;
            }
            catch (Exception ex) when (IsRetryableCommsFailure(ex) && attempt < attempts)
            {
                lastFailure = ex;
                Report(SerialGaugeTransportEventKind.Retry, request.Command, attempt, attempts, ex);
                TryDiscardBuffers(port);
                DelayBeforeRetry(cancellationToken);
            }
            catch (Exception ex) when (IsRetryableCommsFailure(ex))
            {
                lastFailure = ex;
                Report(SerialGaugeTransportEventKind.Failed, request.Command, attempt, attempts, ex);
            }
        }

        ExceptionDispatchInfo.Capture(lastFailure ?? new TimeoutException("Gauge transaction failed without a reply.")).Throw();
        throw new InvalidOperationException("Gauge transaction retry handling failed unexpectedly.");
    }

    private static GaugeFrame TransactOnce(SerialPort port, GaugeFrame request, CancellationToken cancellationToken)
    {
        var requestBytes = GaugeFrameCodec.Encode(request);

        TryDiscardBuffers(port);
        port.Write(requestBytes, 0, requestBytes.Length);

        var replyBytes = ReadWireFrame(port, cancellationToken);
        return GaugeFrameCodec.Decode(replyBytes);
    }

    private static bool IsRetryableCommsFailure(Exception ex)
    {
        return ex is TimeoutException
            or IOException
            or GaugeProtocolException;
    }

    private void Report(
        SerialGaugeTransportEventKind kind,
        GaugeCommand? command,
        int attempt,
        int maximumAttempts,
        Exception? exception)
    {
        try
        {
            _options.EventSink?.Invoke(new SerialGaugeTransportEvent(
                DateTimeOffset.UtcNow,
                kind,
                _options.PortName,
                _options.BaudRate,
                command,
                attempt,
                maximumAttempts,
                ClassifyFailure(exception),
                exception?.GetType().Name,
                exception?.Message));
        }
        catch
        {
            // Diagnostics must never alter serial behaviour.
        }
    }

    private static SerialGaugeTransportFailureKind? ClassifyFailure(Exception? exception)
    {
        return exception switch
        {
            null => null,
            TimeoutException => SerialGaugeTransportFailureKind.Timeout,
            GaugeProtocolException when exception.Message.Contains("CRC", StringComparison.OrdinalIgnoreCase) =>
                SerialGaugeTransportFailureKind.Crc,
            GaugeProtocolException => SerialGaugeTransportFailureKind.Protocol,
            UnauthorizedAccessException or ArgumentOutOfRangeException => SerialGaugeTransportFailureKind.PortAccess,
            IOException => SerialGaugeTransportFailureKind.Io,
            _ => SerialGaugeTransportFailureKind.Other
        };
    }

    private void DelayBeforeRetry(CancellationToken cancellationToken)
    {
        if (_options.RetryDelayMs <= 0)
        {
            return;
        }

        Task.Delay(_options.RetryDelayMs, cancellationToken).GetAwaiter().GetResult();
    }

    private static void TryDiscardBuffers(SerialPort port)
    {
        try
        {
            port.DiscardInBuffer();
            port.DiscardOutBuffer();
        }
        catch (IOException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        _transactionLock.Dispose();
        _port?.Dispose();
        return ValueTask.CompletedTask;
    }

    private static byte[] ReadWireFrame(SerialPort port, CancellationToken cancellationToken)
    {
        ReadStartByte(port, cancellationToken);

        var header = new byte[GaugeProtocolConstants.HeaderLength];
        ReadExactly(port, header, cancellationToken);

        var payloadLength = (ushort)(header[1] | (header[2] << 8));
        var payloadAndCrc = new byte[payloadLength + GaugeProtocolConstants.CrcLength];
        ReadExactly(port, payloadAndCrc, cancellationToken);

        var wire = new byte[1 + header.Length + payloadAndCrc.Length];
        wire[0] = GaugeProtocolConstants.StartByte;
        header.CopyTo(wire.AsSpan(1));
        payloadAndCrc.CopyTo(wire.AsSpan(1 + header.Length));
        return wire;
    }

    private static void ReadStartByte(SerialPort port, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = port.ReadByte();

            if (value == GaugeProtocolConstants.StartByte)
            {
                return;
            }
        }
    }

    private static void ReadExactly(SerialPort port, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;

        while (offset < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = port.Read(buffer, offset, buffer.Length - offset);

            if (read == 0)
            {
                throw new TimeoutException("Serial port returned no data while reading a gauge frame.");
            }

            offset += read;
        }
    }
}
