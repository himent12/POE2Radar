namespace POE2Radar.Core.Game;

public sealed partial class Poe2Live
{
    // Per-entity frozen data, keyed by entity object address (stable within an area).
    private readonly Dictionary<nint, nint> _renderAddr = new();

    private readonly Dictionary<nint, nint> _lifeAddr = new();

    private readonly Dictionary<nint, nint> _posAddr = new();

    private readonly Dictionary<nint, nint> _ompAddr = new();

    private readonly Dictionary<nint, nint> _chestAddr = new();

    private readonly Dictionary<nint, EntityCategory> _category = new();

    private readonly Dictionary<nint, string> _meta = new();

    private readonly Dictionary<nint, nint> _iconAddr = new();

    private readonly Dictionary<nint, Rarity> _rarity = new();

    private readonly Dictionary<nint, string[]> _mods = new();

    private readonly Dictionary<nint, (Rarity rarity, string? art, bool identified, string? name)> _itemIdent = new();

    private readonly Dictionary<nint, uint> _idAt = new();

    private nint _entCacheKey;

    /// <summary>
    /// Walk the awake-entity std::map and project each to a grid dot with a category. Visuals /
    /// decorations (id ≥ 0x40000000) are skipped. Render addresses + categories are cached per
    /// entity for the area's lifetime; the per-tick cost is then ~1 pointer read per entity.
    /// </summary>
    public List<EntityDot> Entities(nint areaInstance)
    {
        if (areaInstance != _entCacheKey)
        {
            _renderAddr.Clear(); _lifeAddr.Clear(); _posAddr.Clear(); _ompAddr.Clear(); _chestAddr.Clear();
            _category.Clear(); _meta.Clear(); _iconAddr.Clear(); _rarity.Clear(); _mods.Clear(); _itemIdent.Clear(); _idAt.Clear();
            _entCacheKey = areaInstance;
        }

        var dots = new List<EntityDot>(256);
        var head = Ptr(areaInstance + Poe2.AreaInstance.AwakeEntities);
        _reader.TryReadStruct<int>(areaInstance + Poe2.AreaInstance.AwakeEntities + 8, out var size);
        if (head == 0 || size <= 0 || size > 100000) return dots;

        var root = Ptr(head + Poe2.StdMapNode.Parent);
        _entQueue.Clear(); _entQueue.Enqueue(root);
        _entVisited.Clear();
        _modReadBudget = ModReadBudgetPerPass;
        _itemReadBudget = ItemReadBudgetPerPass;
        while (_entQueue.Count > 0 && _entVisited.Count < 200000)
        {
            var node = _entQueue.Dequeue();
            if (node == 0 || node == head || !_entVisited.Add(node)) continue;

            // One read for the whole node — Left/Right/IsNil/KeyId/ValueEntityPtr are contiguous in
            // 48 bytes, so this replaces 5 separate ReadProcessMemory syscalls per node with one.
            if (_reader.TryReadBytes(node, _nodeBuf) < _nodeBuf.Length) continue;
            if (_nodeBuf[Poe2.StdMapNode.IsNil] != 0) continue; // sentinel/nil — don't traverse its children

            var id = BitConverter.ToUInt32(_nodeBuf, Poe2.StdMapNode.KeyId);
            var entity = (nint)BitConverter.ToInt64(_nodeBuf, Poe2.StdMapNode.ValueEntityPtr);
            _entQueue.Enqueue((nint)BitConverter.ToInt64(_nodeBuf, Poe2.StdMapNode.Left));
            _entQueue.Enqueue((nint)BitConverter.ToInt64(_nodeBuf, Poe2.StdMapNode.Right));

            if (entity == 0 || id >= Poe2.EntityList.VisualIdThreshold) continue;

            // Recycle guard: entity object addresses are reused within an area as things die/spawn.
            // The std::map key id is the stable per-entity identity (monotonic, never reused in an
            // area), so if THIS address now carries a different id than we cached it under, the prior
            // occupant is gone — evict its frozen component addresses/category/rarity/icon so we don't
            // read a freed/reused Life or Render (stale HP bars over corpses, POIs flickering at stale
            // positions). Re-resolves fresh below.
            if (_idAt.TryGetValue(entity, out var prevId) && prevId != id) EvictEntity(entity);
            _idAt[entity] = id;

            var world = EntityWorld(entity);
            if (world is not { } wv) continue;
            var g = new System.Numerics.Vector2(wv.X / Poe2.WorldToGridRatio, wv.Y / Poe2.WorldToGridRatio);

            var cat = Categorize(entity);
            int hpCur = 0, hpMax = 0;
            var rarity = Rarity.NonMonster;
            var opened = false;
            if (cat is EntityCategory.Monster or EntityCategory.Player) (hpCur, hpMax) = ReadHp(entity);
            if (cat is EntityCategory.Monster or EntityCategory.Chest) rarity = ReadRarity(entity);
            if (cat == EntityCategory.Chest) opened = ReadChestOpened(entity);
            var mods = cat == EntityCategory.Monster ? ReadMods(entity) : null;
            var meta = _meta.GetValueOrDefault(entity, "");
            // Dropped items (WorldItem containers, categorized Other) carry a price-lookup identity: art
            // basename + rarity, read once off the inner item entity. Rarity then reflects the item.
            string? itemArt = null, itemName = null;
            var itemIdentified = true;
            if (cat == EntityCategory.Other && meta.Contains("WorldItem", StringComparison.Ordinal))
                (rarity, itemArt, itemIdentified, itemName) = ReadItemIdentity(entity);

            var (poi, iconComplete) = ReadIcon(entity);
            dots.Add(new EntityDot(id, entity, g, wv, cat, meta, hpCur, hpMax,
                poi, ReadReaction(entity), rarity, opened, iconComplete, mods, itemArt, itemIdentified, itemName));
        }
        return dots;
    }

    /// <summary>Drop every frozen per-entity cache entry for an address whose occupant has changed
    /// (the std::map key id no longer matches). Forces a fresh component re-resolve next read.</summary>
    private void EvictEntity(nint entity)
    {
        _renderAddr.Remove(entity); _lifeAddr.Remove(entity); _posAddr.Remove(entity);
        _ompAddr.Remove(entity); _chestAddr.Remove(entity); _category.Remove(entity);
        _meta.Remove(entity); _iconAddr.Remove(entity); _rarity.Remove(entity); _mods.Remove(entity); _itemIdent.Remove(entity);
    }

    /// <summary>
    /// The entity's POI state from its MinimapIcon component:
    /// <list type="bullet">
    /// <item><c>poi</c> — the game marks it as a map POI (component present).</item>
    /// <item><c>complete</c> — the game has FADED the icon because its encounter is finished
    ///   (CompletedState != 0). The component stays put once resolved, so we cache only its ADDRESS
    ///   and read the flag live every tick (it flips, e.g. on claiming an expedition reward).</item>
    /// </list>
    /// </summary>
    private (bool poi, bool complete) ReadIcon(nint entity)
    {
        if (!_iconAddr.TryGetValue(entity, out var icon))
        {
            icon = ResolveComponent(entity, "MinimapIcon");
            _iconAddr[entity] = icon; // cache even if 0, to avoid re-walking non-POI entities
        }
        if (icon == 0) return (false, false);
        var complete = _reader.TryReadStruct<int>(icon + Poe2.MinimapIcon.CompletedState, out var s) && s != 0;
        return (true, complete);
    }

    /// <summary>Resolve a monolith device's hole count + anchor rune. Stateless per call (no caching — the
    /// station ptr is stable per area but cheap to re-walk, and the caller throttles at world rate).</summary>
    public MonolithState ReadMonolith(nint device)
    {
        var (_, collected) = ReadIcon(device);
        var fail = new MonolithState(false, 0, -1, -1, false, collected);

        var sm = ResolveComponent(device, "StateMachine");
        if (sm == 0) return fail;
        var first = Ptr(sm + Poe2.StateMachine.ListenerVec);
        if (first == 0 || !_reader.TryReadStruct<nint>(sm + Poe2.StateMachine.ListenerVec + 8, out var last)) return fail;
        var n = ((long)last - first) / 8;
        if (n is <= 0 or > 256) return fail;

        nint station = 0;
        for (long i = 0; i < n; i++)
        {
            var node = Ptr(first + (nint)(i * 8));
            if (node == 0) continue;
            var sub = Ptr(node);                                   // = station + ListenerSub
            if (sub == 0) continue;
            var cand = sub - Poe2.RuneStation.ListenerSub;
            if (Ptr(cand + Poe2.RuneStation.Owner) == device) { station = cand; break; }
        }
        if (station == 0) return fail;

        if (!_reader.TryReadStruct<int>(station + Poe2.RuneStation.HoleCount, out var holes) || holes is <= 0 or > 16)
            return fail;
        _reader.TryReadStruct<int>(station + Poe2.RuneStation.AnchorPos, out var pos);

        var rowPtr = Ptr(station + Poe2.RuneStation.AnchorRef);
        if (rowPtr == 0) return new MonolithState(true, holes, -1, -1, true, collected); // anchor-less → "unique"

        // anchor rune index = (rowPtr − tableBase)/RuneStride; tableBase is per-area (deref holder+0x28).
        var holder = Ptr(station + Poe2.RuneStation.AnchorHolder);
        var p1 = Ptr(holder + 0x28);
        if (p1 == 0 || !_reader.TryReadStruct<long>(p1, out var tableBase) || tableBase == 0)
            return new MonolithState(true, holes, -1, pos, false, collected); // station ok, anchor decode failed
        var delta = (long)rowPtr - tableBase;
        var idx = (delta >= 0 && delta % Poe2.RuneStation.RuneStride == 0)
            ? (int)(delta / Poe2.RuneStation.RuneStride) : -1;
        if (idx < 0 || idx >= Poe2.RuneStation.RuneCount) idx = -1;
        return new MonolithState(true, holes, idx, pos, false, collected);
    }

    private Rarity ReadRarity(nint entity)
    {
        // Rarity is fixed at spawn — read it once per entity and cache the value (not just the addr).
        if (_rarity.TryGetValue(entity, out var cached)) return cached;
        if (!_ompAddr.TryGetValue(entity, out var omp))
        {
            omp = ResolveComponent(entity, "ObjectMagicProperties");
            _ompAddr[entity] = omp;
        }
        if (omp == 0) { _rarity[entity] = Rarity.Normal; return Rarity.Normal; }
        if (!_reader.TryReadStruct<int>(omp + Poe2.ObjectMagicProperties.Rarity, out var r))
            return Rarity.Normal; // transient read failure — don't poison the cache
        var rarity = r is >= 0 and <= 3 ? (Rarity)r : Rarity.Normal;
        _rarity[entity] = rarity;
        return rarity;
    }

    /// <summary>
    /// The monster's affix mod ids (auras/buffs) from ObjectMagicProperties+Mods. Like rarity, mods are
    /// fixed at spawn, so the result is cached per entity (even when empty) and read at most once. New
    /// (uncached) reads are bounded by <see cref="_modReadBudget"/> per pass so a fresh pack fills over a
    /// few world ticks rather than stalling one. Reads ONLY the rolled-affix vector (+0x168); the +0x150
    /// rarity-placeholder filler (MonsterRare/Magic/Unique{N}) is intentionally excluded.
    /// </summary>
    private string[]? ReadMods(nint entity)
    {
        if (_mods.TryGetValue(entity, out var cached)) return cached.Length == 0 ? null : cached;
        if (_modReadBudget <= 0) return null;                  // out of budget this pass — retry next tick (don't cache)

        if (!_ompAddr.TryGetValue(entity, out var omp))
        {
            omp = ResolveComponent(entity, "ObjectMagicProperties");
            _ompAddr[entity] = omp;
        }
        if (omp == 0) { _mods[entity] = Array.Empty<string>(); return null; }
        _modReadBudget--;

        // StdVector at omp+Mods: [First, Last, End]. Element stride ModElemStride; each element holds a
        // record pointer at +ModRecordPtr; the record's +ModIdString is a UTF-16 mod-id string.
        if (_reader.TryReadBytes(omp + Poe2.ObjectMagicProperties.Mods, _modVecBuf) < _modVecBuf.Length)
            return null; // transient read failure — leave uncached, retry next tick
        var first = (nint)BitConverter.ToInt64(_modVecBuf, 0);
        var last = (nint)BitConverter.ToInt64(_modVecBuf, 8);
        var len = (long)last - first;
        const int stride = Poe2.ObjectMagicProperties.ModElemStride;
        if (first == 0 || len <= 0 || len > 0x4000 || len % stride != 0)
        {
            _mods[entity] = Array.Empty<string>(); return null; // no/garbage affix vector — cache as empty
        }
        var n = (int)(len / stride);
        if (n > 100) { _mods[entity] = Array.Empty<string>(); return null; }

        var list = new List<string>(n);
        for (var i = 0; i < n; i++)
        {
            var rec = Ptr(first + (nint)(i * stride + Poe2.ObjectMagicProperties.ModRecordPtr));
            if (rec == 0) continue;
            // record's +ModIdString qword is a POINTER to the UTF-16 mod id (not the string inline) — must
            // deref even when the offset is 0 (Ptr(rec+0) = *rec). Skipping this deref read the record
            // pointer's own bytes as text → garbage → every monster cached as "no mods".
            var idPtr = Ptr(rec + Poe2.ObjectMagicProperties.ModIdString);
            if (idPtr == 0) continue;
            var s = _reader.ReadStringUtf16(idPtr, 64);
            if (LooksLikeModId(s) && !list.Contains(s)) list.Add(s);
        }
        var arr = list.Count == 0 ? Array.Empty<string>() : list.ToArray();
        _mods[entity] = arr;
        return arr.Length == 0 ? null : arr;
    }

    /// <summary>
    /// Resolve a dropped item's identity for price lookup: unwrap the WorldItem container → inner item
    /// entity, read its rarity (Mods+0x94) and 2D-art basename (RenderItem+0x28 → UTF-16 .dds path). Like
    /// other item facts these are fixed once dropped, so the result is cached per entity and read at most
    /// once; new reads are bounded per pass by <see cref="_itemReadBudget"/>. Returns (rarity, artBasename);
    /// artBasename is null when the item can't be resolved.
    /// </summary>
    private (Rarity, string?, bool, string?) ReadItemIdentity(nint entity)
    {
        if (_itemIdent.TryGetValue(entity, out var cached)) return cached;
        if (_itemReadBudget <= 0) return (Rarity.NonMonster, null, true, null);   // out of budget — retry next tick (don't cache)

        var wi = ResolveComponent(entity, "WorldItem");
        var item = wi == 0 ? 0 : Ptr(wi + Poe2.WorldItemComponent.ItemEntity);
        if (item == 0) { var v = (Rarity.NonMonster, (string?)null, true, (string?)null); _itemIdent[entity] = v; return v; }
        _itemReadBudget--;

        var result0 = ReadIdentityFromItem(item);
        _itemIdent[entity] = result0;
        return result0;
    }

    /// <summary>Read an item ENTITY's identity directly (rarity/art/identified/base-type name) — the shared
    /// core of <see cref="ReadItemIdentity"/> without the WorldItem unwrap or per-entity cache, for callers
    /// (ritual shop, inventory) that already hold the item entity. See the field notes inline.</summary>
    private (Rarity, string?, bool, string?) ReadIdentityFromItem(nint item)
    {
        // Rarity (+0x94) + Identified (+0x90) from the item's Mods component (distinct from monster
        // ObjectMagicProperties+0x144). Identified defaults true (non-uniques / no Mods comp aren't "unID").
        var rarity = Rarity.NonMonster;
        var identified = true;
        var modsComp = ResolveComponent(item, "Mods");
        if (modsComp != 0)
        {
            if (_reader.TryReadStruct<int>(modsComp + Poe2.ModsComponent.Rarity, out var r) && r is >= 0 and <= 3)
                rarity = (Rarity)r;
            if (_reader.TryReadStruct<int>(modsComp + Poe2.ModsComponent.Identified, out var idf))
                identified = idf != 0;
        }

        // 2D-art .dds path → basename (the price key). RenderItem+0x28 is a pointer to the UTF-16 path.
        string? art = null;
        var renderItem = ResolveComponent(item, "RenderItem");
        if (renderItem != 0)
        {
            var pathPtr = Ptr(renderItem + Poe2.RenderItemComponent.ResourcePath);
            if (pathPtr != 0)
            {
                var full = _reader.ReadStringUtf16(pathPtr, 128);
                art = ArtBasename(full);
            }
        }

        // Rendered base-type display NAME (Base +0x10 → row +0x30 → UTF-16). The price key for NON-uniques:
        // currency/runes/essences TIERS share one .dds art (Orb/Greater/Perfect → "CurrencyAddModToMagic"),
        // so only the exact name (e.g. "Greater Orb of Augmentation") disambiguates them.
        string? name = null;
        var baseComp = ResolveComponent(item, "Base");
        if (baseComp != 0)
        {
            var nameRow = Ptr(baseComp + Poe2.BaseComponent.NameRow);
            var namePtr = nameRow == 0 ? 0 : Ptr(nameRow + Poe2.BaseComponent.RowDisplayName);
            if (namePtr != 0) { var s = _reader.ReadStringUtf16(namePtr, 64); if (!string.IsNullOrWhiteSpace(s)) name = s.Trim(); }
        }

        return (rarity, art, identified, name);
    }

    /// <summary>"Art/2DItems/Weapons/.../Uniques/Earthbound.dds" → "Earthbound" (last path segment, no
    /// extension). Returns null for empty/garbage so callers can ignore it.</summary>
    private static string? ArtBasename(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var slash = path.LastIndexOf('/');
        var start = slash >= 0 ? slash + 1 : 0;
        var dot = path.LastIndexOf('.');
        var end = dot > start ? dot : path.Length;
        if (end <= start) return null;
        var name = path[start..end];
        return name.Length >= 2 ? name : null;
    }

    /// <summary>A GGG mod id is a non-trivial identifier: letters/digits/underscore only, has a letter.</summary>
    private static bool LooksLikeModId(string s)
    {
        if (s.Length is < 3 or > 64) return false;
        var hasLetter = false;
        foreach (var c in s)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')) { hasLetter = true; continue; }
            if (c is (>= '0' and <= '9') or '_') continue;
            return false;
        }
        return hasLetter;
    }

    private byte ReadReaction(nint entity)
    {
        if (!_posAddr.TryGetValue(entity, out var pos))
        {
            pos = ResolveComponent(entity, "Positioned");
            _posAddr[entity] = pos;
        }
        if (pos == 0) return 0;
        return _reader.TryReadStruct<byte>(pos + Poe2.Positioned.Reaction, out var b) ? b : (byte)0;
    }

    private (int cur, int max) ReadHp(nint entity)
    {
        if (!_lifeAddr.TryGetValue(entity, out var life))
        {
            life = ResolveComponent(entity, "Life");
            _lifeAddr[entity] = life;
        }
        if (life == 0) return (0, 0);
        // Use the (possibly auto-relocated) Health offset so monster HP bars survive the same vital-
        // block drift the player vitals do. _healthOff == Poe2.Life.Health unless drift was detected.
        if (!_reader.TryReadStruct<VitalStruct>(life + _healthOff, out var v)) return (0, 0);
        return (v.Current, v.Max);
    }

    private Vector3? EntityWorld(nint entity)
    {
        if (!_renderAddr.TryGetValue(entity, out var render))
        {
            render = ResolveComponent(entity, "Render");
            _renderAddr[entity] = render; // cache even if 0, to avoid re-walking
        }
        if (render == 0) return null;
        if (!_reader.TryReadStruct<Vector3>(render + Poe2.Render.CurrentWorldPosition, out var w)) return null;
        return w;
    }

    private System.Numerics.Vector2? EntityGrid(nint entity)
        => EntityWorld(entity) is { } w ? new System.Numerics.Vector2(w.X / Poe2.WorldToGridRatio, w.Y / Poe2.WorldToGridRatio) : null;

    /// <summary>Chest opened state. The 2026-06-06 patch INVERTED this flag: Chest +0x168 is now 0
    /// while closed/openable and non-zero once opened/used (was the reverse). Validated live by diffing
    /// one rare chest closed-vs-opened — only +0x168 flipped (0→1; loot/interaction pointers nulled).
    /// A read failure returns not-opened (i.e. shows the chest): for chests, over-showing is far safer
    /// than silently hiding a real one — which is exactly the bug this flip caused.</summary>
    private bool ReadChestOpened(nint entity)
    {
        if (!_chestAddr.TryGetValue(entity, out var c)) { c = ResolveComponent(entity, "Chest"); _chestAddr[entity] = c; }
        if (c == 0) return false;
        return _reader.TryReadStruct<byte>(c + Poe2.ChestComponent.OpenState, out var b) && b != 0;
    }

    private EntityCategory Categorize(nint entity)
    {
        if (_category.TryGetValue(entity, out var c)) return c;
        var meta = ReadMetadata(entity);
        _meta[entity] = meta;
        c = meta switch
        {
            // NPCs FIRST: friendly NPCs (Alva, vendors…) live under "Metadata/Monsters/NPC/…", so the
            // "/NPC/" check must precede "/Monsters/" or they'd be miscategorized as combat monsters
            // (and a Unique-rarity NPC would draw the enemy unique star). "/NPC/" is the NPC marker.
            _ when meta.Contains("/NPC/", StringComparison.Ordinal)         => EntityCategory.Npc,
            // Real combat monsters only — exclude on-death/aura effect carriers (MonsterMods),
            // player/ally summons, and invisible effect daemons. Those clutter the map and aren't
            // fight targets. (Friendly/hostile is applied at draw time via Positioned.Reaction.)
            _ when meta.Contains("/Monsters/", StringComparison.Ordinal) && IsNonCombat(meta) => EntityCategory.Other,
            _ when meta.Contains("/Monsters/", StringComparison.Ordinal)   => EntityCategory.Monster,
            _ when meta.Contains("/Characters/", StringComparison.Ordinal)  => EntityCategory.Player,
            // Real chests only — exclude breakable props (urns/vases/pots/etc.) under /Chests/.
            _ when meta.Contains("/Chests", StringComparison.Ordinal) && IsBreakableProp(meta) => EntityCategory.Other,
            _ when meta.Contains("/Chests", StringComparison.Ordinal)       => EntityCategory.Chest,
            _ when meta.Contains("Transition", StringComparison.Ordinal)    => EntityCategory.Transition,
            _ when meta.Contains("/Terrain/", StringComparison.Ordinal)     => EntityCategory.Object,
            _                                                              => EntityCategory.Other,
        };
        _category[entity] = c;
        return c;
    }

    /// <summary>True for "/Chests/" entities that are destructible scenery (urns, vases, pots…) not loot chests.</summary>
    private static bool IsBreakableProp(string meta) =>
        meta.Contains("Urn", StringComparison.Ordinal) ||
        meta.Contains("Vase", StringComparison.Ordinal) ||
        meta.Contains("Pot", StringComparison.Ordinal) ||
        meta.Contains("Jar", StringComparison.Ordinal) ||
        meta.Contains("Sack", StringComparison.Ordinal) ||
        meta.Contains("Barrel", StringComparison.Ordinal) ||
        meta.Contains("Crate", StringComparison.Ordinal) ||
        meta.Contains("Debris", StringComparison.Ordinal) ||
        meta.Contains("Rubble", StringComparison.Ordinal) ||
        meta.Contains("Basket", StringComparison.Ordinal) ||
        meta.Contains("Coffin", StringComparison.Ordinal);

    /// <summary>True for "/Monsters/" entities that aren't real fight targets (effects / summons).</summary>
    private static bool IsNonCombat(string meta) =>
        meta.Contains("MonsterMods", StringComparison.Ordinal) ||
        meta.Contains("Summoned", StringComparison.Ordinal) ||
        meta.Contains("/Daemon/", StringComparison.Ordinal) ||
        meta.Contains("Invisible", StringComparison.Ordinal);
}
