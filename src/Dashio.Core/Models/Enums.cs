namespace Dashio.Core.Models;

public enum AutostartKind
{
    Service,
    ScheduledTask,
    RunKey,
    StartupFolder,
    PackagedStartupTask,
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
