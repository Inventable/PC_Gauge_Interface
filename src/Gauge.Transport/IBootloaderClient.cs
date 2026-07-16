namespace Gauge.Transport;

public interface IBootloaderClient
{
    Task<byte[]> ReadFlashAsync(
        uint address,
        ushort length,
        int maximumAttempts = 3,
        CancellationToken cancellationToken = default);

    Task EraseFlashRowsOnceAsync(
        uint address,
        ushort rowCount,
        CancellationToken cancellationToken = default);

    Task WriteFlashOnceAsync(
        uint address,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default);
}
