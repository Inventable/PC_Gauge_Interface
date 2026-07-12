using Gauge.Protocol;
using Gauge.Transport;

namespace Gauge.Core;

public sealed class GaugeSession
{
    private readonly IGaugeTransport _transport;

    public GaugeSession(IGaugeTransport transport)
    {
        _transport = transport;
    }

    public async Task<GaugeFrame> IdentifyAsync(CancellationToken cancellationToken = default)
    {
        var request = GaugeFrame.Create(GaugeCommand.Identify);
        return await _transport.TransactAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

