using Microsoft.UI.Xaml;

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
}
