using System.ComponentModel;
using System.Diagnostics;
using Dashio.Core.Changes;
using Dashio.Core.Journal;
using Dashio.Core.Models;
using Microsoft.Win32;
using Windows.Management.Deployment;

namespace Dashio.Core.Uninstall;

/// <summary>How one installed entry would be removed: by its own uninstaller, or as a Store package.</summary>
/// <param name="Program">The uninstaller the app registered with Windows. Null for a Store package.</param>
public sealed record UninstallPlan(string AppName, AppSource Source, string? Program, string? Arguments)
{
    public bool IsPackage => Source.Kind == AppSourceKind.StorePackage;
}

public enum UninstallStart
{
    /// <summary>The app's uninstaller is open. Whether the app goes is up to what happens there.</summary>
    Started,

    /// <summary>Windows has removed the app already. Only a Store package ends this way.</summary>
    Removed,
    Cancelled,
    Failed,
}

/// <param name="ProcessId">The uninstaller's process, when Windows said which one it started.</param>
/// <param name="LaunchedAt">Just before it was started, as a Windows file time, to tell it from an older process with the same id.</param>
public sealed record UninstallOutcome(UninstallStart Start, string? Error = null, int? ProcessId = null, long LaunchedAt = 0);

/// <summary>
/// Removes an app the way Windows Settings does: by starting the uninstaller the app itself
/// registered, or by asking Windows to remove a Store package. Dashio deletes nothing itself.
/// </summary>
public static class Uninstaller
{
    private const int ErrorCancelled = 1223;

    /// <summary>The plan for an entry, or null when it cannot or must not be removed from here.</summary>
    public static UninstallPlan? PlanFor(string appName, AppSource source, Func<string, bool>? fileExists = null)
    {
        if (source.IsSystem || source.Kind == AppSourceKind.DriverPackage)
            return null;
        if (source.Kind == AppSourceKind.StorePackage)
            return source.PackageFamilyName is null ? null : new UninstallPlan(appName, source, null, null);

        return Split(source.UninstallCommand, fileExists ?? File.Exists) is { } command
            ? new UninstallPlan(appName, source, command.Program, command.Arguments)
            : null;
    }

    /// <summary>
    /// Splits a registered uninstall command into the program and its arguments. Installers write
    /// these by hand, so the program may be quoted, or unquoted with spaces in its path.
    /// </summary>
    public static (string Program, string Arguments)? Split(string? command, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;
        var text = Environment.ExpandEnvironmentVariables(command.Trim());

        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            return close > 1 ? (text[1..close], text[(close + 1)..].Trim()) : null;
        }
        if (fileExists(text))
            return (text, "");

        // Unquoted: the program ends at the first ".exe" that is followed by a space or the end
        // and names a real file, or a bare name such as "MsiExec.exe" that Windows finds itself.
        var from = 0;
        while (text.IndexOf(".exe", from, StringComparison.OrdinalIgnoreCase) is var at and >= 0)
        {
            var end = at + 4;
            if (end == text.Length || text[end] == ' ')
            {
                var program = text[..end];
                if (fileExists(program) || program.IndexOfAny(['\\', '/']) < 0)
                    return (program, text[end..].Trim());
            }
            from = end;
        }
        return null;
    }

    /// <summary>
    /// Starts the removal. An uninstaller shows its own windows and asks for administrator rights
    /// itself, and this returns as soon as it is open. A Store package is removed by Windows, which
    /// can take a minute when the app is running, and this returns when that is done.
    /// It never throws: whatever goes wrong comes back as <see cref="UninstallStart.Failed"/>.
    /// </summary>
    public static async Task<UninstallOutcome> StartAsync(UninstallPlan plan, CancellationToken cancellation = default)
    {
        try
        {
            if (plan.IsPackage)
                return await Task.Run(() => RemovePackageAsync(plan.Source.PackageFamilyName!, cancellation), cancellation);

            var launchedAt = DateTime.UtcNow.ToFileTimeUtc();
            // Through the shell, so an uninstaller that needs administrator rights can ask for them.
            using var process = await Task.Run(
                () => Process.Start(new ProcessStartInfo(plan.Program!, plan.Arguments ?? "") { UseShellExecute = true }),
                cancellation);
            // No process comes back when the command was handed to a program that was already running.
            return new UninstallOutcome(UninstallStart.Started, null, IdOf(process), launchedAt);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            return new UninstallOutcome(UninstallStart.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return new UninstallOutcome(UninstallStart.Cancelled);
        }
        catch (Exception e)
        {
            return new UninstallOutcome(UninstallStart.Failed, e.Message);
        }
    }

    private static int? IdOf(Process? process)
    {
        try
        {
            return process?.Id;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // An uninstaller that runs elevated does not always let an ordinary program ask.
            return null;
        }
    }

    private static async Task<UninstallOutcome> RemovePackageAsync(string familyName, CancellationToken cancellation)
    {
        var manager = new PackageManager();
        var package = manager.FindPackagesForUser(string.Empty, familyName).FirstOrDefault();
        if (package is null)
            return new UninstallOutcome(UninstallStart.Failed, "The app is no longer installed.");

        var operation = manager.RemovePackageAsync(package.Id.FullName);
        var finished = operation.AsTask(cancellation);
        // Windows waits half a minute for a running app to close. The list of packages is asked
        // meanwhile, so the answer does not hang on the operation alone.
        while (await Task.WhenAny(finished, Task.Delay(PackageLookEvery, cancellation)) != finished)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!manager.FindPackagesForUser(string.Empty, familyName).Any())
                break;
        }
        if (!finished.IsCompleted)
        {
            GC.KeepAlive(operation);
            return new UninstallOutcome(UninstallStart.Removed);
        }

        var result = await finished;
        // Nothing else holds these during the wait, and once they are collected the operation
        // never reports back.
        GC.KeepAlive(operation);
        GC.KeepAlive(manager);
        return result.ExtendedErrorCode is { } error
            ? new UninstallOutcome(UninstallStart.Failed, string.IsNullOrWhiteSpace(result.ErrorText) ? error.Message : result.ErrorText)
            : new UninstallOutcome(UninstallStart.Removed);
    }

    private static readonly TimeSpan PackageLookEvery = TimeSpan.FromSeconds(1);

    /// <summary>Whether Windows still lists the entry. An uninstaller can be closed without removing anything.</summary>
    public static bool IsStillInstalled(AppSource source)
    {
        try
        {
            if (source.Kind == AppSourceKind.StorePackage)
            {
                return source.PackageFamilyName is { } family &&
                       new PackageManager().FindPackagesForUser(string.Empty, family).Any();
            }

            // "app:hklm64:SubKey", as the collector wrote it.
            var parts = source.Id.Split(':', 3);
            if (parts.Length != 3)
                return true;
            var (hive, view) = parts[1] switch
            {
                "hklm64" => (RegistryHive.LocalMachine, RegistryView.Registry64),
                "hklm32" => (RegistryHive.LocalMachine, RegistryView.Registry32),
                _ => (RegistryHive.CurrentUser, RegistryView.Default),
            };
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var key = baseKey.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{parts[2]}");
            return key?.GetValue("DisplayName") is string;
        }
        catch (Exception)
        {
            // Not being able to tell is not the same as gone.
            return true;
        }
    }

    /// <summary>Records the uninstall in the change log. It cannot be undone from there.</summary>
    public static void Record(ChangeJournal journal, string appName, AppSource source, JournalResult result, string? error)
    {
        journal.Append(
        [
            new JournalEntry
            {
                Id = Guid.NewGuid(),
                BatchId = Guid.NewGuid(),
                Time = DateTimeOffset.Now,
                ItemId = $"uninstall:{source.Id}",
                Kind = AutostartKind.App,
                ItemName = source.Name,
                AppName = appName,
                Action = ChangeAction.Uninstall,
                Before = new ItemState(true),
                Target = new ItemState(false),
                Result = result,
                Error = error,
            },
        ]);
    }
}
