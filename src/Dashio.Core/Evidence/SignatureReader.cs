using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace Dashio.Core.Evidence;

/// <summary>
/// Returns the signer of a file, but only when Windows trusts the signature.
/// Handles both signatures embedded in the file and catalog signatures, which most Windows files use.
/// Never touches the network: revocation checks are off and only cached data is used.
/// </summary>
internal static class SignatureReader
{
    private static readonly ConcurrentDictionary<string, string?> CatalogSigners = new(StringComparer.OrdinalIgnoreCase);

    public static string? GetTrustedSigner(string path)
    {
        try
        {
            var result = VerifyEmbedded(path);
            if (result == 0)
                return CertificateName(path);
            return GetCatalogSigner(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException
                                      or ExternalException or ArgumentException)
        {
            return null;
        }
    }

    private static string? CertificateName(string signedFile)
    {
        try
        {
#pragma warning disable SYSLIB0057 // No non-obsolete API reads the signer of an Authenticode-signed file.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(signedFile));
#pragma warning restore SYSLIB0057
            var name = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static int VerifyEmbedded(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
        };
        var pInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pInfo, false);
            return Verify(WTD_CHOICE_FILE, pInfo);
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pInfo);
            Marshal.FreeHGlobal(pInfo);
        }
    }

    private static string? GetCatalogSigner(string path)
    {
        // Newer catalogs index files by SHA-256, older ones by SHA-1.
        return CatalogSignerWith(path, "SHA256") ?? CatalogSignerWith(path, null);
    }

    private static string? CatalogSignerWith(string path, string? hashAlgorithm)
    {
        if (!CryptCATAdminAcquireContext2(out var admin, IntPtr.Zero, hashAlgorithm, IntPtr.Zero, 0))
            return null;
        var catalogInfo = IntPtr.Zero;
        try
        {
            byte[] hash;
            using (var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                uint size = 0;
                CryptCATAdminCalcHashFromFileHandle2(admin, file, ref size, null, 0);
                if (size == 0)
                    return null;
                hash = new byte[size];
                if (!CryptCATAdminCalcHashFromFileHandle2(admin, file, ref size, hash, 0))
                    return null;
            }

            catalogInfo = CryptCATAdminEnumCatalogFromHash(admin, hash, (uint)hash.Length, 0, IntPtr.Zero);
            if (catalogInfo == IntPtr.Zero)
                return null;

            var info = new CATALOG_INFO { cbStruct = (uint)Marshal.SizeOf<CATALOG_INFO>() };
            if (!CryptCATCatalogInfoFromContext(catalogInfo, ref info, 0))
                return null;

            if (VerifyCatalogMember(info.wszCatalogFile, Convert.ToHexString(hash), path, admin) != 0)
                return null;

            return CatalogSigners.GetOrAdd(info.wszCatalogFile, CertificateName);
        }
        finally
        {
            if (catalogInfo != IntPtr.Zero)
                CryptCATAdminReleaseCatalogContext(admin, catalogInfo, 0);
            CryptCATAdminReleaseContext(admin, 0);
        }
    }

    private static int VerifyCatalogMember(string catalogFile, string memberTag, string memberFile, IntPtr admin)
    {
        var catalog = new WINTRUST_CATALOG_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_CATALOG_INFO>(),
            pcwszCatalogFilePath = catalogFile,
            pcwszMemberTag = memberTag,
            pcwszMemberFilePath = memberFile,
            hCatAdmin = admin,
        };
        var pInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_CATALOG_INFO>());
        try
        {
            Marshal.StructureToPtr(catalog, pInfo, false);
            return Verify(WTD_CHOICE_CATALOG, pInfo);
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_CATALOG_INFO>(pInfo);
            Marshal.FreeHGlobal(pInfo);
        }
    }

    private static int Verify(uint unionChoice, IntPtr pInfo)
    {
        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice = unionChoice,
            pInfo = pInfo,
            dwStateAction = WTD_STATEACTION_IGNORE,
            dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL,
        };
        var action = GenericVerifyV2;
        return WinVerifyTrust(IntPtr.Zero, ref action, ref data);
    }

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_CHOICE_CATALOG = 2;
    private const uint WTD_STATEACTION_IGNORE = 0;
    private const uint WTD_REVOCATION_CHECK_NONE = 0x10;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_CATALOG_INFO
    {
        public uint cbStruct;
        public uint dwCatalogVersion;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszCatalogFilePath;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszMemberTag;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszMemberFilePath;
        public IntPtr hMemberFile;
        public IntPtr pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext;
        public IntPtr hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pInfo;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CATALOG_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string wszCatalogFile;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminAcquireContext2(
        out IntPtr phCatAdmin, IntPtr pgSubsystem, string? pwszHashAlgorithm, IntPtr pStrongHashPolicy, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(
        IntPtr hCatAdmin, SafeFileHandle hFile, ref uint pcbHash, byte[]? pbHash, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(
        IntPtr hCatAdmin, byte[] pbHash, uint cbHash, uint dwFlags, IntPtr phPrevCatInfo);

    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATCatalogInfoFromContext(IntPtr hCatInfo, ref CATALOG_INFO psCatInfo, uint dwFlags);

    [DllImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr hCatAdmin, IntPtr hCatInfo, uint dwFlags);

    [DllImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);
}
