using AgenticUrlShortener.Api.Domain;
using AgenticUrlShortener.Api.Infrastructure;
using AgenticUrlShortener.Api.Services;

namespace AgenticUrlShortener.Tests;

public sealed class ShortLinkServiceTests
{
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:password@example.com/path")]
    [InlineData("not a url")]
    public async Task CreateAsync_RejectsUnsafeOrMalformedDestination(string destination)
    {
        var repository = new InMemoryShortLinkRepository();
        var service = new ShortLinkService(repository);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new CreateLinkRequest(destination), CancellationToken.None));
        Assert.Empty(repository.Links);
    }

    [Fact]
    public async Task CreateResolveAndReload_PersistsClicksAndAnalytics()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(directory, "links.json");
        try
        {
            var repository = new JsonShortLinkRepository(filePath);
            var service = new ShortLinkService(repository);
            var created = await service.CreateAsync(new CreateLinkRequest("https://example.com/a?b=c"), CancellationToken.None);

            Assert.Matches("^[A-Za-z0-9_-]{12}$", created.Code);
            Assert.Equal("https://example.com/a?b=c", await service.ResolveAsync(created.Code, CancellationToken.None));
            var analytics = await new ShortLinkService(new JsonShortLinkRepository(filePath)).GetAnalyticsAsync(created.Code, CancellationToken.None);

            Assert.NotNull(analytics);
            Assert.Equal(1, analytics.TotalClicks);
            Assert.Equal(1, Assert.Single(analytics.DailyClicks).Clicks);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNullForExpiredLink()
    {
        var repository = new InMemoryShortLinkRepository();
        await repository.TryCreateAsync(new ShortLink("expired", "https://example.com", DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1), 0, []), CancellationToken.None);

        Assert.Null(await new ShortLinkService(repository).ResolveAsync("expired", CancellationToken.None));
        Assert.Equal(0, (await repository.FindAsync("expired", CancellationToken.None))!.ClickCount);
    }

    private sealed class InMemoryShortLinkRepository : IShortLinkRepository
    {
        public Dictionary<string, ShortLink> Links { get; } = new(StringComparer.Ordinal);

        public Task<bool> TryCreateAsync(ShortLink link, CancellationToken cancellationToken) => Task.FromResult(Links.TryAdd(link.Code, link));
        public Task<ShortLink?> FindAsync(string code, CancellationToken cancellationToken) => Task.FromResult(Links.GetValueOrDefault(code));

        public Task<ShortLink?> RecordClickAsync(string code, DateTimeOffset clickedAt, CancellationToken cancellationToken)
        {
            if (!Links.TryGetValue(code, out var link)) return Task.FromResult<ShortLink?>(null);
            var updated = link with { ClickCount = link.ClickCount + 1, RecentClicks = link.RecentClicks.Append(new ClickEvent(clickedAt)).ToArray() };
            Links[code] = updated;
            return Task.FromResult<ShortLink?>(updated);
        }
    }
}