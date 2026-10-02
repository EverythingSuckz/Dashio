using System.Text.Json;
using Dashio.Core.AdminScan;
using Dashio.Core.Changes;
using Dashio.Core.Scanning;

namespace Dashio.Helper;

/// <summary>
/// The elevated half of Dashio. Started once per batch behind a Windows admin prompt:
///   Dashio.Helper.exe --request &lt;file&gt; --response &lt;file&gt;
///   Dashio.Helper.exe --scan-tasks --request &lt;file&gt; --response &lt;file&gt;
/// It switches existing autostart items on or off, or lists scheduled tasks, and exits.
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
            else
            {
                var processor = new HelperRequestProcessor(scanner.Find, new ChangeExecutor().Apply);
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
