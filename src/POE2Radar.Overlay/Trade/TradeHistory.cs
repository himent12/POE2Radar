using System.Text.Json;
using System.Text.Json.Serialization;

namespace POE2Radar.Overlay.Trade;

public enum TradeSide { Sold, Bought }

public sealed record TradeRecord(string Id, DateTime Utc, TradeSide Direction, string Player, string Item,
    double Amount, string Currency, string League);

/// <summary>Totals since a cutoff. Exalted figures cover only records whose currency converted; the rest are
/// counted in <see cref="Unconverted"/> so a missing rate never silently reads as zero profit.</summary>
public sealed record TradeSummary(int Sold, int Bought,
    IReadOnlyDictionary<string, double> EarnedByCurrency, IReadOnlyDictionary<string, double> SpentByCurrency,
    double EarnedExalted, double SpentExalted, int Unconverted)
{
    public double ProfitExalted => EarnedExalted - SpentExalted;
}

/// <summary>
/// "Smart trade tracker": completed trades persisted to a JSON file (atomic temp+rename on each change —
/// trades are rare, so no debounce). Newest <see cref="MaxRecords"/> are kept. Thread-safe.
/// </summary>
public sealed class TradeHistory
{
    public const int MaxRecords = 5000;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() },
    };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<TradeRecord> _records; // oldest first
    private int _generation;

    public TradeHistory(string path)
    {
        _path = path;
        _records = Load(path);
    }

    public int Generation => Volatile.Read(ref _generation);
    public int Count { get { lock (_gate) return _records.Count; } }
    public string? LastError { get; private set; }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<TradeRecord> Records()
    {
        lock (_gate) return Enumerable.Reverse(_records).ToArray();
    }

    /// <summary>Records a completed session; subscribe to <see cref="TradeSessions.Completed"/>. Ignores non-completed.</summary>
    public TradeRecord? Add(TradeSession session)
    {
        if (session.State != TradeState.Completed) return null;
        var r = session.Request;
        return Add(new TradeRecord(Guid.NewGuid().ToString("N")[..12], session.CompletedUtc ?? DateTime.UtcNow,
            session.Direction == TradeDirection.Incoming ? TradeSide.Sold : TradeSide.Bought,
            session.Player, r.Item, r.Amount, r.Currency, r.League));
    }

    public TradeRecord Add(TradeRecord record)
    {
        lock (_gate)
        {
            _records.Add(record);
            if (_records.Count > MaxRecords) _records.RemoveRange(0, _records.Count - MaxRecords);
            Save();
        }
        return record;
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            if (_records.RemoveAll(r => r.Id == id) == 0) return false;
            Save();
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _records.Clear();
            Save();
        }
    }

    /// <param name="toExalted">Currency display name + amount → exalted value, or null when no rate is known.</param>
    public TradeSummary Summary(DateTime sinceUtc, Func<string, double, double?> toExalted)
    {
        TradeRecord[] rows;
        lock (_gate) rows = _records.Where(r => r.Utc >= sinceUtc).ToArray();
        var earned = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var spent = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        int sold = 0, bought = 0, unconverted = 0;
        double earnedEx = 0, spentEx = 0;
        foreach (var r in rows)
        {
            var isSale = r.Direction == TradeSide.Sold;
            if (isSale) sold++; else bought++;
            if (r.Amount <= 0 || string.IsNullOrEmpty(r.Currency)) continue; // unpriced listing
            var bucket = isSale ? earned : spent;
            bucket[r.Currency] = bucket.GetValueOrDefault(r.Currency) + r.Amount;
            var ex = toExalted(r.Currency, r.Amount);
            if (ex is not double v || double.IsNaN(v)) { unconverted++; continue; }
            if (isSale) earnedEx += v; else spentEx += v;
        }
        return new TradeSummary(sold, bought, earned, spent, earnedEx, spentEx, unconverted);
    }

    private static List<TradeRecord> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            var rows = JsonSerializer.Deserialize<List<TradeRecord>>(File.ReadAllText(path), Json) ?? new();
            rows.RemoveAll(r => r is null);
            return rows.OrderBy(r => r.Utc).TakeLast(MaxRecords).ToList();
        }
        catch (Exception)
        {
            // A corrupt file must not stop the overlay; keep it aside rather than overwrite the user's data.
            try { File.Copy(path, path + ".bad", overwrite: true); } catch (Exception) { }
            return new();
        }
    }

    private void Save()
    {
        Interlocked.Increment(ref _generation);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(temp, JsonSerializer.Serialize(_records, Json));
            File.Move(temp, _path, overwrite: true);
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            try { File.Delete(temp); } catch (Exception) { }
        }
    }
}
