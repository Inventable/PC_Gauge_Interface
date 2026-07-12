using System.IO.Ports;
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

        _port.Open();
        _port.DiscardInBuffer();
        _port.DiscardOutBuffer();

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
                    cancellationToken.ThrowIfCancellationRequested();
                    var requestBytes = GaugeFrameCodec.Encode(request);

                    port.DiscardInBuffer();
                    port.Write(requestBytes, 0, requestBytes.Length);

                    var replyBytes = ReadWireFrame(port, cancellationToken);
                    return GaugeFrameCodec.Decode(replyBytes);
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transactionLock.Release();
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

