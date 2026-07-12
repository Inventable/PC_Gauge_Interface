using Gauge.Protocol;
using Gauge.Transport;

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    Console.WriteLine("Gauge CLI probe");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  list-ports        List serial ports visible to .NET.");
    Console.WriteLine("  encode-identify   Print the legacy wire bytes for an IDENTIFY request.");
    Console.WriteLine("  identify <port> [baud]");
    Console.WriteLine("  scan-identify [baud|auto] [seconds]");
    Console.WriteLine("  wait-identify <port> [baud] [seconds] [interval-ms]");
    Console.WriteLine("  verify-serial <port> [slow-baud] [fast-baud] [delay-ms]");
    Console.WriteLine();
    return 0;
}

if (args[0] == "list-ports")
{
    foreach (var port in SerialPortDiscovery.GetPortNames())
    {
        Console.WriteLine(port);
    }

    return 0;
}

if (args[0] == "encode-identify")
{
    var frame = GaugeFrame.Create(GaugeCommand.Identify);
    var wire = GaugeFrameCodec.Encode(frame);
    Console.WriteLine(Convert.ToHexString(wire));
    return 0;
}

if (args[0] == "identify")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: identify <port> [baud]");
        return 1;
    }

    var portName = args[1];
    var baudRate = args.Length >= 3 ? int.Parse(args[2]) : 57600;
    var reply = await TryIdentifyAsync(portName, baudRate, CancellationToken.None);

    if (reply is null)
    {
        Console.Error.WriteLine($"No gauge responded on {portName} at {baudRate} baud.");
        return 2;
    }
    
    PrintIdentifyResult(portName, baudRate, reply);
    return 0;
}

if (args[0] == "wait-identify")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: wait-identify <port> [baud] [seconds] [interval-ms]");
        return 1;
    }

    var portName = args[1];
    var baudRate = args.Length >= 3 ? int.Parse(args[2]) : 57600;
    var scanSeconds = args.Length >= 4 ? int.Parse(args[3]) : 30;
    var intervalMs = args.Length >= 5 ? int.Parse(args[4]) : 100;
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(scanSeconds));

    Console.WriteLine($"Polling {portName} at {baudRate} baud every {intervalMs} ms for {scanSeconds} seconds.");
    Console.WriteLine("Connect or power the gauge now.");

    try
    {
        var options = new SerialGaugeTransportOptions(
            portName,
            baudRate,
            ReadTimeoutMs: 250,
            WriteTimeoutMs: 250);
        await using var transport = new SerialGaugeTransport(options);
        await transport.OpenAsync(cts.Token).ConfigureAwait(false);

        while (!cts.IsCancellationRequested)
        {
            var result = await TryIdentifyOpenTransportAsync(transport, cts.Token);
            if (result is not null)
            {
                PrintIdentifyResult(portName, baudRate, result);
                return 0;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(intervalMs), cts.Token).ConfigureAwait(false);
        }
    }
    catch (OperationCanceledException)
    {
        // Normal wait timeout.
    }

    Console.Error.WriteLine($"No gauge responded on {portName} at {baudRate} baud before the wait timed out.");
    return 2;
}

if (args[0] == "verify-serial")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: verify-serial <port> [slow-baud] [fast-baud] [delay-ms]");
        return 1;
    }

    var portName = args[1];
    var slowBaud = args.Length >= 3 ? int.Parse(args[2]) : 57600;
    var fastBaud = args.Length >= 4 ? int.Parse(args[3]) : 460800;
    var delayMs = args.Length >= 5 ? int.Parse(args[4]) : 250;

    Console.WriteLine($"Step 1: IDENTIFY on {portName} at {slowBaud} baud.");
    var slowReply = await TryIdentifyAsync(portName, slowBaud, CancellationToken.None);
    if (slowReply is null)
    {
        Console.Error.WriteLine($"No gauge responded on {portName} at {slowBaud} baud.");
        return 2;
    }

    PrintIdentifyResult(portName, slowBaud, slowReply);
    Console.WriteLine($"Waiting {delayMs} ms for serial mode / PLL baud change.");
    await Task.Delay(TimeSpan.FromMilliseconds(delayMs)).ConfigureAwait(false);

    Console.WriteLine($"Step 2: IDENTIFY on {portName} at {fastBaud} baud.");
    var fastReply = await TryIdentifyAsync(portName, fastBaud, CancellationToken.None);
    if (fastReply is null)
    {
        Console.Error.WriteLine($"Slow identify succeeded, but no gauge responded on {portName} at {fastBaud} baud.");
        return 3;
    }

    PrintIdentifyResult(portName, fastBaud, fastReply);
    Console.WriteLine("Verified serial connection.");
    return 0;
}

if (args[0] == "scan-identify")
{
    var baudRates = args.Length >= 2 && !args[1].Equals("auto", StringComparison.OrdinalIgnoreCase)
        ? new[] { int.Parse(args[1]) }
        : new[] { 460800, 115200, 57600 };
    var scanSeconds = args.Length >= 3 ? int.Parse(args[2]) : 30;
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(scanSeconds));

    Console.WriteLine($"Scanning serial ports once per second at {string.Join(", ", baudRates)} baud for {scanSeconds} seconds.");
    Console.WriteLine("Connect or power the gauge now.");

    try
    {
        while (!cts.IsCancellationRequested)
        {
            var ports = SerialPortDiscovery.GetPortNames();
            foreach (var port in ports)
            {
                foreach (var baudRate in baudRates)
                {
                    var result = await TryIdentifyAsync(port, baudRate, cts.Token);
                    if (result is not null)
                    {
                        PrintIdentifyResult(port, baudRate, result);
                        return 0;
                    }
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cts.Token).ConfigureAwait(false);
        }
    }
    catch (OperationCanceledException)
    {
        // Normal scan timeout.
    }

    Console.Error.WriteLine("No gauge responded before the scan timed out.");
    return 2;
}

Console.Error.WriteLine($"Unknown command: {args[0]}");
return 1;

static async Task<GaugeFrame?> TryIdentifyAsync(string portName, int baudRate, CancellationToken cancellationToken)
{
    try
    {
        var options = new SerialGaugeTransportOptions(
            portName,
            baudRate,
            ReadTimeoutMs: 250,
            WriteTimeoutMs: 250);
        await using var transport = new SerialGaugeTransport(options);

        await transport.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await transport.TransactAsync(GaugeFrame.Create(GaugeCommand.Identify), cancellationToken).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is TimeoutException
        or InvalidOperationException
        or ArgumentOutOfRangeException
        or UnauthorizedAccessException
        or OperationCanceledException
        or IOException
        or GaugeProtocolException)
    {
        return null;
    }
}

static async Task<GaugeFrame?> TryIdentifyOpenTransportAsync(SerialGaugeTransport transport, CancellationToken cancellationToken)
{
    try
    {
        return await transport.TransactAsync(GaugeFrame.Create(GaugeCommand.Identify), cancellationToken).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is TimeoutException
        or InvalidOperationException
        or ArgumentOutOfRangeException
        or UnauthorizedAccessException
        or OperationCanceledException
        or IOException
        or GaugeProtocolException)
    {
        return null;
    }
}

static void PrintIdentifyResult(string portName, int baudRate, GaugeFrame reply)
{
    Console.WriteLine($"Gauge found on {portName} at {baudRate} baud.");
    Console.WriteLine($"Command: {reply.Command}");
    Console.WriteLine($"Payload bytes: {reply.Payload.Length}");
    Console.WriteLine(Convert.ToHexString(reply.Payload));

    if (reply.Payload.Length >= 32)
    {
        PrintDevice(DeviceData.DecodeAcousticGauge(reply.Payload));
    }
    else if (reply.Payload.Length >= 22)
    {
        PrintDevice(DeviceData.DecodeMemoryGauge(reply.Payload));
    }
}

static void PrintDevice(DeviceData device)
{
    Console.WriteLine($"Firmware: {device.FirmwareMajor}.{device.FirmwareMinor}");
    Console.WriteLine($"Device type: {device.DeviceType}");
    Console.WriteLine($"Device serial: {device.DeviceSerial}");
    Console.WriteLine($"PCB type: {device.PcbType}");
    Console.WriteLine($"PCB serial: {device.PcbSerial}");
    Console.WriteLine($"Measurement interval: {device.MeasurementInterval}");
    Console.WriteLine($"Memory mode: {device.MemoryMode}");
    Console.WriteLine($"Erase status: {device.EraseStatus}");
}
