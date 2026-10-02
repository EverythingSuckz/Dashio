using Dashio.Core.Parsing;

namespace Dashio.Core.Tests;

public class CommandLineParserTests
{
    private static readonly string System32 = Environment.SystemDirectory;
    private static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static Func<string, bool> Existing(params string[] paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_command_resolves_to_nothing(string? command)
    {
        var r = CommandLineParser.Resolve(command, Existing());
        Assert.Null(r.TargetPath);
        Assert.Null(r.HostPath);
    }

    [Fact]
    public void Quoted_path_with_arguments()
    {
        var r = CommandLineParser.Resolve(@"""C:\Program Files\Foo\foo.exe"" --bar", Existing());
        Assert.Equal(@"C:\Program Files\Foo\foo.exe", r.TargetPath);
        Assert.Equal("--bar", r.Arguments);
        Assert.Null(r.HostPath);
    }

    [Fact]
    public void Unquoted_path_with_spaces_uses_the_file_that_exists()
    {
        var r = CommandLineParser.Resolve(
            @"C:\Program Files\Foo Bar\svc.exe -k x",
            Existing(@"C:\Program Files\Foo Bar\svc.exe"));
        Assert.Equal(@"C:\Program Files\Foo Bar\svc.exe", r.TargetPath);
        Assert.Equal("-k x", r.Arguments);
    }

    [Fact]
    public void Unquoted_path_with_spaces_falls_back_to_the_extension_when_the_file_is_gone()
    {
        var r = CommandLineParser.Resolve(@"C:\Program Files\Foo Bar\svc.exe -k x", Existing());
        Assert.Equal(@"C:\Program Files\Foo Bar\svc.exe", r.TargetPath);
    }

    [Fact]
    public void Environment_variables_are_expanded()
    {
        var r = CommandLineParser.Resolve(@"%SystemRoot%\System32\foo.exe", Existing());
        Assert.Equal(Path.Combine(WinDir, @"System32\foo.exe"), r.TargetPath, ignoreCase: true);
    }

    [Theory]
    [InlineData(@"\SystemRoot\System32\drivers\x.sys", @"System32\drivers\x.sys")]
    [InlineData(@"System32\drivers\x.sys", @"System32\drivers\x.sys")]
    public void Kernel_style_paths_are_made_absolute(string command, string relative)
    {
        var r = CommandLineParser.Resolve(command, Existing());
        Assert.Equal(Path.Combine(WinDir, relative), r.TargetPath, ignoreCase: true);
    }

    [Fact]
    public void Nt_prefix_is_stripped()
    {
        var r = CommandLineParser.Resolve(@"\??\C:\x\y.exe", Existing());
        Assert.Equal(@"C:\x\y.exe", r.TargetPath);
    }

    [Fact]
    public void Bare_executable_name_is_found_in_system32()
    {
        var notepad = Path.Combine(System32, "notepad.exe");
        var r = CommandLineParser.Resolve("notepad.exe", Existing(notepad));
        Assert.Equal(notepad, r.TargetPath, ignoreCase: true);
    }

    [Theory]
    [InlineData(@"rundll32.exe ""C:\App\x.dll"",Entry arg")]
    [InlineData(@"C:\Windows\System32\rundll32.exe C:\App\x.dll,Entry")]
    public void Rundll32_resolves_to_the_dll(string command)
    {
        var r = CommandLineParser.Resolve(command, Existing(Path.Combine(System32, "rundll32.exe")));
        Assert.Equal(@"C:\App\x.dll", r.TargetPath);
        Assert.EndsWith("rundll32.exe", r.HostPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cmd_resolves_to_the_started_file()
    {
        var r = CommandLineParser.Resolve(@"cmd.exe /c start """" ""C:\App\app.exe"" --min", Existing());
        Assert.Equal(@"C:\App\app.exe", r.TargetPath);
        Assert.EndsWith("cmd.exe", r.HostPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Powershell_file_resolves_to_the_script()
    {
        var r = CommandLineParser.Resolve(
            @"powershell.exe -ExecutionPolicy Bypass -File ""C:\Scripts\run.ps1""", Existing());
        Assert.Equal(@"C:\Scripts\run.ps1", r.TargetPath);
    }

    [Fact]
    public void Hosted_path_with_spaces_and_no_quotes_is_joined()
    {
        var r = CommandLineParser.Resolve(@"wscript.exe C:\My Scripts\boot.vbs /quiet", Existing());
        Assert.Equal(@"C:\My Scripts\boot.vbs", r.TargetPath);
    }

    [Fact]
    public void Host_with_no_file_has_no_target()
    {
        var r = CommandLineParser.Resolve(@"powershell.exe -Command ""Get-Date""", Existing());
        Assert.Null(r.TargetPath);
        Assert.EndsWith("powershell.exe", r.HostPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Svchost_is_a_host_with_no_target()
    {
        var r = CommandLineParser.Resolve(@"C:\Windows\system32\svchost.exe -k netsvcs -p", Existing());
        Assert.Null(r.TargetPath);
        Assert.EndsWith("svchost.exe", r.HostPath, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"C:\Windows\system32\svchost.exe -k netsvcs -p", true)]
    [InlineData(@"C:\Windows\system32\msiexec.exe /V", true)]
    [InlineData(@"powershell.exe -Command ""Get-Date""", false)]
    [InlineData(@"cmd.exe /c echo hi", false)]
    public void Only_passive_hosts_stand_in_for_a_missing_target(string command, bool hostIsTarget)
    {
        var r = CommandLineParser.Resolve(command, Existing());
        Assert.Equal(hostIsTarget ? r.HostPath : null, r.TargetOrPassiveHost);
    }

    [Fact]
    public void Unquoted_path_gains_exe_when_only_that_file_exists()
    {
        var r = CommandLineParser.Resolve(
            @"C:\Apps\Ollama\ollama app",
            Existing(@"C:\Apps\Ollama\ollama.exe", @"C:\Apps\Ollama\ollama app.exe"));
        Assert.Equal(@"C:\Apps\Ollama\ollama.exe", r.TargetPath);
        Assert.Equal("app", r.Arguments);
    }
}
