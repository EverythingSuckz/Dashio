using System.Diagnostics;
using Dashio.Core.Models;
using Windows.ApplicationModel.DataTransfer;

namespace Dashio.App.Services;

/// <summary>Small actions offered from buttons and right-click menus.</summary>
public static class ItemActions
{
    public static void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    /// <summary>Opens File Explorer with the file selected.</summary>
    public static void Reveal(string? path)
    {
        if (path is null || !File.Exists(path))
            return;
        // The path comes from the registry, so it goes in as one argument rather than into a command string.
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add($"/select,{path}");
        Process.Start(start)?.Dispose();
    }

    /// <summary>Opens a folder in File Explorer.</summary>
    public static void OpenFolder(string? folder)
    {
        if (folder is null || !Directory.Exists(folder))
            return;
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add(folder);
        Process.Start(start)?.Dispose();
    }

    /// <summary>Whether any changeable item of the app is on, counting queued changes.</summary>
    public static bool AnyOn(AppGroup group) => group.Items
        .Where(i => !i.Item.IsProtected && !i.Item.IsHiddenTask)
        .Any(i => AppServices.Pending.Desired(i.Item.Id) ?? i.Item.Enabled);

    /// <summary>Queues everything of the app off, or everything back on when it is all off already.</summary>
    public static void ToggleAll(AppGroup group)
    {
        var turnOn = !AnyOn(group);
        foreach (var attributed in group.Items)
            AppServices.Pending.SetDesired(attributed.Item, group.Name, turnOn);
    }
}
