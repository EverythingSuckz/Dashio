using System.Text;
using System.Text.RegularExpressions;

namespace Dashio.Core.Parsing;

/// <summary>Name normalisation used to compare publishers and spot the same product under different names.</summary>
public static partial class NameTokens
{
    private static readonly HashSet<string> CorporateSuffixes = new(StringComparer.Ordinal)
    {
        "corporation", "corp", "incorporated", "inc", "llc", "ltd", "limited", "co", "company",
        "gmbh", "ag", "sa", "bv", "pty", "plc", "srl", "sro", "oy", "ab", "the",
    };

    /// <summary>Words that appear in many unrelated product names and so identify nothing.</summary>
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "service", "services", "driver", "drivers", "suite", "software", "update", "updater", "updates",
        "helper", "manager", "management", "client", "agent", "application", "apps", "launcher",
        "utility", "utilities", "tool", "tools", "system", "support", "assistant", "component",
        "components", "performance", "network", "networking", "audio", "graphics", "display",
        "experience", "program", "framework", "runtime", "platform", "security", "plus", "core",
        "host", "engine", "control", "center", "centre", "panel", "online", "desktop", "windows",
        "server", "monitor", "console", "command", "setup", "install", "installer", "maintenance",
        "provider", "data", "device", "devices", "technology", "technologies", "container",
        "local", "user", "with", "from", "that", "this", "version", "edition", "free", "home",
        "professional", "enterprise", "universal", "extension", "extensions", "module", "kernel",
        "wireless", "bluetooth", "wifi", "ethernet", "adapter", "controller", "interface", "task",
        "startup", "start", "background", "notification", "notifications", "tray", "telemetry",
        "analytics", "collector", "improvement", "feedback", "info", "information", "portal",
        "binaries", "files", "common", "shared", "redistributable", "package", "packages",
        "preview", "beta", "mobile", "default", "screen", "dynamic", "smart", "quick", "easy",
    };

    [GeneratedRegex(@"\((?:r|tm|c)\)|[®™©]", RegexOptions.IgnoreCase)]
    private static partial Regex Marks();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\s*\((?:x64|x86|64-bit|32-bit|arm64)[^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex ArchitectureNote();

    [GeneratedRegex(@"\s+(?:version\s+|v)?\d+\.\d+[\w.\-]*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingVersion();

    [GeneratedRegex(@"[\s\-]+(?:x64|x86|arm64)$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingArchitecture();

    /// <summary>Removes trademark symbols from a name shown to the user.</summary>
    public static string CleanDisplayName(string name) =>
        Spaces().Replace(Marks().Replace(name, ""), " ").Trim();

    /// <summary>"Everything 1.5.0.1396a (x64)" → "Everything": installers put the version in the name.</summary>
    public static string CleanAppName(string name)
    {
        var cleaned = CleanDisplayName(name).Replace("&&", "&");
        for (var pass = 0; pass < 2; pass++)
        {
            cleaned = ArchitectureNote().Replace(cleaned, "");
            cleaned = TrailingArchitecture().Replace(cleaned, "");
            cleaned = TrailingVersion().Replace(cleaned, "");
        }
        cleaned = cleaned.Trim();
        return cleaned.Length == 0 ? name : cleaned;
    }

    /// <summary>"Microsoft Corporation" → "Microsoft": the name without its company suffix.</summary>
    public static string ShortPublisher(string name)
    {
        var words = CleanDisplayName(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 1 && CorporateSuffixes.Contains(words[^1].Trim('.', ',').ToLowerInvariant()))
            words.RemoveAt(words.Count - 1);
        return string.Join(' ', words).TrimEnd(',');
    }

    /// <summary>Lower-case words, split on punctuation and digits, and optionally on camel-case boundaries.</summary>
    public static List<string> Words(string? name, bool splitCamelCase = true)
    {
        var words = new List<string>();
        if (string.IsNullOrWhiteSpace(name))
            return words;

        var text = Marks().Replace(name, "");
        var current = new StringBuilder();
        void Flush()
        {
            if (current.Length > 0)
                words.Add(current.ToString().ToLowerInvariant());
            current.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsLetter(c))
            {
                Flush();
                continue;
            }

            if (splitCamelCase && current.Length > 0 && char.IsUpper(c))
            {
                var previous = text[i - 1];
                var nextIsLower = i + 1 < text.Length && char.IsLower(text[i + 1]);
                // "RivetNetworks" → rivet|networks; "NVIDIAContainer" → nvidia|container.
                if (char.IsLower(previous) || (char.IsUpper(previous) && nextIsLower))
                    Flush();
            }
            current.Append(c);
        }
        Flush();
        return words;
    }

    /// <summary>"Intel® Corporation" and "INTEL CORP" both become "intel".</summary>
    public static string NormalizePublisher(string? name) =>
        string.Join(' ', Words(name).Where(w => w.Length > 1 && !CorporateSuffixes.Contains(w)));

    /// <summary>
    /// Two normalised publishers are compatible when either is unknown, they are equal,
    /// or one is the other plus extra words ("realtek" / "realtek semiconductor").
    /// </summary>
    public static bool PublishersCompatible(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0 || a == b)
            return true;
        return a.StartsWith(b + " ", StringComparison.Ordinal) || b.StartsWith(a + " ", StringComparison.Ordinal);
    }

    /// <summary>
    /// For a product named after its maker ("Spotify" by Spotify AB, "Seelen UI" by Seelen) the brand
    /// is the only identifying word. Returns it when the name holds nothing but the publisher's
    /// words and very short ones; otherwise nothing.
    /// </summary>
    public static List<string> BrandTokens(string? name, string normalizedPublisher)
    {
        var publisherWords = normalizedPublisher.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var words = Words(name, splitCamelCase: false);
        if (words.Count == 0 || !words.All(w => w.Length <= 3 || publisherWords.Contains(w)))
            return [];
        return words.Where(w => w.Length >= 4 && publisherWords.Contains(w)).Distinct().ToList();
    }

    /// <summary>
    /// The words in a product name that could identify it: not filler, not its own publisher.
    /// Display names are kept as written ("PowerToys" stays one word, so it never matches "PowerShell").
    /// Identifiers such as "KillerControlCenter" are split, and then only longer words count.
    /// </summary>
    public static List<string> DistinctiveTokens(string? name, string normalizedPublisher, bool splitCamelCase = false)
    {
        var publisherWords = normalizedPublisher.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var minimumLength = splitCamelCase ? 6 : 4;
        return Words(name, splitCamelCase)
            .Where(w => w.Length >= minimumLength && !Stopwords.Contains(w) && !CorporateSuffixes.Contains(w)
                        && !publisherWords.Contains(w))
            .Distinct()
            .ToList();
    }
}
