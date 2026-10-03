using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Dashio.App.Services;

/// <summary>
/// The question asked before something happens: who it is about, then what will happen, one
/// point per line with an icon.
/// </summary>
public static class ConfirmDialog
{
    public const string CancelGlyph = "\uE711";

    /// <summary>One point of the confirmation: an icon, what happens, and the detail under it.</summary>
    public sealed record Point(string Glyph, string Title, string Detail, bool IsWarning = false);

    /// <param name="extra">Shown under the points, for a choice the user has to make first.</param>
    /// <param name="headerGlyph">Stands in when there is no file to take an icon from.</param>
    /// <param name="primaryIsDefault">False when Cancel is the safe answer, so that Enter picks it.</param>
    public static async Task<bool> ShowAsync(
        FrameworkElement anchor, string title, string summary, string? iconPath, IReadOnlyList<Point> points,
        string primaryText, string primaryGlyph, string footer = "", UIElement? extra = null,
        string headerGlyph = "\uECAA", bool primaryIsDefault = false)
    {
        var resources = Application.Current.Resources;
        var secondary = (Brush)resources["TextFillColorSecondaryBrush"];
        var caption = (Style)resources["CaptionTextBlockStyle"];

        // Who it is about: the icon, the name and a line of detail.
        var header = new Grid { ColumnSpacing = 16 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = await AppServices.Icons.GetAsync(iconPath);
        header.Children.Add(icon is not null
            ? new Image { Source = icon, Width = 40, Height = 40, VerticalAlignment = VerticalAlignment.Center }
            : new FontIcon { Glyph = headerGlyph, FontSize = 32, Foreground = secondary, Width = 40, Height = 40 });
        var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(new TextBlock
        {
            Text = title, Style = (Style)resources["SubtitleTextBlockStyle"], TextWrapping = TextWrapping.Wrap,
        });
        if (summary.Length > 0)
            heading.Children.Add(new TextBlock { Text = summary, Style = caption, Foreground = secondary, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(heading, 1);
        header.Children.Add(heading);

        var list = new StackPanel { Spacing = 16 };
        foreach (var point in points)
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
            text.Children.Add(new TextBlock { Text = point.Title, Style = (Style)resources["BodyStrongTextBlockStyle"], TextWrapping = TextWrapping.Wrap });
            text.Children.Add(new TextBlock { Text = point.Detail, Style = caption, Foreground = secondary, TextWrapping = TextWrapping.Wrap });
            list.Children.Add(Row(point.Glyph, text, point.IsWarning));
        }
        if (extra is not null)
            list.Children.Add(extra);

        var content = new StackPanel { Spacing = 20, Width = 420 };
        content.Children.Add(header);
        content.Children.Add(new Border { Height = 1, Background = (Brush)resources["DividerStrokeColorDefaultBrush"] });
        content.Children.Add(list);
        if (footer.Length > 0)
            content.Children.Add(new TextBlock { Text = footer, TextWrapping = TextWrapping.Wrap, Style = caption, Foreground = secondary });

        var dialog = new ContentDialog
        {
            XamlRoot = anchor.XamlRoot,
            RequestedTheme = (anchor.XamlRoot.Content as FrameworkElement)?.RequestedTheme ?? ElementTheme.Default,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            DefaultButton = primaryIsDefault ? ContentDialogButton.Primary : ContentDialogButton.Close,
        };
        AutomationProperties.SetName(dialog, title);
        WithIcons(dialog, primaryGlyph);
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>A line of the dialog: an icon in a tile, then whatever goes beside it.</summary>
    public static Grid Row(string glyph, FrameworkElement beside, bool isWarning = false)
    {
        var resources = Application.Current.Resources;
        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new Border
        {
            Style = (Style)resources["RowIconTileStyle"],
            VerticalAlignment = VerticalAlignment.Top,
            Child = new FontIcon
            {
                Glyph = glyph,
                FontSize = 18,
                Foreground = (Brush)resources[isWarning ? "SystemFillColorCautionBrush" : "TextFillColorPrimaryBrush"],
            },
        });
        Grid.SetColumn(beside, 1);
        row.Children.Add(beside);
        return row;
    }

    /// <summary>A dialog's buttons take text only, so once it is open each gets its icon in front of the text.</summary>
    public static void WithIcons(ContentDialog dialog, string primaryGlyph, string closeGlyph = CancelGlyph)
    {
        void Set()
        {
            SetIcon(dialog, "PrimaryButton", primaryGlyph, dialog.PrimaryButtonText);
            SetIcon(dialog, "CloseButton", closeGlyph, dialog.CloseButtonText);
        }
        // Loaded comes before the dialog has faded in, so the icons are there from the start.
        dialog.Loaded += (_, _) => Set();
        dialog.Opened += (_, _) => Set();
    }

    private static void SetIcon(DependencyObject root, string buttonName, string glyph, string text)
    {
        if (glyph.Length == 0 || text.Length == 0 || Find(root, buttonName) is not { Content: string } button)
            return;

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        content.Children.Add(new TextBlock { Text = text });
        button.Content = content;
        AutomationProperties.SetName(button, text);
    }

    private static Button? Find(DependencyObject parent, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button button && button.Name == name)
                return button;
            if (Find(child, name) is { } found)
                return found;
        }
        return null;
    }
}
