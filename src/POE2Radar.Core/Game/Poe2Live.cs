namespace POE2Radar.Core.Game;

/// <summary>
/// Live PoE2 game-state reader for the radar overlay. Resolves the top-level chain each tick
/// (GameState → InGameState → AreaInstance) and exposes the player position, the entity list,
/// the walkable terrain grid, and the large-map UI state — all via offsets validated live and
/// recorded in <see cref="Poe2"/> / resources/community-offsets.md.
///
/// <para>Construct once with the AOB-resolved GameState pointer slot (see Bootstrap). Call
/// <see cref="TryResolve"/> at the start of each tick; everything else takes the resolved
/// AreaInstance / InGameState.</para>
/// </summary>
public sealed partial class Poe2Live
{
    private readonly MemoryReader _reader;

    private readonly nint _gameStateSlot;

    // Bounds the number of NEW (uncached) monster mod reads per Entities() pass so walking into a large
    // pack can't stall the world tick. Cached monsters cost nothing; new ones fill over a few ticks.
    private int _modReadBudget;

    private const int ModReadBudgetPerPass = 16;

    private readonly byte[] _modVecBuf = new byte[24];

    // Same budgeting for the (cheap, read-once) dropped-item identity reads.
    private int _itemReadBudget;

    private const int ItemReadBudgetPerPass = 12;

    // Reused across Entities() calls (tick thread only) to avoid per-tick allocations. The std::map
    // walk reads each 48-byte node in ONE ReadProcessMemory (fields are contiguous), not 5 syscalls.
    private readonly Queue<nint> _entQueue = new();

    private readonly HashSet<nint> _entVisited = new();

    private readonly byte[] _nodeBuf = new byte[0x30];

    // Reused camera-matrix buffers (read every render frame).
    private readonly byte[] _camBytes = new byte[64];

    private readonly float[] _camMatrix = new float[16];

    public Poe2Live(MemoryReader reader, nint gameStateSlot)
    {
        _reader = reader;
        _gameStateSlot = gameStateSlot;
    }

    public enum EntityCategory { Player, Monster, Npc, Chest, Transition, Object, Other }

    /// <summary>Monster rarity from ObjectMagicProperties.Rarity. NonMonster = not applicable.</summary>
    public enum Rarity { Normal = 0, Magic = 1, Rare = 2, Unique = 3, NonMonster = -1 }

    public readonly record struct EntityDot(
        uint Id, nint Address, System.Numerics.Vector2 Grid, Vector3 World, EntityCategory Category, string Metadata,
        int HpCur, int HpMax, bool Poi, byte Reaction, Rarity Rarity, bool Opened, bool IconComplete = false,
        IReadOnlyList<string>? Mods = null, string? ItemArt = null, bool ItemIdentified = true, string? ItemName = null)
    {
        // ItemName (positional): a dropped item's rendered BASE-TYPE display name (Base +0x10 → +0x30),
        // e.g. "Greater Orb of Augmentation". The price key for NON-uniques (currency/runes/essences),
        // where one .dds art is shared across tiers so art can't disambiguate. Null for non-items / unread.
        // ItemIdentified (positional): for a dropped unique, whether the game has identified it (Mods+0x90).
        // Drives the loot overlay's unique rule — unID → reveal the resolved name; ID → value only. Defaults
        // true so non-item / non-unique entities are never treated as "unidentified".
        /// <summary>The monster's affix mod ids (auras/buffs), never null. Empty for non-monsters,
        /// unrolled monsters, or before the budgeted mod read has filled this entity in.</summary>
        public IReadOnlyList<string> ModList => Mods ?? Array.Empty<string>();
        // ItemArt (positional): for a dropped-item (WorldItem) entity, the basename of its 2D art (.dds),
        // e.g. "Earthbound" — the price-lookup key (matches poe2scout IconUrl basename). Null for non-items
        // / not-yet-read. When set, Rarity carries the dropped item's rarity (Unique=3) for gating.

        /// <summary>Monsters are "alive" only with positive HP; non-life entities are always shown.</summary>
        public bool IsAlive => HpMax <= 0 || HpCur > 0;
        public bool HasLife => HpMax > 0;
        /// <summary>GameHelper2 rule: friendly when (Reaction &amp; 0x7F) == 1.</summary>
        public bool IsFriendly => (Reaction & 0x7F) == 1;
        public float HpFraction => HpMax > 0 ? Math.Clamp((float)HpCur / HpMax, 0f, 1f) : 1f;
    }

    public readonly record struct MapUi(bool IsVisible, float ShiftX, float ShiftY, float Zoom);

    /// <summary>A static tile-based landmark: a notable terrain feature and its grid centroid.
    /// <paramref name="CuratedName"/> is an optional curated friendly label (null when none matches);
    /// <paramref name="Name"/> is the derived-from-path fallback.</summary>
    public readonly record struct Landmark(string Name, string Path, System.Numerics.Vector2 Center, int TileCount, string? CuratedName = null)
    {
        /// <summary>Stable per-CLUSTER identity for nav selection. A tile path can now yield several
        /// landmarks (one per spatial cluster — e.g. each stair-up section of a multi-level dungeon),
        /// so the path alone is ambiguous; qualify it with the integer centroid, which is stable per
        /// area (tiles are static terrain).</summary>
        public string Key => $"{Path}@{(int)Center.X},{(int)Center.Y}";
    }

    public sealed record TerrainData(byte[] Walkable, int Width, int Height);

    /// <summary>Resolve the in-game chain. Returns false during loading / character select.</summary>
    public bool TryResolve(out nint inGameState, out nint areaInstance, out nint localPlayer)
    {
        inGameState = areaInstance = localPlayer = 0;
        var gameState = Ptr(_gameStateSlot);
        if (gameState == 0) return false;

        // InGameState = first element of the CurrentStatePtr StdVector; fall back to States[].
        var candidates = new List<nint>(13);
        var vecFirst = Ptr(gameState + Poe2.GameState.CurrentStatePtr);
        if (vecFirst != 0) candidates.Add(Ptr(vecFirst));
        for (var i = 0; i < Poe2.GameState.StateSlotCount; i++)
            candidates.Add(Ptr(gameState + Poe2.GameState.States + (nint)(i * Poe2.GameState.StateSlotStride)));

        foreach (var igs in candidates)
        {
            if (igs == 0) continue;
            var ai = Ptr(igs + Poe2.InGameState.AreaInstanceData);
            if (ai == 0) continue;
            var lp = Ptr(ai + Poe2.AreaInstance.LocalPlayer);
            if (lp == 0) continue;
            if (!ReadMetadata(lp).StartsWith("Metadata/", StringComparison.Ordinal)) continue;
            inGameState = igs; areaInstance = ai; localPlayer = lp;
            return true;
        }
        return false;
    }

    /// <summary>Per-area instance hash. (Caches key on the AreaInstance address; this is for display/ID.)</summary>
    public uint AreaHash(nint areaInstance)
    {
        _reader.TryReadStruct<uint>(areaInstance + Poe2.AreaInstance.CurrentAreaHash, out var h);
        return h;
    }

    /// <summary>Monster/area level (validated live: 27, 32).</summary>
    public int AreaLevel(nint areaInstance)
    {
        _reader.TryReadStruct<int>(areaInstance + Poe2.AreaInstance.CurrentAreaLevel, out var l);
        return l;
    }

    private string _areaCode = "";

private nint _areaCodeFor = -1;

    /// <summary>Area code identifier (e.g. "G1_town"). Cached per area.</summary>
    public string AreaCode(nint areaInstance)
    {
        if (areaInstance == _areaCodeFor) return _areaCode;
        _areaCodeFor = areaInstance;
        var info = Ptr(areaInstance + Poe2.AreaInstance.AreaInfoPtr);
        var s = Ptr(info);
        _areaCode = s == 0 ? "" : _reader.ReadStringUtf16(s, 64);
        return _areaCode;
    }

    private string _league = "";

private nint _leagueFor = -1;

    /// <summary>Current league name as the game stores it (ServerData @ AreaInstance+0x580 → std::wstring
    /// +0x21E0). Matches poe.ninja/poe2scout's league Value verbatim, including the "HC " prefix — so it
    /// disambiguates the two IsCurrent leagues (softcore vs hardcore) for price auto-detect. Cached per area.</summary>
    public string LeagueName(nint areaInstance)
    {
        if (areaInstance == _leagueFor) return _league;
        _leagueFor = areaInstance;
        var serverData = Ptr(areaInstance + Poe2.AreaInstance.ServerDataPtr);
        _league = serverData == 0 ? "" : ReadStdWString(serverData + Poe2.ServerData.League);
        return _league;
    }

    private nint _plPlayer, _plPlayerFor;

    private nint PlayerComp(nint localPlayer)
    {
        if (localPlayer != _plPlayerFor) { _plPlayerFor = localPlayer; _plPlayer = ResolveComponent(localPlayer, "Player"); }
        return _plPlayer;
    }

    /// <summary>Local character name (validated via StdWString @ Player+0x1B0).</summary>
    public string PlayerName(nint localPlayer)
    {
        var c = PlayerComp(localPlayer);
        return c == 0 ? "" : ReadStdWString(c + Poe2.PlayerComponent.Name);
    }

    /// <summary>Local character level (byte @ Player+0x204).</summary>
    public int PlayerLevel(nint localPlayer)
    {
        var c = PlayerComp(localPlayer);
        return c != 0 && _reader.TryReadStruct<byte>(c + Poe2.PlayerComponent.Level, out var b) ? b : 0;
    }

    /// <summary>Player grid position (from the Render component's world position ÷ grid ratio).</summary>
    public System.Numerics.Vector2? PlayerGrid(nint localPlayer) => EntityGrid(localPlayer);

    /// <summary>Player world position (the Render component's CurrentWorldPosition, incl. Z). RENDER-RATE
    /// safe — resolves + caches the player's Render component on demand (same path as <see cref="PlayerGrid"/>),
    /// so the render thread can anchor the guidance line at the player's live feet without the local player
    /// needing to be in the (world-rate) entity list.</summary>
    public Vector3? PlayerWorld(nint localPlayer) => EntityWorld(localPlayer);

    public readonly record struct Vitals(int HpCur, int HpUnreserved, int ManaCur, int ManaUnreserved,
        int EsCur, int EsUnreserved)
    {
        public float HpPct   => HpUnreserved   > 0 ? 100f * HpCur   / HpUnreserved   : 100f;
        public float ManaPct => ManaUnreserved > 0 ? 100f * ManaCur / ManaUnreserved : 100f;
        // ES% is 100 when there is no ES pool (Max 0, or the offset couldn't be confirmed) so an
        // "ES" / "Either" flask trigger never fires on a build that has no shield to restore.
        public float EsPct   => EsUnreserved   > 0 ? 100f * EsCur   / EsUnreserved   : 100f;
        public bool  HasEs   => EsUnreserved > 0;
    }

    private nint _plLife, _plLifeFor;

    // Self-healing vital offsets. Components are resolved by NAME (robust across patches), but the
    // VitalStruct offsets WITHIN the Life component slide between patches (e.g. 2026-06-04: Health
    // 0x1A8→0x1B0, Mana 0x1F8→0x208, ES 0x230→0x248 — each by a different small amount). We validate
    // each configured offset against a live Life component once; if it doesn't read a valid pool we
    // re-anchor it (see ResolveVitalOffset) so a minor layout shift degrades gracefully (auto-flask +
    // HP bars keep working) instead of silently reading 0. The same offsets back the monster HP reads
    // (identical component layout). Logged loudly so the table still gets updated.
    //
    // Health and ES BOTH self-heal; Mana is best-effort (kept for the mana flask but never gated on).
    // _esOffKnown gates the ES read: if ES can't be confirmed near its offset we suppress the read
    // entirely (→ ES% reads 100 → the ES/Either trigger never fires) rather than risk reading a decoy
    // and misfiring the flask.
    private int _healthOff = Poe2.Life.Health, _manaOff = Poe2.Life.Mana, _esOff = Poe2.Life.EnergyShield;

    private bool _esOffKnown = true;

    private bool _vitalOffsetsResolved;

    // Stricter than VitalStruct.LooksValid: ReservedFraction is reservation in basis-points, so a real
    // pool keeps it in [0, 10000]. The Life component is littered with decoy structs that pass the
    // loose check but carry out-of-range/garbage ReservedFraction — this filters most of them out.
    private static bool LooksLikeRealPool(in VitalStruct v)
        => v.LooksValid() && v.ReservedFraction >= 0 && v.ReservedFraction <= 10000;

    // Resolve one pool's offset within the Life component, healing small drift. Returns the configured
    // offset if it still reads a valid pool (the normal case); otherwise searches a TIGHT window
    // anchored on the configured offset and returns the valid pool nearest to it, or -1 if none. The
    // window is deliberately narrow so the distant decoy VitalStructs (verified live to sit well away
    // from each real pool) stay out of reach — we heal a slide, we don't hunt blindly.
    private int ResolveVitalOffset(nint lifeComp, int configured)
    {
        if (_reader.TryReadStruct<VitalStruct>(lifeComp + configured, out var v) && v.LooksValid())
            return configured;
        int best = -1, bestDist = int.MaxValue;
        for (var off = Math.Max(0x80, configured - 0x18); off <= configured + 0x30; off += 4)
        {
            if (_reader.TryReadStruct<VitalStruct>(lifeComp + off, out var c) && LooksLikeRealPool(c))
            {
                var d = Math.Abs(off - configured);
                if (d < bestDist) { bestDist = d; best = off; }
            }
        }
        return best;
    }

    private void EnsureVitalOffsets(nint lifeComp)
    {
        if (_vitalOffsetsResolved || lifeComp == 0) return;

        // Health is safety-critical and reliably the FIRST valid pool, so it gets an extra fallback:
        // if it won't anchor near its configured offset, take the first valid pool in the component.
        var health = ResolveVitalOffset(lifeComp, Poe2.Life.Health);
        if (health < 0)
        {
            for (var off = 0x80; off <= 0x400; off += 4)
                if (_reader.TryReadStruct<VitalStruct>(lifeComp + off, out var v) && LooksLikeRealPool(v)) { health = off; break; }
            if (health < 0) return; // not in-game yet / unreadable — retry next call (don't latch)
        }

        _vitalOffsetsResolved = true;
        _healthOff = health;
        if (_healthOff != Poe2.Life.Health)
            Console.WriteLine($"Poe2Live: Life Health offset appears to have drifted — auto-relocated " +
                $"0x{Poe2.Life.Health:X}->0x{_healthOff:X} (life flask + HP bars keep working). Update " +
                $"Poe2.Life + re-validate (Research --vitals).");

        // ES self-heals the same way; if it can't be confirmed we suppress the read (safe: ES% → 100).
        var es = ResolveVitalOffset(lifeComp, Poe2.Life.EnergyShield);
        _esOffKnown = es >= 0;
        if (es >= 0)
        {
            _esOff = es;
            if (_esOff != Poe2.Life.EnergyShield)
                Console.WriteLine($"Poe2Live: Life EnergyShield offset appears to have drifted — auto-relocated " +
                    $"0x{Poe2.Life.EnergyShield:X}->0x{_esOff:X} (ES flask keeps working). Update Poe2.Life + re-validate (Research --vitals).");
        }
        else
        {
            Console.WriteLine($"Poe2Live: Life EnergyShield offset (0x{Poe2.Life.EnergyShield:X}) couldn't be confirmed — " +
                "ES flask trigger suppressed (reads as full) until the table is updated (Research --vitals).");
        }

        // Mana: best-effort relocation only. The mana flask is never gated on a confident read — if it
        // drifts past the window it keeps the configured offset (reads 0 → mana% 100 → no misfire).
        var mana = ResolveVitalOffset(lifeComp, Poe2.Life.Mana);
        if (mana >= 0) _manaOff = mana;
    }

    /// <summary>
    /// Local player HP/mana as current vs. *unreserved* max (auras reserve part of the pool, so
    /// raw Max would understate the real % full). Drives the auto-flask thresholds. Returns null
    /// when the Life component / vitals can't be read plausibly (Max &lt;= 0) — the caller MUST treat
    /// that as "unknown" and NOT fire flasks, rather than assuming full/empty.
    /// </summary>
    public Vitals? PlayerVitals(nint localPlayer)
    {
        if (localPlayer != _plLifeFor) { _plLifeFor = localPlayer; _plLife = ResolveComponent(localPlayer, "Life"); }
        if (_plLife == 0) return null;
        EnsureVitalOffsets(_plLife);
        if (!_reader.TryReadStruct<VitalStruct>(_plLife + _healthOff, out var hp) || hp.Max <= 0) return null;
        _reader.TryReadStruct<VitalStruct>(_plLife + _manaOff, out var mana);
        VitalStruct es = default; // suppressed (stays 0 → ES% 100) when the offset isn't confirmed
        if (_esOffKnown) _reader.TryReadStruct<VitalStruct>(_plLife + _esOff, out es);
        return new Vitals(hp.Current, Unreserved(hp), mana.Current, Unreserved(mana), es.Current, Unreserved(es));
    }

    private static int Unreserved(VitalStruct v)
    {
        var reserved = (int)Math.Ceiling(v.ReservedFraction / 10000f * v.Max) + v.ReservedFlat;
        return Math.Max(0, v.Max - reserved);
    }

    /// <summary>The live state of a runeshape-monolith device (the persistent Expedition2Encounter POI
    /// entity), read off the device → StateMachine → RuneStation chain (see <see cref="Poe2.RuneStation"/>).
    /// <paramref name="Resolved"/> false means the station chain didn't resolve (e.g. transient read, or the
    /// device isn't a monolith). Feeds <see cref="RuneMonolithCatalog.Offers"/> to compute the rewards the
    /// monolith will offer WITHOUT opening its panel. Persists out of the network bubble → readable
    /// area-wide. <paramref name="Collected"/> = the MinimapIcon completed flag (reward already claimed).</summary>
    public readonly record struct MonolithState(
        bool Resolved, int HoleCount, int AnchorIdx, int AnchorPos, bool IsUnique, bool Collected);

    /// <summary>Optional Overlay-supplied matcher: given a tile path, returns a friendly label (possibly
    /// empty) when the user wants that tile surfaced as a landmark, or null to ignore it. Lets users add
    /// their own landmark/tile patterns at runtime on top of the built-in keyword filter + curated list.
    /// Set by RadarApp; call <see cref="InvalidateLandmarks"/> after the pattern set changes so the
    /// per-area scan cache rebuilds.</summary>
    public Func<string, string?>? CustomLandmarkMatch { get; set; }

    /// <summary>Optional Overlay-supplied curated-label lookup: (areaCode, tilePath) → friendly label,
    /// or null. Lets a user-editable overlay sit on top of the baked-in <see cref="CustomLandmarkData"/>
    /// (the "Landmarks" tab). When unset, the baked data is used directly. Call <see cref="InvalidateLandmarks"/>
    /// after edits so the per-area scan rebuilds.</summary>
    public Func<string, string, string?>? CuratedLookup { get; set; }

    /// <summary>Max gap (in TILES, Chebyshev) between cells still treated as one landmark cluster.
    /// Larger merges nearby copies of a reusable tile into fewer markers; smaller splits them. Set by
    /// the Overlay from <c>RadarSettings.LandmarkClusterGap</c>; call <see cref="InvalidateLandmarks"/>
    /// after changing it so the per-area scan rebuilds. Clamped to a sane range when used.</summary>
    public int LandmarkClusterGap { get; set; }

= 2;

    private readonly HashSet<nint> _everHidden = new();

    private readonly HashSet<nint> _everVisible = new();

    /// <summary>One offered reward in the post-ritual TRIBUTE SHOP: the reward item's identity (for pricing —
    /// uniques key off <see cref="Art"/>, everything else off the base-type <see cref="Name"/>) and its tile's
    /// SCREEN rect (already scaled). Value lookup + drawing happen overlay-side.</summary>
    public readonly record struct RitualReward(Rarity Rarity, string? Art, string? Name, bool Identified, float X, float Y, float W, float H);

    /// <summary>Identity + slot box for the item under the cursor in ANY item UI (inventory, stash, vendor).
    /// <see cref="Item"/> is the item entity; <see cref="Rarity"/>/<see cref="Art"/>/<see cref="Identified"/>/
    /// <see cref="Name"/> are its price identity (uniques key off Art, everything else off the base Name),
    /// <see cref="Stack"/> is the stack size (1 when not a stack). <see cref="BoxX"/>..<see cref="BoxH"/> are
    /// the hovered item SLOT's screen rect — the overlay anchors its price chip to the icon. (We anchor to the
    /// item, not the game tooltip: the tooltip is hosted behind invisible containers whose rect math we can't
    /// reproduce reliably, so it mislocated the chip. The slot rect is computed by the same projection as every
    /// other UI element and is dependable.)</summary>
    public readonly record struct HoveredItem(nint Item, Rarity Rarity, string? Art, bool Identified,
        string? Name, int Stack, float BoxX, float BoxY, float BoxW, float BoxH);

    /// <summary>Resolve a component address by name via EntityDetails → ComponentLookUp (StdBucket) → ComponentList.</summary>
    private nint ResolveComponent(nint entity, string name)
    {
        var details = Ptr(entity + Poe2.Entity.EntityDetailsPtr);
        if (details == 0) return 0;
        var lookup = Ptr(details + Poe2.EntityDetails.ComponentLookUpPtr);
        if (lookup == 0) return 0;
        if (!_reader.TryReadStruct<StdVector>(entity + Poe2.Entity.ComponentList, out var compList)) return 0;
        var compCount = ((long)compList.Last - (long)compList.First) / 8;
        if (compCount is <= 0 or > 256) return 0;

        var bFirst = Ptr(lookup + Poe2.ComponentLookUp.NameAndIndexBucket);
        if (!_reader.TryReadStruct<nint>(lookup + Poe2.ComponentLookUp.NameAndIndexBucket + 8, out var bLast)) return 0;
        var entries = ((long)bLast - (long)bFirst) / Poe2.ComponentLookUp.EntryStride;
        if (bFirst == 0 || entries is <= 0 or > 256) return 0;

        for (long i = 0; i < entries; i++)
        {
            var e = bFirst + (nint)(i * Poe2.ComponentLookUp.EntryStride);
            var namePtr = Ptr(e);
            if (!_reader.TryReadStruct<int>(e + 8, out var index)) continue;
            if (index < 0 || index >= compCount) continue;
            if (_reader.ReadStringUtf8(namePtr, 32) != name) continue;
            return Ptr(compList.First + (nint)(index * 8));
        }
        return 0;
    }

    /// <summary>Read an entity's metadata path: EntityDetails(+0x08) → name StdWString(+0x08).</summary>
    private string ReadMetadata(nint entity)
    {
        var details = Ptr(entity + Poe2.Entity.EntityDetailsPtr);
        if (details == 0) return string.Empty;
        return ReadStdWString(details + Poe2.EntityDetails.Name);
    }

    private string ReadStdWString(nint addr)
    {
        if (!_reader.TryReadStruct<int>(addr + 0x10, out var len) || len <= 0 || len > 1024) return string.Empty;
        if (len < 8) return _reader.ReadStringUtf16(addr, len);
        var ptr = Ptr(addr);
        return ptr == 0 ? string.Empty : _reader.ReadStringUtf16(ptr, len);
    }

    /// <summary>Safe pointer read: 0 on failure or implausible (non-user-mode) value.</summary>
    private nint Ptr(nint addr)
    {
        if (!_reader.TryReadStruct<nint>(addr, out var p)) return 0;
        var u = (ulong)p;
        return (u < 0x10000 || u > 0x7FFFFFFFFFFF) ? 0 : p;
    }
}
