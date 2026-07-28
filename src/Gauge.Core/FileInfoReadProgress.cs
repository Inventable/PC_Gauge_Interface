namespace Gauge.Core;

public sealed record FileInfoReadProgress(
    double Percent,
    string Stage);

internal sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}
