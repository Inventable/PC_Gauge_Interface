using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Collections.Specialized;

namespace Gauge.Interface.App;

public sealed class GaugeTrendChart : Control
{
    public static readonly StyledProperty<IEnumerable<ChartSampleViewModel>?> SamplesProperty =
        AvaloniaProperty.Register<GaugeTrendChart, IEnumerable<ChartSampleViewModel>?>(nameof(Samples));

    private INotifyCollectionChanged? _observableSamples;

    public IEnumerable<ChartSampleViewModel>? Samples
    {
        get => GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    static GaugeTrendChart()
    {
        SamplesProperty.Changed.AddClassHandler<GaugeTrendChart>((chart, args) => chart.OnSamplesChanged(args));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        var plot = new Rect(42, 18, Math.Max(0, bounds.Width - 62), Math.Max(0, bounds.Height - 44));
        var axisPen = new Pen(new SolidColorBrush(Color.Parse("#CDD6DC")), 1);
        var gridPen = new Pen(new SolidColorBrush(Color.Parse("#E4EAEE")), 1);
        var pressurePen = new Pen(new SolidColorBrush(Color.Parse("#CE0E2D")), 2);
        var temperaturePen = new Pen(new SolidColorBrush(Color.Parse("#2DA55D")), 2);
        var textBrush = new SolidColorBrush(Color.Parse("#5D5D66"));

        context.FillRectangle(new SolidColorBrush(Colors.White), bounds);

        for (var i = 0; i <= 4; i++)
        {
            var y = plot.Top + (plot.Height * i / 4);
            context.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        context.DrawRectangle(null, axisPen, plot);
        DrawLabel(context, "Pressure", new Point(plot.Left, 0), new SolidColorBrush(Color.Parse("#CE0E2D")));
        DrawLabel(context, "Temperature", new Point(plot.Left + 92, 0), new SolidColorBrush(Color.Parse("#2DA55D")));

        var samples = Samples?.ToArray() ?? [];
        if (samples.Length < 2 || plot.Width <= 0 || plot.Height <= 0)
        {
            DrawLabel(context, "No downloaded data", new Point(plot.Left + 12, plot.Top + 12), textBrush);
            return;
        }

        DrawSeries(context, samples, plot, pressurePen, sample => sample.Pressure);
        DrawSeries(context, samples, plot, temperaturePen, sample => sample.Temperature);
    }

    private void OnSamplesChanged(AvaloniaPropertyChangedEventArgs args)
    {
        if (_observableSamples is not null)
        {
            _observableSamples.CollectionChanged -= OnSampleCollectionChanged;
        }

        _observableSamples = args.NewValue as INotifyCollectionChanged;
        if (_observableSamples is not null)
        {
            _observableSamples.CollectionChanged += OnSampleCollectionChanged;
        }

        InvalidateVisual();
    }

    private void OnSampleCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateVisual();
    }

    private static void DrawSeries(
        DrawingContext context,
        IReadOnlyList<ChartSampleViewModel> samples,
        Rect plot,
        Pen pen,
        Func<ChartSampleViewModel, double> selector)
    {
        var min = samples.Min(selector);
        var max = samples.Max(selector);
        if (Math.Abs(max - min) < 0.000001)
        {
            max = min + 1;
        }

        Point? previous = null;
        for (var index = 0; index < samples.Count; index++)
        {
            var x = plot.Left + (plot.Width * index / Math.Max(1, samples.Count - 1));
            var normalized = (selector(samples[index]) - min) / (max - min);
            var y = plot.Bottom - (plot.Height * normalized);
            var point = new Point(x, y);
            if (previous is not null)
            {
                context.DrawLine(pen, previous.Value, point);
            }

            previous = point;
        }
    }

    private static void DrawLabel(DrawingContext context, string text, Point origin, IBrush brush)
    {
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            12,
            brush);
        context.DrawText(formatted, origin);
    }
}
