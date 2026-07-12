using Gauge.Protocol;

namespace Gauge.Transport;

public interface IGaugeTransport : IAsyncDisposable
{
    string Name { get; }

    Task OpenAsync(CancellationToken cancellationToken = default);

    Task CloseAsync(CancellationToken cancellationToken = default);

    Task<GaugeFrame> TransactAsync(GaugeFrame request, CancellationToken cancellationToken = default);
}

