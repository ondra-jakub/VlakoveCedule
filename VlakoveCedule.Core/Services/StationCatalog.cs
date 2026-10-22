using VlakoveCedule.Contracts;

namespace VlakoveCedule.Core.Services;

/// <summary>Katalog stanic: mapa název → písmeno (bucket) plněná prefetchem + vyhledávání nad ní.</summary>
public sealed class StationCatalog
{
    private readonly object _bucketsLock = new();
    private readonly Dictionary<string, string> _buckets = new(StringComparer.OrdinalIgnoreCase);

    public int Count
    {
        get { lock (_bucketsLock) return _buckets.Count; }
    }

    public void Remember(LetterListing listing)
    {
        lock (_bucketsLock)
            foreach (var s in listing.Stations)
                _buckets[s.Name] = listing.Bucket;
    }

    public string? BucketFor(string name)
    {
        lock (_bucketsLock)
            return _buckets.TryGetValue(name, out var b) ? b : null;
    }

    public StationSearchResult Search(string query)
    {
        var q = query.Trim();
        if (q.Length == 0)
            return new StationSearchResult([], 0);

        List<(string name, string bucket)> matches;
        int total;
        lock (_bucketsLock)
        {
            matches = _buckets
                .Where(kv => kv.Key.Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderBy(kv => kv.Key, StringComparer.CurrentCulture)
                .Take(200)
                .Select(kv => (kv.Key, kv.Value))
                .ToList();
            total = _buckets.Keys.Count(k => k.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        return new StationSearchResult(
            matches.Select(m => new Station { Name = m.name, ClientId = "", Bucket = m.bucket }).ToList(),
            total);
    }
}
