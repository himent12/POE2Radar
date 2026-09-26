using System.Text.Json;
using POE2Radar.Core.Game;

namespace POE2Radar.Overlay.Pricing;

/// <summary>Presentation and comparison links for an explicit item hover. Never appraises rare modifiers.</summary>
public sealed record HoverValuation(string Text, string Detail, bool Highlight, string? TradeUrl)
{
    public static HoverValuation Build(Poe2Live.HoveredItem item, PriceBook book, int minListings, double highlightMinEx)
    {
        var snapshot = book.Current;
        var loaded = snapshot.ByName.Count > 0;
        var stale = loaded && DateTime.UtcNow - snapshot.FetchedUtc > TimeSpan.FromMinutes(Math.Max(5, book.RefreshIntervalMinutes));
        string Format(double value) => PriceBook.Format(value, snapshot.ExPerDivine);
        var unique = item.Rarity == Poe2Live.Rarity.Unique;
        var equipment = item.Rarity is Poe2Live.Rarity.Rare or Poe2Live.Rarity.Magic;
        PriceResult? price = unique
            ? item.Art is { } art && snapshot.ByArt.TryGetValue(art, out var byArt) ? byArt : null
            : !equipment && item.Name is { } key && snapshot.ByName.TryGetValue(key, out var byName) ? byName : null;
        var name = price?.Name ?? item.Name ?? "Unknown item";
        var trade = TradeSearch(loaded ? snapshot.League : book.ComparisonLeague, item.Name, unique ? price?.Name : null, item.Rarity);
        var source = loaded
            ? $"poe.ninja · {snapshot.League} · {(stale ? "STALE cache" : "cached")} {Age(snapshot.FetchedUtc)}"
            : "poe.ninja · " + book.Status;
        var action = trade == null ? "Set price league in F12 → Item Value" : "Ctrl+D: full price check vs live listings";
        if (price is not { } p)
        {
            var text = equipment ? "Reading item modifiers" : loaded ? "No market estimate" : "Market prices unavailable";
            return new(text, $"{name}\n{source}\n{action}", false, trade);
        }
        var count = Math.Max(1, item.Stack);
        var total = p.Exalted * count;
        var detail = $"{name}";
        if (count > 1) detail += $" · {count:N0} × {Format(p.Exalted)} each";
        var evidence = p.IsListingPrice
            ? $"{p.Quantity:N0} listings{(p.LowConfidence(minListings) ? " · LOW CONFIDENCE" : "")}" : "Currency exchange estimate";
        if (unique || p.Category == "PrecursorTablets") evidence += " · rolls may change value";
        if (!string.IsNullOrWhiteSpace(p.Variant)) evidence += $" · {p.Variant}";
        if (p.Corrupted) evidence += " · corrupted reference";
        if (unique && !item.Identified) evidence += " · identified reference";
        if (p.TrendPercent is { } change) evidence += $"\nRecent trend: {change:+0.##;-0.##;0}%";
        if (p.TradedVolumeEx is { } volume) evidence += $" · traded volume {Format(volume)}";
        if (snapshot.RangesByName.TryGetValue(p.Name, out var range) && range.Variants > 1)
            evidence += $"\n{range.Variants} market variants: {Format(range.MinExalted)}–{Format(range.MaxExalted)} · {range.Listings:N0} listings";
        return new($"Estimated {(count > 1 ? "stack" : "value")}: {Format(total)}",
            $"{detail}\n{evidence}\n{source}\n{action}", !stale && total >= highlightMinEx, trade);
    }

    private static string Age(DateTime fetched)
    {
        var age = DateTime.UtcNow - fetched;
        return age.TotalHours >= 1 ? $"{Math.Max(1, (int)age.TotalHours)}h ago" : $"{Math.Max(0, (int)age.TotalMinutes)}m ago";
    }

    public static string? TradeSearch(string league, string? baseName, string? uniqueName, Poe2Live.Rarity rarity)
    {
        if (string.IsNullOrWhiteSpace(league) || PriceCheck.BaseQuery(baseName, uniqueName, rarity) is not { } payload) return null;
        return $"https://www.pathofexile.com/trade2/search/poe2/{Uri.EscapeDataString(league)}?q={Uri.EscapeDataString(payload)}";
    }
}
