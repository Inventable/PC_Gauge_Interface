using System.Globalization;
using Gauge.Core;
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
    Console.WriteLine("  find-eof <port> [baud]");
    Console.WriteLine("  read-file-sector <port> [baud] [address] [length]");
    Console.WriteLine("  list-files <port> [baud] [table-bytes] [chunk-bytes]");
    Console.WriteLine("  download-file <port> <file-index> <output-path> [baud] [chunk-bytes]");
    Console.WriteLine("  decode-raw <input-path> [start-address] [measurement-interval] [count-bias]");
    Console.WriteLine("  export-raw-csv <input-path> <output-path> [start-address] [measurement-interval] [count-bias]");
    Console.WriteLine("  initialise-sensor <port> [baud]");
    Console.WriteLine("  read-sensor-serial <port> [baud]");
    Console.WriteLine("  read-sensor-cal <port> [baud]");
    Console.WriteLine("  read-pressure-poly <port> [baud]");
    Console.WriteLine("  read-temperature-poly <port> [baud]");
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

if (args[0] == "find-eof")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: find-eof <port> [baud]");
        return 1;
    }

    var portName = args[1];
    var baudRate = args.Length >= 3 ? int.Parse(args[2]) : 460800;
    await using var transport = await OpenSerialTransportAsync(portName, baudRate, CancellationToken.None);
    var session = new GaugeSession(transport);
    var eof = await session.FindEndOfFileAsync(CancellationToken.None).ConfigureAwait(false);

    Console.WriteLine($"EOF: {eof} ({eof.Value})");
    return 0;
}

if (args[0] == "read-file-sector")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: read-file-sector <port> [baud] [address] [length]");
        return 1;
    }

    var portName = args[1];
    var baudRate = args.Length >= 3 ? int.Parse(args[2]) : 460800;
    var address = args.Length >= 4 ? ParseUInt32(args[3]) : 0;
    var length = args.Length >= 5 ? ParseUInt16(args[4]) : (ushort)256;

    await using var transport = await OpenSerialTransportAsync(portName, baudRate, CancellationToken.None);
    var session = new GaugeSession(transport);
    var bytes = await session
        .ReadExternalMemoryAsync(address, length, GaugeCommand.ReadFileSector, CancellationToken.None)
        .ConfigureAwait(false);

    Console.WriteLine($"Read {bytes.Length} byte(s) from 0x{address:X8}.");
    PrintHexDump(address, bytes);
    return 0;
}

if (args[0] == "list-files")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: list-files <port> [baud] [table-bytes] [chunk-bytes]");
        return 1;
    }

    var portName = args[1];
    var baudRate = args.Length >= 3 ? int.Parse(args[2]) : 460800;
    var tableBytes = args.Length >= 4 ? ParseInt32(args[3]) : 0x4000;
    var chunkBytes = args.Length >= 5 ? ParseUInt16(args[4]) : (ushort)1024;

    await using var transport = await OpenSerialTransportAsync(portName, baudRate, CancellationToken.None);
    var session = new GaugeSession(transport);
    var eof = await session.FindEndOfFileAsync(CancellationToken.None).ConfigureAwait(false);
    var table = await session
        .ReadExternalMemoryChunkedAsync(0, tableBytes, chunkBytes, GaugeCommand.ReadFileSector, CancellationToken.None)
        .ConfigureAwait(false);
    var records = MemoryGaugeFileRecord.ParseTable(table);

    Console.WriteLine($"EOF: {eof} ({eof.Value})");
    Console.WriteLine($"Valid file records: {records.Count}");

    if (records.Count == 0)
    {
        return 0;
    }

    Console.WriteLine("Idx  Type      Data Addr   Rate  Est Records  Reset  CRC");
    for (var index = 0; index < records.Count; index++)
    {
        var record = records[index];
        var nextAddress = GetNextDataAddress(records, index, eof);
        var estimatedRecords = nextAddress > record.DataAddress.Value
            ? (nextAddress - record.DataAddress.Value) / MemoryGaugeFileRecord.Length
            : 0;

        Console.WriteLine(
            $"{record.Index,3}  {record.RecordType,-8}  {record.DataAddress}  {record.MeasurementInterval,4}  {estimatedRecords,11}  0x{record.ResetCause:X2}   {(record.IsCrcValid ? "OK" : "BAD")}");
    }

    return 0;
}

if (args[0] == "download-file")
{
    if (args.Length < 4)
    {
        Console.Error.WriteLine("Usage: download-file <port> <file-index> <output-path> [baud] [chunk-bytes]");
        return 1;
    }

    var portName = args[1];
    var fileIndex = ParseInt32(args[2]);
    var outputPath = args[3];
    var baudRate = args.Length >= 5 ? int.Parse(args[4]) : 460800;
    var chunkBytes = args.Length >= 6 ? ParseUInt16(args[5]) : (ushort)1024;

    await using var transport = await OpenSerialTransportAsync(portName, baudRate, CancellationToken.None);
    var session = new GaugeSession(transport);
    var eof = await session.FindEndOfFileAsync(CancellationToken.None).ConfigureAwait(false);
    var table = await session
        .ReadExternalMemoryChunkedAsync(0, 0x4000, chunkBytes, GaugeCommand.ReadFileSector, CancellationToken.None)
        .ConfigureAwait(false);
    var records = MemoryGaugeFileRecord.ParseTable(table);

    if (fileIndex < 0 || fileIndex >= records.Count)
    {
        Console.Error.WriteLine($"File index {fileIndex} is not valid. Valid range is 0 to {records.Count - 1}.");
        return 2;
    }

    var record = records[fileIndex];
    var nextAddress = GetNextDataAddress(records, fileIndex, eof);
    if (nextAddress <= record.DataAddress.Value)
    {
        Console.Error.WriteLine($"File index {fileIndex} has no readable data range.");
        return 3;
    }

    var bytesToRead = checked((int)(nextAddress - record.DataAddress.Value));
    Console.WriteLine($"Downloading file {fileIndex}: {bytesToRead} byte(s) from {record.DataAddress} to 0x{nextAddress - 1:X8}.");
    var bytes = await session
        .ReadExternalMemoryChunkedAsync(record.DataAddress.Value, bytesToRead, chunkBytes, GaugeCommand.ReadRecordSector, CancellationToken.None)
        .ConfigureAwait(false);

    var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    await File.WriteAllBytesAsync(outputPath, bytes, CancellationToken.None).ConfigureAwait(false);
    Console.WriteLine($"Wrote {bytes.Length} byte(s) to {outputPath}.");
    return 0;
}

if (args[0] == "decode-raw")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: decode-raw <input-path> [start-address] [measurement-interval] [count-bias]");
        return 1;
    }

    var inputPath = args[1];
    var startAddress = args.Length >= 3 ? ParseUInt32(args[2]) : 0;
    var measurementInterval = args.Length >= 4 ? ParseUInt32(args[3]) : 1;
    var countBias = args.Length >= 5 ? ParseUInt32(args[4]) : 0;
    var bytes = await File.ReadAllBytesAsync(inputPath, CancellationToken.None).ConfigureAwait(false);

    if (bytes.Length % MemoryGaugeDataRecord.Length != 0)
    {
        Console.Error.WriteLine($"Input length {bytes.Length} is not a multiple of {MemoryGaugeDataRecord.Length}; trailing bytes will be ignored.");
    }

    var records = MemoryGaugeDataRecord.ParseMany(startAddress, bytes);

    Console.WriteLine("P Counts,T Counts,Seq,Counter,Address,Timestamp,CRC ERR,Batt Status");
    foreach (var row in BuildRawRows(records, measurementInterval, countBias))
    {
        Console.WriteLine(row);
    }

    return 0;
}

if (args[0] == "export-raw-csv")
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: export-raw-csv <input-path> <output-path> [start-address] [measurement-interval] [count-bias]");
        return 1;
    }

    var inputPath = args[1];
    var outputPath = args[2];
    var startAddress = args.Length >= 4 ? ParseUInt32(args[3]) : 0;
    var measurementInterval = args.Length >= 5 ? ParseUInt32(args[4]) : 1;
    var countBias = args.Length >= 6 ? ParseUInt32(args[5]) : 0;
    var bytes = await File.ReadAllBytesAsync(inputPath, CancellationToken.None).ConfigureAwait(false);
    var records = MemoryGaugeDataRecord.ParseMany(startAddress, bytes);

    var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
    if (!string.IsNullOrWhiteSpace(directory))
    {
        Directory.CreateDirectory(directory);
    }

    var lines = new List<string> { "P Counts,T Counts,Seq,Counter,Address,Timestamp,CRC ERR,Batt Status" };
    lines.AddRange(BuildRawRows(records, measurementInterval, countBias));
    await File.WriteAllLinesAsync(outputPath, lines, CancellationToken.None).ConfigureAwait(false);
    Console.WriteLine($"Wrote {lines.Count - 1} row(s) to {outputPath}.");
    return 0;
}

if (args[0] == "initialise-sensor")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: initialise-sensor <port> [baud]");
        return 1;
    }

    var portName = args[1];
    var baudRate = args.Length >= 3 ? int.Parse(args[2]) : 460800;

    try
    {
        await using var transport = await OpenSerialTransportWithTimeoutAsync(portName, baudRate, 12000, CancellationToken.None);
        var session = new GaugeSession(transport);
        var reply = await session.SendCommandAsync(GaugeCommand.InitialiseSensor, CancellationToken.None).ConfigureAwait(false);

        Console.WriteLine($"Command: {reply.Command}");
        Console.WriteLine($"Payload bytes: {reply.Payload.Length}");
        Console.WriteLine(Convert.ToHexString(reply.Payload));
        return reply.Payload is [0x01] ? 0 : 2;
    }
    catch (Exception ex) when (IsExpectedSerialFailure(ex))
    {
        Console.Error.WriteLine($"Sensor initialise did not complete: {ex.Message}");
        return 2;
    }
}

if (args[0] is "read-sensor-serial" or "read-sensor-cal" or "read-pressure-poly" or "read-temperature-poly")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine($"Usage: {args[0]} <port> [baud]");
        return 1;
    }

    var portName = args[1];
    var baudRate = args.Length >= 3 ? int.Parse(args[2]) : 460800;
    var command = args[0] switch
    {
        "read-sensor-serial" => GaugeCommand.ReadSensorSerial,
        "read-sensor-cal" => GaugeCommand.ReadSensorCalibration,
        "read-pressure-poly" => GaugeCommand.ReadSensorPressurePolynomial,
        "read-temperature-poly" => GaugeCommand.ReadSensorTemperaturePolynomial,
        _ => throw new InvalidOperationException("Unexpected sensor command.")
    };

    try
    {
        await using var transport = await OpenSerialTransportWithTimeoutAsync(portName, baudRate, 12000, CancellationToken.None);
        var session = new GaugeSession(transport);
        var payload = await session.ReadSensorDataAsync(command, CancellationToken.None).ConfigureAwait(false);

        Console.WriteLine($"Command: {command}");
        Console.WriteLine($"Payload bytes: {payload.Length}");
        Console.WriteLine(Convert.ToHexString(payload));
        Console.WriteLine("ASCII:");
        Console.WriteLine(ToPrintableAscii(payload));
        if (command == GaugeCommand.ReadSensorCalibration)
        {
            PrintCalibrationHeader(payload);
        }

        if (command is GaugeCommand.ReadSensorPressurePolynomial or GaugeCommand.ReadSensorTemperaturePolynomial)
        {
            PrintPolynomialRows(payload);
        }

        return 0;
    }
    catch (Exception ex) when (IsExpectedSerialFailure(ex))
    {
        Console.Error.WriteLine($"{command} did not complete: {ex.Message}");
        return 2;
    }
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

static async Task<SerialGaugeTransport> OpenSerialTransportAsync(
    string portName,
    int baudRate,
    CancellationToken cancellationToken)
{
    return await OpenSerialTransportWithTimeoutAsync(portName, baudRate, 1000, cancellationToken).ConfigureAwait(false);
}

static async Task<SerialGaugeTransport> OpenSerialTransportWithTimeoutAsync(
    string portName,
    int baudRate,
    int timeoutMs,
    CancellationToken cancellationToken)
{
    var options = new SerialGaugeTransportOptions(
        portName,
        baudRate,
        ReadTimeoutMs: timeoutMs,
        WriteTimeoutMs: timeoutMs);
    var transport = new SerialGaugeTransport(options);

    try
    {
        await transport.OpenAsync(cancellationToken).ConfigureAwait(false);
        return transport;
    }
    catch
    {
        await transport.DisposeAsync().ConfigureAwait(false);
        throw;
    }
}

static bool IsExpectedSerialFailure(Exception ex)
{
    return ex is TimeoutException
        or InvalidOperationException
        or ArgumentOutOfRangeException
        or UnauthorizedAccessException
        or OperationCanceledException
        or IOException
        or GaugeProtocolException;
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

static uint GetNextDataAddress(IReadOnlyList<MemoryGaugeFileRecord> records, int index, GaugeMemoryAddress eof)
{
    for (var next = index + 1; next < records.Count; next++)
    {
        if (records[next].DataAddress.Value > records[index].DataAddress.Value)
        {
            return records[next].DataAddress.Value;
        }
    }

    return eof.Value == 0 ? records[index].DataAddress.Value : eof.Value + MemoryGaugeFileRecord.Length;
}

static void PrintHexDump(uint startAddress, ReadOnlySpan<byte> bytes)
{
    for (var offset = 0; offset < bytes.Length; offset += 16)
    {
        var line = bytes.Slice(offset, Math.Min(16, bytes.Length - offset));
        Console.Write($"0x{startAddress + (uint)offset:X8}  ");

        for (var index = 0; index < line.Length; index++)
        {
            Console.Write($"{line[index]:X2} ");
        }

        Console.WriteLine();
    }
}

static IEnumerable<string> BuildRawRows(
    IReadOnlyList<MemoryGaugeDataRecord> records,
    uint measurementInterval,
    uint countBias)
{
    foreach (var record in records)
    {
        yield return BuildRawRow(record, record.FirstSample, measurementInterval, countBias);
        yield return BuildRawRow(record, record.SecondSample, measurementInterval, countBias);
    }
}

static string BuildRawRow(MemoryGaugeDataRecord record, MemoryGaugeSample sample, uint measurementInterval, uint countBias)
{
    var timestamp = sample.SampleIndex * measurementInterval;
    var pressureCounts = sample.PressureCounts + countBias;
    var temperatureCounts = sample.TemperatureCounts + countBias;

    return $"{pressureCounts},{temperatureCounts},{sample.SampleIndex},{record.Counter},{record.Address},{timestamp},{(record.IsCrcValid ? 0 : 1)},{record.BatteryStatus}";
}

static string ToPrintableAscii(ReadOnlySpan<byte> bytes)
{
    var chars = new char[bytes.Length];

    for (var index = 0; index < bytes.Length; index++)
    {
        chars[index] = bytes[index] switch
        {
            0x0D => '\r',
            0x0A => '\n',
            >= 0x20 and <= 0x7E => (char)bytes[index],
            _ => '.'
        };
    }

    return new string(chars);
}

static void PrintPolynomialRows(ReadOnlySpan<byte> payload)
{
    try
    {
        var rows = SensorAsciiData.ParseHexDoubleRows(payload);
        Console.WriteLine("Decoded coefficients:");
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            Console.WriteLine($"{rowIndex}: {string.Join(", ", rows[rowIndex].Select(value => value.ToString("G17", CultureInfo.InvariantCulture)))}");
        }
    }
    catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
    {
        Console.WriteLine($"Could not decode coefficient rows: {ex.Message}");
    }
}

static void PrintCalibrationHeader(ReadOnlySpan<byte> payload)
{
    var header = SensorCalibrationHeader.Parse(payload);
    Console.WriteLine("Parsed header:");
    Console.WriteLine($"Reference clock: {header.ReferenceClock}");
    Console.WriteLine($"Sensor ID: {header.SensorId}");
    Console.WriteLine($"Count bias: {header.CountBias}");
    Console.WriteLine($"Pressure startup ms: {header.PressureStartupMilliseconds}");
    Console.WriteLine($"PLL clock: {header.PllClock}");
}

static ushort ParseUInt16(string value)
{
    var parsed = ParseUInt32(value);
    if (parsed > ushort.MaxValue)
    {
        throw new ArgumentOutOfRangeException(nameof(value), value, "Value is too large for UInt16.");
    }

    return (ushort)parsed;
}

static int ParseInt32(string value)
{
    var parsed = ParseUInt32(value);
    if (parsed > int.MaxValue)
    {
        throw new ArgumentOutOfRangeException(nameof(value), value, "Value is too large for Int32.");
    }

    return (int)parsed;
}

static uint ParseUInt32(string value)
{
    return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? Convert.ToUInt32(value[2..], 16)
        : Convert.ToUInt32(value);
}
