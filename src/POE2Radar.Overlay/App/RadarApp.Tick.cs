using System.Linq;
using POE2Radar.Overlay.Draw;
using NumVec2 = System.Numerics.Vector2;
using POE2Radar.Core;
using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Input;
using POE2Radar.Core.Native;
using POE2Radar.Overlay.Navigation;
using POE2Radar.Overlay.Web;

namespace POE2Radar.Overlay;

public sealed partial class RadarApp
{
    /// <summary>The background world loop (~<see cref="WorldHz"/> Hz, adaptive): resolve the chain on its
    /// own reader stack and run <see cref="WorldTick"/>, then sleep the remainder of the frame budget. All
    /// heavy reads live here so the render thread is never blocked. Never throws out (a read failure mid
    /// zone-load just publishes nothing this pass).</summary>
    private void WorldLoop()
    {
        var sw = new System.Diagnostics.Stopwatch();
        var budgetMs = 1000 / WorldHz;
        while (!_shutdown)
        {
            sw.Restart();
            try
            {
                if (_live.TryResolve(out var inGameState, out var areaInstance, out var localPlayer))
                    WorldTick(inGameState, areaInstance, localPlayer);
                else
                    PublishEmptyWorld();
            }
            catch (Exception ex) { Console.Error.WriteLine($"World tick error: {ex.Message}"); }
            _worldMs = (float)sw.Elapsed.TotalMilliseconds;
            Thread.Sleep(Math.Max(1, budgetMs - (int)sw.ElapsedMilliseconds));
        }
    }

    /// <summary>Not in game: publish an empty world snapshot + closed atlas so the render thread draws no
    /// stale entities/route (the selection itself is left intact so a loading screen keeps it).</summary>
    private void PublishEmptyWorld()
    {
        if (!ReferenceEquals(_world, WorldSnapshot.Empty)) _world = WorldSnapshot.Empty;
        if (!ReferenceEquals(_atlasRender, AtlasRender.Closed))
        {
            _atlasRender = AtlasRender.Closed;
            _builtAtlasOnce = false; _lastAtlasSig = 0;
        }
        if (!ReferenceEquals(_runeRender, RuneRender.Closed)) _runeRender = RuneRender.Closed;
        if (!ReferenceEquals(_exchangeRender, ExchangeRender.Closed)) _exchangeRender = ExchangeRender.Closed;
        if (!ReferenceEquals(_lootTags, LootTagRender.Empty)) _lootTags = LootTagRender.Empty;
    }

    /// <summary>One RENDER frame (render thread): fast per-frame reads on the render reader stack
    /// (player/vitals/camera/map + auto-flask + HP-bar live pos), then draw from the lock-free world
    /// snapshot. The heavy walk is on <see cref="WorldLoop"/>.</summary>
    private void Tick()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();   // no per-frame Stopwatch allocation
        _keyMemo.Clear();   // one key-state read per vk per frame across all hotkey handlers
        HandleHotkeys();
        HandleMacroHotkeys();

        var inGame = _liveRender.TryResolve(out var inGameState, out var areaInstance, out var localPlayer);
        var player = NumVec2.Zero;
        POE2Radar.Core.Game.Vector3? playerWorld = null;   // live player feet (incl. Z) for the world-ground route anchor
        var map = default(Poe2Live.MapUi);
        // Atlas routes/markers re-read per frame (positions from node elements) so they track pan at full FPS.
        NumVec2? atlasStart = null, atlasEnd = null, atlasCurrent = null;
        List<NumVec2>? atlasRoute = null;
        List<AtlasRouteInfo>? atlasAutoRoutes = null;

        // One lock-free read each of the two published snapshots — everything drawn this frame comes from
        // these two + the live render-rate reads below.
        var snap = _world;
        var ar = _atlasRender;
        var rr = _runeRender;
        var ex = _exchangeRender;
        var rit = _ritualRender;
        var mr = _monoRender;
        var lt = _lootTags;

        if (inGame)
        {
            _areaInstanceForApi = areaInstance; // for /api/tiles (read by _liveApi on the HTTP thread)
            _inGameStateForApi = inGameState;   // for /api/atlas + F10 route pick
            _areaHash = _liveRender.AreaHash(areaInstance);

            player = _liveRender.PlayerGrid(localPlayer) ?? NumVec2.Zero;
            playerWorld = _liveRender.PlayerWorld(localPlayer);   // same Render read PlayerGrid uses; live each frame
            map = _liveRender.ReadMap(inGameState, areaInstance);
            // Player name reads a StdWString (allocates a string) — read it only when the local-player
            // pointer changes (i.e. once per session), not every render frame.
            if (localPlayer != _charNameFor || _charName.Length == 0)
            {
                _charNameFor = localPlayer;
                _charName = _liveRender.PlayerName(localPlayer);
            }
            _cameraMatrix = _liveRender.CameraMatrix(inGameState);
            TickAutoFlask(localPlayer);

            // Refresh each HP-bar mob's live position + HP from the world tick's spec (which captured the
            // mob's Render/Life component addresses) using the RENDER reader — so bars track moving mobs
            // smoothly with no shared cache. Cheap: two tiny reads per bar, only the ~dozens of bar mobs.
            _hpFrame.Clear();
            foreach (var spec in snap.HpSpecs)
            {
                if (!_liveRender.TryLiveBarAt(spec.Render, spec.Life, out var w, out var cur, out var max) || max <= 0 || cur <= 0) continue;
                _hpFrame.Add(new HpBarTarget(w, Math.Clamp((float)cur / max, 0f, 1f), spec.Width, spec.Fill, spec.BorderWidth, spec.Border));
            }

            // Ground-item labels: re-read each priced item's live world position THIS frame (dropped items
            // bob), so the renderer projects a current position — the same per-frame reposition that keeps
            // HP bars smooth. life arg 0 → world-pos only.
            _itemFrame.Clear();
            foreach (var s in snap.ItemLabels)
            {
                if (!_liveRender.TryLiveBarAt(s.Render, 0, out var w, out _, out _)) continue;
                _itemFrame.Add(new ItemLabel(w, s.Name, s.Value, s.Highlight, s.ShowName));
            }

            // Loot-tag chips: re-read each matched tag's LIVE screen rect this frame on the render reader.
            // TryUiElementRect returns false for a tag that's gone invisible (panel/map open) or whose
            // element is stale after a zone change → it simply drops out. No projection → no jitter.
            _lootTagFrame.Clear();
            foreach (var s in lt.Specs)
            {
                if (!_liveRender.TryUiElementRect(s.El, _window.Width, _window.Height, out var rx, out var ry, out var rw, out var rh, requireFirstLine: s.TagText)) continue;
                _lootTagFrame.Add(new LootTagLabel(rx, ry, rw, rh, s.Value, s.Highlight));
            }

            // Hover market panel: the throttled world scan publishes the item slot (or ground cursor)
            // anchor and an immutable estimate; no network requests or UI-tree walk on the render thread.
            _hoverFrame = _hoverPrice is { Spec: var hs } hover
                && hover.AreaHash == _areaHash && DateTime.UtcNow - hover.UpdatedUtc < TimeSpan.FromMilliseconds(500)
                ? new HoverPriceLabel(hs.BoxX, hs.BoxY, hs.BoxW, hs.BoxH, hs.Text, hs.Sub, hs.Highlight, hs.Danger)
                : null;

            // Atlas marks/routes: re-read each node's live RelativePos this frame so the rings + route lines
            // track the atlas pan at full FPS (the world walk only refreshes baked positions ~30 Hz). Cheap —
            // only the handful of DRAWN marks + route points, one atomic 8-byte read each; a stale/closed node
            // falls back to its baked position (marks) or drops out (routes).
            _atlasMarkFrame.Clear();
            if (ar.Open)
            {
                foreach (var m in ar.Marks)
                    _atlasMarkFrame.Add(m.Element != 0 && _liveRender.TryRelPos(m.Element, out var mx, out var my) ? m with { X = mx, Y = my } : m);

                // Fresh live pos when the read validates, else the world-walk's baked pos — never a garbage
                // coordinate (which would streak route lines off-screen). Points stay contiguous (no dropping).
                NumVec2 Pt(AtlasPoint p) => p.El != 0 && _liveRender.TryRelPos(p.El, out var rx, out var ry) ? new NumVec2(rx, ry) : new NumVec2(p.Bx, p.By);
                atlasStart = ar.Start is { } sp ? Pt(sp) : (NumVec2?)null;
                atlasEnd = ar.End is { } ep ? Pt(ep) : (NumVec2?)null;
                atlasCurrent = ar.Current is { } cp ? Pt(cp) : (NumVec2?)null;
                if (ar.Route is { Count: >= 2 })
                {
                    var rl = new List<NumVec2>(ar.Route.Count);
                    foreach (var p in ar.Route) rl.Add(Pt(p));
                    atlasRoute = rl;
                }
                if (ar.AutoRoutes is { Count: > 0 })
                {
                    atlasAutoRoutes = new List<AtlasRouteInfo>(ar.AutoRoutes.Count);
                    foreach (var spec in ar.AutoRoutes)
                    {
                        var pl = new List<NumVec2>(spec.Points.Count);
                        foreach (var p in spec.Points) pl.Add(Pt(p));
                        if (pl.Count >= 2) atlasAutoRoutes.Add(new AtlasRouteInfo(pl, spec.Color, spec.Hops));
                    }
                }
            }
        }
        else { if (_hpFrame.Count > 0) _hpFrame.Clear(); if (_itemFrame.Count > 0) _itemFrame.Clear(); if (_lootTagFrame.Count > 0) _lootTagFrame.Clear(); if (_atlasMarkFrame.Count > 0) _atlasMarkFrame.Clear(); _hoverFrame = null; }

        // Zone-load guard: the world snapshot lags the live chain by up to one world pass, so right after a
        // zone change its entities/terrain/route still belong to the PREVIOUS area. Only draw them once the
        // snapshot's area hash matches the live one; otherwise draw none this frame (player blip + map still
        // draw). The API still serves the latest snapshot regardless (no visual artifact there).
        var worldFresh = inGame && snap.InGame && snap.AreaHash == _areaHash
            && DateTime.UtcNow - snap.ObservedAt < TimeSpan.FromMilliseconds(500);
        var entities = worldFresh ? snap.Entities : Array.Empty<Poe2Live.EntityDot>();
        var landmarks = worldFresh ? snap.Landmarks : Array.Empty<Poe2Live.Landmark>();
        var terrain = worldFresh ? snap.Terrain : null;
        var selectedPaths = worldFresh ? snap.SelectedPaths : Array.Empty<SelectedPath>();
        var legend = worldFresh ? snap.Legend : (IReadOnlyList<LegendEntry>)Array.Empty<LegendEntry>();
        var hpTargets = worldFresh ? (IReadOnlyList<HpBarTarget>)_hpFrame : Array.Empty<HpBarTarget>();
        var itemLabels = worldFresh ? (IReadOnlyList<ItemLabel>)_itemFrame : Array.Empty<ItemLabel>();
        // Monoliths are world-space (grid) → gate on the same zone-load guard, and only when the bundle's
        // own area hash matches the live area (the bundle is published independently of the world snapshot).
        var monoliths = worldFresh && mr.AreaHash == _areaHash
            ? mr.Markers : (IReadOnlyList<MonolithMarker>)Array.Empty<MonolithMarker>();

        _state = new RadarState(inGame, snap.AreaHash, snap.AreaLevel, map.IsVisible, map.Zoom, player,
            snap.Entities, snap.Landmarks, _hpPct, _manaPct, _esPct, _autoFlask, _flaskNote,
            snap.AreaCode, _charName, snap.CharLevel, _worldMs, _renderMs, mr.Markers, _fps,
            ex.Open, ex.Summary, ex.Offered, ex.Wanted, ex.HaveQty, ex.FillNote,
            _buffKeeperArmed, _buffNote, OpenTradeCount(), _chat.LastResult);

        var realActive = GameFocused();
        _inGameNow = inGame;
        TickBuffKeeper(inGame, localPlayer, realActive, entities, player, snap.AreaCode);
        var status = BuildStatusChips();
        // "Always show" draws the overlay even when PoE2 isn't focused (for dashboard calibration).
        var drawActive = realActive || _settings.AlwaysShowOverlay;
        var atlasProj = AtlasProjection(); // resolution-correct (auto from window height) or manual calib
        // Cursor in client pixels for the menus' hover highlights (only read while one is showing).
        var mouse = (X: -1f, Y: -1f);
        if (drawActive && (_insMenuOpen || _pcView is not null) && GameHost.GetCursorPos(out var cursor))
        {
            var (mx, my) = ScreenToClientPoint(cursor);
            mouse = (mx, my);
        }
        var ctx = new RenderContext(
            InGame: inGame,
            Active: drawActive,
            WindowWidth: _window.Width,
            WindowHeight: _window.Height,
            PlayerGrid: player,
            PlayerWorld: playerWorld,
            Map: map,
            Entities: entities,
            Landmarks: landmarks,
            AreaHash: _areaHash,
            Terrain: terrain,
            ScaleMul: _settings.ScaleMul,
            OffsetX: _settings.OffX,
            OffsetY: _settings.OffY,
            HpPct: _hpPct,
            ManaPct: _manaPct,
            EsPct: _esPct,
            FlaskNote: _flaskNote,
            AreaCode: snap.AreaCode,
            CharLevel: snap.CharLevel,
            CameraMatrix: _cameraMatrix,
            HideJunk: _settings.HideJunk,
            ShowPath: _settings.ShowPath,
            UseCuratedLandmarks: _settings.UseCuratedLandmarks,
            ShowMonsters: _settings.ShowMonsters,
            ShowTerrain: _settings.ShowTerrain,
            ShowPlayerBlip: _settings.ShowPlayerBlip,
            HpBarNormal: _settings.HpBarNormal,
            HpBarMagic: _settings.HpBarMagic,
            HpBarRare: _settings.HpBarRare,
            HpBarUnique: _settings.HpBarUnique,
            SelectedPaths: selectedPaths,
            Legend: legend,
            NavMenuExpanded: _navMenuExpanded,
            NavMenuCorner: _settings.NavMenuCorner,
            Styles: _settings.Styles,
            HpBars: _settings.HpBars,
            HpBarTargets: hpTargets,
            TerrainStyle: _settings.Terrain,
            ItemLabels: itemLabels,
            Resolve: _resolveEntity,
            ResolveTile: _resolveTileDraw,
            AtlasOpen: ar.Open,
            AtlasNodes: _atlasMarkFrame,   // marks with per-frame-fresh relPos (smooth pan), not the baked world-walk positions
            // Projection: derived live from the window height (UIscale = winH/1600) × live zoom. relPos is
            // read live so pan is already handled; the zoom term is folded into the scale. atlasProj is the
            // 8-coeff homography layout {h0..h7}. This is what makes non-1080p resolutions line up.
            AtlasScale: (float)atlasProj[0],
            AtlasScaleY: (float)atlasProj[4],
            AtlasOffX: (float)atlasProj[2],
            AtlasOffY: (float)atlasProj[5],
            AtlasShearX: (float)atlasProj[1],
            AtlasShearY: (float)atlasProj[3],
            AtlasPersX: (float)atlasProj[6],
            AtlasPersY: (float)atlasProj[7],
            // F10 route: START/END markers + the graph path between them (from the atlas render bundle).
            AtlasStart: (ar.Open && _settings.AtlasShowRoute) ? atlasStart : null,
            AtlasEnd: (ar.Open && _settings.AtlasShowRoute) ? atlasEnd : null,
            AtlasRoute: (ar.Open && _settings.AtlasShowRoute) ? atlasRoute : null,
            // Auto-route from the current node to tracked tiles + the "you are here" marker (improvement 1).
            AtlasCurrent: (ar.Open && _settings.AtlasShowRoute) ? atlasCurrent : null,
            AtlasAutoRoutes: (ar.Open && _settings.AtlasShowRoute && _settings.AtlasAutoRoute) ? atlasAutoRoutes : null,
            AtlasBiomeBorder: _settings.AtlasShowBiomeBorder,
            AtlasContentIcons: _settings.AtlasShowContentIcons,
            AtlasContentIconSize: _settings.AtlasContentIconSize,
            AtlasRouteArrowSpacing: _settings.AtlasRouteArrowSpacing,
            AtlasDrawAll: _settings.AtlasDrawAll,
            // Rune-crafting reward prices (screen-space; only when the panel is open).
            RuneLabels: rr.Open ? rr.Labels : null,
            // Ritual tribute-shop reward prices (screen-space; only when the shop is open).
            RitualRewards: rit.Open ? rit.Labels : null,
            // Loot-tag value chips (screen-space; rects re-read live each frame, so no zone-load gate needed —
            // a stale element just fails the rect read and drops out).
            LootTags: _lootTagFrame.Count > 0 ? _lootTagFrame : null,
            HoverPrice: _hoverFrame,
            // Runeshape monoliths: value-coloured map markers + nearby reward panel (world-space).
            Monoliths: monoliths,
            ShowMonolithPanel: _settings.Monoliths.ShowPanel,
            MonolithPanelCollapsed: _settings.Monoliths.PanelCollapsed,
            // Currency Exchange order-book depth panel (screen-space; only when the exchange panel is open).
            // RenderContext can't reference the private ExchangeRender record, so pass its pieces.
            ExchangeOpen: ex.Open,
            ExchangeOffered: ex.Open ? ex.Offered : null,
            ExchangeWanted: ex.Open ? ex.Wanted : null,
            ExchangeSummary: ex.Open ? ex.Summary : null,
            ExchangeHaveQty: ex.Open ? ex.HaveQty : 0,
            ExchangeFillNote: ex.Open ? ex.FillNote : null,
            ExchangePanelX: ex.Open ? ex.PanelX : 0f,
            ExchangePanelY: ex.Open ? ex.PanelY : 0f,
            ExchangeCollapsed: ex.Open && ex.Collapsed,
            AutoFlask: _autoFlask,
            Status: status,
            PriceCheck: inGame ? _pcView : null,
            Toast: _toast is { } toast && toast.Until > DateTime.UtcNow ? toast.Text : null,
            Trades: inGame ? TradeCards() : null,
            TradePanelX: _settings.Trade.PanelX,
            TradePanelY: _settings.Trade.PanelY,
            MouseX: mouse.X,
            MouseY: mouse.Y,
            ReduceMotion: _settings.ReduceMotion,
            InsMenu: _insMenuOpen ? new InsMenuData(
                Tab: _insMenuTab,
                Settings: _settings,
                Fps: (int)MathF.Round(_fps),
                WorldMs: _worldMs,
                RenderMs: _renderMs,
                CharName: _charName,
                Version: UpdateChecker.Current,
                Status: status,
                BuffKeeperArmed: _buffKeeperArmed,
                Buffs: _liveBuffs,
                BuffNotes: _buffRuleNotes,
                ChatNote: _chat.LastResult,
                Trade: _insMenuTab == OverlayRenderer.InsTradeTab ? TradeMenu() : null) : null);
        // The overlay is only visible while PoE2 is foreground (Render draws nothing otherwise). Skip
        // the whole draw + UpdateLayeredWindow blit when unfocused — but render once on the focus-loss
        // transition so the last visible frame is cleared rather than left frozen on screen.
        if (ctx.Active || _overlayHadContent)
        {
            _renderer.Render(ctx);
            _overlayHadContent = ctx.Active;
        }

        // Make the overlay grab clicks only while the cursor is over a clickable legend row;
        // otherwise stay click-through so the game receives the clicks. Runs after Render so
        // LegendRowRects reflects the frame just drawn. Gate on REAL focus (never grab clicks when
        // PoE2 isn't foreground, even if "always show overlay" is keeping it drawn).
        UpdateClickThrough(realActive);
        _renderMs = (float)System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    /// <summary>
    /// The world-rate pass (~30 Hz), run on the dedicated <see cref="WorldLoop"/> thread: the heavy
    /// entity/terrain/landmark walk + mod catalog + HP-bar specs + item labels + atlas update +
    /// nav-target/route maintenance, on the world reader stack (<see cref="_live"/>). Publishes an
    /// immutable <see cref="WorldSnapshot"/> at the end for the render thread to consume lock-free.
    /// </summary>
    private void WorldTick(nint inGameState, nint areaInstance, nint localPlayer)
    {
        // AreaInstance is a fresh object per area — use its address to invalidate per-area caches.
        if (areaInstance != _lastAreaInstance) { _terrain = null; _lastAreaInstance = areaInstance; }
        var areaHash = _live.AreaHash(areaInstance);
        var areaLevel = _live.AreaLevel(areaInstance);
        var areaCode = _live.AreaCode(areaInstance);
        var player = _live.PlayerGrid(localPlayer) ?? NumVec2.Zero;
        _worldPlayer = player;   // for off-thread replans (EnqueueReplan)

        // Tick the player's vitals on THIS (world) reader too — not for the flask (that's on the render
        // thread's _liveRender), but for the side effect: it self-heals _live's Health-offset (drift) which
        // backs the monster HP reads in Entities()/ReadHp. Pre-split the one _live instance did both, so the
        // heal benefited monster bars; with split readers, _live must heal independently. Result discarded.
        _ = _live.PlayerVitals(localPlayer);
        _charLevel = _live.PlayerLevel(localPlayer);   // changes ~never; 30 Hz is plenty
        _terrain ??= _live.Terrain(areaInstance);
        _entities = _live.Entities(areaInstance);
        // Drop the local player's own entity — it lives in the AwakeEntities map like any
        // other Player, but the dedicated center blip already represents "you" (gated by
        // ShowPlayerBlip). Without this, a Player-category dot renders at map-center even with
        // the blip off. Filtering here (not the renderer) keeps the nav builder and HTTP API
        // consistent, and still leaves party members visible as Player dots.
        // Drop user-hidden entities + the local player's own entity in ONE in-place pass (the list is a
        // fresh List from Entities() and isn't published yet) — so the renderer, nav-target builder, and the
        // published RadarState (HTTP API) all see the same filtered list, without copying it twice.
        var culling = _hidden.Count > 0;
        if (localPlayer != 0 || culling)
            _entities.RemoveAll(e => e.Address == localPlayer || (culling && _hidden.IsHidden(e.Metadata)));
        // Accumulate any newly-seen monster mod ids into the persistent catalog (debounced write)
        // so the dashboard rule editor can offer them and they survive restarts / new content.
        _modCatalog.Observe(_entities);
        // If the user edited the custom landmark patterns, drop the cached per-area scan so it
        // rebuilds with the new patterns this tick (otherwise it only refreshes on zone change).
        if (_landmarkPatterns.Generation != _landmarkGen)
        {
            _landmarkGen = _landmarkPatterns.Generation;
            _live.InvalidateLandmarks();
        }
        // A changed display ruleset can add/remove "Tile" rules that surface tiles — rebuild.
        if (_displayRules.Generation != _displayRulesGen)
        {
            _displayRulesGen = _displayRules.Generation;
            _live.InvalidateLandmarks();
        }
        // Curated-landmark edits (Landmarks tab) change what surfaces + the labels — rebuild.
        if (_landmarkStore.Generation != _landmarkStoreGen)
        {
            _landmarkStoreGen = _landmarkStore.Generation;
            _live.InvalidateLandmarks();
        }
        // Live-apply a changed cluster radius (dashboard/config edit) the same way.
        if (_settings.LandmarkClusterGap != _appliedClusterGap)
        {
            _appliedClusterGap = _settings.LandmarkClusterGap;
            _live.LandmarkClusterGap = _appliedClusterGap;
            _live.InvalidateLandmarks();
        }
        // Live-apply a changed price league (dashboard/config edit) → re-fetch for that league.
        if (_settings.GroundItems.League != _appliedLeague)
        {
            _appliedLeague = _settings.GroundItems.League;
            _priceBook.SetLeagueOverride(_appliedLeague);
        }
        // Auto-detect the league from game memory (HC vs SC) so prices match the character's actual
        // league when no manual override is set. Cached per area in Poe2Live; SetDetectedLeague no-ops
        // unless it changed (and triggers a re-fetch only while auto-detecting).
        _priceBook.SetDetectedLeague(_live.LeagueName(areaInstance));
        _landmarks = _live.Landmarks(areaInstance); // cached per area in Poe2Live

        // Decide which mobs get an HP bar + their style ONCE here (rule resolve + colour parse) —
        // the per-render-frame path then only re-reads position/HP for this small set. Returns a fresh
        // immutable list so the render thread can read it lock-free off the published snapshot.
        var hpSpecs = BuildHpSpecs();

        // Resolve + price unique ground drops (art basename → name + ex value) for the loot overlay.
        _priceBook.RefreshIfDue();
        var itemLabels = BuildItemLabels();

        // Atlas F10 route — ReadNodes is cheap when the atlas is closed (it gates on the atlas
        // panel's visible bit before any whole-tree scan), so this is safe each world tick. Publishes
        // its own _atlasRender bundle.
        UpdateAtlas(inGameState);

        // Rune-crafting reward prices — cheap when the panel is closed (the fingerprint walk bails at the
        // visible-gate step). Publishes its own _runeRender bundle.
        UpdateRuneforge(inGameState);
        UpdateRitualRewards(inGameState);

        // Currency Exchange (Kalguur market) order book — cheap when the panel is closed (the reader returns
        // Book.Closed without resolving). Publishes its own _exchangeRender bundle.
        UpdateCurrencyExchange(inGameState);

        // Loot-tag value chips — throttled, invisible-subtree-pruned UI scan; matches tag text → price.
        // Publishes its own _lootTags bundle (render thread re-reads each tag's live rect).
        UpdateLootTags(inGameState);

        // Hover price — the item under the cursor in an item UI (inventory/stash/vendor), priced + published
        // as a spec the render thread anchors beside the game tooltip (its rect re-read live).
        UpdateHoverPrice(inGameState, areaHash);

        // Price-check panel (hotkey): serve open/close/refresh requests + poll the trade search.
        UpdatePriceCheck(inGameState, areaHash);

        // Runeshape monolith rewards — resolve each in-world monolith device + price its offered rewards
        // (area-wide, before the panel is opened). Publishes its own _monoRender bundle.
        UpdateMonoliths(areaInstance, areaLevel, areaHash);


        // Rebuild the unified navigation-target list (tiles + entity POIs) for this tick.
        _navTargets = BuildNavTargets(player);

        // On a zone change: drop the (now-stale) selection, then apply the persistent
        // auto-nav patterns against the new zone's targets. Keyed off the AreaInstance
        // address (a fresh object per area), same signal the per-area caches use.
        if (areaInstance != _navTargetsArea)
        {
            _navTargetsArea = areaInstance;
            OnAreaChanged(areaHash);
        }

        // Auto-deselect entity targets the game has marked complete (e.g. a looted expedition):
        // they're already gone from the map + nav-target list, but the still-present (faded)
        // entity would otherwise keep resolving, so the route would keep pathing to it.
        PruneCompletedTargets();

        // Per-tick route maintenance (draw-only, NO A* on this thread). For each selected
        // target: cheaply advance its cursor; fire a BACKGROUND replan only on a real trigger.
        // Then drain finished routes and rebuild _selectedPaths from the trackers' cursors.
        MaintainRoutes(player);

        // Selection snapshot + legend are render inputs that change only with the selection /
        // nav-target list — rebuild them here (30 Hz) rather than every render frame.
        _selectedSnapshot = SnapshotSelection();
        _legend = BuildLegend(_selectedSnapshot);

        // Publish the whole immutable world snapshot atomically for the render thread.
        _world = new WorldSnapshot(true, areaHash, areaLevel, areaCode, _charLevel,
            _entities, _landmarks, _terrain, hpSpecs, itemLabels, _selectedPaths, _legend, _selectedSnapshot);
    }
}
