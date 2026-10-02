using System.Text.Json.Serialization;

namespace Dashio.Core.Models;

/// <summary>Facts about the file an item runs.</summary>
public sealed record FileEvidence
{
    public string? Path { get; init; }
    public bool Exists { get; init; }
    public string? Company { get; init; }
    public string? Product { get; init; }
    public string? Description { get; init; }
    public string? Signer { get; init; }

    /// <summary>The signer is shared by many vendors (hardware-compatibility signing) and says nothing about the maker.</summary>
    public bool SignerIsGeneric { get; init; }

    /// <summary>Signed with one of the certificates Windows uses for its own files.</summary>
    public bool IsWindowsComponent { get; init; }
}

/// <summary>One thing that can start an app without the user launching it.</summary>
public sealed record AutostartItem
{
    /// <summary>Stable key: <c>{kind}:{scope}:{name}</c>.</summary>
    public required string Id { get; init; }
    public required AutostartKind Kind { get; init; }
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }

    /// <summary>The raw command line as stored by Windows.</summary>
    public string? Command { get; init; }

    /// <summary>The executable, DLL or script the command resolves to.</summary>
    public string? TargetPath { get; init; }

    /// <summary>Set when the target runs through a host such as rundll32 or cmd.</summary>
    public string? HostPath { get; init; }

    public ItemScope Scope { get; init; }

    /// <summary>Whether the item is allowed to start by itself.</summary>
    public bool Enabled { get; init; }
    public StartTrigger Trigger { get; init; }

    /// <summary>Services only; null for other kinds.</summary>
    public bool? IsRunning { get; init; }

    public bool RequiresAdmin { get; init; }

    /// <summary>Windows components are read-only.</summary>
    public bool IsProtected { get; init; }

    public FileEvidence? Evidence { get; init; }

    public ServiceStartType? ServiceStartType { get; init; }

    /// <summary>Registry key, folder or task folder the item lives in.</summary>
    public string? Location { get; init; }

    public string? PackageFamilyName { get; init; }

    /// <summary>Found only by the admin scan.</summary>
    public bool VisibleOnlyWithAdmin { get; init; }

    /// <summary>Present in the task registry but not returned by the Task Scheduler API.</summary>
    public bool IsHiddenTask { get; init; }

    [JsonIgnore]
    public bool ShownInTaskManager =>
        Kind is AutostartKind.RunKey or AutostartKind.StartupFolder or AutostartKind.PackagedStartupTask;

    [JsonIgnore]
    public bool StartsWithWindows => Enabled && Trigger is StartTrigger.Boot or StartTrigger.Logon;
}
