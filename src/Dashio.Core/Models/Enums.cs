namespace Dashio.Core.Models;

public enum AutostartKind
{
    Service,
    ScheduledTask,
    RunKey,
    StartupFolder,
    PackagedStartupTask,

    /// <summary>A running program. Not an autostart item: used only in the change log, for "Ended".</summary>
    Process,

    /// <summary>An installed app. Not an autostart item: used only in the change log, for "Uninstalled".</summary>
    App,

    /// <summary>A file or folder. Not an autostart item: used only in the change log, for "Deleted".</summary>
    File,
}

public enum ItemScope
{
    Machine,
    User,
}

public enum StartTrigger
{
    Boot,
    Logon,
    Schedule,
    OnDemand,
}

public enum ServiceStartType
{
    Automatic,
    AutomaticDelayed,
    Manual,
    Disabled,
}

public enum Confidence
{
    High,
    Medium,
    Low,
}

public enum AppSourceKind
{
    InstalledApp,
    StorePackage,
    DriverPackage,
}
