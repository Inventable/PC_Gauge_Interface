using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace Gauge.Interface.App;

public enum NorthstarActivitySpeed
{
    Slow,
    Fast,
}

public sealed partial class NorthstarActivityMark : UserControl
{
    public static readonly StyledProperty<NorthstarActivitySpeed> SpeedProperty =
        AvaloniaProperty.Register<NorthstarActivityMark, NorthstarActivitySpeed>(
            nameof(Speed),
            NorthstarActivitySpeed.Slow);

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

    public NorthstarActivitySpeed Speed
    {
        get => GetValue(SpeedProperty);
        set => SetValue(SpeedProperty, value);
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
        var cycleSeconds = Speed == NorthstarActivitySpeed.Fast ? 1.8 : 4.0;
        ApplyFrame((_clock.Elapsed.TotalSeconds % cycleSeconds) / cycleSeconds);
    }

    private void ApplyFrame(double cyclePosition)
    {
        var isFast = Speed == NorthstarActivitySpeed.Fast;
        var pulseWidth = isFast ? 0.09 : 0.18;
        var restingOpacity = isFast ? 0.34 : 0.58;
        var pulseOpacity = isFast ? 0.66 : 0.30;
        var glowOpacity = isFast ? 0.2 : 0.07;

        for (var index = 0; index < _segments.Length; index++)
        {
            var segmentPosition = index / (double)_segments.Length;
            var distance = Math.Abs(cyclePosition - segmentPosition);
            distance = Math.Min(distance, 1 - distance);
            var pulse = Math.Exp(-(distance * distance) / (2 * pulseWidth * pulseWidth));

            _segments[index].Opacity = restingOpacity + (pulseOpacity * pulse);
            _glows[index].Opacity = glowOpacity * pulse;
        }
    }
}
