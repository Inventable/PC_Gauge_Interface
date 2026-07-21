using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace Gauge.Interface.App;

public sealed partial class NorthstarActivityMark : UserControl
{
    private static readonly TimeSpan CycleDuration = TimeSpan.FromSeconds(1.8);
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _timer;
    private readonly ShapePath[] _segments;
    private readonly ShapePath[] _glows;

    public NorthstarActivityMark()
    {
        InitializeComponent();
        _segments = FindPaths("Segment");
        _glows = FindPaths("Glow");
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += Animate;

        AttachedToVisualTree += (_, _) => Start();
        DetachedFromVisualTree += (_, _) => Stop();
        ApplyFrame(0);
    }

    private ShapePath[] FindPaths(string prefix)
    {
        return Enumerable.Range(0, 6)
            .Select(index => this.FindControl<ShapePath>($"{prefix}{index}")!)
            .ToArray();
    }

    private void Start()
    {
        _clock.Restart();
        _timer.Start();
    }

    private void Stop()
    {
        _timer.Stop();
        _clock.Stop();
    }

    private void Animate(object? sender, EventArgs e)
    {
        ApplyFrame((_clock.Elapsed.TotalSeconds % CycleDuration.TotalSeconds) / CycleDuration.TotalSeconds);
    }

    private void ApplyFrame(double cyclePosition)
    {
        const double pulseWidth = 0.09;
        for (var index = 0; index < _segments.Length; index++)
        {
            var segmentPosition = index / (double)_segments.Length;
            var distance = Math.Abs(cyclePosition - segmentPosition);
            distance = Math.Min(distance, 1 - distance);
            var pulse = Math.Exp(-(distance * distance) / (2 * pulseWidth * pulseWidth));

            _segments[index].Opacity = 0.34 + (0.66 * pulse);
            _glows[index].Opacity = 0.2 * pulse;
        }
    }
}
