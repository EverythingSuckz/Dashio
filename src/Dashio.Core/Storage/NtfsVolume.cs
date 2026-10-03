using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dashio.Core.Storage;

/// <summary>A drive opened for reading as one run of bytes. Only an administrator may open one.</summary>
public sealed class NtfsVolume : IVolume, IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint ShareAll = 0x00000007;
    private const uint OpenExisting = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFilePointerEx(SafeFileHandle file, long distance, out long position, uint method);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle file, byte[] buffer, int count, out int read, IntPtr overlapped);

    private readonly SafeFileHandle _handle;

    /// <param name="letter">The drive's letter. Nothing else is accepted, so no other path can be opened this way.</param>
    public NtfsVolume(char letter)
    {
        if (!char.IsAsciiLetter(letter))
            throw new ArgumentException("A drive letter is needed.", nameof(letter));
        _handle = CreateFileW($@"\\.\{char.ToUpperInvariant(letter)}:", GenericRead, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (_handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Read(long offset, byte[] buffer, int count)
    {
        if (!SetFilePointerEx(_handle, offset, out _, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var filled = 0;
        while (filled < count)
        {
            // A read may return less than was asked for; what is left is read into a second buffer.
            if (filled == 0)
            {
                if (!ReadFile(_handle, buffer, count, out var read, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (read == 0)
                    throw new EndOfStreamException("The drive ended before the file table did.");
                filled = read;
            }
            else
            {
                var rest = new byte[count - filled];
                if (!ReadFile(_handle, rest, rest.Length, out var read, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (read == 0)
                    throw new EndOfStreamException("The drive ended before the file table did.");
                Array.Copy(rest, 0, buffer, filled, read);
                filled += read;
            }
        }
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>Lets an administrator's program list folders whatever their permissions say, as a backup program does.</summary>
public static class BackupPrivilege
{
    private const uint AdjustPrivileges = 0x0020;
    private const uint Query = 0x0008;
    private const uint Enabled = 0x00000002;

    // Packed, because the identifier inside is two 32-bit halves and so is not aligned like a 64-bit number.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Privilege
    {
        public uint Count;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        SafeAccessTokenHandle token, bool disableAll, ref Privilege state, int length, IntPtr previous, IntPtr returned);

    /// <summary>True when the privilege was switched on. It only can be for a process that runs elevated.</summary>
    public static bool TryEnable()
    {
        if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle, AdjustPrivileges | Query, out var token))
            return false;
        using (token)
        {
            if (!LookupPrivilegeValueW(null, "SeBackupPrivilege", out var luid))
                return false;
            var state = new Privilege { Count = 1, Luid = luid, Attributes = Enabled };
            // The call succeeds even when the privilege is not held; the last error says which it was.
            return AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastWin32Error() == 0;
        }
    }
}
