using System.Buffers.Binary;

namespace POE2Radar.Core.Game;

/// <summary>
/// Local-player buff (status effect) reads for the buff keeper. Buffs component → StatusEffectPtr
/// StdVector of StatusEffect* → each effect's BuffDefinitions.dat row → UTF-16 internal id.
///
/// <para><b>Cost / threading.</b> The first call per refresh window costs 2 + N ReadProcessMemory calls
/// (the vector header, the whole pointer array in one read, one 0x48-byte read per effect). Buff names are
/// cached per BuffDefinition row pointer, so a steady state reads no strings. Results are cached for
/// <see cref="BuffsRefreshMs"/> (default 100 ms), so calling this every render frame at 60 Hz still reads
/// memory at most ~10 times a second and allocates one small array per refresh. Resolving the component
/// (~10-40 reads) happens once per local-player entity (i.e. once per zone). The drift scan (≤ 112 vector
/// reads plus entry reads) runs at most once every 2 s and only while the offset is unconfirmed or broken.
/// Like every Poe2Live method this is NOT thread-safe: call it from the one thread that owns this reader
/// stack (the render thread's <c>_liveRender</c> is the natural home, next to auto-flask).</para>
/// </summary>
public sealed partial class Poe2Live
{
    /// <summary>
    /// One active buff on the local player. <paramref name="Name"/> is the BuffDefinitions internal id
    /// (e.g. "arcane_surge"). <paramref name="TimeLeft"/> / <paramref name="TotalTime"/> are seconds; buffs
    /// with no duration (auras, permanent effects) read inf/NaN/huge in memory and are normalized to
    /// <see cref="float.PositiveInfinity"/> (<see cref="IsInfinite"/>). Negative times clamp to 0.
    /// <paramref name="Charges"/> is the effect's stack/charge count (GH2 offset, uncertain until validated).
    /// </summary>
    public readonly record struct BuffInfo(string Name, float TimeLeft, float TotalTime, int Charges)
    {
        public bool IsInfinite => float.IsPositiveInfinity(TimeLeft);
    }

    /// <summary>Upper bound on the status-effect vector length; longer vectors are treated as garbage.</summary>
    public const int MaxBuffEntries = 256;

    /// <summary>Durations at or above this (≈ 11.5 days) are treated as infinite.</summary>
    public const float InfiniteBuffSeconds = 1_000_000f;

    public const int MinBuffIdLength = 2;
    public const int MaxBuffIdLength = 96;

    private const int BuffNameCacheLimit = 512;
    private const int BuffScanStart = 0x80;
    private const int BuffScanEnd = 0x400;
    private const int BuffRescanIntervalMs = 2000;
    private const int BuffComponentRetryMs = 1000;
    private const int BuffInvalidBeforeRescan = 3;

    private enum BuffVecResult { Invalid, Empty, Named }

    private nint _plBuffs, _plBuffsFor;
    private long _plBuffsRetryAt;
    private int _buffVecOff = Poe2.Buffs.StatusEffectPtr;
    private bool _buffVecConfirmed;
    private bool _buffFailLogged;
    private int _buffInvalidStreak;
    private long _buffNextScanAt;
    private nint _buffCachedFor;
    private long _buffCachedAt;
    private IReadOnlyList<BuffInfo>? _buffCached;
    private readonly Dictionary<nint, string> _buffNames = new();
    private readonly byte[] _buffVecBuf = new byte[MaxBuffEntries * 8];
    private readonly byte[] _buffEffBuf = new byte[Poe2.StatusEffect.ReadSize];
    private readonly List<BuffInfo> _buffScratch = new(32);

    /// <summary>How long a <see cref="PlayerBuffs"/> result is reused before memory is read again (ms).
    /// 0 = read on every call.</summary>
    public int BuffsRefreshMs { get; set; } = 100;

    /// <summary>The StatusEffectPtr offset currently in use (the configured one unless self-heal relocated it).</summary>
    public int BuffVectorOffset => _buffVecOff;

    /// <summary>True once the vector offset has been seen holding at least one validly named buff.</summary>
    public bool BuffOffsetConfirmed => _buffVecConfirmed;

    /// <summary>
    /// The local player's active buffs, or <c>null</c> when they can't be read (no Buffs component, a
    /// drifted/garbage vector, a transient read failure). Callers MUST treat null as UNKNOWN and not act on
    /// it; an empty list means "readable, no buffs". Entries whose name doesn't resolve are dropped.
    /// Cached for <see cref="BuffsRefreshMs"/>; see the class remarks for cost.
    /// </summary>
    public IReadOnlyList<BuffInfo>? PlayerBuffs(nint localPlayer)
    {
        if (localPlayer == 0) return null;
        var now = Environment.TickCount64;
        if (localPlayer == _buffCachedFor && now - _buffCachedAt < Math.Max(0, BuffsRefreshMs)) return _buffCached;
        _buffCachedFor = localPlayer;
        _buffCachedAt = now;
        _buffCached = ReadPlayerBuffs(localPlayer, now);
        return _buffCached;
    }

    private IReadOnlyList<BuffInfo>? ReadPlayerBuffs(nint localPlayer, long now)
    {
        if (localPlayer != _plBuffsFor || (_plBuffs == 0 && now >= _plBuffsRetryAt))
        {
            _plBuffsFor = localPlayer;
            _plBuffs = ResolveComponent(localPlayer, "Buffs");
            _plBuffsRetryAt = now + BuffComponentRetryMs;
        }
        if (_plBuffs == 0) return null;

        _buffScratch.Clear();
        switch (ReadBuffVector(_plBuffs, _buffVecOff, _buffScratch, out _, out _))
        {
            case BuffVecResult.Named:
                _buffVecConfirmed = true;
                _buffInvalidStreak = 0;
                return _buffScratch.ToArray();

            case BuffVecResult.Empty:
                _buffInvalidStreak = 0;
                // An empty vector is a valid "no buffs" state, but a drifted offset can also land on zeros.
                // Until we've once seen named buffs here, look (throttled) for a populated vector elsewhere.
                if (!_buffVecConfirmed && now >= _buffNextScanAt && TryRelocateBuffVector(_plBuffs, now, logFailure: false))
                    return _buffScratch.ToArray();
                return Array.Empty<BuffInfo>();

            default:
                // Could be a transient (vector reallocating mid-read) — only rescan after a short streak.
                if (++_buffInvalidStreak >= BuffInvalidBeforeRescan && now >= _buffNextScanAt
                    && TryRelocateBuffVector(_plBuffs, now, logFailure: true))
                    return _buffScratch.ToArray();
                return null;
        }
    }

    // Scan the component for a StdVector whose entries resolve to buff names. Accepts a candidate only when
    // at least half of its entries resolve (a stricter bar than the configured offset's ≥1, since a random
    // vector elsewhere in the component must not be mistaken for the buff list); the nearest to the
    // configured offset wins. On success the offset is latched and _buffScratch holds its buffs.
    private bool TryRelocateBuffVector(nint comp, long now, bool logFailure)
    {
        _buffNextScanAt = now + BuffRescanIntervalMs;
        int best = -1, bestDist = int.MaxValue;
        for (var off = BuffScanStart; off <= BuffScanEnd; off += 8)
        {
            if (off == _buffVecOff) continue;
            if (ReadBuffVector(comp, off, null, out var named, out var count) != BuffVecResult.Named) continue;
            if (named * 2 < count) continue;
            var d = Math.Abs(off - Poe2.Buffs.StatusEffectPtr);
            if (d < bestDist) { bestDist = d; best = off; }
        }
        if (best < 0)
        {
            if (logFailure && !_buffFailLogged)
            {
                _buffFailLogged = true;
                Console.WriteLine($"Poe2Live: Buffs StatusEffectPtr offset (0x{Poe2.Buffs.StatusEffectPtr:X}) couldn't be confirmed " +
                    "— buff-based buff-keeper rules pause while buffs are unreadable. Update Poe2.Buffs + re-validate (Research --buffs).");
            }
            return false;
        }

        _buffScratch.Clear();
        if (ReadBuffVector(comp, best, _buffScratch, out _, out _) != BuffVecResult.Named) return false;
        var from = _buffVecOff;
        _buffVecOff = best;
        _buffVecConfirmed = true;
        _buffInvalidStreak = 0;
        Console.WriteLine($"Poe2Live: Buffs StatusEffectPtr offset appears to have drifted — auto-relocated " +
            $"0x{from:X}->0x{best:X} (buff keeper keeps working). Update Poe2.Buffs + re-validate (Research --buffs).");
        return true;
    }

    // Read one candidate status-effect vector. `into` (optional) receives every validly named entry.
    private BuffVecResult ReadBuffVector(nint comp, int off, List<BuffInfo>? into, out int named, out int count)
    {
        named = 0; count = 0;
        if (!_reader.TryReadStruct<StdVector>(comp + off, out var v)) return BuffVecResult.Invalid;
        if (!IsPlausibleBuffVector(v.First, v.Last, v.End, out count)) return BuffVecResult.Invalid;
        if (count == 0) return BuffVecResult.Empty;

        var span = _buffVecBuf.AsSpan(0, count * 8);
        if (_reader.TryReadBytes(v.First, span) != span.Length) return BuffVecResult.Invalid;
        for (var i = 0; i < count; i++)
        {
            var effect = (nint)BinaryPrimitives.ReadInt64LittleEndian(span[(i * 8)..]);
            if (!IsUserPointer(effect) || !TryReadStatusEffect(effect, out var info)) continue;
            named++;
            into?.Add(info);
        }
        return named > 0 ? BuffVecResult.Named : BuffVecResult.Invalid;
    }

    private bool TryReadStatusEffect(nint effect, out BuffInfo info)
    {
        info = default;
        var buf = _buffEffBuf.AsSpan();
        if (_reader.TryReadBytes(effect, buf) != buf.Length) return false;
        var def = (nint)BinaryPrimitives.ReadInt64LittleEndian(buf[Poe2.StatusEffect.BuffDefinitionPtr..]);
        var name = BuffName(def);
        if (name.Length == 0) return false;
        var total = BinaryPrimitives.ReadSingleLittleEndian(buf[Poe2.StatusEffect.TotalTime..]);
        var left = BinaryPrimitives.ReadSingleLittleEndian(buf[Poe2.StatusEffect.TimeLeft..]);
        var charges = BinaryPrimitives.ReadUInt16LittleEndian(buf[Poe2.StatusEffect.Charges..]);
        info = new BuffInfo(name, NormalizeBuffTime(left), NormalizeBuffTime(total), charges);
        return true;
    }

    // BuffDefinitions row → UTF-16 id, cached per row pointer (dat rows live for the process). Only valid
    // names are cached, so a pointer read mid-construction is retried rather than latched as garbage.
    private string BuffName(nint def)
    {
        if (!IsUserPointer(def)) return "";
        if (_buffNames.TryGetValue(def, out var cached)) return cached;
        var s = _reader.ReadStringUtf16(Ptr(def + Poe2.BuffDefinition.IdPtr), MaxBuffIdLength + 1);
        if (!IsPlausibleBuffId(s)) return "";
        if (_buffNames.Count >= BuffNameCacheLimit) _buffNames.Clear();
        _buffNames[def] = s;
        return s;
    }

    /// <summary>Is this a plausible BuffDefinitions internal id? 2..96 chars of ASCII letters, digits,
    /// <c>_</c>, <c>-</c>, <c>.</c>, with at least one letter (rejects garbage memory decoded as UTF-16).</summary>
    public static bool IsPlausibleBuffId(ReadOnlySpan<char> id)
    {
        if (id.Length is < MinBuffIdLength or > MaxBuffIdLength) return false;
        var letter = false;
        foreach (var c in id)
        {
            if (char.IsAsciiLetter(c)) letter = true;
            else if (!char.IsAsciiDigit(c) && c is not ('_' or '-' or '.')) return false;
        }
        return letter;
    }

    /// <summary>Is this a sane StdVector of 8-byte pointers? All-null = empty. Otherwise First ≤ Last ≤ End,
    /// all user-mode, 8-byte aligned length, ≤ <see cref="MaxBuffEntries"/> entries and a bounded capacity.</summary>
    public static bool IsPlausibleBuffVector(nint first, nint last, nint end, out int count)
    {
        count = 0;
        if (first == 0 && last == 0 && end == 0) return true;
        if (!IsUserPointer(first) || !IsUserPointer(last) || !IsUserPointer(end)) return false;
        if ((long)last < (long)first || (long)end < (long)last) return false;
        var bytes = (long)last - (long)first;
        if (bytes % 8 != 0 || bytes / 8 > MaxBuffEntries || ((long)end - (long)first) / 8 > MaxBuffEntries * 16) return false;
        count = (int)(bytes / 8);
        return true;
    }

    /// <summary>Normalize a raw duration: NaN, +inf and anything ≥ <see cref="InfiniteBuffSeconds"/> →
    /// <see cref="float.PositiveInfinity"/> (no expiry); negative (incl. −inf) → 0.</summary>
    public static float NormalizeBuffTime(float seconds)
        => float.IsNaN(seconds) || seconds >= InfiniteBuffSeconds ? float.PositiveInfinity
            : seconds < 0 ? 0f : seconds;

    private static bool IsUserPointer(nint p) => (ulong)p is >= 0x10000 and <= 0x7FFFFFFFFFFF;
}
