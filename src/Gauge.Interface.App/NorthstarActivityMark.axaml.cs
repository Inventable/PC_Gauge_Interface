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
        ApplyFrame(0, isFast: false, strength: 0);
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
        if (Speed == NorthstarActivitySpeed.Fast)
        {
            ApplyFrame((_clock.Elapsed.TotalSeconds % 1.8) / 1.8, isFast: true, strength: 1);
            return;
        }

        const double rotationSeconds = 3.0;
        const double pauseSeconds = 1.0;
        var elapsed = _clock.Elapsed.TotalSeconds % (rotationSeconds + pauseSeconds);
        if (elapsed >= rotationSeconds)
        {
            ApplyFrame(0, isFast: false, strength: 0);
            return;
        }

        var rotationPosition = elapsed / rotationSeconds;
        var fadeIn = SmoothStep(Math.Min(1, rotationPosition / 0.08));
        var fadeOut = SmoothStep(Math.Min(1, (1 - rotationPosition) / 0.08));
        ApplyFrame(rotationPosition, isFast: false, strength: Math.Min(fadeIn, fadeOut));
    }

    private void ApplyFrame(double cyclePosition, bool isFast, double strength)
    {
        var pulseWidth = isFast ? 0.09 : 0.16;

        for (var index = 0; index < _segments.Length; index++)
        {
            var segmentPosition = index / (double)_segments.Length;
            var distance = Math.Abs(cyclePosition - segmentPosition);
            distance = Math.Min(distance, 1 - distance);
            var pulse = Math.Exp(-(distance * distance) / (2 * pulseWidth * pulseWidth));

            _segments[index].Opacity = isFast
                ? 0.34 + (0.66 * pulse)
                : 1 - (0.38 * pulse * strength);
            _glows[index].Opacity = isFast ? 0.2 * pulse : 0;
        }
    }

    private static double SmoothStep(double value)
    {
        return value * value * (3 - (2 * value));
    }
}
