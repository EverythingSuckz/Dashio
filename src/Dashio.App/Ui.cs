using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Dashio.App;

/// <summary>A page with a filter box of its own, which Ctrl+F goes to.</summary>
public interface IFilterPage
{
    void FocusFilter();
}

/// <summary>Small functions for <c>x:Bind</c>, used instead of value converters.</summary>
public static class Ui
{
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility VisibleIfNull(object? value) => value is null ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility VisibleIfText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
    public static bool Not(bool value) => !value;

    /// <summary>Pages are centred and never wider than this.</summary>
    public const double ShellMaxWidth = 1480;

    /// <summary>
    /// Sets a page's content to the page width, capped at <see cref="ShellMaxWidth"/>.
    /// The width is set outright because a MaxWidth alone lets it follow the content, which makes
    /// the whole page jump sideways when something expands.
    /// </summary>
    public static void FitToPage(FrameworkElement content, double pageWidth) =>
        content.Width = Math.Min(pageWidth, ShellMaxWidth);

    /// <summary>
    /// Puts a page's headline figures beside the title when there is room, under it otherwise,
    /// and stacks them when even a row does not fit.
    /// </summary>
    public static void PlaceStats(StackPanel stats, double pageWidth, double besideTitleFrom, double inARowFrom)
    {
        var beside = pageWidth >= besideTitleFrom;
        Grid.SetRow(stats, beside ? 0 : 1);
        Grid.SetColumn(stats, beside ? 1 : 0);
        stats.Margin = beside ? new Thickness(24, 0, 0, 0) : new Thickness(0, 16, 0, 0);

        var inARow = pageWidth >= inARowFrom;
        stats.Orientation = inARow ? Orientation.Horizontal : Orientation.Vertical;
        stats.Spacing = inARow ? 32 : 12;
    }

    public static GridLength ColumnWidth(bool shown, double width) => new(shown ? width : 0);

    /// <summary>Lays a grid's children out side by side in equal columns, or stacked when there is no room.</summary>
    public static void Flow(Grid grid, bool sideBySide)
    {
        var count = grid.Children.Count;
        if (grid.Tag is bool current && current == sideBySide && grid.ColumnDefinitions.Count > 0)
            return;
        grid.Tag = sideBySide;

        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        for (var i = 0; i < (sideBySide ? count : 1); i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < (sideBySide ? 1 : count); i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var i = 0; i < count; i++)
        {
            var child = (FrameworkElement)grid.Children[i];
            Grid.SetColumn(child, sideBySide ? i : 0);
            Grid.SetRow(child, sideBySide ? 0 : i);
        }
    }
}
