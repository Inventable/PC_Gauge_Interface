using Gauge.Protocol;
using Gauge.Transport;

namespace Gauge.Core;

public enum FirmwareUpdatePhase
{
    Erasing,
    Programming,
    Verifying,
    CommittingStartVector,
    Complete
}

public sealed record FirmwareUpdateProgress(
    FirmwareUpdatePhase Phase,
    int CompletedOperations,
    int TotalOperations,
    uint Address,
    string Message);

public sealed record FirmwareUpdateResult(
    int ErasedRows,
    int ProgrammedRows,
    string ImageSha256);

public sealed class GaugeFirmwareUpdater
{
    private readonly IBootloaderClient _bootloader;
    private readonly BootloaderVersion _version;

    public GaugeFirmwareUpdater(IBootloaderClient bootloader, BootloaderVersion version)
    {
        _bootloader = bootloader;
        _version = version;
    }

    public async Task<FirmwareUpdateResult> ProgramAsync(
        BootloaderApplicationImage image,
        IProgress<FirmwareUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateLoaderGeometry();

        var eraseRows = checked((int)((BootloaderApplicationImage.FlashEndExclusive
            - BootloaderApplicationImage.ApplicationStart) / BootloaderApplicationImage.RowSize));
        var dataRows = image.DataRows.OrderByDescending(row => row.Address).ToArray();
        var totalOperations = eraseRows + dataRows.Length + dataRows.Length + 1;
        var completed = 0;

        cancellationToken.ThrowIfCancellationRequested();
        Report(progress, FirmwareUpdatePhase.Erasing, completed, totalOperations,
            BootloaderApplicationImage.ApplicationStart, "Erasing application start row first");
        await EnsureRowErasedAsync(BootloaderApplicationImage.ApplicationStart).ConfigureAwait(false);
        completed++;

        for (var address = BootloaderApplicationImage.FlashEndExclusive - BootloaderApplicationImage.RowSize;
             address > BootloaderApplicationImage.ApplicationStart;
             address -= BootloaderApplicationImage.RowSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, FirmwareUpdatePhase.Erasing, completed, totalOperations, address, "Erasing application row");
            await EnsureRowErasedAsync(address).ConfigureAwait(false);
            completed++;
        }

        foreach (var row in dataRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, FirmwareUpdatePhase.Programming, completed, totalOperations, row.Address, "Programming application row");
            await WriteAndVerifyRowAsync(row).ConfigureAwait(false);
            completed++;
        }

        foreach (var row in dataRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, FirmwareUpdatePhase.Verifying, completed, totalOperations, row.Address, "Final readback verification");
            await VerifyRowAsync(row).ConfigureAwait(false);
            completed++;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Report(progress, FirmwareUpdatePhase.CommittingStartVector, completed, totalOperations,
            image.StartRow.Address, "Programming application start row last");
        await WriteAndVerifyRowAsync(image.StartRow).ConfigureAwait(false);
        completed++;

        Report(progress, FirmwareUpdatePhase.Complete, completed, totalOperations,
            image.StartRow.Address, "Firmware image programmed and verified");
        return new FirmwareUpdateResult(eraseRows, dataRows.Length + 1, image.Sha256);
    }

    private void ValidateLoaderGeometry()
    {
        if (_version.EraseBlockSize != BootloaderApplicationImage.RowSize
            || _version.WriteBlockSize != BootloaderApplicationImage.RowSize)
        {
            throw new InvalidOperationException(
                $"Unsupported loader geometry: erase {_version.EraseBlockSize}, write {_version.WriteBlockSize}; expected 64-byte rows.");
        }

        if (_version.MaximumPacketSize < BootloaderApplicationImage.RowSize)
        {
            throw new InvalidOperationException(
                $"Loader maximum packet size {_version.MaximumPacketSize} is smaller than one flash row.");
        }
    }

    private async Task EnsureRowErasedAsync(uint address)
    {
        Exception? acknowledgementFailure = null;
        try
        {
            await _bootloader.EraseFlashRowsOnceAsync(address, 1, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsAmbiguousMutationFailure(ex))
        {
            acknowledgementFailure = ex;
        }

        var readback = await _bootloader
            .ReadFlashAsync(address, BootloaderApplicationImage.RowSize, maximumAttempts: 3, CancellationToken.None)
            .ConfigureAwait(false);
        if (readback.All(value => value == 0xFF))
        {
            return;
        }

        if (acknowledgementFailure is null)
        {
            throw new IOException($"Flash row 0x{address:X4} was acknowledged erased but readback is not blank.");
        }

        await _bootloader.EraseFlashRowsOnceAsync(address, 1, CancellationToken.None).ConfigureAwait(false);
        readback = await _bootloader
            .ReadFlashAsync(address, BootloaderApplicationImage.RowSize, maximumAttempts: 3, CancellationToken.None)
            .ConfigureAwait(false);
        if (!readback.All(value => value == 0xFF))
        {
            throw new IOException($"Flash row 0x{address:X4} failed erase verification after a readback-resolved retry.");
        }
    }

    private async Task WriteAndVerifyRowAsync(FirmwareFlashRow row)
    {
        try
        {
            await _bootloader.WriteFlashOnceAsync(row.Address, row.Data, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsAmbiguousMutationFailure(ex))
        {
            // Readback below determines whether the unacknowledged write completed.
        }

        var readback = await _bootloader
            .ReadFlashAsync(row.Address, BootloaderApplicationImage.RowSize, maximumAttempts: 3, CancellationToken.None)
            .ConfigureAwait(false);
        if (readback.AsSpan().SequenceEqual(row.Data))
        {
            return;
        }

        await EnsureRowErasedAsync(row.Address).ConfigureAwait(false);
        try
        {
            await _bootloader.WriteFlashOnceAsync(row.Address, row.Data, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsAmbiguousMutationFailure(ex))
        {
            // The final readback resolves the second write acknowledgement too.
        }

        await VerifyRowAsync(row).ConfigureAwait(false);
    }

    private async Task VerifyRowAsync(FirmwareFlashRow row)
    {
        var readback = await _bootloader
            .ReadFlashAsync(row.Address, BootloaderApplicationImage.RowSize, maximumAttempts: 3, CancellationToken.None)
            .ConfigureAwait(false);
        if (!readback.AsSpan().SequenceEqual(row.Data))
        {
            throw new IOException($"Flash verification failed at row 0x{row.Address:X4}.");
        }
    }

    private static bool IsAmbiguousMutationFailure(Exception ex)
    {
        return ex is TimeoutException or IOException or GaugeProtocolException;
    }

    private static void Report(
        IProgress<FirmwareUpdateProgress>? progress,
        FirmwareUpdatePhase phase,
        int completed,
        int total,
        uint address,
        string message)
    {
        progress?.Report(new FirmwareUpdateProgress(phase, completed, total, address, message));
    }
}
