using System.Collections.Concurrent;
using System.Diagnostics;
using Dashio.Core.Models;

namespace Dashio.Core.Evidence;

/// <summary>Reads version info and the trusted signer of a file. Results are cached per path.</summary>
public sealed class FileEvidenceReader
{
    /// <summary>The certificates Windows uses for its own files.</summary>
    private static readonly HashSet<string> WindowsSigners = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft Windows",
        "Microsoft Windows Publisher",
    };

    /// <summary>Signers shared by many vendors, so they say nothing about who made the file.</summary>
    private static readonly HashSet<string> GenericSigners = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft Windows Hardware Compatibility Publisher",
        "Microsoft Windows Third Party Component CA 2012",
        "Microsoft Windows Third Party Component CA 2014",
    };

    private readonly ConcurrentDictionary<string, FileEvidence> _cache = new(StringComparer.OrdinalIgnoreCase);

    public FileEvidence Read(string path) => _cache.GetOrAdd(path, ReadCore);

    private static FileEvidence ReadCore(string path)
    {
        if (!File.Exists(path))
            return new FileEvidence { Path = path, Exists = false };

        string? company = null, product = null, description = null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            company = Clean(info.CompanyName);
            product = Clean(info.ProductName);
            description = Clean(info.FileDescription);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        var signer = SignatureReader.GetTrustedSigner(path);
        return new FileEvidence
        {
            Path = path,
            Exists = true,
            Company = company,
            Product = product,
            Description = description,
            Signer = signer,
            SignerIsGeneric = signer is not null && GenericSigners.Contains(signer),
            IsWindowsComponent = signer is not null && WindowsSigners.Contains(signer),
        };
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
