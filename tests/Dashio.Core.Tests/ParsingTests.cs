using Dashio.Core.Parsing;

namespace Dashio.Core.Tests;

public class StartupApprovedCodecTests
{
    [Theory]
    [InlineData(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 3, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, false)]
    [InlineData(new byte[] { 7, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, false)]
    [InlineData(new byte[] { 3 }, false)]
    [InlineData(new byte[0], true)]
    [InlineData(null, true)]
    public void Reads_the_enabled_flag(byte[]? value, bool expected) =>
        Assert.Equal(expected, StartupApprovedCodec.IsEnabled(value));

    [Fact]
    public void Disabled_round_trips_and_carries_the_time()
    {
        var now = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
        var bytes = StartupApprovedCodec.Encode(enabled: false, now);
        Assert.Equal(12, bytes.Length);
        Assert.False(StartupApprovedCodec.IsEnabled(bytes));
        Assert.Equal(now.ToFileTimeUtc(), BitConverter.ToInt64(bytes, 4));
    }

    [Fact]
    public void Enabled_round_trips()
    {
        var bytes = StartupApprovedCodec.Encode(enabled: true, DateTime.UtcNow);
        Assert.Equal(12, bytes.Length);
        Assert.True(StartupApprovedCodec.IsEnabled(bytes));
    }
}

public class InfParserTests
{
    [Fact]
    public void Resolves_string_table_references()
    {
        const string inf = """
            ; comment
            [Version]
            Signature = "$WINDOWS NT$"
            Class     = SoftwareComponent
            Provider  = %ManufacturerName% ; trailing comment

            [Strings]
            ManufacturerName = "Rivet Networks"
            """;
        var info = InfParser.Parse(inf);
        Assert.Equal("Rivet Networks", info.Provider);
        Assert.Equal("SoftwareComponent", info.Class);
    }

    [Fact]
    public void Reads_literal_quoted_provider()
    {
        var info = InfParser.Parse("[version]\nprovider=\"Acer Incorporated\"\n");
        Assert.Equal("Acer Incorporated", info.Provider);
    }

    [Fact]
    public void Missing_version_section_gives_nulls()
    {
        var info = InfParser.Parse("[Strings]\nA=\"b\"\n");
        Assert.Null(info.Provider);
        Assert.Null(info.Class);
    }

    [Theory]
    [InlineData("DiskDesc = \"Predator Service Installation Disk\"", "Predator Service")]
    [InlineData("DiskId1 = \"Realtek Service Installation Disk\"", "Realtek Service")]
    [InlineData("DiskName = \"Acer ART AIMMX Installation Disk\"", "Acer ART AIMMX")]
    [InlineData("DiskId = \"Intel(R) Dynamic Application Loader Host Interface Installation Disk\"",
        "Intel(R) Dynamic Application Loader Host Interface")]
    [InlineData("DiskDesc = \"Disk\"", null)]
    [InlineData("Other = \"Something Installation Disk\"", null)]
    public void Reads_a_description_from_the_disk_label(string stringsLine, string? expected)
    {
        var info = InfParser.Parse("[Version]\nProvider=\"X\"\n[Strings]\n" + stringsLine + "\n");
        Assert.Equal(expected, info.Description);
    }

    [Fact]
    public void Unresolvable_reference_gives_null()
    {
        var info = InfParser.Parse("[Version]\nProvider=%Missing%\n");
        Assert.Null(info.Provider);
    }
}

public class NameTokensTests
{
    [Theory]
    [InlineData("Intel® Corporation", "intel")]
    [InlineData("INTEL CORP", "intel")]
    [InlineData("Intel(R) Corporation", "intel")]
    [InlineData("Google LLC", "google")]
    [InlineData("Valve Corp.", "valve")]
    [InlineData("Rivet Networks", "rivet networks")]
    [InlineData("RivetNetworks", "rivet networks")]
    [InlineData("NVIDIA Corporation", "nvidia")]
    [InlineData("Acer Incorporated", "acer")]
    [InlineData("BattlEye Innovations e.K.", "battl eye innovations")]
    [InlineData(null, "")]
    public void Normalises_publishers(string? input, string expected) =>
        Assert.Equal(expected, NameTokens.NormalizePublisher(input));

    [Theory]
    [InlineData("intel", "intel", true)]
    [InlineData("realtek", "realtek semiconductor", true)]
    [InlineData("adobe systems", "adobe", true)]
    [InlineData("intel", "interl", false)]
    [InlineData("", "intel", true)]
    [InlineData("acer", "intel", false)]
    public void Publisher_compatibility(string a, string b, bool expected) =>
        Assert.Equal(expected, NameTokens.PublishersCompatible(a, b));

    [Fact]
    public void Distinctive_tokens_drop_filler_and_the_own_publisher()
    {
        var tokens = NameTokens.DistinctiveTokens("Intel® Killer™ Performance Suite", "intel");
        Assert.Equal(["killer"], tokens);
    }

    [Fact]
    public void Distinctive_tokens_split_package_style_names()
    {
        var tokens = NameTokens.DistinctiveTokens("RivetNetworks.KillerControlCenter", "rivet networks", splitCamelCase: true);
        Assert.Equal(["killer"], tokens);
    }

    [Fact]
    public void Generic_product_words_are_not_distinctive()
    {
        Assert.Empty(NameTokens.DistinctiveTokens("Driver and Support Assistant", "intel"));
        Assert.Empty(NameTokens.DistinctiveTokens("Epic Games Launcher", "epic games"));
    }

    [Fact]
    public void Display_names_are_not_split_so_similar_brands_do_not_collide()
    {
        Assert.Equal(["powertoys"], NameTokens.DistinctiveTokens("PowerToys (Preview) x64", "microsoft"));
        Assert.Equal(["powershell"], NameTokens.DistinctiveTokens("PowerShell 7-x64", "microsoft"));
    }

    [Theory]
    [InlineData("Everything 1.5.0.1396a (x64)", "Everything")]
    [InlineData("Ollama version 0.18.2", "Ollama")]
    [InlineData("Sandboxie-Plus v1.17.2", "Sandboxie-Plus")]
    [InlineData("LibreOffice 26.2.2.2", "LibreOffice")]
    [InlineData("PostgreSQL 17", "PostgreSQL 17")]
    [InlineData("Microsoft SQL Server 2022 (64-bit)", "Microsoft SQL Server 2022")]
    [InlineData("PowerShell 7.6.6.0-x64", "PowerShell")]
    [InlineData("Zen Browser (x64 en-US)", "Zen Browser")]
    [InlineData("Intel® Driver && Support Assistant", "Intel Driver & Support Assistant")]
    [InlineData("7-Zip 24.08 (x64)", "7-Zip")]
    public void Cleans_app_names(string input, string expected) =>
        Assert.Equal(expected, NameTokens.CleanAppName(input));

    [Theory]
    [InlineData("Microsoft Corporation", "Microsoft")]
    [InlineData("Acer Incorporated", "Acer")]
    [InlineData("Epic Games, Inc.", "Epic Games")]
    [InlineData("Intel", "Intel")]
    public void Shortens_publishers(string input, string expected) =>
        Assert.Equal(expected, NameTokens.ShortPublisher(input));

    [Theory]
    [InlineData("Spotify", "spotify", "spotify")]
    [InlineData("Seelen UI", "seelen", "seelen")]
    [InlineData("NVIDIA App", "nvidia", "nvidia")]
    [InlineData("Intel Graphics Software", "intel", "")]
    [InlineData("Intel Killer", "intel", "")]
    [InlineData("Steam", "valve", "")]
    public void Brand_tokens_only_for_products_named_after_their_maker(string name, string publisher, string expected) =>
        Assert.Equal(expected, string.Join(' ', NameTokens.BrandTokens(name, publisher)));

    [Theory]
    [InlineData("Intel® Killer™ Performance Suite", "Intel Killer Performance Suite")]
    [InlineData("Intel(R) Graphics", "Intel Graphics")]
    public void Cleans_display_names(string input, string expected) =>
        Assert.Equal(expected, NameTokens.CleanDisplayName(input));
}
