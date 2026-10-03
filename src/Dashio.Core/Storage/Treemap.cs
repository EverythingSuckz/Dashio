namespace Dashio.Core.Storage;

/// <summary>A tile of a treemap, in the units of the area it was laid out in.</summary>
public readonly record struct Tile(double X, double Y, double Width, double Height);

/// <summary>
/// Fills a rectangle with one tile per weight, each tile's area in proportion to its weight,
/// keeping the tiles as close to square as it can (the "squarified" layout).
/// </summary>
public static class Treemap
{
    /// <param name="weights">Largest first. A weight of zero or less gets an empty tile.</param>
    public static IReadOnlyList<Tile> Layout(IReadOnlyList<double> weights, double width, double height)
    {
        var tiles = new Tile[weights.Count];
        var total = weights.Where(w => w > 0).Sum();
        if (total <= 0 || width <= 0 || height <= 0)
            return tiles;

        // Work in areas, so a weight is directly the area of its tile.
        var scale = width * height / total;
        double x = 0, y = 0, freeWidth = width, freeHeight = height;
        var index = 0;
        while (index < weights.Count)
        {
            if (weights[index] <= 0)
            {
                index++;
                continue;
            }

            // A row runs along the shorter side of what is left; tiles are added while that keeps them squarer.
            var side = Math.Min(freeWidth, freeHeight);
            var end = index;
            double rowArea = 0, worst = double.MaxValue;
            while (end < weights.Count && weights[end] > 0)
            {
                var withNext = rowArea + weights[end] * scale;
                var ratio = WorstRatio(weights, index, end, scale, withNext, side);
                if (ratio > worst)
                    break;
                worst = ratio;
                rowArea = withNext;
                end++;
            }

            var thickness = side <= 0 ? 0 : rowArea / side;
            var offset = 0.0;
            for (var i = index; i < end; i++)
            {
                var length = thickness <= 0 ? 0 : weights[i] * scale / thickness;
                tiles[i] = freeWidth >= freeHeight
                    ? new Tile(x, y + offset, thickness, length)
                    : new Tile(x + offset, y, length, thickness);
                offset += length;
            }

            if (freeWidth >= freeHeight)
            {
                x += thickness;
                freeWidth -= thickness;
            }
            else
            {
                y += thickness;
                freeHeight -= thickness;
            }
            index = end;
        }
        return tiles;
    }

    /// <summary>The least square tile of a row holding the weights from first to last.</summary>
    private static double WorstRatio(IReadOnlyList<double> weights, int first, int last, double scale, double rowArea, double side)
    {
        var thickness = rowArea / side;
        var worst = 0.0;
        for (var i = first; i <= last; i++)
        {
            var length = weights[i] * scale / thickness;
            worst = Math.Max(worst, Math.Max(length / thickness, thickness / length));
        }
        return worst;
    }
}
