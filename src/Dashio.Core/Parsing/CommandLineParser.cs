using System.Text;
using System.Text.RegularExpressions;

namespace Dashio.Core.Parsing;

/// <param name="TargetPath">The file that actually runs; null when it cannot be determined.</param>
/// <param name="HostPath">The host program (cmd, rundll32, svchost…) when the target runs through one.</param>
public sealed record ResolvedCommand(string? TargetPath, string? HostPath, string? Arguments)
{
    /// <summary>
    /// The target, or the host itself when the host cannot run arbitrary inline code
    /// (svchost, dllhost, msiexec…). Script hosts such as cmd and powershell never qualify,
    /// so an inline script is not mistaken for the Windows program that runs it.
    /// </summary>
    public string? TargetOrPassiveHost =>
        TargetPath ?? (HostPath is not null && CommandLineParser.IsPassiveHost(HostPath) ? HostPath : null);
}

/// <summary>Turns a stored command line into the file it runs.</summary>
public static partial class CommandLineParser
{
    private static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "rundll32", "regsvr32", "wscript", "cscript", "mshta",
        "msiexec", "conhost", "explorer", "svchost", "dllhost",
    };

    private static readonly HashSet<string> PassiveHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost", "dllhost", "msiexec", "explorer", "conhost",
    };

    public static bool IsPassiveHost(string hostPath) =>
        PassiveHosts.Contains(Path.GetFileNameWithoutExtension(hostPath));

    private const string Extensions =
        "exe|dll|sys|com|bat|cmd|scr|vbs|vbe|js|jse|wsf|ps1|msc|cpl|hta|msi|lnk|py|jar";

    [GeneratedRegex(@"^(.+?\.(?:" + Extensions + @"))(?=\s|,|$)", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingFile();

    [GeneratedRegex(@"\.(?:" + Extensions + @")$", RegexOptions.IgnoreCase)]
    private static partial Regex EndsWithKnownExtension();

    public static ResolvedCommand Resolve(string? command, Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        if (string.IsNullOrWhiteSpace(command))
            return new ResolvedCommand(null, null, null);

        var text = Normalize(Environment.ExpandEnvironmentVariables(command.Trim()));
        var (exe, args) = SplitExecutable(text, fileExists);
        if (string.IsNullOrWhiteSpace(exe))
            return new ResolvedCommand(null, null, null);

        exe = Qualify(exe, fileExists);
        if (!Hosts.Contains(Path.GetFileNameWithoutExtension(exe)))
            return new ResolvedCommand(exe, null, NullIfEmpty(args));

        return new ResolvedCommand(FindHostedFile(exe, args, fileExists), exe, NullIfEmpty(args));
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Service image paths may use kernel-style prefixes or be relative to the Windows folder.</summary>
    private static string Normalize(string text)
    {
        if (text.StartsWith(@"\??\", StringComparison.Ordinal))
            return text[4..];
        if (text.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(WinDir, text[12..]);
        if (text.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith(@"SysWOW64\", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(WinDir, text);
        return text;
    }

    private static (string? Exe, string Args) SplitExecutable(string text, Func<string, bool> fileExists)
    {
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end < 0
                ? (text.Trim('"'), "")
                : (text[1..end], text[(end + 1)..].Trim());
        }

        // An unquoted path may contain spaces: take the shortest prefix that is a real file.
        for (var i = text.IndexOf(' '); i >= 0; i = text.IndexOf(' ', i + 1))
        {
            if (RootedFile(text[..i], fileExists) is { } file)
                return (file, text[(i + 1)..].Trim());
        }
        if (RootedFile(text, fileExists) is { } whole)
            return (whole, "");

        var match = LeadingFile().Match(text);
        if (match.Success)
            return (match.Groups[1].Value, text[match.Length..].Trim());

        var space = text.IndexOf(' ');
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..].Trim());
    }

    /// <summary>A rooted path that is a real file, with ".exe" added when that is what exists.</summary>
    private static string? RootedFile(string path, Func<string, bool> fileExists)
    {
        if (!path.Contains('\\'))
            return null;
        if (fileExists(path))
            return path;
        return fileExists(path + ".exe") ? path + ".exe" : null;
    }

    /// <summary>A bare name such as <c>rundll32.exe</c> is looked up in System32.</summary>
    private static string Qualify(string exe, Func<string, bool> fileExists)
    {
        if (exe.Contains('\\') || exe.Contains('/'))
            return exe;

        var name = Path.HasExtension(exe) ? exe : exe + ".exe";
        var inSystem = Path.Combine(Environment.SystemDirectory, name);
        if (fileExists(inSystem) || Hosts.Contains(Path.GetFileNameWithoutExtension(name)))
            return inSystem;
        return exe;
    }

    private static string? FindHostedFile(string host, string args, Func<string, bool> fileExists)
    {
        var hostName = Path.GetFileNameWithoutExtension(host);
        if (hostName.Equals("svchost", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(args))
            return null;

        if (hostName.Equals("rundll32", StringComparison.OrdinalIgnoreCase))
        {
            var comma = args.IndexOf(',');
            var dll = (comma < 0 ? args : args[..comma]).Trim().Trim('"').Trim();
            return dll.Length == 0 ? null : Qualify(dll, fileExists);
        }

        var tokens = Tokenize(args);
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Length == 0 || token[0] is '-' or '/')
                continue;

            if (EndsWithKnownExtension().IsMatch(token))
                return Qualify(token, fileExists);

            // An unquoted path with spaces arrives as several tokens; join until it ends in a file name.
            if (!token.Contains(@":\"))
                continue;
            var joined = new StringBuilder(token);
            for (var j = i + 1; j < tokens.Count && j <= i + 6; j++)
            {
                joined.Append(' ').Append(tokens[j]);
                if (EndsWithKnownExtension().IsMatch(tokens[j]))
                    return joined.ToString();
            }
        }
        return null;
    }

    private static List<string> Tokenize(string args)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var wasQuoted = false;
        foreach (var c in args)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                wasQuoted = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0 || wasQuoted)
                    tokens.Add(current.ToString());
                current.Clear();
                wasQuoted = false;
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0 || wasQuoted)
            tokens.Add(current.ToString());
        return tokens;
    }
}
