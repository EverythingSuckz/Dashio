using Dashio.Core.Evidence;
using Dashio.Core.Models;
using Dashio.Core.Parsing;

namespace Dashio.Core.Tests;

public class EvidenceTests
{
    private static readonly string System32 = Environment.SystemDirectory;
    private readonly FileEvidenceReader _reader = new();

    [Fact]
    public void Catalog_signed_windows_file_is_a_windows_component()
    {
        var evidence = _reader.Read(Path.Combine(System32, "kernel32.dll"));
        Assert.True(evidence.Exists);
        Assert.True(evidence.IsWindowsComponent, $"signer was '{evidence.Signer}'");
        Assert.False(evidence.SignerIsGeneric);
    }

    [Fact]
    public void Embedded_signed_windows_file_is_a_windows_component()
    {
        var evidence = _reader.Read(Path.Combine(System32, "svchost.exe"));
        Assert.True(evidence.IsWindowsComponent, $"signer was '{evidence.Signer}'");
        Assert.Contains("Microsoft", evidence.Company);
    }

    [Fact]
    public void Unsigned_file_has_no_signer()
    {
        var evidence = _reader.Read(typeof(EvidenceTests).Assembly.Location);
        Assert.True(evidence.Exists);
        Assert.Null(evidence.Signer);
        Assert.False(evidence.IsWindowsComponent);
    }

    [Fact]
    public void Missing_file_is_reported_not_thrown()
    {
        var evidence = _reader.Read(@"C:\does\not\exist.exe");
        Assert.False(evidence.Exists);
        Assert.Null(evidence.Signer);
    }

    [Fact]
    public void Script_run_through_cmd_is_not_protected()
    {
        var resolved = CommandLineParser.Resolve(@"cmd.exe /c ""C:\Stuff\boot.bat""");
        var item = new AutostartItem
        {
            Id = "runkey:user:Run:Boot",
            Kind = AutostartKind.RunKey,
            Name = "Boot",
            DisplayName = "Boot",
            TargetPath = resolved.TargetPath,
            HostPath = resolved.HostPath,
            Evidence = _reader.Read(resolved.TargetPath!),
        };
        Assert.False(ProtectionPolicy.IsProtected(item));
    }

    [Fact]
    public void Host_with_no_target_is_not_protected()
    {
        var resolved = CommandLineParser.Resolve(@"powershell.exe -Command ""Get-Date""");
        var item = new AutostartItem
        {
            Id = "runkey:user:Run:Ps",
            Kind = AutostartKind.RunKey,
            Name = "Ps",
            DisplayName = "Ps",
            TargetPath = resolved.TargetPath,
            HostPath = resolved.HostPath,
        };
        Assert.False(ProtectionPolicy.IsProtected(item));
    }

    [Fact]
    public void Windows_file_run_directly_is_protected()
    {
        var path = Path.Combine(System32, "svchost.exe");
        var item = new AutostartItem
        {
            Id = "service:machine:x",
            Kind = AutostartKind.Service,
            Name = "x",
            DisplayName = "x",
            TargetPath = path,
            Evidence = _reader.Read(path),
        };
        Assert.True(ProtectionPolicy.IsProtected(item));
    }

    [Fact]
    public void Task_in_the_windows_folder_is_protected_without_evidence()
    {
        var item = new AutostartItem
        {
            Id = @"task:machine:\Microsoft\Windows\Defrag\ScheduledDefrag",
            Kind = AutostartKind.ScheduledTask,
            Name = "ScheduledDefrag",
            DisplayName = "ScheduledDefrag",
            Location = @"\Microsoft\Windows\Defrag\",
        };
        Assert.True(ProtectionPolicy.IsProtected(item));
    }
}
