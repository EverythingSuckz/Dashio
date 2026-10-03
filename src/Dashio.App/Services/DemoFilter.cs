namespace Dashio.App.Services;

/// <summary>
/// Leaves named apps out of every list, for taking screenshots on a PC whose own apps should
/// not be in them. Set <c>DASHIO_HIDE_APPS</c> to parts of names, separated by semicolons.
/// Nothing is hidden when the variable is not set.
/// </summary>
public static class DemoFilter
{
    public const string Variable = "DASHIO_HIDE_APPS";

    private static readonly string[] Words =
        (Environment.GetEnvironmentVariable(Variable) ?? "")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool IsHidden(string? name) =>
        Words.Length > 0 && name is not null && Words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
}
