using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dashio.Core.Inventory;
using Dashio.Core.Models;
using Dashio.Core.Processes;

namespace Dashio.Core.Changes;

/// <param name="Ends">Processes to end after the changes, by id and start time only.</param>
public sealed record HelperRequest(
    int SchemaVersion, IReadOnlyList<ChangeRequest> Changes, IReadOnlyList<EndRequest>? Ends = null)
{
    public const int CurrentVersion = 2;

    /// <summary>More than this in one batch is not something the app would ever send.</summary>
    public const int MaxChanges = 500;
}

public sealed record HelperResponse(
    IReadOnlyList<ChangeResult> Results, string? Error = null, IReadOnlyList<EndResult>? EndResults = null);

public sealed record TaskScanResponse(IReadOnlyList<AutostartItem> Items, string? Error = null);

public static class HelperJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public static void Write<T>(string path, T value) =>
        File.WriteAllText(path, JsonSerializer.Serialize(value, Options));

    public static T? Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
}

/// <summary>
/// What the elevated helper does with a request. It trusts nothing in the request beyond item ids
/// with target states and process ids with start times: every item and process is looked up
/// again from Windows, and Windows components are refused.
/// </summary>
public sealed class HelperRequestProcessor
{
    private readonly Func<IEnumerable<string>, IReadOnlyDictionary<string, AutostartItem>> _find;
    private readonly Func<AutostartItem, ItemState, ChangeResult> _apply;
    private readonly Func<IReadOnlyList<EndRequest>, IReadOnlyList<EndResult>>? _endAll;

    public HelperRequestProcessor(
        Func<IEnumerable<string>, IReadOnlyDictionary<string, AutostartItem>> find,
        Func<AutostartItem, ItemState, ChangeResult> apply,
        Func<IReadOnlyList<EndRequest>, IReadOnlyList<EndResult>>? endAll = null)
    {
        _find = find;
        _apply = apply;
        _endAll = endAll;
    }

    public HelperResponse Process(HelperRequest? request)
    {
        if (request is null)
            return new HelperResponse([], "The request could not be read.");
        if (request.SchemaVersion != HelperRequest.CurrentVersion)
            return new HelperResponse([], $"Unsupported request version {request.SchemaVersion}.");
        var changes = request.Changes ?? [];
        var ends = request.Ends ?? [];
        if (changes.Count == 0 && ends.Count == 0)
            return new HelperResponse([], "The request contains no changes.");
        if (changes.Count > HelperRequest.MaxChanges || ends.Count > HelperRequest.MaxChanges)
            return new HelperResponse([], "The request contains too many changes.");
        if (ends.Count > 0 && _endAll is null)
            return new HelperResponse([], "This helper cannot end programs.");

        var current = changes.Count == 0
            ? new Dictionary<string, AutostartItem>()
            : _find(changes.Select(c => c.ItemId));
        var results = new List<ChangeResult>();
        foreach (var change in changes)
        {
            if (change.ItemId is null || change.Target is null)
            {
                results.Add(new ChangeResult(change.ItemId ?? "", false, "The change is incomplete."));
                continue;
            }
            if (!current.TryGetValue(change.ItemId, out var item))
            {
                results.Add(new ChangeResult(change.ItemId, false, "The item no longer exists."));
                continue;
            }
            if (item.IsProtected)
            {
                results.Add(new ChangeResult(change.ItemId, false, "Windows components cannot be changed."));
                continue;
            }
            results.Add(_apply(item, change.Target));
        }

        // Services are stopped first, so by now their processes have usually gone by themselves.
        return new HelperResponse(results, EndResults: ends.Count == 0 ? null : _endAll!(ends));
    }

    /// <summary>
    /// The response is written next to the request, as a new .json file. The helper runs elevated,
    /// so it must not be talked into overwriting or creating a file somewhere else.
    /// </summary>
    public static bool AreSafePaths(string requestPath, string responsePath, out string reason)
    {
        reason = "";
        string request, response;
        try
        {
            request = Path.GetFullPath(requestPath);
            response = Path.GetFullPath(responsePath);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = "A path is not valid.";
            return false;
        }

        if (!string.Equals(Path.GetDirectoryName(request), Path.GetDirectoryName(response), StringComparison.OrdinalIgnoreCase))
            reason = "The response must be written next to the request.";
        else if (!response.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            reason = "The response must be a .json file.";
        else if (File.Exists(response))
            reason = "The response file already exists.";
        else if (IsUnder(response, Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ||
                 IsUnder(response, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) ||
                 IsUnder(response, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)))
            reason = "The response cannot be written to a system folder.";
        return reason.Length == 0;
    }

    private static bool IsUnder(string path, string folder) =>
        folder.Length > 0 && path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}

public sealed record HelperRunResult(bool Cancelled, IReadOnlyList<ChangeResult> Results, string? Error = null)
{
    public IReadOnlyList<EndResult> EndResults { get; init; } = [];
}

public sealed record HelperScanResult(bool Cancelled, IReadOnlyList<AutostartItem> Items, string? Error = null);

public sealed record HelperPrefetchResult(bool Cancelled, IReadOnlyList<PrefetchEntry> Entries, string? Error = null);

/// <summary>Starts the elevated helper. Abstracted so the change flow can be tested without elevation.</summary>
public interface IHelperLauncher
{
    Task<HelperRunResult> ApplyAsync(IReadOnlyList<ChangeRequest> changes, CancellationToken cancellation = default);

    /// <summary>Stops the given services, then ends the given processes, behind one prompt.</summary>
    Task<HelperRunResult> EndAsync(
        IReadOnlyList<ChangeRequest> stops, IReadOnlyList<EndRequest> ends, CancellationToken cancellation = default);
    Task<HelperScanResult> ScanTasksAsync(CancellationToken cancellation = default);

    /// <summary>Reads when each program last ran, which only an administrator may.</summary>
    Task<HelperPrefetchResult> ScanPrefetchAsync(CancellationToken cancellation = default);
}

/// <summary>Runs <c>Dashio.Helper.exe</c> behind one Windows admin prompt and reads back its response file.</summary>
public sealed class HelperLauncher : IHelperLauncher
{
    private const int ErrorCancelled = 1223;

    private readonly string _helperPath;
    private readonly string _workFolder;

    public HelperLauncher(string? helperPath = null, string? workFolder = null)
    {
        _helperPath = helperPath ?? Path.Combine(AppContext.BaseDirectory, "Dashio.Helper.exe");
        _workFolder = workFolder ?? DashioPaths.Requests;
    }

    public Task<HelperRunResult> ApplyAsync(
        IReadOnlyList<ChangeRequest> changes, CancellationToken cancellation = default) =>
        EndAsync(changes, [], cancellation);

    public async Task<HelperRunResult> EndAsync(
        IReadOnlyList<ChangeRequest> changes, IReadOnlyList<EndRequest> ends, CancellationToken cancellation = default)
    {
        var (requestPath, responsePath) = NewPaths();
        try
        {
            HelperJson.Write(requestPath, new HelperRequest(HelperRequest.CurrentVersion, changes, ends.Count == 0 ? null : ends));
            var run = await RunAsync($"--request \"{requestPath}\" --response \"{responsePath}\"", cancellation);
            if (run.Cancelled)
                return new HelperRunResult(true, []);
            if (run.Error is not null)
                return new HelperRunResult(false, [], run.Error);

            var response = ReadResponse<HelperResponse>(responsePath);
            return response is null
                ? new HelperRunResult(false, [], "The helper did not report a result.")
                : new HelperRunResult(false, response.Results ?? [], response.Error) { EndResults = response.EndResults ?? [] };
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(responsePath);
        }
    }

    public async Task<HelperScanResult> ScanTasksAsync(CancellationToken cancellation = default)
    {
        var (requestPath, responsePath) = NewPaths();
        try
        {
            // The helper only checks that the response sits next to a request file it was given.
            File.WriteAllText(requestPath, "{}");
            var run = await RunAsync($"--scan-tasks --request \"{requestPath}\" --response \"{responsePath}\"", cancellation);
            if (run.Cancelled)
                return new HelperScanResult(true, []);
            if (run.Error is not null)
                return new HelperScanResult(false, [], run.Error);

            var response = ReadResponse<TaskScanResponse>(responsePath);
            return response is null
                ? new HelperScanResult(false, [], "The helper did not report a result.")
                : new HelperScanResult(false, response.Items ?? [], response.Error);
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(responsePath);
        }
    }

    public async Task<HelperPrefetchResult> ScanPrefetchAsync(CancellationToken cancellation = default)
    {
        var (requestPath, responsePath) = NewPaths();
        try
        {
            File.WriteAllText(requestPath, "{}");
            var run = await RunAsync($"--scan-prefetch --request \"{requestPath}\" --response \"{responsePath}\"", cancellation);
            if (run.Cancelled)
                return new HelperPrefetchResult(true, []);
            if (run.Error is not null)
                return new HelperPrefetchResult(false, [], run.Error);

            var response = ReadResponse<PrefetchScanResponse>(responsePath);
            return response is null
                ? new HelperPrefetchResult(false, [], "The helper did not report a result.")
                : new HelperPrefetchResult(false, response.Entries ?? [], response.Error);
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(responsePath);
        }
    }

    private (string Request, string Response) NewPaths()
    {
        Directory.CreateDirectory(_workFolder);
        var id = Guid.NewGuid().ToString("N");
        return (Path.Combine(_workFolder, $"{id}.request.json"), Path.Combine(_workFolder, $"{id}.response.json"));
    }

    private async Task<(bool Cancelled, string? Error)> RunAsync(string arguments, CancellationToken cancellation)
    {
        if (!File.Exists(_helperPath))
            return (false, $"The helper was not found at {_helperPath}.");

        try
        {
            // Process.Start does not return until the admin prompt is answered, so it runs off the
            // caller's thread; otherwise the window would freeze for as long as the prompt is open.
            using var process = await Task.Run(() => Process.Start(new ProcessStartInfo(_helperPath, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            }), cancellation);
            if (process is null)
                return (false, "The helper could not be started.");
            await process.WaitForExitAsync(cancellation);
            return (false, null);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            return (true, null);
        }
        catch (Win32Exception e)
        {
            return (false, e.Message);
        }
    }

    private static T? ReadResponse<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? HelperJson.Read<T>(path) : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
