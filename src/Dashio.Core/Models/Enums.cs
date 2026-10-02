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
