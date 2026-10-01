using System.Text.Json;
using AgenticUrlShortener.Api.Domain;

namespace AgenticUrlShortener.Api.Infrastructure;

public sealed class JsonShortLinkRepository : IShortLinkRepository
{
    private const int RecentClickLimit = 1000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ShortLink> _links;

    public JsonShortLinkRepository(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
        if (!File.Exists(_filePath))
        {
            _links = new Dictionary<string, ShortLink>(StringComparer.Ordinal);
            return;
        }

        var links = JsonSerializer.Deserialize<List<ShortLink>>(File.ReadAllText(_filePath), JsonOptions) ?? [];
        _links = links.ToDictionary(link => link.Code, StringComparer.Ordinal);
    }

    public async Task<bool> TryCreateAsync(ShortLink link, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_links.ContainsKey(link.Code)) return false;
            _links.Add(link.Code, link);
            try { await SaveAsync(cancellationToken); }
            catch
            {
                _links.Remove(link.Code);
                throw;
            }
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<ShortLink?> FindAsync(string code, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return _links.GetValueOrDefault(code); }
        finally { _gate.Release(); }
    }

    public async Task<ShortLink?> RecordClickAsync(string code, DateTimeOffset clickedAt, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_links.TryGetValue(code, out var link)) return null;
            var recentClicks = link.RecentClicks.Append(new ClickEvent(clickedAt)).TakeLast(RecentClickLimit).ToArray();
            var updated = link with { ClickCount = link.ClickCount + 1, RecentClicks = recentClicks };
            _links[code] = updated;
            try { await SaveAsync(cancellationToken); }
            catch
            {
                _links[code] = link;
                throw;
            }
            return updated;
        }
        finally { _gate.Release(); }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, _links.Values, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, _filePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}