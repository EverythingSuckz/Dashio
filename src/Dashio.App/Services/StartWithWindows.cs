using Dashio.Core.Collectors;
using Microsoft.Win32;

namespace Dashio.App.Services;

/// <summary>
/// Dashio's own startup entry, for the person signed in. It is an ordinary entry in the Run key,
/// the same one the installer offers to add, so it shows in Dashio's Startup page and in Task
/// Manager and can be switched off from either.
/// </summary>
public static class StartWithWindows
{
    private const string ValueName = "Dashio";
    private const string ApprovedKey = RunKeyLocation.ApprovedPath + @"\Run";

    /// <summary>The entry is there and has not been switched off, by Dashio or by Task Manager.</summary>
    public static bool IsOn
    {
        get
        {
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKeyLocation.RunPath);
                if (run?.GetValue(ValueName) is not string)
                    return false;
                using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
                // Windows marks a switched-off entry with an odd first byte.
                return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } state || state[0] % 2 == 0;
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
    }

    /// <returns>False when the registry could not be changed.</returns>
    public static bool Set(bool on)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKeyLocation.RunPath, writable: true);
            if (on && Environment.ProcessPath is { } path)
                run.SetValue(ValueName, $"\"{path}\"");
            else
                run.DeleteValue(ValueName, throwOnMissingValue: false);

            // Whatever was recorded about the old entry no longer applies.
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
