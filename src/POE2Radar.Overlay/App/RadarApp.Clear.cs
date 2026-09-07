using System.Linq;
using POE2Radar.Overlay.Draw;
using NumVec2 = System.Numerics.Vector2;
using POE2Radar.Core;
using POE2Radar.Core.Game;
using POE2Radar.Core.Pathfinding;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Input;
using POE2Radar.Core.Native;
using POE2Radar.Overlay.Navigation;
using POE2Radar.Overlay.Web;

namespace POE2Radar.Overlay;

public sealed partial class RadarApp
{
    // Bot master (opt-in). F3 kill-switch. Default OFF. Arms quest follow + path move + combat.
    // F4 combat stays independently toggleable. Not writable via the dashboard.
    private volatile bool _botEnabled;

    private volatile string _botNote = "OFF (F3)";

    // Quest follow (opt-in nav). Armed by F3 bot master. Selects a nav target per zone;
    // MaintainRoutes owns the A*; DecideUse taps interact on arrival. Not writable via the dashboard.
    // Paused while map-clear (F2) is on.
    private volatile bool _questFollow;

    private volatile string _questFollowNote = "OFF (F3)";

    private volatile string? _questFollowId;

    // User pin from F6 / legend while quest follow is on. Last press wins; auto-pick
    // from zone notes is skipped until F7 or the pin despawns. Unique bosses still interrupt.
    private volatile string? _questPinId;

    private volatile bool _questFollowHasGrid;

    private volatile float _questFollowGx, _questFollowGy;

    // Map clear (opt-in nav). F2 kill-switch. Walks unexplored walkable cells; unique bosses and
    // live hostiles take priority. Arms path move + combat. Not writable via the dashboard.
    private volatile bool _mapClear;

    private volatile string _mapClearNote = "OFF (F2)";

    private readonly HashSet<long> _mapClearVisited = new();

    private readonly List<MapClear.MobHint> _clearMobs = new();

    // Stuck-target watchdog (world thread): give up on a cell/mob the bot stops getting closer to.
    private string? _clearStuckId;

    private float _clearStuckBestD;

    private DateTime _clearStuckSince;

    private readonly Dictionary<uint, DateTime> _clearIgnoredMobs = new();
    // Map events (world thread writes, render thread reads).
    private readonly Dictionary<uint, DateTime> _eventBlacklist = new();
    private sealed record EventBox(MapEvents.Event Ev);
    private volatile EventBox? _eventTargetBox;
    private MapEvents.Event? _eventTarget
    {
        get => _eventTargetBox?.Ev;
        set => _eventTargetBox = value is { } v ? new EventBox(v) : null;
    }
    private volatile HashSet<uint> _imprisonedIds = new();
    private volatile HashSet<uint> _pendingEventIds = new();
    private readonly Dictionary<uint, int> _eventClicks = new();
    private DateTime _eventFiredAt = DateTime.MinValue;
    private volatile string _eventNote = "";

    private NumVec2 _clearPrevPlayer;

    private NumVec2 _clearHeading;

    private readonly List<QuestFollow.LandmarkHint> _questLm = new();

    private readonly List<QuestFollow.EntityHint> _questEnt = new();

    /// <summary>Zone change: remember the leaving zone's selection (by its instance hash), then either
    /// RESTORE the selection we previously had for the zone we're entering (so a town round-trip keeps
    /// your pathing) or — on a first visit — seed it from the persistent auto-nav patterns. Trackers are
    /// NOT touched here — the per-tick reconciliation (ReconcileTrackers) syncs them to _selectedIds.</summary>
    private void OnAreaChanged(uint areaHash)
    {
        int count; bool restored;
        var follow = _questFollow || _mapClear || _farmLoop;
        _questFollowId = null;
        _questPinId = null;
        _mapClearVisited.Clear();
        _clearIgnoredMobs.Clear();
        _clearStuckId = null;
        _combatWatch.Reset();
        lock (_eventBlacklist) _eventBlacklist.Clear();
        _eventTarget = null;
        _imprisonedIds = new HashSet<uint>();
        _pendingEventIds = new HashSet<uint>();
        lock (_navLock)
        {
            // Save what was selected in the zone we're leaving, keyed by ITS instance hash.
            if (_selectionAreaHash != 0) RememberZoneSelection(_selectionAreaHash, _selectedIds);

            _selectedIds.Clear();
            _selectionCapWarned = false;
            _selectionAreaHash = areaHash;

            // Quest follow / map-clear owns the selection this visit — the picker fills it.
            // Skip restore/auto-path so the bot doesn't inherit leftover AutoPath targets.
            if (follow)
            {
                restored = false;
                count = 0;
            }
            else
            {
                // Returning to a remembered instance → restore its selection verbatim (the user's explicit
                // choices win, including an intentionally-empty one, so a zone they cleared stays cleared).
                List<string>? remembered = null;
                restored = areaHash != 0 && _zoneSelections.TryGetValue(areaHash, out remembered);
                if (restored)
                {
                    foreach (var id in remembered!)
                    {
                        if (_selectedIds.Count >= MaxSelectedTargets) break;
                        if (!_selectedIds.Contains(id)) _selectedIds.Add(id);
                    }
                }
                else
                {
                    // First visit to this instance: auto-select every target whose display rule opted into
                    // auto-pathing (the per-rule "Auto-path" flag), capped so colors/planning stay bounded.
                    foreach (var t in _navTargets)
                    {
                        if (_selectedIds.Count >= MaxSelectedTargets) break;
                        if (t.AutoPath && !_selectedIds.Contains(t.Id))
                            _selectedIds.Add(t.Id);
                    }
                }
                count = _selectedIds.Count;
            }
        }
        _selectedPaths = new List<SelectedPath>();

        if (count > 0)
            Console.WriteLine($"\nNav: {(restored ? "restored" : "auto-selected")} {count} target(s) on zone change.");
    }

    /// <summary>
    /// When quest follow is armed, pick one nav target from the current area's zone notes
    /// (or a live unique boss, then a Transition/waypoint/boss fallback) and select it so
    /// <see cref="MaintainRoutes"/> draws/A*s the route. Pure pick lives in
    /// <see cref="QuestFollow.PickTarget"/>. Unique monsters win the moment they spawn.
    /// </summary>
    private void ApplyQuestFollow(string areaCode, NumVec2 player)
    {
        if (!_questFollow)
        {
            _questFollowNote = _botEnabled && _mapClear ? "paused (clear)" : "OFF (F3)";
            _questFollowHasGrid = false;
            return;
        }

        var notes = ZoneGuide.Shared.Notes(areaCode)?.Notes ?? "";
        _questLm.Clear();
        foreach (var lm in _landmarks)
            _questLm.Add(new QuestFollow.LandmarkHint("t:" + lm.Key, lm.Name, lm.CuratedName, lm.Path));

        _questEnt.Clear();
        foreach (var e in _entities)
        {
            if (!e.IsAlive || e.IconComplete) continue;
            _questEnt.Add(new QuestFollow.EntityHint(
                "e:" + e.Id, EntityLabel(e.Metadata), e.Metadata, e.Poi,
                e.Category == Poe2Live.EntityCategory.Monster && e.Rarity == Poe2Live.Rarity.Unique,
                e.Category, e.Grid));
        }

        if (_questPinId is { } pin && !QuestFollow.IsLiveTarget(pin, _questLm, _questEnt))
            _questPinId = null;

        var id = QuestFollow.PickTarget(areaCode, notes, _questLm, _questEnt, player, _questPinId);
        var note = SetAutoNavTarget(id, "armed (no target)", "Quest follow");
        if (_questPinId is not null && id == _questPinId && note.StartsWith("→ ", StringComparison.Ordinal))
            note += " (F6)";
        _questFollowNote = note;
    }

    /// <summary>
    /// When map-clear is armed, stamp visited walkable cells around the player and pick the
    /// next clear target (unique boss, nearest hostile, then unexplored frontier). Pure pick
    /// lives in <see cref="MapClear.PickTarget"/>.
    /// </summary>
    /// <summary>A mob whose cell is off walkable ground counts as reachable if walkable ground in the
    /// player's region lies within this many cells of it (melee range slop).</summary>
    private const int UnreachableMobSnapCells = 4;

    private void ApplyMapClear(string areaCode, NumVec2 player)
    {
        if (!_mapClear && !_farmLoop)
        {
            _mapClearNote = "OFF (F2)";
            return;
        }

        var terrain = _terrain;
        if (terrain is { Walkable: { } walk, Width: var w, Height: var h } && w > 0 && h > 0)
            MapClear.StampVisited(_mapClearVisited, walk, w, h, player, _settings.MapClearStampRadius);

        var now = DateTime.UtcNow;
        if (_clearIgnoredMobs.Count > 0)
        {
            List<uint>? expired = null;
            foreach (var kv in _clearIgnoredMobs)
                if (kv.Value <= now) (expired ??= new()).Add(kv.Key);
            if (expired is not null) foreach (var k in expired) _clearIgnoredMobs.Remove(k);
        }

        // Map events: what to click, what is imprisoned (immune until its crystal is clicked).
        HashSet<uint> blacklist;
        lock (_eventBlacklist)
        {
            List<uint>? expiredEv = null;
            foreach (var kv in _eventBlacklist) if (kv.Value <= now) (expiredEv ??= new()).Add(kv.Key);
            if (expiredEv is not null) foreach (var k in expiredEv) _eventBlacklist.Remove(k);
            blacklist = new HashSet<uint>(_eventBlacklist.Keys);
        }
        var eventOpts = new MapEvents.Options(_settings.EventEssence, _settings.EventStrongbox, _settings.EventShrine,
            _settings.EventBreach, _settings.EventRitual, _settings.EventChests, _settings.EventClickStalled);
        var imprisoned = MapEvents.ImprisonedMonsters(_entities);
        var events = MapEvents.Pending(_entities, eventOpts, blacklist, _combatWatch.IgnoredIds);
        _imprisonedIds = imprisoned;
        _pendingEventIds = new HashSet<uint>(events.Select(ev => ev.Id));

        _clearMobs.Clear();
        foreach (var e in _entities)
        {
            if (!e.IsAlive || e.IconComplete) continue;
            if (e.Category != Poe2Live.EntityCategory.Monster || !e.HasLife || e.IsFriendly) continue;
            if (imprisoned.Contains(e.Id)) continue; // immune: click the crystal instead
            // Skip monsters the fight watchdog / stuck watchdog gave up on (unreachable, untargetable).
            if (_clearIgnoredMobs.ContainsKey(e.Id) || _combatWatch.IsIgnored(e.Id)) continue;
            // Provably unreachable (different walkable region — across a chasm, on a ledge): skip it NOW
            // instead of walking to the edge and waiting out the stuck watchdog.
            if (terrain is not null && !PathPlanner.IsReachable(terrain,
                    ((int)player.X, (int)player.Y), ((int)e.Grid.X, (int)e.Grid.Y), UnreachableMobSnapCells))
                continue;
            _clearMobs.Add(new MapClear.MobHint(
                "e:" + e.Id, e.Grid,
                e.Rarity == Poe2Live.Rarity.Unique));
        }

        // Smoothed heading (grid units/tick) so frontier ties keep the sweep going straight.
        var step = player - _clearPrevPlayer;
        _clearPrevPlayer = player;
        if (step.LengthSquared() < 25f) _clearHeading = _clearHeading * 0.85f + step * 0.15f;

        var id = MapClear.PickTarget(
            areaCode, player,
            terrain?.Walkable, terrain?.Width ?? 0, terrain?.Height ?? 0,
            _mapClearVisited, _clearMobs, _questFollowId, _settings.MapClearAggroRange, _clearHeading,
            _settings.MapClearStampRadius, events, _settings.EventRange);

        // Publish the event under the picked id (if any) for the render thread's use/click tick.
        _eventTarget = null;
        if (id is not null && id.StartsWith("e:", StringComparison.Ordinal) && uint.TryParse(id.AsSpan(2), out var evId))
            foreach (var ev in events) if (ev.Id == evId) { _eventTarget = ev; break; }

        // Stuck watchdog: no progress toward the target for MapClearStuckMs while not fighting → skip it.
        if (id is not null && TryResolveTargetGrid(id, out var goal))
        {
            var d = NumVec2.Distance(player, goal);
            if (!string.Equals(id, _clearStuckId, StringComparison.Ordinal))
            {
                _clearStuckId = id;
                _clearStuckBestD = d;
                _clearStuckSince = now;
            }
            else if (d < _clearStuckBestD - 1.5f || _inCombat)
            {
                _clearStuckBestD = d;
                _clearStuckSince = now;
            }
            else if ((now - _clearStuckSince).TotalMilliseconds >= Math.Max(1000, _settings.MapClearStuckMs))
            {
                var ignoreFor = TimeSpan.FromMilliseconds(Math.Max(1000, _settings.CombatIgnoreMs));
                if (MapClear.TryParseCell(id, out var cx, out var cy))
                {
                    if (terrain is { Walkable: { } wk, Width: var ww, Height: var wh })
                        MapClear.StampDisc(_mapClearVisited, wk, ww, wh, cx, cy, Math.Max(2, _settings.MapClearStampRadius / 2));
                }
                else if (id.StartsWith("e:", StringComparison.Ordinal) && uint.TryParse(id.AsSpan(2), out var eid))
                {
                    _clearIgnoredMobs[eid] = now + ignoreFor;
                }
                Console.WriteLine($"\nMap clear: stuck on {TargetLabel(id)} for {(now - _clearStuckSince).TotalSeconds:F0}s — skipping.");
                _clearStuckId = null;
                id = null;
            }
        }
        else
        {
            _clearStuckId = null;
        }

        _mapClearIdle = id is null && terrain is not null && _clearMobs.Count == 0 && events.Count == 0;
        _mapClearNote = SetAutoNavTarget(id, terrain is null ? "armed (no terrain)" : "armed (cleared)", "Map clear");
        if (_eventTarget is { } evt && !string.IsNullOrEmpty(_eventNote)) _mapClearNote = _eventNote;
        else if (_eventTarget is { } evt2) _mapClearNote = $"→ {evt2.Label}";
    }

    /// <summary>Select <paramref name="id"/> as the bot's A* target (quest follow or map-clear).</summary>
    private string SetAutoNavTarget(string? id, string noneNote, string logPrefix)
    {
        if (id is null)
        {
            _questFollowHasGrid = false;
            if (_questFollowId is { } stale)
            {
                lock (_navLock) _selectedIds.Remove(stale);
                _questFollowId = null;
            }
            return noneNote;
        }

        PublishQuestGrid(id);
        var label = TargetLabel(id);
        if (string.Equals(_questFollowId, id, StringComparison.Ordinal))
        {
            lock (_navLock)
            {
                PromoteAutoTarget(_selectedIds, id, MaxSelectedTargets);
            }
            return "→ " + label;
        }

        lock (_navLock)
        {
            if (_questFollowId is { } prev) _selectedIds.Remove(prev);
            PromoteAutoTarget(_selectedIds, id, MaxSelectedTargets);
        }
        _questFollowId = id;
        Console.WriteLine($"\n{logPrefix}: {label}");
        return "→ " + label;
    }

    internal static void PromoteAutoTarget(List<string> selected, string id, int capacity)
    {
        selected.Remove(id); // an already selected route must still become the mover's first route
        if (selected.Count >= capacity) selected.RemoveAt(selected.Count - 1);
        selected.Insert(0, id);
    }

    private void PublishQuestGrid(string id)
    {
        if (TryResolveTargetGrid(id, out var g))
        {
            _questFollowGx = g.X;
            _questFollowGy = g.Y;
            _questFollowHasGrid = true;
        }
        else _questFollowHasGrid = false;
    }

    /// <summary>
    /// Drop selected ENTITY targets the game has marked complete (IconComplete — e.g. a claimed
    /// expedition / used incursion device). Such an entity is hidden from the map and excluded from
    /// the nav-target list, but it lingers (faded) in the live entity set, so <see cref="TryResolveTargetGrid"/>
    /// would still resolve it and the route would keep pathing there. Pruning the id stops the route
    /// (its tracker is removed by the next ReconcileTrackers) and "sticks" via the per-zone memory.
    /// <para>Only prunes targets whose entity is PRESENT-and-complete — an entity merely out of network
    /// range (temporarily absent) is left selected so it resumes when you return to it.</para>
    /// </summary>
    private void PruneCompletedTargets()
    {
        lock (_navLock)
        {
            if (_selectedIds.Count == 0) return;
            _selectedIds.RemoveAll(id =>
            {
                if (!id.StartsWith("e:", StringComparison.Ordinal) || !uint.TryParse(id.AsSpan(2), out var eid))
                    return false;
                foreach (var e in _entities)
                    if (e.Id == eid) return e.IconComplete; // present → prune iff completed; else keep
                return false; // absent (out of range) → keep; it may return
            });
        }
    }

    /// <summary>
    /// F6 while quest follow is armed: replace the selection with the next-nearest nav target
    /// and pin it so zone-note auto-pick cannot steal it. Wraps. Unique bosses still interrupt.
    /// </summary>
    private void CycleQuestTarget()
    {
        var targets = _navTargets;
        if (targets.Count == 0) return;
        var pts = new (string Id, NumVec2 Grid)[targets.Count];
        for (var i = 0; i < targets.Count; i++)
            pts[i] = (targets[i].Id, targets[i].Grid);
        var next = QuestFollow.CycleTarget(pts, _state.Player, _questPinId ?? _questFollowId);
        if (next is null) return;
        lock (_navLock)
        {
            _selectedIds.Clear();
            _selectedIds.Add(next);
            _selectionCapWarned = false;
        }
        _questPinId = next;
        Console.WriteLine($"\nQuest follow (F6): {TargetLabel(next)}");
    }

    /// <summary>
    /// F3 just armed onto an existing F6 stack: keep only the last selected target (the one
    /// the user finished on) and pin it so the bot does not jump to a different zone-note quest.
    /// </summary>
    private void PinLastSelection()
    {
        string? last;
        lock (_navLock)
        {
            if (_selectedIds.Count < 2) return;
            last = _selectedIds[^1];
            if (last.StartsWith("c:", StringComparison.Ordinal)) return;
            _selectedIds.Clear();
            _selectedIds.Add(last);
            _questPinId = last;
        }
        Console.WriteLine($"\nQuest follow: pinned {TargetLabel(last)} (last F6)");
    }
}
