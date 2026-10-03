using System.Net;
using Dashio.Core.Updates;

namespace Dashio.Core.Tests;

public class UpdateCheckerTests
{
    private const string Releases = """
        [
          { "tag_name": "v0.2.0", "draft": false, "prerelease": true },
          { "tag_name": "v0.10.1", "draft": false, "prerelease": true },
          { "tag_name": "v0.9.0", "draft": false, "prerelease": false },
          { "tag_name": "v1.0.0", "draft": true },
          { "tag_name": "nightly", "draft": false }
        ]
        """;

    /// <summary>Answers every request from a fixed text, so no test touches the network.</summary>
    private sealed class FixedAnswer(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Asked { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    [Fact]
    public void The_newest_release_is_the_highest_version_not_the_first_listed()
    {
        var newest = UpdateChecker.Newest(Releases);

        Assert.Equal(new Version(0, 10, 1), newest?.Version);
        Assert.Equal("https://github.com/EverythingSuckz/Dashio/releases/tag/v0.10.1", newest?.Page);
    }

    [Fact]
    public void Drafts_and_tags_that_are_not_versions_are_passed_over()
    {
        Assert.Null(UpdateChecker.Newest("""[ { "tag_name": "v1.0.0", "draft": true }, { "tag_name": "nightly" } ]"""));
        Assert.Null(UpdateChecker.Newest("[]"));
        Assert.Null(UpdateChecker.Newest("""{ "message": "Not Found" }"""));
    }

    [Theory]
    [InlineData("0.1.0", true)]
    [InlineData("0.10.1", false)]
    [InlineData("1.0.0", false)]
    public async Task A_release_is_newer_only_when_its_version_is_higher(string current, bool newer)
    {
        var answer = new FixedAnswer(HttpStatusCode.OK, Releases);

        var result = await new UpdateChecker(answer).CheckAsync(Version.Parse(current));

        Assert.Null(result.Error);
        Assert.Equal(newer, result.IsNewer);
        Assert.Equal("api.github.com", answer.Asked?.RequestUri?.Host);
        Assert.Equal("Dashio", answer.Asked?.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task A_refusal_or_a_broken_answer_is_reported_and_never_thrown()
    {
        var refused = await new UpdateChecker(new FixedAnswer(HttpStatusCode.Forbidden, "")).CheckAsync(new Version(0, 1, 0));
        var broken = await new UpdateChecker(new FixedAnswer(HttpStatusCode.OK, "<html>")).CheckAsync(new Version(0, 1, 0));

        Assert.NotNull(refused.Error);
        Assert.False(refused.IsNewer);
        Assert.NotNull(broken.Error);
    }
}
