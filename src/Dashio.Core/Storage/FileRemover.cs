using Microsoft.VisualBasic.FileIO;

namespace Dashio.Core.Storage;

/// <summary>
/// Deletes a file or folder the user picked on the Storage page: to the Recycle Bin, or for good
/// when asked. It runs with the user's own rights only, so what needs an administrator fails.
/// </summary>
public static class FileRemover
{
    /// <summary>Places that hold the system, every program or a whole profile. They and what contains them stay.</summary>
    private static readonly string[] Kept = new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        DashioPaths.DataFolder,
        AppContext.BaseDirectory,
    }.Where(p => p.Length > 0).Select(Normal).ToArray();

    private static readonly string WindowsFolder = Normal(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    /// <summary>Why the path may not be deleted from here, or null when it may.</summary>
    public static string? WhyNot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return "It has no place on a drive.";

        var target = Normal(path);
        if (Path.GetPathRoot(target + "\\")?.TrimEnd('\\').Equals(target, StringComparison.OrdinalIgnoreCase) == true)
            return "A whole drive cannot be deleted.";
        if (IsInside(target, WindowsFolder))
            return "It is part of Windows.";
        if (Kept.Any(kept => IsInside(kept, target)))
            return "Windows, your programs or your profile live in it.";
        return null;
    }

    /// <summary>Deletes the file or folder. Throws what Windows throws when it cannot.</summary>
    /// <exception cref="InvalidOperationException">The path is one this never deletes.</exception>
    public static void Delete(string path, bool permanently)
    {
        if (WhyNot(path) is { } reason)
            throw new InvalidOperationException(reason);

        var recycle = permanently ? RecycleOption.DeletePermanently : RecycleOption.SendToRecycleBin;
        if (Directory.Exists(path))
            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, recycle, UICancelOption.ThrowException);
        else
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, recycle, UICancelOption.ThrowException);
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or lies under it.</summary>
    private static bool IsInside(string path, string folder) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);

    private static string Normal(string path) => Path.GetFullPath(path).TrimEnd('\\');
}
