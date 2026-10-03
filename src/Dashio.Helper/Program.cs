using System.Text.Json;
using Dashio.Core.AdminScan;
using Dashio.Core.Changes;
using Dashio.Core.Inventory;
using Dashio.Core.Processes;
using Dashio.Core.Scanning;
using Dashio.Core.Storage;

namespace Dashio.Helper;

/// <summary>
/// The elevated half of Dashio. Started once per batch behind a Windows admin prompt:
///   Dashio.Helper.exe --request &lt;file&gt; --response &lt;file&gt;
///   Dashio.Helper.exe --scan-tasks --request &lt;file&gt; --response &lt;file&gt;
///   Dashio.Helper.exe --scan-prefetch --request &lt;file&gt; --response &lt;file&gt;
///   Dashio.Helper.exe --read-drive &lt;letter&gt; --request &lt;file&gt; --response &lt;file&gt;
/// It switches existing autostart items on or off, ends programs that are not part of Windows,
/// lists scheduled tasks, lists when programs last ran, or reads the folder sizes of a fixed
/// drive, and exits.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitFailed = 1;
    private const int ExitBadArguments = 2;
    private const int ExitUnsafePath = 3;

    private static int Main(string[] args)
    {
        var request = Argument(args, "--request");
        var response = Argument(args, "--response");
        if (request is null || response is null)
            return ExitBadArguments;
        if (!HelperRequestProcessor.AreSafePaths(request, response, out _))
            return ExitUnsafePath;

        try
        {
            var scanner = new SystemScanner();
            if (args.Contains("--scan-tasks", StringComparer.OrdinalIgnoreCase))
            {
                WriteNew(response, new TaskScanResponse(AdminTaskScan.Run(scanner)));
            }
            else if (args.Contains("--scan-prefetch", StringComparer.OrdinalIgnoreCase))
            {
                WriteNew(response, ReadPrefetch());
            }
            else if (args.Contains("--read-drive", StringComparer.OrdinalIgnoreCase))
            {
                WriteNew(response, ReadDrive(Argument(args, "--read-drive"), response));
            }
            else
            {
                var ender = new ProcessEnder();
                var processor = new HelperRequestProcessor(
                    scanner.Find, new ChangeExecutor().Apply, ends => ender.EndAll(ends));
                WriteNew(response, processor.Process(ReadRequest(request)));
            }
            return ExitOk;
        }
        catch (Exception e)
        {
            try
            {
                WriteNew(response, new HelperResponse([], e.Message));
            }
            catch (IOException)
            {
            }
            return ExitFailed;
        }
    }

    private static PrefetchScanResponse ReadPrefetch()
    {
        try
        {
            return new PrefetchScanResponse(PrefetchReader.Read());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new PrefetchScanResponse([], e.Message);
        }
    }

    /// <summary>
    /// Reads a drive's folder sizes and writes the tree beside the response. The request gives a
    /// drive letter only; it is checked against this PC's fixed drives before anything is opened.
    /// </summary>
    private static DriveReadResponse ReadDrive(string? letter, string responsePath)
    {
        if (AdminDriveReader.RootFor(letter) is not { } root)
            return new DriveReadResponse(false, "That is not a fixed drive of this PC.");
        var treePath = HelperRequestProcessor.TreePathFor(responsePath);
        var progressPath = HelperRequestProcessor.ProgressPathFor(responsePath);
        if (File.Exists(treePath) || File.Exists(progressPath))
            return new DriveReadResponse(false, "The files for the result already exist.");

        var lastWritten = DateTime.MinValue;
        void Report(double fraction)
        {
            // Often enough for a progress bar; not so often that the disk is busy with it.
            if ((DateTime.UtcNow - lastWritten).TotalMilliseconds < 250)
                return;
            lastWritten = DateTime.UtcNow;
            try
            {
                File.WriteAllText(progressPath, fraction.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (IOException)
            {
            }
        }

        var (tree, fromTable) = AdminDriveReader.Read(root, Report);
        FolderTreeFile.Save(treePath, tree, DateTimeOffset.Now, readEverything: true);
        return new DriveReadResponse(fromTable);
    }

    private static string? Argument(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static HelperRequest? ReadRequest(string path)
    {
        try
        {
            return HelperJson.Read<HelperRequest>(path);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Creates the response file; never overwrites an existing one.</summary>
    private static void WriteNew<T>(string path, T value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, value, HelperJson.Options);
    }
}
