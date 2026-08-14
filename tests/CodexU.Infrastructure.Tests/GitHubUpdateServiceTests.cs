using System.Net;
using System.Text;
using CodexU.Infrastructure;

namespace CodexU.Infrastructure.Tests;

public sealed class GitHubUpdateServiceTests
{
    [Fact]
    public async Task CheckAsync_DetectsStableReleaseAfterSameCorePrerelease()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-update-{Guid.NewGuid():N}");
        try
        {
            using var client = new HttpClient(new StaticHandler(HttpStatusCode.OK,
                """{"tag_name":"v0.3.0","name":"codexU 0.3.0","html_url":"https://example.test/release","published_at":"2026-07-14T00:00:00Z","draft":false,"prerelease":false}"""));
            using var service = new GitHubUpdateService(client, root);

            var result = await service.CheckAsync("0.3.0-beta.2", includePrereleases: false, force: true);

            Assert.True(result.IsUpdateAvailable);
            Assert.Equal("0.3.0", result.LatestVersion);
            Assert.Equal("https://example.test/release", result.ReleaseUrl);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CheckAsync_ExplainsPrivateReleaseAuthenticationFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codexu-update-private-{Guid.NewGuid():N}");
        try
        {
            using var client = new HttpClient(new StaticHandler(HttpStatusCode.NotFound, "{}"));
            using var service = new GitHubUpdateService(client, root);

            var result = await service.CheckAsync("0.2.0", includePrereleases: false, force: true);

            Assert.False(result.IsUpdateAvailable);
            Assert.Contains("CODEXU_GITHUB_TOKEN", result.Status, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StaticHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(
                new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(content, Encoding.UTF8, "application/json"),
                    RequestMessage = request
                });
    }
}
