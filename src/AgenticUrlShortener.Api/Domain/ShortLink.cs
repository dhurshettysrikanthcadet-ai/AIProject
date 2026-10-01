namespace AgenticUrlShortener.Api.Domain;

public sealed record ClickEvent(DateTimeOffset ClickedAt);
public sealed record ShortLink(string Code, string Destination, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, long ClickCount, IReadOnlyList<ClickEvent> RecentClicks);
public sealed record DailyClickCount(DateOnly Date, int Clicks);
public sealed record ShortLinkAnalytics(string Code, string Destination, long TotalClicks, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, IReadOnlyList<DailyClickCount> DailyClicks, DateTimeOffset GeneratedAt);
public sealed record CreateLinkRequest(string Destination, DateTimeOffset? ExpiresAt = null);
public sealed record CreateLinkResponse(string Code, string ShortUrl, string Destination, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);