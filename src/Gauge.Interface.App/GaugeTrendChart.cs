using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ScottPlot.Avalonia;
using ScottPlot.Interactivity;
using ScottPlot.Interactivity.UserActionResponses;

namespace Gauge.Interface.App;

public sealed class GaugeTrendChart : UserControl
{
    private static readonly ScottPlot.Color PressureColor = ScottPlot.Color.FromHex("#CE0E2D");
    private static readonly ScottPlot.Color TemperatureColor = ScottPlot.Color.FromHex("#168A57");
    private static readonly ScottPlot.Color TextColor = ScottPlot.Color.FromHex("#414149");
    private static readonly ScottPlot.Color GridColor = ScottPlot.Color.FromHex("#E2E7EA");
    private readonly AvaPlot _plot = new();
    private bool _followData = true;
    private ScottPlot.Plottables.VerticalLine? _cursorLine;
    private int _cursorIndex = -1;

    public static readonly StyledProperty<ChartDataSet?> DataProperty =
        AvaloniaProperty.Register<GaugeTrendChart, ChartDataSet?>(nameof(Data));

    public GaugeTrendChart()
    {
        Content = _plot;
        ClipToBounds = true;
        ConfigurePlot();
        _plot.PointerMoved += Plot_PointerMoved;
    }

    public event EventHandler<ChartCursorEventArgs>? CursorChanged;

    public ChartDataSet? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public bool IsZoomWindowMode { get; private set; }

    static GaugeTrendChart()
    {
        DataProperty.Changed.AddClassHandler<GaugeTrendChart>((chart, _) => chart.UpdatePlot());
    }

    public void Fit()
    {
        _followData = true;
        FitPlot();
    }

    public void ResetCursor()
    {
        _cursorIndex = -1;
        if (_cursorLine is not null)
        {
            _plot.Plot.Remove(_cursorLine);
            _cursorLine = null;
            _plot.Refresh();
        }
    }

    private void FitPlot()
    {
        _plot.Plot.Axes.Margins(horizontal: 0.02, vertical: 0.08);
        _plot.Plot.Axes.AutoScale();
        _plot.Refresh();
    }

    public void ZoomIn()
    {
        _followData = false;
        _plot.Plot.Axes.Zoom(1.25, 1.25);
        _plot.Refresh();
    }

    public void ZoomOut()
    {
        _followData = false;
        _plot.Plot.Axes.ZoomOut(1.25, 1.25);
        _plot.Refresh();
    }

    public void SetZoomWindowMode(bool enabled)
    {
        IsZoomWindowMode = enabled;
        _plot.UserInputProcessor.Reset();

        if (enabled)
        {
            _followData = false;
            _plot.UserInputProcessor.UserActionResponses.Clear();
            _plot.UserInputProcessor.UserActionResponses.Add(new MouseDragZoomRectangle(StandardMouseButtons.Left));
        }

        _plot.Refresh();
    }

    private void ConfigurePlot()
    {
        var plot = _plot.Plot;
        plot.FigureBackground.Color = ScottPlot.Colors.White;
        plot.DataBackground.Color = ScottPlot.Colors.White;
        plot.Axes.Color(TextColor);
        plot.Legend.IsVisible = false;

        plot.Axes.Left.Label.Text = "Pressure (psi)";
        plot.Axes.Left.Label.ForeColor = PressureColor;
        plot.Axes.Left.TickLabelStyle.ForeColor = PressureColor;
        plot.Axes.Left.MajorTickStyle.Color = PressureColor;

        plot.Axes.Right.IsVisible = true;
        plot.Axes.Right.Label.Text = "Temperature (C)";
        plot.Axes.Right.Label.ForeColor = TemperatureColor;
        plot.Axes.Right.TickLabelStyle.ForeColor = TemperatureColor;
        plot.Axes.Right.MajorTickStyle.Color = TemperatureColor;

        plot.Axes.Bottom.Label.Text = "Elapsed time";
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
        {
            LabelFormatter = FormatElapsedTime,
            MinimumTickSpacing = 72
        };

        // A single quiet grid follows the pressure and elapsed-time axes.
        plot.Grid.MajorLineColor = GridColor;
        plot.Grid.MinorLineWidth = 0;
        plot.Grid.XAxisStyle.IsVisible = true;
        plot.Grid.YAxisStyle.IsVisible = true;
    }

    private void UpdatePlot()
    {
        var plot = _plot.Plot;
        plot.Clear();
        _cursorLine = null;
        var data = Data;

        if (data is null || data.Count < 2)
        {
            _cursorIndex = -1;
            _plot.Refresh();
            return;
        }

        var pressure = plot.Add.SignalXY(data.ElapsedSeconds, data.Pressure);
        pressure.Color = PressureColor;
        pressure.LineWidth = 2;
        pressure.MarkerStyle.Size = 0;
        pressure.Axes.XAxis = plot.Axes.Bottom;
        pressure.Axes.YAxis = plot.Axes.Left;

        var temperature = plot.Add.SignalXY(data.ElapsedSeconds, data.Temperature);
        temperature.Color = TemperatureColor;
        temperature.LineWidth = 2;
        temperature.LinePattern = ScottPlot.LinePattern.Dashed;
        temperature.MarkerStyle.Size = 0;
        temperature.Axes.XAxis = plot.Axes.Bottom;
        temperature.Axes.YAxis = plot.Axes.Right;

        if (_cursorIndex >= data.Count)
        {
            _cursorIndex = data.Count - 1;
        }

        if (_cursorIndex >= 0)
        {
            AddCursorLine(data.ElapsedSeconds[_cursorIndex]);
        }

        if (_followData)
        {
            FitPlot();
        }
        else
        {
            _plot.Refresh();
        }
    }

    private void Plot_PointerMoved(object? sender, PointerEventArgs e)
    {
        var data = Data;
        if (IsZoomWindowMode || data is null || data.Count < 2)
        {
            return;
        }

        var position = e.GetPosition(_plot);
        var coordinates = _plot.Plot.GetCoordinates(
            (float)position.X,
            (float)position.Y,
            _plot.Plot.Axes.Bottom,
            _plot.Plot.Axes.Left);
        var index = FindNearestIndex(data.ElapsedSeconds, coordinates.X);
        if (index == _cursorIndex)
        {
            return;
        }

        _cursorIndex = index;
        if (_cursorLine is not null)
        {
            _plot.Plot.Remove(_cursorLine);
        }

        AddCursorLine(data.ElapsedSeconds[index]);
        _plot.Refresh();
        CursorChanged?.Invoke(this, new ChartCursorEventArgs(
            index,
            data.ElapsedSeconds[index],
            data.Pressure[index],
            data.Temperature[index]));
    }

    private void AddCursorLine(double elapsedSeconds)
    {
        _cursorLine = _plot.Plot.Add.VerticalLine(elapsedSeconds);
        _cursorLine.Color = TextColor.WithAlpha(0.55);
        _cursorLine.LineWidth = 1;
        _cursorLine.LinePattern = ScottPlot.LinePattern.Dotted;
        _cursorLine.EnableAutoscale = false;
    }

    private static int FindNearestIndex(double[] values, double target)
    {
        var index = Array.BinarySearch(values, target);
        if (index >= 0)
        {
            return index;
        }

        var next = ~index;
        if (next <= 0)
        {
            return 0;
        }

        if (next >= values.Length)
        {
            return values.Length - 1;
        }

        return target - values[next - 1] <= values[next] - target
            ? next - 1
            : next;
    }

    private static string FormatElapsedTime(double value)
    {
        var sign = value < 0 ? "-" : string.Empty;
        var elapsed = TimeSpan.FromSeconds(Math.Abs(value));

        if (elapsed.TotalDays >= 1)
        {
            return $"{sign}{(int)elapsed.TotalDays}d {elapsed.Hours:00}h";
        }

        if (elapsed.TotalHours >= 1)
        {
            return $"{sign}{(int)elapsed.TotalHours}h {elapsed.Minutes:00}m";
        }

        if (elapsed.TotalMinutes >= 1)
        {
            return $"{sign}{(int)elapsed.TotalMinutes}m {elapsed.Seconds:00}s";
        }

        return $"{sign}{elapsed.TotalSeconds:F0}s";
    }
}

public sealed record ChartCursorEventArgs(
    int SampleIndex,
    double ElapsedSeconds,
    double Pressure,
    double Temperature);
