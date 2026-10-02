using System.Runtime.InteropServices;
using System.Text;
using Dashio.Core.Models;

namespace Dashio.Core.Collectors;

/// <summary>Reads one kind of autostart item. A bad entry is skipped, never thrown.</summary>
public interface IItemCollector
{
    string Name { get; }
    IReadOnlyList<AutostartItem> Collect();
}

internal static class NativeStrings
{
    /// <summary>Resolves indirect strings such as <c>@%SystemRoot%\system32\foo.dll,-100</c>.</summary>
    public static string? Resolve(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = value.Trim();
        // Task Scheduler wraps the same syntax in $( ).
        if (text.StartsWith("$(", StringComparison.Ordinal) && text.EndsWith(')'))
            text = text[2..^1];
        if (text.Length == 0 || text[0] != '@')
            return text;

        var buffer = new StringBuilder(1024);
        return SHLoadIndirectString(text, buffer, (uint)buffer.Capacity, IntPtr.Zero) == 0 && buffer.Length > 0
            ? buffer.ToString()
            : null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, uint cchOutBuf, IntPtr ppvReserved);
}
