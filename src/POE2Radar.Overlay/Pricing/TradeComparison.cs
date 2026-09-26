using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using POE2Radar.Core.Game;

namespace POE2Radar.Overlay.Pricing;

/// <summary>One online listing. <see cref="Exalted"/> is null when its currency has no known rate.</summary>
public sealed record MarketListing(double Amount, string Currency, double? Exalted, string Seller, DateTime? Indexed, int Stack);

/// <summary>A trade query built once the stat catalogue is known: the JSON to POST (null = nothing searchable), a
/// human note on what was matched, and an optional looser <see cref="Next"/> tried when this one finds nothing —
/// so a rare with no exact twin on the market still gets priced against its closest matches. <see cref="Comparable"/>
/// is false for the last-resort "every item of this base" step: its listings say nothing about this item's price.</summary>
public sealed record QueryPlan(string? Query, string Note, QueryPlan? Next = null, bool Comparable = true);

/// <summary>One of an item's mod lines matched to a trade-site stat id; <see cref="Value"/> is the roll (the average
/// for "# to #" ranges), null for mods without a number.</summary>
public sealed record MatchedStat(string Id, double? Value, string Text);

/// <summary>Result of an explicit price-check search (cheapest listings first, one per seller). <see cref="Comparable"/>:
/// the listings came from a step that compared the item's stats (see <see cref="QueryPlan.Comparable"/>).</summary>
public sealed record MarketSearch(string Status, IReadOnlyList<MarketListing> Listings, int Total, string? Url, string Note,
    bool Pending = false, string? Error = null, bool Comparable = true);

/// <summary>Online trade-site lookups for the price check: one request at a time, rate-limit headers honoured, results
/// cached per item. Rares are matched on their actual modifiers, stepping down to the closest similar items.</summary>
public sealed class TradeComparison
{
    private const string Api = "https://www.pathofexile.com/api/trade2";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private Task? _pending;
    private DateTime _nextRequest;
    private List<TradeStat>? _stats;
    private readonly Dictionary<string, (MarketSearch Value, DateTime Until)> _searches = new();
    private DateTime _nextExplicit;
    private DateTime _blockedUntil;
    public Task Pending { get { lock (_gate) return _pending ?? Task.CompletedTask; } }
    public TradeComparison(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        if (http == null) _http.DefaultRequestHeaders.UserAgent.ParseAdd("POE2Radar/1.0 (+https://github.com/himent12/POE2Radar)");
    }

    /// <summary>
    /// Explicit price check (a user keypress, not a hover): run <paramref name="plan"/> against the trade site and
    /// return the cheapest online listings. Shares the one-request-at-a-time worker and rate-limit backoff with the
    /// hover comparison, but only enforces a short gap between searches. Results are cached 5 minutes per
    /// <paramref name="key"/>; <paramref name="refresh"/> forces a new search. Poll it: while a search is running
    /// it returns a <see cref="MarketSearch.Pending"/> placeholder.
    /// </summary>
    public MarketSearch GetOrQueueSearch(string key, Func<IReadOnlyList<TradeStat>?, QueryPlan> plan, bool needsStats,
        PriceBook prices, bool refresh = false)
    {
        var market = prices.Current;
        var league = prices.ComparisonLeague;
        if (league.Length == 0) return new("Set the price league on the dashboard (Item Value)", [], 0, null, "");
        if (market.League != league || market.ExPerDivine <= 0) return new("Loading currency rates…", [], 0, null, "", Pending: true);
        var full = league + "\n" + key;
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (!refresh && _searches.TryGetValue(full, out var cached) && cached.Until > now) return cached.Value;
            if (now < _blockedUntil)
                return new($"Trade site asked us to wait {Math.Ceiling((_blockedUntil - now).TotalSeconds)}s", [], 0, null, "", Error: "rate limited");
            if (_pending is { IsCompleted: false } || now < _nextExplicit)
                return new("Waiting for the previous lookup…", [], 0, null, "", Pending: true);
            _nextExplicit = now.AddSeconds(2);
            var placeholder = new MarketSearch("Searching the trade site…", [], 0, null, "", Pending: true);
            if (_searches.Count >= 64) _searches.Clear();
            _searches[full] = (placeholder, now.AddMinutes(1));
            _pending = Task.Run(async () =>
            {
                MarketSearch result;
                try
                {
                    if (needsStats) await EnsureStatsAsync();
                    var p = plan(_stats);
                    result = p.Query is null
                        ? new("Nothing to search for", [], 0, null, p.Note)
                        : await SearchLadderAsync(p, league, market);
                }
                catch (Exception ex) { result = new("Trade lookup failed", [], 0, null, "", Error: ex.Message); }
                lock (_gate) _searches[full] = (result, DateTime.UtcNow.AddMinutes(result.Error is null ? 5 : 0.5));
            });
            return placeholder;
        }
    }

    private async Task EnsureStatsAsync()
    {
        if (_stats != null) return;
        using var stats = await Request(new(HttpMethod.Get, Api + "/data/stats"));
        _stats = stats.RootElement.GetProperty("result").EnumerateArray()
            .SelectMany(g => g.GetProperty("entries").EnumerateArray())
            .Select(e => new TradeStat(e.GetProperty("id").GetString()!, e.GetProperty("text").GetString()!))
            .Where(s => s.Id.StartsWith("explicit.") || s.Id.StartsWith("implicit.")).ToList();
    }

    /// <summary>Enough listings at one step of the ladder to price from; fewer and the next, looser step is tried.</summary>
    public const int MinComparable = 3;

    /// <summary>
    /// Walk a plan and its looser fallbacks (at most 5 searches). The first comparable step with at least
    /// <see cref="MinComparable"/> results wins; if none gets there, the comparable step with the most results
    /// (earliest on a tie) — even one or two real comparables say more than hundreds of unrelated listings. The
    /// non-comparable last resort (<see cref="QueryPlan.Comparable"/> false) is searched only when no comparable step
    /// found anything. Only the chosen step's listings are fetched, so a ladder costs searches, not fetches.
    /// </summary>
    private async Task<MarketSearch> SearchLadderAsync(QueryPlan plan, string league, PriceBook.Snapshot market)
    {
        (QueryPlan Plan, string Id, string[] Ids, int Total, int Step)? best = null;
        var step = 0;
        for (var p = plan; p is not null && step < 5; p = p.Next)
        {
            if (p.Query is null) continue;
            if (!p.Comparable && best is { Total: > 0 }) break;
            step++;
            var (id, ids, total) = await SearchIdsAsync(p.Query, league);
            if (!p.Comparable || total >= MinComparable) { best = (p, id, ids, total, step); break; }
            if (best is null || total > best.Value.Total) best = (p, id, ids, total, step);
        }
        if (best is not { } b) return new("Nothing to search for", [], 0, null, plan.Note);
        var note = b.Step == 1 || !b.Plan.Comparable ? b.Plan.Note : "Closest match — " + b.Plan.Note;
        return await FetchListingsAsync(b.Id, b.Ids, b.Total, league, market, note) with { Comparable = b.Plan.Comparable };
    }

    private async Task<(string Id, string[] Ids, int Total)> SearchIdsAsync(string query, string league)
    {
        using var search = await Request(new(HttpMethod.Post, $"{Api}/search/poe2/{Uri.EscapeDataString(league)}")
        { Content = new StringContent(query, System.Text.Encoding.UTF8, "application/json") });
        var id = search.RootElement.GetProperty("id").GetString()!;
        var ids = search.RootElement.GetProperty("result").EnumerateArray().Take(10).Select(e => e.GetString()!).ToArray();
        var total = search.RootElement.TryGetProperty("total", out var t) && t.TryGetInt32(out var n) ? n : ids.Length;
        return (id, ids, total);
    }

    private async Task<MarketSearch> FetchListingsAsync(string id, string[] ids, int total, string league, PriceBook.Snapshot market, string note)
    {
        var url = $"https://www.pathofexile.com/trade2/search/poe2/{Uri.EscapeDataString(league)}/{Uri.EscapeDataString(id)}";
        if (ids.Length == 0) return new("No listings online", [], 0, url, note);
        using var fetched = await Request(new(HttpMethod.Get,
            $"{Api}/fetch/{string.Join(',', ids.Select(Uri.EscapeDataString))}?query={Uri.EscapeDataString(id)}&realm=poe2"));
        var sellers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listings = ParseListings(fetched.RootElement, market).Where(l => sellers.Add(l.Seller)).ToList();
        return new($"{total:N0} listed online", listings, total, url, note);
    }

    // Trade-site currency ids → the price book's display names (for the few not covered by the fixed rates).
    private static readonly Dictionary<string, string> CurrencyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["annul"] = "Orb of Annulment", ["regal"] = "Regal Orb", ["alch"] = "Orb of Alchemy", ["vaal"] = "Vaal Orb",
        ["mirror"] = "Mirror of Kalandra", ["aug"] = "Orb of Augmentation", ["transmute"] = "Orb of Transmutation",
        ["chance"] = "Orb of Chance", ["gcp"] = "Gemcutter's Prism", ["bauble"] = "Glassblower's Bauble",
    };

    /// <summary>Exalted per unit of a trade-site currency id, or 0 when unknown.</summary>
    internal static double Rate(string? currency, PriceBook.Snapshot market) => currency switch
    {
        "exalted" => 1, "divine" => market.ExPerDivine, "chaos" => market.ExPerChaos,
        { } c when CurrencyNames.TryGetValue(c, out var name) && market.ByName.TryGetValue(name, out var p) => p.Exalted,
        _ => 0,
    };

    /// <summary>Every priced listing in a /fetch response, in the order returned (the search sorts by price).</summary>
    internal static List<MarketListing> ParseListings(JsonElement root, PriceBook.Snapshot market)
    {
        var list = new List<MarketListing>();
        foreach (var entry in root.GetProperty("result").EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("listing", out var listing) || !listing.TryGetProperty("price", out var price)
                || !price.TryGetProperty("amount", out var amount) || !amount.TryGetDouble(out var number)
                || !price.TryGetProperty("currency", out var currency) || number <= 0) continue;
            var cur = currency.GetString() ?? "";
            var rate = Rate(cur, market);
            double? ex = rate > 0 && double.IsFinite(number * rate) ? number * rate : null;
            var seller = listing.TryGetProperty("account", out var account) && account.TryGetProperty("name", out var name)
                ? name.GetString() ?? "" : "";
            DateTime? indexed = listing.TryGetProperty("indexed", out var ix) && ix.ValueKind == JsonValueKind.String
                && DateTime.TryParse(ix.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when)
                ? when : null;
            var stack = entry.TryGetProperty("item", out var item) && item.TryGetProperty("stackSize", out var ss) && ss.TryGetInt32(out var st) ? st : 1;
            list.Add(new MarketListing(number, cur, ex, seller, indexed, stack));
        }
        return list;
    }

    public sealed record TradeStat(string Id, string Text);
    private static readonly Regex Numbers = new(@"[+-]?\d+(?:\.\d+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static string Normalize(string text) => Regex.Replace(Numbers.Replace(text, "#").Replace("+#", "#").Replace("-#", "#"), @"\s+", " ").Trim();
    private const string LocalSuffix = " (Local)";

    // Two trade stats with the same wording: the one ordinary item affixes are listed under (checked live 2026-09-26 by
    // searching amulets, rings and sceptres with each id).
    private static readonly Dictionary<string, string> Preferred = new(StringComparer.Ordinal)
    {
        ["explicit.# to Spirit"] = "explicit.stat_3981240776",
        ["explicit.# to all Attributes"] = "explicit.stat_1379411836",
        ["explicit.#% increased Spirit"] = "explicit.stat_3984865854",   // sceptres
    };

    // Lines the trade site words differently from the item: the crossbow bolt count is one stat whose value is the count.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["explicit.Loads # additional bolts"] = "explicit.stat_1967051901",
    };

    /// <summary>
    /// Match one mod line to its trade stat by the text with numbers masked. Weapon and armour mods that act on the item
    /// itself (<paramref name="local"/>: attack speed, flat/% armour, evasion, ES, accuracy) are listed as "… (Local)"
    /// stats — matching them to the global wording would search for a stat no weapon or armour piece has. Null when the
    /// line has no stat, more than two numbers, or stays ambiguous. Two-number lines ("Adds 5 to 12 …") match with their
    /// average, the way the trade site filters them.
    /// </summary>
    public static MatchedStat? MatchLine(string kind, string line, IReadOnlyList<TradeStat> stats, bool local = false)
    {
        var numbers = Numbers.Matches(line);
        if (numbers.Count > 2) return null;
        var text = Normalize(line);
        double? value = numbers.Count == 0 ? null
            : numbers.Select(n => Math.Abs(double.Parse(n.Value, CultureInfo.InvariantCulture))).Average();
        if (Aliases.TryGetValue(kind + "." + text, out var alias))
            return stats.Any(s => s.Id == alias) ? new MatchedStat(alias, value, line) : null;
        var candidates = stats.Where(s => s.Id.StartsWith(kind + ".", StringComparison.Ordinal)
            && Normalize(s.Text.EndsWith(LocalSuffix, StringComparison.Ordinal) ? s.Text[..^LocalSuffix.Length] : s.Text) == text).ToList();
        if (candidates.Count > 1)
        {
            var sameScope = candidates.Where(s => s.Text.EndsWith(LocalSuffix, StringComparison.Ordinal) == local).ToList();
            if (sameScope.Count > 0) candidates = sameScope;
        }
        if (candidates.Count > 1 && Preferred.TryGetValue(kind + "." + text, out var preferred))
            candidates = candidates.Where(c => c.Id == preferred).ToList();
        return candidates.Count == 1 ? new MatchedStat(candidates[0].Id, value, line) : null;
    }

    /// <summary>Whether a rendered line of affix <paramref name="modId"/> acts on the item itself: the affix stat the line
    /// shares the most words with decides, so the global half of a hybrid ("+400 to Accuracy Rating" beside local attack
    /// speed) keeps its global trade stat.</summary>
    public static bool IsLocalLine(string modId, string line)
    {
        var stats = ItemModTranslator.Shared.StatIdsFor(modId);
        if (stats is not { Length: > 0 }) return false;
        var locals = stats.Count(s => s.StartsWith("local_", StringComparison.Ordinal));
        if (locals == 0 || locals == stats.Length) return locals > 0;
        return stats.MaxBy(s => ItemAppraiser.WordOverlap(s, line))!.StartsWith("local_", StringComparison.Ordinal);
    }

    /// <summary>
    /// Match each mod line to exactly one trade stat (see <see cref="MatchLine"/>). Lines with no match, or with
    /// several equally-worded stats, are counted in <paramref name="ignored"/>.
    /// </summary>
    public static List<MatchedStat> MatchStats(ItemTradeProfile item, IReadOnlyList<TradeStat> stats, out int ignored)
    {
        var localLines = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in item.Affixes ?? [])
            foreach (var line in a.Lines)
                if (IsLocalLine(a.Id, line)) localLines.Add(line);
        var result = new List<MatchedStat>(); var used = new HashSet<string>();
        ignored = 0;
        foreach (var mod in item.Mods)
        {
            if (MatchLine(mod.Kind, mod.Text, stats, localLines.Contains(mod.Text)) is not { } m || !used.Add(m.Id)) { ignored++; continue; }
            result.Add(m);
        }
        return result;
    }

    private async Task<JsonDocument> Request(HttpRequestMessage request)
    {
        using (request)
        {
            var delay = _nextRequest - DateTime.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay);
            using var response = await _http.SendAsync(request);
            var wait = TimeSpan.FromSeconds(1);
            if (response.Headers.RetryAfter is { } retry)
                wait = retry.Delta ?? (retry.Date - DateTimeOffset.UtcNow) ?? wait;
            // Honor each dynamic rate-limit window before another request is sent.
            if (response.Headers.TryGetValues("X-Rate-Limit-Rules", out var rules))
            foreach (var rule in string.Join(',', rules).Split(',', StringSplitOptions.TrimEntries))
            {
                if (!response.Headers.TryGetValues("X-Rate-Limit-" + rule, out var limits)
                    || !response.Headers.TryGetValues("X-Rate-Limit-" + rule + "-State", out var states)) continue;
                var ceilings = string.Join(',', limits).Split(','); var usage = string.Join(',', states).Split(',');
                for (int i = 0; i < Math.Min(ceilings.Length, usage.Length); i++)
                {
                    var limit = ceilings[i].Split(':'); var state = usage[i].Split(':');
                    if (limit.Length < 3 || state.Length < 3 || !int.TryParse(limit[0], out var max)
                        || !int.TryParse(state[0], out var count) || !int.TryParse(state[1], out var seconds)
                        || !int.TryParse(state[2], out var blocked)) continue;
                    var pause = TimeSpan.FromSeconds(Math.Max(blocked, count >= max ? seconds : 0));
                    if (pause > wait) wait = pause;
                }
            }
            if (response.StatusCode == HttpStatusCode.TooManyRequests && wait < TimeSpan.FromSeconds(60)) wait = TimeSpan.FromSeconds(60);
            if (wait < TimeSpan.FromSeconds(1)) wait = TimeSpan.FromSeconds(1);
            _nextRequest = DateTime.UtcNow.Add(wait);
            if (!response.IsSuccessStatusCode)
            {
                lock (_gate)
                {
                    if (response.StatusCode == HttpStatusCode.TooManyRequests) _blockedUntil = DateTime.UtcNow.Add(wait);
                }
                throw new HttpRequestException(response.StatusCode switch
                {
                    HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => "Trade access denied; open Ctrl+D in your browser. ",
                    HttpStatusCode.TooManyRequests => "Trade rate limit reached; automatic retries are paused.",
                    _ => $"Trade service returned HTTP {(int)response.StatusCode}.",
                });
            }
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }
    }
}
