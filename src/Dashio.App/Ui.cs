using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Dashio.App;

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
