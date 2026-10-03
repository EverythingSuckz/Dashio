using System.Diagnostics;
using Dashio.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Dashio.App.Controls;

/// <summary>
/// Lays the items of an <see cref="ItemsRepeater"/> out as a treemap: each item gets a tile
/// whose area is in proportion to its weight. There is no stock layout that does this.
/// When the weights change the tiles glide to their new places instead of jumping.
/// </summary>
public sealed partial class TreemapLayout : NonVirtualizingLayout
{
    /// <summary>The space left between neighbouring tiles.</summary>
    private const double Gap = 4;

    private static readonly TimeSpan GlideTime = TimeSpan.FromMilliseconds(380);

    /// <summary>Where a tile is coming from and where it is going.</summary>
    private sealed class Glide
    {
        public Rect From;
        public Rect To;
    }

    private readonly Dictionary<UIElement, Glide> _glides = [];
    private readonly Stopwatch _clock = new();
    private IReadOnlyList<double> _weights = [];
    private bool _isGliding;

    /// <summary>One weight per item, in the order of the items. They need not be sorted.</summary>
    public IReadOnlyList<double> Weights
    {
        get => _weights;
        set
        {
            _weights = value;
            InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(NonVirtualizingLayoutContext context, Size availableSize)
    {
        // The map fills whatever it is given; it has no size of its own.
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        var children = context.Children;

        // The layout wants the largest first; the items stay in their own order.
        var order = Enumerable.Range(0, children.Count)
            .OrderByDescending(i => i < _weights.Count ? _weights[i] : 0)
            .ToList();
        var tiles = Treemap.Layout(order.Select(i => i < _weights.Count ? _weights[i] : 0).ToList(), width, height);

        var targets = new Rect[children.Count];
        for (var place = 0; place < order.Count; place++)
        {
            var tile = tiles[place];
            targets[order[place]] = new Rect(
                tile.X + Gap / 2, tile.Y + Gap / 2,
                Math.Max(0, tile.Width - Gap), Math.Max(0, tile.Height - Gap));
        }

        var moved = false;
        for (var i = 0; i < children.Count; i++)
        {
            if (!_glides.TryGetValue(children[i], out var glide) || glide.To != targets[i])
                moved = true;
        }

        if (moved)
        {
            // Every tile sets off from where it is showing right now, so a change in mid-glide stays smooth.
            var progress = Progress();
            var next = new Dictionary<UIElement, Glide>(children.Count);
            for (var i = 0; i < children.Count; i++)
            {
                var from = _glides.TryGetValue(children[i], out var glide) ? Between(glide, progress) : targets[i];
                next[children[i]] = new Glide { From = from, To = targets[i] };
            }
            _glides.Clear();
            foreach (var (element, glide) in next)
                _glides[element] = glide;

            _clock.Restart();
            if (!_isGliding)
            {
                _isGliding = true;
                CompositionTarget.Rendering += OnFrame;
            }
        }

        for (var i = 0; i < children.Count; i++)
            children[i].Measure(new Size(targets[i].Width, targets[i].Height));
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(NonVirtualizingLayoutContext context, Size finalSize)
    {
        var progress = Progress();
        foreach (var child in context.Children)
        {
            if (_glides.TryGetValue(child, out var glide))
                child.Arrange(Between(glide, progress));
        }
        return finalSize;
    }

    private void OnFrame(object? sender, object e)
    {
        if (_clock.Elapsed >= GlideTime)
        {
            _isGliding = false;
            CompositionTarget.Rendering -= OnFrame;
        }
        InvalidateArrange();
    }

    /// <summary>How far the glide has got, 0 to 1, slowing as it arrives.</summary>
    private double Progress()
    {
        if (!_isGliding)
            return 1;
        var t = Math.Min(1, _clock.Elapsed / GlideTime);
        return 1 - Math.Pow(1 - t, 3);
    }

    private static Rect Between(Glide glide, double progress) => new(
        glide.From.X + (glide.To.X - glide.From.X) * progress,
        glide.From.Y + (glide.To.Y - glide.From.Y) * progress,
        Math.Max(0, glide.From.Width + (glide.To.Width - glide.From.Width) * progress),
        Math.Max(0, glide.From.Height + (glide.To.Height - glide.From.Height) * progress));
}
