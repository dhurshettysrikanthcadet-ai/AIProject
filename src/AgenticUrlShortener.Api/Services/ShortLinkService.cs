using System.Security.Cryptography;
using AgenticUrlShortener.Api.Domain;
using AgenticUrlShortener.Api.Infrastructure;

namespace AgenticUrlShortener.Api.Services;

public sealed class ShortLinkService(IShortLinkRepository repository)
{
    private const int MaximumDestinationLength = 2048;

    public async Task<ShortLink> CreateAsync(CreateLinkRequest request, CancellationToken cancellationToken)
    {
        if (request.Destination is null || request.Destination.Length > MaximumDestinationLength ||
            !Uri.TryCreate(request.Destination, UriKind.Absolute, out var destination) ||
            (destination.Scheme != Uri.UriSchemeHttp && destination.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(destination.UserInfo))
            throw new ArgumentException("Destination must be an absolute HTTP(S) URL without embedded credentials.");

        var now = DateTimeOffset.UtcNow;
        if (request.ExpiresAt is { } expiresAt && expiresAt <= now) throw new ArgumentException("Expiry must be in the future.");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(9)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var link = new ShortLink(code, destination.AbsoluteUri, now, request.ExpiresAt, 0, []);
            if (await repository.TryCreateAsync(link, cancellationToken)) return link;
        }

        throw new InvalidOperationException("Could not allocate a unique short code.");
    }

    public async Task<string?> ResolveAsync(string code, CancellationToken cancellationToken)
    {
        var link = await repository.FindAsync(code, cancellationToken);
        if (link is null || link.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow) return null;
        await repository.RecordClickAsync(code, DateTimeOffset.UtcNow, cancellationToken);
        return link.Destination;
    }

    public async Task<ShortLinkAnalytics?> GetAnalyticsAsync(string code, CancellationToken cancellationToken)
    {
        var link = await repository.FindAsync(code, cancellationToken);
        if (link is null) return null;
        var dailyClicks = link.RecentClicks
            .Where(click => click.ClickedAt >= DateTimeOffset.UtcNow.AddDays(-30))
            .GroupBy(click => DateOnly.FromDateTime(click.ClickedAt.UtcDateTime))
            .Select(group => new DailyClickCount(group.Key, group.Count()))
            .OrderByDescending(item => item.Date)
            .ToArray();
        return new ShortLinkAnalytics(link.Code, link.Destination, link.ClickCount, link.CreatedAt, link.ExpiresAt, dailyClicks, DateTimeOffset.UtcNow);
    }
}