namespace Dashio.Core.Models;

/// <summary>Something Windows already calls an app.</summary>
public sealed record AppSource
{
    public required AppSourceKind Kind { get; init; }

    /// <summary><c>app:…</c>, <c>pkg:…</c> or <c>drv:…</c>.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Publisher { get; init; }
    public string? InstallLocation { get; init; }
    public string? IconPath { get; init; }
    public string? PackageFamilyName { get; init; }

    /// <summary>Shipped as part of Windows.</summary>
    public bool IsSystem { get; init; }

    /// <summary>
    /// A part of another product that Windows leaves out of its Installed apps list, such as one
    /// of the dozens of pieces an SDK registers.
    /// </summary>
    public bool IsHiddenComponent { get; init; }

    /// <summary>The size Windows recorded at install time, when it recorded one. Often rough.</summary>
    public long? EstimatedBytes { get; init; }

    public DateTimeOffset? InstalledOn { get; init; }

    /// <summary>For a Store package: the apps in it that can be opened, as <c>family!AppId</c>.</summary>
    public IReadOnlyList<string> AppIds { get; init; } = [];

    /// <summary>For a Store package: the program files those apps run.</summary>
    public IReadOnlyList<string> AppPrograms { get; init; } = [];
}

/// <summary>An item together with why it was placed in its group.</summary>
public sealed record AttributedItem(AutostartItem Item, string Rule, string Reason, Confidence Confidence);

/// <summary>A card in the UI: one app and everything it starts.</summary>
public sealed class AppGroup
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Publisher { get; init; }
    public required IReadOnlyList<AttributedItem> Items { get; init; }
    public IReadOnlyList<AppSource> Sources { get; init; } = [];
    public Confidence Confidence { get; init; }
    public string? IconPath { get; init; }

    /// <summary>Every item is a Windows component.</summary>
    public bool IsWindows { get; init; }

    /// <summary>The catch-all group for items nothing could place.</summary>
    public bool IsUnmatched { get; init; }

    /// <summary>"Other <i>vendor</i> components": the vendor is known, the product is not.</summary>
    public bool IsVendorBucket { get; init; }

    public int RunningCount => Items.Count(i => i.Item.IsRunning == true);
    public int StartsWithWindowsCount => Items.Count(i => i.Item.StartsWithWindows);

    /// <summary>At least one item starts with Windows and is not listed in Task Manager's Startup tab.</summary>
    public bool HiddenFromTaskManager => Items.Any(i => i.Item.StartsWithWindows && !i.Item.ShownInTaskManager);
}

public sealed record CollectorError(string Collector, string Message);

/// <summary>Everything one scan found.</summary>
public sealed record Snapshot(
    IReadOnlyList<AutostartItem> Items,
    IReadOnlyList<AppSource> Sources,
    IReadOnlyList<CollectorError> Errors,
    DateTimeOffset TakenAt);
