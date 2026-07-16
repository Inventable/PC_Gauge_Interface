using System.IO.Ports;
using Gauge.Protocol;

namespace Gauge.Transport;

public sealed class SerialBootloaderClient : IAsyncDisposable
{
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly int _timeoutMs;
    private readonly SemaphoreSlim _transactionLock = new(1, 1);
    private SerialPort? _port;

    public SerialBootloaderClient(string portName, int baudRate = 57600, int timeoutMs = 1000)
    {
        _portName = portName;
        _baudRate = baudRate;
        _timeoutMs = timeoutMs;
    }

    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_port?.IsOpen == true)
        {
            return Task.CompletedTask;
        }

        _port = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = _timeoutMs,
            WriteTimeout = _timeoutMs
        };
        _port.Open();
        _port.DiscardInBuffer();
        _port.DiscardOutBuffer();
        return Task.CompletedTask;
    }

    public async Task<BootloaderVersion> ReadVersionAsync(
        int maximumAttempts = 3,
        CancellationToken cancellationToken = default)
    {
        if (maximumAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                var request = BootloaderFrame.Create(BootloaderCommand.ReadVersion);
                var response = await TransactAsync(
                    request,
                    BootloaderProtocolConstants.VersionPayloadLength,
                    cancellationToken).ConfigureAwait(false);
                return BootloaderVersion.Decode(response.Payload);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or GaugeProtocolException)
            {
                lastFailure = ex;
                if (attempt < maximumAttempts)
                {
                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new IOException($"Bootloader did not return a valid version after {maximumAttempts} attempts.", lastFailure);
    }

    public async Task ResetToApplicationAsync(CancellationToken cancellationToken = default)
    {
        var request = BootloaderFrame.Create(BootloaderCommand.ResetDevice);
        var response = await TransactAsync(request, 1, cancellationToken).ConfigureAwait(false);
        if (response.Payload is not [BootloaderProtocolConstants.CommandSuccess])
        {
            throw new GaugeProtocolException("Bootloader rejected the reset command.");
        }
    }

    private async Task<BootloaderFrame> TransactAsync(
        BootloaderFrame request,
        int expectedPayloadLength,
        CancellationToken cancellationToken)
    {
        var port = _port;
        if (port is null || !port.IsOpen)
        {
            throw new InvalidOperationException("Bootloader serial port is not open.");
        }

        await _transactionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                port.DiscardInBuffer();
                port.DiscardOutBuffer();

                var requestBytes = BootloaderFrameCodec.EncodeRequest(request);
                port.Write(requestBytes, 0, requestBytes.Length);

                var responseBytes = ReadResponse(port, expectedPayloadLength, cancellationToken);
                var response = BootloaderFrameCodec.DecodeResponse(responseBytes, expectedPayloadLength);
                if (response.Command != request.Command)
                {
                    throw new GaugeProtocolException(
                        $"Bootloader response command mismatch. Sent {request.Command}, received {response.Command}.");
                }

                return response;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _transactionLock.Release();
        }
    }

    private static byte[] ReadResponse(SerialPort port, int payloadLength, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (port.ReadByte() == BootloaderProtocolConstants.StartByte)
            {
                break;
            }
        }

        var response = new byte[1 + BootloaderProtocolConstants.HeaderLength + payloadLength];
        response[0] = BootloaderProtocolConstants.StartByte;
        ReadExactly(port, response.AsSpan(1), cancellationToken);
        return response;
    }

    private static void ReadExactly(SerialPort port, Span<byte> target, CancellationToken cancellationToken)
    {
        var bytes = new byte[target.Length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = port.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                throw new TimeoutException("Serial port returned no data while reading a bootloader response.");
            }

            offset += read;
        }

        bytes.CopyTo(target);
    }

    public ValueTask DisposeAsync()
    {
        _transactionLock.Dispose();
        _port?.Dispose();
        return ValueTask.CompletedTask;
    }
}
