using Dashio.Core.Models;
using Dashio.Core.Uninstall;
using Microsoft.Win32;
using Windows.Management.Deployment;
using Xunit.Abstractions;

namespace Dashio.Core.Tests;

/// <summary>
/// Removes a real Store-style package: a throwaway one that the test registers for the current
/// user from a folder of its own. Registering a folder needs Developer Mode, so the test does
/// nothing where that is off.
/// </summary>
[Trait("Category", "Integration")]
public sealed class UninstallIntegrationTests(ITestOutputHelper output) : IDisposable
{
    private const string PackageName = "Dashio.IntegrationTest.Throwaway";

    // The smallest valid PNG: one transparent pixel.
    private static readonly byte[] Pixel = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"dashio-pkg-{Guid.NewGuid():N}");

    public void Dispose()
    {
        var manager = new PackageManager();
        foreach (var left in manager.FindPackagesForUser(string.Empty).Where(p => p.Id.Name == PackageName).ToList())
        {
            // Held until it is done: a collected operation never reports back.
            var removing = manager.RemovePackageAsync(left.Id.FullName);
            removing.AsTask().GetAwaiter().GetResult();
            GC.KeepAlive(removing);
        }
        GC.KeepAlive(manager);
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private static bool DeveloperMode()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock");
        return key?.GetValue("AllowDevelopmentWithoutDevLicense") is int allowed && allowed != 0;
    }

    private async Task<AppSource> RegisterAsync()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(Path.Combine(_folder, "logo.png"), Pixel);
        // Never started: the package only has to be listed.
        File.WriteAllBytes(Path.Combine(_folder, "app.exe"), []);
        File.WriteAllText(Path.Combine(_folder, "AppxManifest.xml"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                     xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                     xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
                     IgnorableNamespaces="uap rescap">
              <Identity Name="{PackageName}" Publisher="CN=Dashio Integration Test" Version="1.0.0.0" ProcessorArchitecture="neutral" />
              <Properties>
                <DisplayName>Dashio integration test</DisplayName>
                <PublisherDisplayName>Dashio Integration Test</PublisherDisplayName>
                <Logo>logo.png</Logo>
              </Properties>
              <Dependencies>
                <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.26100.0" />
              </Dependencies>
              <Resources>
                <Resource Language="en-us" />
              </Resources>
              <Applications>
                <Application Id="App" Executable="app.exe" EntryPoint="Windows.FullTrustApplication">
                  <uap:VisualElements DisplayName="Dashio integration test" Description="Throwaway"
                                      BackgroundColor="transparent" Square150x150Logo="logo.png" Square44x44Logo="logo.png" />
                </Application>
              </Applications>
              <Capabilities>
                <rescap:Capability Name="runFullTrust" />
              </Capabilities>
            </Package>
            """);

        var manager = new PackageManager();
        var registering = manager.RegisterPackageAsync(
            new Uri(Path.Combine(_folder, "AppxManifest.xml")), null, DeploymentOptions.DevelopmentMode);
        var result = await registering;
        GC.KeepAlive(registering);
        Assert.True(result.ExtendedErrorCode is null, result.ErrorText);

        var package = manager.FindPackagesForUser(string.Empty).Single(p => p.Id.Name == PackageName);
        return new AppSource
        {
            Kind = AppSourceKind.StorePackage,
            Id = $"pkg:{package.Id.FamilyName}",
            Name = "Dashio integration test",
            PackageFamilyName = package.Id.FamilyName,
        };
    }

    [Fact]
    public async Task A_store_package_is_removed_and_no_longer_listed()
    {
        if (!DeveloperMode())
        {
            output.WriteLine("Developer Mode is off, so no package can be registered. Nothing was tested.");
            return;
        }

        var source = await RegisterAsync();
        Assert.True(Uninstaller.IsStillInstalled(source));
        var plan = Uninstaller.PlanFor("Dashio integration test", source);
        Assert.NotNull(plan);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var outcome = await Uninstaller.StartAsync(plan);
        output.WriteLine($"{outcome.Start} after {watch.ElapsedMilliseconds} ms: {outcome.Error}");

        Assert.Equal(UninstallStart.Removed, outcome.Start);
        Assert.False(Uninstaller.IsStillInstalled(source));
    }
}
