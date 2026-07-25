using System.Diagnostics;
using Gauge.Protocol;

namespace Gauge.Core;

public enum ExternalEraseMode
{
    Progress,
    LegacyEstimated
}

public sealed record ExternalEraseProgress(
    ExternalEraseMode Mode,
    double Percent,
    bool IsEstimated,
    ushort Completed,
    ushort Total,
    uint Address,
    byte BusyMask,
    byte ErrorMask,
    TimeSpan Elapsed);

public sealed record ExternalEraseResult(
    ExternalEraseMode Mode,
    TimeSpan Elapsed,
    ushort Completed,
    ushort Total);

public sealed class ExternalMemoryEraseService
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(20);
    public static readonly TimeSpan DefaultPairTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultOverallTimeout = TimeSpan.FromHours(2);
    public static readonly TimeSpan LegacyEstimatedDuration = TimeSpan.FromSeconds(131);

    private const byte CommandSuccess = 0x01;
    private const byte ErrorMemoryWrite = 0xFB;
    private const byte ErrorMemoryBusy = 0xFC;
    private const byte ErrorInvalidCommand = 0xFF;

    private readonly GaugeSession _session;

    public ExternalMemoryEraseService(GaugeSession session)
    {
        _session = session;
    }

    public async Task<ExternalEraseResult> EraseAsync(
        IProgress<ExternalEraseProgress>? progress = null,
        CancellationToken cancellationToken = default,
        TimeSpan? pollInterval = null,
        TimeSpan? pairTimeout = null,
        TimeSpan? overallTimeout = null)
    {
        ExternalEraseResult result;
        var startReply = await _session
            .SendCommandAsync(GaugeCommand.StartProgressErase, cancellationToken)
            .ConfigureAwait(false);
        if (startReply.Payload is [ErrorInvalidCommand])
        {
            result = await EraseLegacyAsync(
                progress,
                cancellationToken,
                pollInterval ?? DefaultPollInterval,
                overallTimeout ?? DefaultOverallTimeout).ConfigureAwait(false);
        }
        else
        {
            ExternalEraseStatus status;
            if (startReply.Payload is [ErrorMemoryBusy])
            {
                status = await ReadProgressAsync(cancellationToken).ConfigureAwait(false);
                if (status.State != ExternalEraseState.Busy)
                {
                    throw new GaugeProtocolException(
                        "Gauge reported memory busy without an active progress erase.");
                }
            }
            else if (startReply.Payload is [ErrorMemoryWrite])
            {
                throw new InvalidDataException("Gauge could not start external-memory erase.");
            }
            else
            {
                status = ExternalEraseStatus.Parse(startReply.Payload);
            }

            result = await PollProgressEraseAsync(
                status,
                progress,
                cancellationToken,
                pollInterval ?? DefaultPollInterval,
                pairTimeout ?? DefaultPairTimeout,
                overallTimeout ?? DefaultOverallTimeout).ConfigureAwait(false);
        }

        await FinalizeAndVerifyEraseAsync(cancellationToken).ConfigureAwait(false);
        if (result.Mode == ExternalEraseMode.LegacyEstimated)
        {
            progress?.Report(new ExternalEraseProgress(
                ExternalEraseMode.LegacyEstimated,
                100,
                true,
                100,
                100,
                0,
                0,
                0,
                result.Elapsed));
        }
        return result;
    }

    public async Task PrepareRestartFromBeginningAsync(
        CancellationToken cancellationToken = default,
        TimeSpan? pollInterval = null,
        TimeSpan? overallTimeout = null)
    {
        var interval = pollInterval ?? DefaultPollInterval;
        var deadline = overallTimeout ?? DefaultOverallTimeout;

        await WaitForMemoryIdleAsync(
            cancellationToken,
            interval,
            deadline).ConfigureAwait(false);

        var reset = await _session
            .SendCommandAsync(GaugeCommand.ResetDevice, cancellationToken)
            .ConfigureAwait(false);
        if (reset.Payload is not [CommandSuccess])
        {
            throw new InvalidDataException(
                "Gauge did not accept the reset required to restart the erase from address zero.");
        }
    }

    private async Task<ExternalEraseResult> PollProgressEraseAsync(
        ExternalEraseStatus status,
        IProgress<ExternalEraseProgress>? progress,
        CancellationToken cancellationToken,
        TimeSpan pollInterval,
        TimeSpan pairTimeout,
        TimeSpan overallTimeout)
    {
        var overall = Stopwatch.StartNew();
        var pair = Stopwatch.StartNew();
        var lastCompleted = status.Completed;
        Report(status);

        while (status.State == ExternalEraseState.Busy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (overall.Elapsed > overallTimeout)
            {
                throw new TimeoutException("External-memory erase exceeded its two-hour deadline.");
            }

            if (pair.Elapsed > pairTimeout)
            {
                throw new TimeoutException(
                    $"External-memory erase pair {status.Completed + 1} made no progress for {pairTimeout.TotalSeconds:F0} seconds.");
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
            status = await ReadProgressAsync(cancellationToken).ConfigureAwait(false);

            if (status.Completed < lastCompleted)
            {
                throw new GaugeProtocolException("Erase progress moved backwards.");
            }

            if (status.Completed > lastCompleted)
            {
                lastCompleted = status.Completed;
                pair.Restart();
            }

            Report(status);
        }

        if (status.ErrorMask != 0 || status.State == ExternalEraseState.Error)
        {
            throw BuildChipError(status.ErrorMask);
        }

        if (status.State != ExternalEraseState.Complete ||
            status.Completed != ExternalEraseStatus.ExpectedTotal)
        {
            throw new InvalidDataException(
                $"External-memory erase stopped in state {status.State} at {status.Completed}/{status.Total}.");
        }

        return new ExternalEraseResult(
            ExternalEraseMode.Progress,
            overall.Elapsed,
            status.Completed,
            status.Total);

        void Report(ExternalEraseStatus value)
        {
            if (value.ErrorMask != 0 || value.State == ExternalEraseState.Error)
            {
                throw BuildChipError(value.ErrorMask);
            }

            progress?.Report(new ExternalEraseProgress(
                ExternalEraseMode.Progress,
                value.Percent,
                false,
                value.Completed,
                value.Total,
                value.Address,
                value.BusyMask,
                value.ErrorMask,
                overall.Elapsed));
        }
    }

    private async Task<ExternalEraseResult> EraseLegacyAsync(
        IProgress<ExternalEraseProgress>? progress,
        CancellationToken cancellationToken,
        TimeSpan pollInterval,
        TimeSpan overallTimeout)
    {
        var start = await _session
            .SendCommandAsync(GaugeCommand.EraseExternalMemory, cancellationToken)
            .ConfigureAwait(false);
        if (start.Payload is not [CommandSuccess] and not [ErrorMemoryBusy])
        {
            throw new InvalidDataException(
                $"Legacy external-memory erase was rejected with {Convert.ToHexString(start.Payload)}.");
        }

        var clock = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed > overallTimeout)
            {
                throw new TimeoutException("Legacy external-memory erase exceeded its two-hour deadline.");
            }

            var percent = Math.Min(
                99,
                clock.Elapsed.TotalMilliseconds / LegacyEstimatedDuration.TotalMilliseconds * 100);
            progress?.Report(new ExternalEraseProgress(
                ExternalEraseMode.LegacyEstimated,
                percent,
                true,
                (ushort)Math.Floor(percent),
                100,
                0,
                0,
                0,
                clock.Elapsed));

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
            var reply = await _session
                .SendCommandAsync(GaugeCommand.MemoryStatus, cancellationToken)
                .ConfigureAwait(false);
            if (reply.Payload.Length != 1 || (reply.Payload[0] & 0xF0) != 0)
            {
                throw new GaugeProtocolException("Legacy memory status returned an invalid payload.");
            }

            var status = reply.Payload[0];
            var errorMask = (byte)((status >> 2) & 0x03);
            if (errorMask != 0)
            {
                throw BuildChipError(errorMask);
            }

            if ((status & 0x03) != 0)
            {
                continue;
            }

            return new ExternalEraseResult(
                ExternalEraseMode.LegacyEstimated,
                clock.Elapsed,
                100,
                100);
        }
    }

    private async Task FinalizeAndVerifyEraseAsync(CancellationToken cancellationToken)
    {
        var end = await _session
            .SendCommandAsync(GaugeCommand.EndMemoryErase, cancellationToken)
            .ConfigureAwait(false);
        if (end.Payload is not [CommandSuccess])
        {
            throw new InvalidDataException(
                "Gauge memory became idle but the erase interlock could not be cleared.");
        }

        var identity = await _session.IdentifyAsync(cancellationToken).ConfigureAwait(false);
        var device = DeviceData.DecodeMemoryGauge(identity.Payload);
        if (device.EraseStatus.GetValueOrDefault() != 0)
        {
            throw new InvalidDataException(
                "Gauge completed the memory operation but still reports an active erase interlock.");
        }
    }

    private async Task WaitForMemoryIdleAsync(
        CancellationToken cancellationToken,
        TimeSpan pollInterval,
        TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed > timeout)
            {
                throw new TimeoutException(
                    "Timed out waiting for the current flash operation before restarting the erase.");
            }

            var reply = await _session
                .SendCommandAsync(GaugeCommand.MemoryStatus, cancellationToken)
                .ConfigureAwait(false);
            if (reply.Payload.Length != 1 || (reply.Payload[0] & 0xF0) != 0)
            {
                throw new GaugeProtocolException(
                    "Memory status returned an invalid payload before erase restart.");
            }

            var status = reply.Payload[0];
            var errorMask = (byte)((status >> 2) & 0x03);
            if (errorMask != 0)
            {
                throw BuildChipError(errorMask);
            }

            if ((status & 0x03) == 0)
            {
                return;
            }

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ExternalEraseStatus> ReadProgressAsync(
        CancellationToken cancellationToken)
    {
        var reply = await _session
            .SendCommandAsync(GaugeCommand.GetEraseProgress, cancellationToken)
            .ConfigureAwait(false);
        return ExternalEraseStatus.Parse(reply.Payload);
    }

    private static InvalidDataException BuildChipError(byte mask)
    {
        var chips = mask switch
        {
            1 => "chip 1",
            2 => "chip 2",
            3 => "chips 1 and 2",
            _ => "unknown chip"
        };
        return new InvalidDataException(
            $"External-memory erase failed on {chips} (error mask 0x{mask:X2}).");
    }
}
