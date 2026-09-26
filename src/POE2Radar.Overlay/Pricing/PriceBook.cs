using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace POE2Radar.Overlay.Pricing;

public readonly record struct PriceResult(string Name, double Exalted, int Quantity, string Category,
    string? Variant = null, bool Corrupted = false, double? TrendPercent = null,
    double? TradedVolumeEx = null, string? BaseType = null, string? DetailsId = null)
{
    public bool IsListingPrice => Category.StartsWith("Unique", StringComparison.Ordinal) || Category == "PrecursorTablets";
    public bool LowConfidence(int minQty) => IsListingPrice && Quantity < minQty;
}

/// <summary>Cached poe.ninja economy estimates. No HTTP runs on the game/render thread.
/// League, currency rates and indexes are published as one immutable snapshot.</summary>
public sealed class PriceBook
{
    private static readonly string[] ExchangeTypes =
        ["Currency", "Runes", "Fragments", "Essences", "Expedition", "Verisium", "Breach", "Ritual",
         "Delirium", "UncutGems", "Abyss", "SoulCores", "LineageSupportGems", "Idols"];
    private static readonly string[] UniqueTypes =
        ["UniqueWeapons", "UniqueArmours", "UniqueAccessories", "UniqueFlasks", "UniqueCharms", "UniqueJewels",
         "UniqueSanctumRelics", "UniqueTablets", "PrecursorTablets"];
    private const string Api = "https://poe.ninja/poe2/api/economy";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly HttpClient SharedHttp = CreateHttp();
    private readonly HttpClient _http;
    private readonly string _cachePath;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, (string Body, string? Etag)> _responses = new();
    private volatile Snapshot _snapshot = new();
    private string? _leagueOverride, _detectedLeague;
    private int _generation;
    private Task? _fetchTask;
    private DateTime _nextAttemptUtc;
    private string _status = "not started";

    public sealed class Snapshot
    {
        public int Version { get; set; } = 3;
        public string League { get; set; } = "";
        public DateTime FetchedUtc { get; set; }
        public double ExPerDivine { get; set; }
        public double ExPerChaos { get; set; }
        public int MarketRowCount { get; set; }
        public int CategoryCount { get; set; }
        public double FetchMilliseconds { get; set; }
        public Dictionary<string, List<PriceResult>> VariantsByName { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, MarketRange> RangesByName { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PriceResult> ByArt { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PriceResult> ByName { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed record MarketRange(int Variants, long Listings, double MinExalted, double MaxExalted);
    public int MarketRowCount => _snapshot.MarketRowCount;
    public int CategoryCount => _snapshot.CategoryCount;
    public double FetchMilliseconds => _snapshot.FetchMilliseconds;

    internal Snapshot Current => _snapshot;
    public string ComparisonLeague { get { lock (_gate) return _leagueOverride ?? _detectedLeague ?? League; } }
    public double ExPerDivine => _snapshot.ExPerDivine;
    public double ExPerChaos => _snapshot.ExPerChaos;
    public bool IsLoaded => _snapshot.ByName.Count > 0;
    public int ItemCount => _snapshot.ByName.Count;
    public string League => _snapshot.League;
    public string Status => Volatile.Read(ref _status);
    public DateTime LastFetchUtc => _snapshot.FetchedUtc;
    public bool IsStale => IsLoaded && DateTime.UtcNow - LastFetchUtc > TimeSpan.FromMinutes(Math.Max(5, RefreshIntervalMinutes));
    public int RefreshIntervalMinutes { get; set; } = 60;

    public PriceBook(string cachePath, string? leagueOverride = null, HttpClient? http = null)
    {
        _cachePath = cachePath;
        _leagueOverride = Clean(leagueOverride);
        _http = http ?? SharedHttp;
        TryLoadCache();
    }

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("POE2Radar/1.0 (+https://github.com/himent12/POE2Radar)");
        return client;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void SetLeagueOverride(string? league)
    {
        lock (_gate)
        {
            var value = Clean(league);
            if (value == _leagueOverride) return;
            _leagueOverride = value;
            InvalidateLeague();
        }
    }

    public void SetDetectedLeague(string? league)
    {
        lock (_gate)
        {
            var value = Clean(league);
            // A transient memory-read failure must not switch the character to the default league.
            if (value == null || value == _detectedLeague) return;
            _detectedLeague = value;
            if (_leagueOverride == null) InvalidateLeague();
        }
    }

    private void InvalidateLeague()
    {
        _generation++;
        var requested = _leagueOverride ?? _detectedLeague;
        if (!string.Equals(requested, League, StringComparison.OrdinalIgnoreCase))
            _snapshot = new(); // never show another league's prices while loading
        _nextAttemptUtc = DateTime.MinValue;
        _status = IsLoaded ? $"cached prices for '{League}'" : "league changed; waiting for prices";
    }

    public void RefreshIfDue() => _ = RefreshAsync();
    public void ForceRefresh() => _ = RefreshAsync(force: true);

    /// <summary>Coalesces simultaneous requests. Manual refresh also respects the network backoff.</summary>
    public Task RefreshAsync(bool force = false)
    {
        lock (_gate)
        {
            if (_fetchTask is { IsCompleted: false }) return _fetchTask;
            var now = DateTime.UtcNow;
            if (now < _nextAttemptUtc || (!force && IsLoaded && !IsStale)) return Task.CompletedTask;
            var requested = _leagueOverride ?? _detectedLeague;
            var generation = _generation;
            _fetchTask = Task.Run(() => FetchAsync(requested, generation));
            return _fetchTask;
        }
    }

    public PriceResult? TryByArt(string? art) => Clean(art) is { } key && _snapshot.ByArt.TryGetValue(key, out var p) ? p : null;
    public PriceResult? TryByName(string? name) => Clean(name) is { } key && _snapshot.ByName.TryGetValue(key, out var p) ? p : null;
    public string Format(double ex) => Format(ex, ExPerDivine);
    internal static string Format(double ex, double rate)
        => rate > 1 && ex >= rate ? $"{ex / rate:0.##} div" : ex is > 0 and < 0.01 ? "<0.01 ex" : $"{ex:0.##} ex";

    private async Task FetchAsync(string? requested, int generation)
    {
        var timer = Stopwatch.StartNew();
        var retry = TimeSpan.FromMinutes(5);
        try
        {
            _status = "fetching market prices…";
            var league = requested;
            if (league == null)
            {
                var leagues = JsonSerializer.Deserialize<List<LeagueDto>>(await GetAsync($"{Api}/leagues"), Json);
                league = leagues?.FirstOrDefault()?.Id;
            }
            if (string.IsNullOrWhiteSpace(league)) throw new InvalidDataException("No market league available");
            var next = new Snapshot { League = league };
            var ambiguousArt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Currency must finish first. Two requests per subsequent batch bounds community API load.
            var batches = new[] { new[] { "Currency" } }.Concat(ExchangeTypes.Skip(1).Concat(UniqueTypes).Chunk(2));
            foreach (var batch in batches)
            {
                lock (_gate) { if (generation != _generation) return; }
                var tasks = batch.Select(type => ReadOverviewAsync(league, type)).ToArray();
                Overview[] responses;
                try { responses = await Task.WhenAll(tasks).ConfigureAwait(false); }
                catch
                {
                    // Inspect every completed request; a sibling failure must not mask Retry-After.
                    var limit = tasks.Where(t => t.Exception != null)
                        .SelectMany(t => t.Exception!.Flatten().InnerExceptions).OfType<RateLimitException>()
                        .OrderByDescending(e => e.Delay).FirstOrDefault();
                    if (limit != null) throw limit;
                    throw;
                }
                for (int index = 0; index < batch.Length; index++)
                {
                    var type = batch[index];
                    var exchange = ExchangeTypes.Contains(type);
                    var data = responses[index];
                    next.CategoryCount++;
                    // Category responses may have older cached exchange rates. Normalize all categories
                    // to this snapshot's Currency overview so an 18-divine listing still displays as 18 div.
                    var rate = data.Core?.Primary switch
                    {
                        "divine" when next.ExPerDivine > 0 => next.ExPerDivine,
                        "chaos" when next.ExPerChaos > 0 => next.ExPerChaos,
                        _ => ExaltedPerPrimary(data.Core),
                    };
                    // Never assume a missing rate is 1 or reuse a rate from a different reference currency.
                    if (rate <= 0) throw new InvalidDataException($"Missing currency conversion for {type}");
                    if (type == "Currency")
                    {
                        next.ExPerDivine = CurrencyValue(data.Core!, "divine", rate);
                        next.ExPerChaos = CurrencyValue(data.Core!, "chaos", rate);
                    }
                    var metadata = (data.Items ?? []).Concat(data.Core?.Items ?? [])
                        .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
                    foreach (var line in data.Lines!)
                    {
                        string? name = line.Name, icon = line.Icon;
                        if (exchange)
                        {
                            if (line.Id.ValueKind != JsonValueKind.String || !metadata.TryGetValue(line.Id.GetString()!, out var item)) continue;
                            name = item.Name; icon = item.Image;
                        }
                        var value = line.PrimaryValue * rate;
                        if (string.IsNullOrWhiteSpace(name) || !double.IsFinite(value) || value <= 0) continue;
                        // Exchange volume is a currency value, not a count of item listings.
                        var result = new PriceResult(name.Trim(), value, exchange ? 0 : Math.Max(0, line.ListingCount ?? 0),
                            type, line.Variant, line.Corrupted, Finite(line.SparkLine?.TotalChange),
                            exchange ? Positive(line.VolumePrimaryValue * rate) : null, line.BaseType, line.DetailsId);
                        next.MarketRowCount++;
                        if (!next.VariantsByName.TryGetValue(result.Name, out var variants))
                            next.VariantsByName[result.Name] = variants = [];
                        variants.Add(result);
                        Upsert(next.ByName, result.Name, result);
                        if (!exchange && ArtBasename(icon) is { } art && !ambiguousArt.Contains(art))
                        {
                            if (next.ByArt.TryGetValue(art, out var prior) && prior.Name != result.Name)
                            {
                                next.ByArt.Remove(art); ambiguousArt.Add(art);
                            }
                            else Upsert(next.ByArt, art, result);
                        }
                    }
                }
            }
            foreach (var (name, variants) in next.VariantsByName)
                next.RangesByName[name] = new(variants.Count, variants.Sum(v => (long)v.Quantity),
                    variants.Min(v => v.Exalted), variants.Max(v => v.Exalted));
            next.FetchMilliseconds = timer.Elapsed.TotalMilliseconds;
            if (next.ByName.Count == 0) throw new InvalidDataException("Market returned no prices");
            next.FetchedUtc = DateTime.UtcNow;
            lock (_gate)
            {
                if (generation != _generation) return; // league changed during the request
                _snapshot = next;
                _nextAttemptUtc = DateTime.UtcNow.AddMinutes(5);
                _status = $"loaded {next.ByName.Count} items for '{league}'";
            }
            // Disk serialization is independent of readers; never hold the game-thread gate during IO.
            SaveCache(next);
        }
        catch (RateLimitException ex)
        {
            retry = ex.Delay > retry ? ex.Delay : retry;
            if (generation == _generation) _status = "market rate limited; retrying later";
        }
        catch (Exception ex)
        {
            if (generation == _generation) _status = $"price refresh failed: {ex.Message}";
        }
        finally
        {
            lock (_gate)
                if (generation == _generation && _nextAttemptUtc < DateTime.UtcNow)
                    _nextAttemptUtc = DateTime.UtcNow.Add(retry);
        }
    }

    private async Task<Overview> ReadOverviewAsync(string league, string type)
    {
        var route = ExchangeTypes.Contains(type) ? "exchange/current" : "stash/current/item";
        var body = await GetAsync($"{Api}/{route}/overview?league={Uri.EscapeDataString(league)}&type={type}");
        var data = JsonSerializer.Deserialize<Overview>(body, Json);
        if (data?.Lines == null) throw new InvalidDataException($"Missing market price rows for {type}");
        return data;
    }

    private static double? Finite(double? value) => value.HasValue && double.IsFinite(value.Value) ? value : null;
    private static double? Positive(double? value) => Finite(value) is > 0 ? value : null;

    private async Task<string> GetAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        _responses.TryGetValue(url, out var cached);
        if (cached.Etag != null) request.Headers.TryAddWithoutValidation("If-None-Match", cached.Etag);
        using var response = await _http.SendAsync(request).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified && cached.Body != null) return cached.Body;
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new RateLimitException(response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromMinutes(5));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (_responses.Count > 100) _responses.Clear();
        _responses[url] = (body, response.Headers.ETag?.ToString());
        return body;
    }

    private sealed class RateLimitException(TimeSpan delay) : Exception { public TimeSpan Delay { get; } = delay; }
    private static double ExaltedPerPrimary(Core? core)
    {
        if (core?.Primary == "exalted") return 1;
        return core?.Rates?.TryGetValue("exalted", out var rate) == true && double.IsFinite(rate) && rate > 0 ? rate : 0;
    }
    private static double CurrencyValue(Core core, string currency, double rate)
    {
        if (core.Primary == currency) return rate;
        return core.Rates?.TryGetValue(currency, out var units) == true && double.IsFinite(units) && units > 0 ? rate / units : 0;
    }
    private static void Upsert(Dictionary<string, PriceResult> map, string key, PriceResult item)
    {
        // Representative market row: most listings; cheaper on ties, never the highest outlier.
        if (!map.TryGetValue(key, out var current) || item.Quantity > current.Quantity
            || (item.Quantity == current.Quantity && item.Exalted < current.Exalted)) map[key] = item;
    }
    private static string? ArtBasename(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        return Path.GetFileNameWithoutExtension(icon.Split('?')[0]);
    }

    private void TryLoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            var body = File.ReadAllText(_cachePath);
            var data = JsonSerializer.Deserialize<Snapshot>(body, Json);
            // Older caches do not retain market variants and trend/volume evidence.
            using var raw = JsonDocument.Parse(body);
            if (!raw.RootElement.TryGetProperty("Version", out var version) || version.GetInt32() != 3 || data == null) return;
            if (_leagueOverride != null && !string.Equals(data.League, _leagueOverride, StringComparison.OrdinalIgnoreCase)) return;
            if (data.FetchedUtc > DateTime.UtcNow || string.IsNullOrWhiteSpace(data.League)
                || !double.IsFinite(data.ExPerDivine) || data.ExPerDivine <= 0
                || data.ByName.Values.Concat(data.ByArt.Values).Any(p => !double.IsFinite(p.Exalted) || p.Exalted <= 0)) return;
            data.ByName = new(data.ByName, StringComparer.OrdinalIgnoreCase);
            data.ByArt = new(data.ByArt, StringComparer.OrdinalIgnoreCase);
            data.VariantsByName = new(data.VariantsByName, StringComparer.OrdinalIgnoreCase);
            data.RangesByName = new(data.RangesByName, StringComparer.OrdinalIgnoreCase);
            _snapshot = data;
            _status = $"cached prices for '{data.League}'";
        }
        catch { _status = "price cache unavailable; will fetch"; }
    }
    private void SaveCache(Snapshot snapshot)
    {
        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_cachePath + ".tmp", JsonSerializer.Serialize(snapshot, Json));
            File.Move(_cachePath + ".tmp", _cachePath, overwrite: true);
        }
        catch { /* Pricing still works if the cache directory is read-only. */ }
    }
    private sealed class LeagueDto { public string Id { get; set; } = ""; }
    private sealed class Overview
    {
        public Core? Core { get; set; }
        public List<Line>? Lines { get; set; }
        public List<Item>? Items { get; set; }
    }
    private sealed class Core
    {
        public string? Primary { get; set; }
        public Dictionary<string, double>? Rates { get; set; }
        public List<Item>? Items { get; set; }
    }
    private sealed class Item
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Image { get; set; }
    }
    private sealed class SparkLine { public double? TotalChange { get; set; } }
    private sealed class Line
    {
        public JsonElement Id { get; set; }
        public string? Name { get; set; }
        public string? Icon { get; set; }
        public double PrimaryValue { get; set; }
        public int? ListingCount { get; set; }
        public string? Variant { get; set; }
        public bool Corrupted { get; set; }
        public double? VolumePrimaryValue { get; set; }
        public SparkLine? SparkLine { get; set; }
        public string? BaseType { get; set; }
        public string? DetailsId { get; set; }
    }
}
