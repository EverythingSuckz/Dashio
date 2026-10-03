using Dashio.Core.Models;
using Dashio.Core.Processes;
using Dashio.Core.Uninstall;

namespace Dashio.Core.Tests;

public class UninstallerProcessesTests
{
    private const long Launched = 1_000;

    private static ProcessSample Process(int pid, int parent, long started) => new(pid, "x.exe", 0, 0, started, parent);

    [Fact]
    public void The_uninstaller_itself_counts_while_it_runs()
    {
        var watch = new UninstallerProcesses(40, Launched);

        Assert.True(watch.AnyRunning([Process(40, 4, 1_010)]));
        Assert.False(watch.AnyRunning([]));
    }

    [Fact]
    public void A_copy_it_started_before_exiting_still_counts()
    {
        var watch = new UninstallerProcesses(40, Launched);
        watch.AnyRunning([Process(40, 4, 1_010)]);

        // The first process is gone; what it started is not, and neither is what that started.
        Assert.True(watch.AnyRunning([Process(52, 40, 1_050), Process(60, 52, 1_090)]));
        Assert.True(watch.AnyRunning([Process(60, 52, 1_090)]));
        Assert.False(watch.AnyRunning([Process(7, 4, 900)]));
    }

    [Fact]
    public void A_copy_counts_even_when_the_uninstaller_was_gone_before_the_first_look()
    {
        var watch = new UninstallerProcesses(40, Launched);

        Assert.True(watch.AnyRunning([Process(52, 40, 1_050)]));
    }

    [Fact]
    public void An_older_process_with_a_reused_id_is_not_mistaken_for_it()
    {
        var watch = new UninstallerProcesses(40, Launched);

        // Started before the uninstaller was: another program that had the id first, and its child.
        Assert.False(watch.AnyRunning([Process(40, 4, 500), Process(52, 40, 600)]));
    }
}

public class UninstallFollowerTests
{
    private static readonly FollowTimes Times = new(
        LookEvery: TimeSpan.FromSeconds(1), ClosedFor: TimeSpan.FromSeconds(4), OpenFor: TimeSpan.FromSeconds(8),
        HandOverWait: TimeSpan.FromSeconds(120), GiveUpAfter: TimeSpan.FromMinutes(30));

    /// <summary>Follows with a clock that only moves when the follower waits.</summary>
    /// <param name="goneAt">The second from which Windows no longer lists the app, or null for never.</param>
    /// <param name="openUntil">The second until which the uninstaller is running, or null when its process is not known.</param>
    private static async Task<(UninstallEnd End, int Seconds)> Follow(int? goneAt, int? openUntil)
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var now = start;
        int Elapsed() => (int)(now - start).TotalSeconds;

        var end = await UninstallFollower.FollowAsync(
            () => goneAt is null || Elapsed() < goneAt,
            openUntil is { } until ? () => Elapsed() < until : null,
            Times,
            wait =>
            {
                now += wait;
                return Task.CompletedTask;
            },
            () => now);
        return (end, Elapsed());
    }

    [Fact]
    public async Task It_ends_as_soon_as_Windows_no_longer_lists_the_app()
    {
        Assert.Equal((UninstallEnd.Removed, 20), await Follow(goneAt: 20, openUntil: 25));
    }

    [Fact]
    public async Task An_uninstaller_that_is_closed_with_the_app_still_listed_ends_as_closed()
    {
        // Open for 30 seconds, then gone for the 4 it takes to be sure.
        var (end, seconds) = await Follow(goneAt: null, openUntil: 30);

        Assert.Equal(UninstallEnd.Closed, end);
        Assert.InRange(seconds, 33, 35);
    }

    [Fact]
    public async Task The_app_going_just_after_the_uninstaller_closes_still_counts_as_removed()
    {
        Assert.Equal(UninstallEnd.Removed, (await Follow(goneAt: 32, openUntil: 30)).End);
    }

    [Fact]
    public async Task An_uninstaller_that_is_gone_at_once_is_not_waited_for_and_nothing_is_concluded()
    {
        // "No" was pressed straight away, or a launcher was told to remove the app: both look the same.
        var (end, seconds) = await Follow(goneAt: null, openUntil: 2);

        Assert.Equal(UninstallEnd.Unknown, end);
        Assert.InRange(seconds, 5, 7);
    }

    [Fact]
    public async Task Without_a_process_to_watch_nothing_is_concluded_from_its_absence()
    {
        Assert.Equal((UninstallEnd.Unknown, 120), await Follow(goneAt: null, openUntil: null));
    }

    [Fact]
    public async Task A_very_long_uninstall_is_given_up_on()
    {
        Assert.Equal((UninstallEnd.Unknown, 1800), await Follow(goneAt: null, openUntil: 5000));
    }
}

public sealed class PendingUninstallsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dashio-pending-{Guid.NewGuid():N}.json");

    public void Dispose() => File.Delete(_path);

    private static PendingUninstall Pending(string name, DateTimeOffset started) =>
        new($"app:hkcu:{name}", AppSourceKind.InstalledApp, name, name, null, started);

    [Fact]
    public void What_is_pending_survives_being_loaded_again()
    {
        new PendingUninstalls(_path).Add(Pending("Fabrikam Editor", Now));

        var loaded = Assert.Single(new PendingUninstalls(_path).Load());
        Assert.Equal("app:hkcu:Fabrikam Editor", loaded.SourceId);
        Assert.Equal("app:hkcu:Fabrikam Editor", loaded.Source.Id);
    }

    [Fact]
    public void An_app_that_has_gone_is_reported_once_and_forgotten()
    {
        var pending = new PendingUninstalls(_path);
        pending.Add(Pending("Fabrikam Editor", Now));
        pending.Add(Pending("Contoso Sync", Now));

        var (gone, givenUp) = pending.Settle(s => s.Name != "Fabrikam Editor", Now.AddMinutes(10));

        Assert.Equal("Fabrikam Editor", Assert.Single(gone).Name);
        Assert.Empty(givenUp);
        Assert.Equal("Contoso Sync", Assert.Single(pending.Load()).Name);
        Assert.Empty(pending.Settle(s => s.Name != "Fabrikam Editor", Now.AddMinutes(20)).Gone);
    }

    [Fact]
    public void An_app_that_is_still_there_long_after_is_given_up_on()
    {
        var pending = new PendingUninstalls(_path);
        pending.Add(Pending("Contoso Sync", Now));

        var (gone, givenUp) = pending.Settle(_ => true, Now + PendingUninstalls.KeptFor + TimeSpan.FromMinutes(1));

        Assert.Empty(gone);
        Assert.Single(givenUp);
        Assert.Empty(pending.Load());
    }

    [Fact]
    public void An_uninstall_that_is_still_being_followed_is_left_alone()
    {
        var pending = new PendingUninstalls(_path);
        pending.Add(Pending("Fabrikam Editor", Now));

        var (gone, _) = pending.Settle(_ => false, Now, skip: p => p.Name == "Fabrikam Editor");

        Assert.Empty(gone);
        Assert.Single(pending.Load());
    }

    [Fact]
    public void A_file_that_cannot_be_read_counts_as_nothing_pending()
    {
        File.WriteAllText(_path, "not json");

        Assert.Empty(new PendingUninstalls(_path).Load());
    }
}
