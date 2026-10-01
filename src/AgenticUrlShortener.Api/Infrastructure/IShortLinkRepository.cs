using AgenticUrlShortener.Api.Domain;

namespace AgenticUrlShortener.Api.Infrastructure;

public interface IShortLinkRepository
{
    Task<bool> TryCreateAsync(ShortLink link, CancellationToken cancellationToken);
    Task<ShortLink?> FindAsync(string code, CancellationToken cancellationToken);
    Task<ShortLink?> RecordClickAsync(string code, DateTimeOffset clickedAt, CancellationToken cancellationToken);
}