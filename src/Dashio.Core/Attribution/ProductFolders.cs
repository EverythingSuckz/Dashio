using Dashio.Core.Parsing;

namespace Dashio.Core.Attribution;

/// <summary>The vendor/product folder rule, shared by item grouping and process matching so both agree.</summary>
internal static class ProductFolders
{
    /// <param name="company">The company named by the file, if known. It helps tell a vendor folder from a product folder.</param>
    /// <param name="knownVendors">Publisher keys of the apps Windows already lists.</param>
    public static FolderKey? For(
        string path, string? company, IReadOnlySet<string> knownVendors, AttributionOverrides overrides)
    {
        var companyKey = overrides.PublisherKey(company);
        bool IsVendor(string folder)
        {
            var folderKey = overrides.PublisherKey(folder);
            if (folderKey.Length == 0)
                return false;
            return knownVendors.Contains(folderKey) ||
                   (companyKey.Length > 0 && NameTokens.PublishersCompatible(folderKey, companyKey));
        }

        return FolderKey.For(path, IsVendor);
    }

    public static string DisplayName(FolderKey folder)
    {
        var name = NameTokens.CleanDisplayName(folder.Name);
        if (folder.Vendor is null)
            return name;
        var vendorWord = NameTokens.Words(folder.Vendor).FirstOrDefault();
        return vendorWord is not null && NameTokens.Words(name).Contains(vendorWord)
            ? name
            : $"{folder.Vendor} {name}";
    }
}
