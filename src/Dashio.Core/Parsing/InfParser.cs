using System.Text.RegularExpressions;

namespace Dashio.Core.Parsing;

/// <param name="Description">What the package installs, taken from its disk label when it has one.</param>
public sealed record InfInfo(string? Provider, string? Class, string? Description = null);

/// <summary>Reads the provider, class and a description from a driver package's INF file.</summary>
public static partial class InfParser
{
    [GeneratedRegex(
        @"\s*(?:installation|install|installer|driver|drivers|setup)?\s*(?:disk|disc|media|source)\s*#?\d*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DiskSuffix();

    public static InfInfo Parse(string text)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0)
                continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                var name = line[1..^1].Trim();
                if (!sections.TryGetValue(name, out current))
                    sections[name] = current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            var eq = line.IndexOf('=');
            if (current is null || eq <= 0)
                continue;
            current[line[..eq].Trim()] = line[(eq + 1)..].Trim().Trim('"');
        }

        // Localised string tables are named Strings.0409 and so on; the plain one wins.
        sections.TryGetValue("Strings", out var strings);
        strings ??= sections.FirstOrDefault(s => s.Key.StartsWith("Strings", StringComparison.OrdinalIgnoreCase)).Value;

        var description = Description(strings);
        if (!sections.TryGetValue("Version", out var version))
            return new InfInfo(null, null, description);

        return new InfInfo(Lookup(version, "Provider", strings), Lookup(version, "Class", strings), description);
    }

    /// <summary>
    /// Driver packages label their install disk "Predator Service Installation Disk";
    /// the part before the suffix is the nearest thing an INF has to a product name.
    /// </summary>
    private static string? Description(Dictionary<string, string>? strings)
    {
        if (strings is null)
            return null;
        foreach (var (key, value) in strings)
        {
            if (!key.StartsWith("Disk", StringComparison.OrdinalIgnoreCase))
                continue;
            var match = DiskSuffix().Match(value);
            if (!match.Success)
                continue;
            var name = value[..match.Index].Trim();
            if (name.Length >= 3)
                return name;
        }
        return null;
    }

    private static string? Lookup(
        Dictionary<string, string> section, string key, Dictionary<string, string>? strings)
    {
        if (!section.TryGetValue(key, out var value) || value.Length == 0)
            return null;
        if (value.Length > 2 && value[0] == '%' && value[^1] == '%')
            return strings is not null && strings.TryGetValue(value[1..^1], out var resolved) && resolved.Length > 0
                ? resolved
                : null;
        return value;
    }

    private static string StripComment(string line)
    {
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
                inQuotes = !inQuotes;
            else if (line[i] == ';' && !inQuotes)
                return line[..i];
        }
        return line;
    }
}
