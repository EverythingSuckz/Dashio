using Dashio.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI.ViewManagement;
using AutomationProperties = Microsoft.UI.Xaml.Automation.AutomationProperties;

namespace Dashio.App.Controls;

/// <summary>
/// A small line graph of a percentage over the last minute. WinUI has no chart control, so this
/// draws one line and a faint fill with stock shapes. It is decoration: the figure beside it is
/// what assistive technology reads.
/// </summary>
public sealed partial class Sparkline : Grid
{
    private const double LineThickness = 2;

    private static readonly bool AnimationsEnabled = new UISettings().AnimationsEnabled;

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline), new PropertyMetadata(null, (d, _) => ((Sparkline)d).ApplyBrush()));

    private readonly Polygon _area = new() { Opacity = 0.16 };
    private readonly Polyline _line = new()
    {
        StrokeThickness = LineThickness,
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
    };
    private readonly TranslateTransform _slide = new();
    private readonly Grid _plot = new();

    private IReadOnlyList<(double Age, double Percent)> _points = [];
    private TimeSpan? _interval;
    private Storyboard? _sliding;

    public Sparkline()
    {
        _plot.RenderTransform = _slide;
        _plot.Children.Add(_area);
        _plot.Children.Add(_line);
        Children.Add(_plot);
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        SizeChanged += (_, e) =>
        {
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
            Draw();
        };
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    private void ApplyBrush()
    {
        _line.Stroke = Stroke;
        _area.Fill = Stroke;
    }

    /// <param name="points">How many seconds ago each reading was taken, and its value from 0 to 100, oldest first.</param>
    /// <param name="interval">
    /// The time until the next reading. When given, the graph glides left over that time so the
    /// newest reading slides in from the right edge instead of the whole line jumping.
    /// </param>
    public void Show(IReadOnlyList<(double Age, double Percent)> points, TimeSpan? interval)
    {
        _points = points;
        _interval = AnimationsEnabled ? interval : null;
        Draw();
        Slide();
    }

    /// <summary>How far the graph travels between two readings.</summary>
    private double Step => _interval is { } interval
        ? ActualWidth * interval.TotalSeconds / ResourceMonitor.HistorySpan.TotalSeconds
        : 0;

    private void Draw()
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var line = new PointCollection();
        var area = new PointCollection();
        if (width > 0 && height > LineThickness && _points.Count >= 2)
        {
            var span = ResourceMonitor.HistorySpan.TotalSeconds;
            // Half a line width of room keeps the stroke inside the box at 0% and 100%.
            var usable = height - LineThickness;
            // The newest reading starts one step beyond the right edge and glides into view.
            var step = Step;
            foreach (var (age, percent) in _points)
            {
                var x = width * (1 - age / span) + step;
                var y = LineThickness / 2 + usable * (1 - Math.Clamp(percent, 0, 100) / 100);
                line.Add(new Point(x, y));
                area.Add(new Point(x, y));
            }
            area.Add(new Point(line[^1].X, height));
            area.Add(new Point(line[0].X, height));
        }
        _line.Points = line;
        _area.Points = area;
    }

    private void Slide()
    {
        _sliding?.Stop();
        _sliding = null;
        _slide.X = 0;
        if (_interval is not { } interval || Step <= 0)
            return;

        var glide = new DoubleAnimation { From = 0, To = -Step, Duration = interval };
        Storyboard.SetTarget(glide, _slide);
        Storyboard.SetTargetProperty(glide, "X");
        _sliding = new Storyboard();
        _sliding.Children.Add(glide);
        _sliding.Begin();
    }
}
