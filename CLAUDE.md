# POE2Radar — Contributor Guide

External memory-reading **map/radar overlay + trade/QoL companion for Path of Exile 2**. .NET 10, Windows
and Linux x64 (Proton/Wine on Linux). Overlay renders with SkiaSharp.
Reads game state out of process (no injection) and draws an overlay; opt-in auto-flask, the buff keeper
and chat macros send keystrokes. Forked from a PoE1 framework, since rewritten around the live PoE2 layout.
There is **no bot**: nothing moves the character, targets monsters or plays for the user.

## Non-negotiable rules

**PoE2, not PoE1.** Offsets are PoE2-specific and drift with patches. Validated values live in
`Game/Poe2Offsets.cs` (marked `✓` when confirmed live); re-discover via the `POE2Radar.Research` probes.

**Stay external.** Memory access via `OpenProcess` + `ReadProcessMemory` (Windows) or
`process_vm_readv` (Linux/Proton). **Never** inject into the
PoE2 process — no DLL injection, no function hooking, no packet manipulation.

**Input/automation (opt-in).** The overlay may send keystrokes only for: auto-flask (`GameHost.TapKey`),
the buff keeper (one key tap to recast an expired self-buff), and chat lines (`ChatSender` →
`GameHost.TypeText`) — one message per user hotkey press or trade-panel click. Rules: foreground-gated (only
when PoE2 is focused), in-game-gated, per-action cooldowns, local kill-switch hotkeys (F8 flask, the buff
keeper's configurable toggle — default F4). Arm bits (`AutoFlaskEnabled`, `BuffKeeper.Enabled`) are never
writable over HTTP, and the HTTP API never sends chat. Do not add movement, targeting or skill rotations —
the bot was removed on purpose. Keep automation minimal and clearly gated.

**Offset discovery lives in Research.** The overlay just reads; reverse-engineering/probes live in
`POE2Radar.Research`. When a patch breaks reads, run the Research probes, re-validate, commit.

**Three-pillar layout.** Product projects:
- `src/POE2Radar.Core` — memory plumbing + the PoE2 offset table + the live read layer. Read-side.
- `src/POE2Radar.Overlay` — tick loop, Skia overlay, HTTP API, opt-in input. The deliverable.
- `src/POE2Radar.Research` — dev-time discovery/validation tooling. Never linked into the overlay.
- `src/POE2Radar.Tests` — unit tests of overlay decision code. Never linked into the overlay.

## Architecture

**Entry point:** `src/POE2Radar.Overlay/Program.cs` — attach (`ProcessHandle.AttachToPoE`) →
`Bootstrap.ResolveGameStateSlot` (AOB scan for the GameState pointer, validated by a working chain)
→ `RadarApp.Run`.

**Core read layer:**
- `MemoryReader.cs`, `ProcessHandle.cs`, `Native/` — Win32 + typed reads. `AttachToPoE` lists the
  PoE2 client process names.
- `Game/Poe2Offsets.cs` — **single source of truth for all PoE2 offsets** (validated + GameHelper2-
  sourced; markers `✓` = confirmed live).
- `Game/Poe2Live.cs` — the live reader: resolves GameState → InGameState → AreaInstance →
  LocalPlayer each tick; reads player vitals, walks the entity std::maps into categorized dots
  (rarity, reaction/hostility, POI via MinimapIcon, HP), reads the walkable terrain grid, the map
  UI element (visibility/shift/zoom), tile landmarks, and area/character info. Caches per-entity
  component addresses; cache key is the AreaInstance address (invalidates on zone change).
- `Game/GameStructs.cs` — blittable structs (`StdVector`, `Vector2/3`, `VitalStruct`).
- `Game/AobScanner.cs` + `AobPatterns.cs` — pattern scan for the GameState global slot.
- `Game/LifeValidator.cs` — value-scan to find the Life component by HP (Research `--hp`).
- `Game/ItemModTranslator.cs` — renders item mods (internal mod id + rolled values read from memory) to
  the game's English stat lines (e.g. `IncreasedLife5 [67]` → "+67 to maximum Life"). Two embedded RePoE
  PoE2 tables (`poe2_mod_stats.json` mod→stat-ids, `poe2_stat_descriptions.json` GGG stat descriptions),
  regenerated per patch via `resources/poe2-data/regenerate.py`. Validate with `--inventory --itemmods`.
- `Game/ItemAffixData.cs` — affix tiers + equipment base stats (embedded `poe2_item_data.json`, from the RePoE PoE2
  `mods.json`/`base_items.json`; regenerate per patch with `resources/poe2-data/generate_item_data.py`). Places a
  read mod in its family on the item's base (tier = rank among the mods that base can roll; ordered spawn tags,
  "!tag" = excluded), best roll per stat per base, base damage/attack time/crit/armour/evasion/ES by metadata path.
- `Pathfinding/MapProjection.cs` + `GridConstants.cs` — isometric grid→screen projection and the
  grid↔world scale (250/23 ≈ 10.87).

**Overlay** (`src/POE2Radar.Overlay/`):
- `RadarApp.cs` — **two threads** (the render thread is never blocked by the heavy walk):
  - *Render loop* (`Tick`, ~`FpsCap` Hz): fast per-frame reads on its OWN reader stack (`_liveRender`) —
    live player/vitals/camera/map + auto-flask + HP-bar live pos — then draws from the lock-free
    published snapshots. Publishes `RadarState` for the API.
  - *World loop* (`WorldLoop`→`WorldTick`, ~30 Hz, own thread + reader `_live`): the heavy entity/terrain/
    landmark walk + mod catalog + HP-bar specs + item labels + atlas update + nav/route maintenance.
    Publishes an immutable `WorldSnapshot` (+ a separate `AtlasRender` bundle) the render thread reads
    lock-free (volatile reference swap, same idiom as `_state`).
  - Opt-in input on the render thread: `RadarApp.Flask.cs` (auto-flask), `RadarApp.Macros.cs` (buff keeper
    via `Input/BuffKeeper.cs` pure decisions + `Poe2Live.PlayerBuffs`; user hotkeys via `Input/Hotkey.cs`
    `HotkeyWatcher` for chat commands / bookmarks / wiki+poe2db inspect). Chat is typed by `Input/ChatSender`
    on its own worker thread, re-checking foreground + in-game right before each line.
  - Trade assistant (`Trade/`): `ClientLogTailer` tails the game's `logs/Client.txt` (located by
    `ClientLogLocator`) on its own thread → `ClientLogParser` → thread-safe `TradeSessions` (whisper trade
    requests, join/leave, "Trade accepted") → `TradeHistory` (earnings tracker, `config/trade_history.json`).
    The render thread draws `OverlayRenderer.Trade.cs` cards from `TradeSessions.Snapshot()`.
  - Three INDEPENDENT reader stacks over the one `ProcessHandle` (RPM is concurrency-safe; the per-instance
    buffers/caches are NOT): `_live` (world), `_liveRender` (render), `_liveApi` (HTTP/tile scans). `_atlas`
    is internally locked, so it's shared. HP-bar live reads carry the mob's Render/Life component addresses
    in the spec (`Poe2Live.TryBarComponents`/`TryLiveBarAt`) so the render thread reads them with no shared
    cache. The render thread gates drawing of snapshot data on `snap.AreaHash == liveAreaHash` (zone-load
    guard). `/state` exposes `worldMs`/`renderMs` timers. Auto-flask STAYS on the render thread.
- `Overlay/OverlayWindow.cs` — per-pixel-alpha layered window (`UpdateLayeredWindow`), tracks the
  game window. `Overlay/OverlayRenderer.cs` — Skia: terrain bitmap + entity dots + landmark
  markers + world-space HP bars + player blip + HUD. Drawn only when PoE2 is focused. Icon
  shape/color/opacity/size per item, metadata-matched "mechanic" overrides, and HP-bar geometry are
  config-driven via `RadarSettings.Styles` / `.HpBars` (defaults mirror the old hardcoded look) and
  editable live in the Console Settings tab. HP-bar rarity is signaled by scaling border weight.
  Icon *shapes* are named SVGs from `Overlay/IconLibrary.cs` — built-in set materialized to an
  `icons/` folder next to the exe on first run (sibling of `config/`); any `*.svg` dropped there
  (single/multi `<path>`) overrides a built-in or adds a new icon. `Overlay/SvgPath.cs` parses each
  path `d` (M/L/H/V/C/S/Q/T/Z + A→cubic) into figures the renderer normalizes (viewBox→unit) and
  caches as an `ID2D1PathGeometry` per name.
- `Overlay/TerrainBitmap.cs` — bakes the walkable grid into a bitmap, rebuilt per area.
- `Overlay/OverlayRenderer.InsMenu.cs` — the Insert-key in-game control center (Overview / Flask / Macros /
  Trade / Radar). Click grammar is documented at the top of the file; `RadarApp.Input.cs` dispatches it.
  `InsSliderSpec` (RenderContext.cs) is the single table of menu sliders + their settings accessors.
- `Web/ApiServer.cs` — HTTP API on `localhost:7777` (`/state`, `/entities`, `/landmarks`, `/api/settings`,
  `/api/buffs`, `/api/trade`, `/api/icons`, …) + the dashboard (`Web/DashboardHtml*.cs`). Writes are
  loopback-Host-gated and sanitized per key (`ApiServer.Settings.cs`).
- `Pricing/MapCheck.cs` — waystone dangerous-mod checker (hover a waystone → flagged mod lines).
- Price check (`HoverPrice.PriceCheckHotkey`, default Ctrl+D): `RadarApp.PriceCheck.cs` (render thread raises the
  request; world thread reads the hovered item, polls `TradeComparison.GetOrQueueSearch` and publishes a
  `PriceCheckView`), `Pricing/PriceCheck.cs` (pure: query per item kind, poe.ninja reference, verdict, tier),
  `OverlayRenderer.PriceCheck.cs` ("the appraisal" panel: rune circle, stamp, spread histogram). One trade request
  at a time, rate-limit headers honoured.
- Rare/magic appraisal: `Pricing/ItemAppraisal.cs` (pure) judges an item on what its slot is bought for — a
  per-slot stat role table (Premium: boots' movement speed, spirit, +skill levels, % life, max res; Key: life, all
  res, the slot's damage/defence scalers; Useful: resistances…; Filler: thorns, stun threshold, light radius…),
  each stat's roll against the best that stat reaches on the base, weapon DPS vs a top-tier roll of the same base,
  local defences, must-have caps (boots < 30% MS, weak weapon DPS) → grade (Vendor/Low/Decent/Good/Top) and ordered
  price drivers. `PriceCheck.DriverLadder` searches those drivers as "at least about this good" (min = 90% of the
  item's value, no max; pseudo totals for life incl. 2×Str, resistances, MS; `equipment_filters` pdps/edps/dps at
  the site's Q20 figure and es/ar/ev; valuable affixes matched to their trade stat, "(Local)" variants for local
  mods) by category, `status: securable`, collapsed by account; loosening to the top ~60%, then the top two at 80%;
  the last "every item of this base" step is marked not comparable. The rare's worth is the cheapest genuine
  comparable (`PriceCheck.RobustLow`). Hover: rares graded Low/Vendor are called out without a trade request.
- UI kit: `OverlayRenderer.Ui.cs` — the black-and-white "grimoire" look (palette, frame with corner brackets, cards,
  diamond switches, keycaps, chips, sigils, backdrop motes) shared by the Insert menu, trade panel and price check.
  Big panels are authored at a fixed design size and scaled to the window (`BeginScaled`/`EndScaled` map their
  click rects back to screen pixels). Motion: ambient loops run off one clock (`Now`); entrances use `BeginRise`/`EndRise`
(fade + slide via `DrawTarget.PushLayer`), switch knobs and hover washes use `Anim` (keyed by the control's action,
cursor from `RenderContext.MouseX/Y`). `Settings.ReduceMotion` freezes the clock and finishes every transition;
headless previews also draw entrances settled. `UiIcons.cs`
  (24×24 line icons); `UiFonts.cs` loads the embedded Manrope + Cinzel + Noto Sans Runic (`Assets/Fonts`, SIL OFL —
  licences copied next to the exe under `Assets/Fonts`). The text cache falls back to a system font per glyph when
  the embedded faces lack one. `POE2RADAR_PREVIEW_DIR=<dir> dotnet test --filter InsMenuRenderTests` (and the
  price-check / trade-panel render tests) writes PNG previews.
- `--demo` (Overlay) serves the dashboard with sample data and no game attach — for web-UI work.
- Focus gate: every "is PoE2 in front?" check goes through `RadarApp.GameFocused()` →
  `GameHost.IsGameForeground(hwnd, pid)`. On Hyprland it asks the compositor over IPC (`j/activewindow`, cached
  ~100 ms): Hyprland does NOT keep XWayland's `_NET_ACTIVE_WINDOW` current (verified 0.56 — it reads None or a
  dead window id while an X client is focused). Elsewhere it matches by window id or `_NET_WM_PID`. The overlay's
  X window sets `WM_HINTS input=False` so clicking the Insert menu never takes focus from the game.

**Research** (`src/POE2Radar.Research/Program.cs`) — probes: `--hp` (value-scan), `--vitals`
(dump the local player's Life component — what the configured Health/Mana/ES offsets read + every
valid VitalStruct in the component; the per-patch re-validation for the auto-flask pools), `--buffs
[--seconds N]` (buff keeper: validates `Poe2.Buffs`/`StatusEffect`/`BuffDefinition` PASS/⚠DRIFT, brute-scans
the Buffs component for the status-effect vector, finds TimeLeft by tick-down, prints each buff per second
+ paste-ready offsets — run with a timed buff up), `--chain`,
`--invui [--floats]` (finds which UI elements hold your inventory items + at what offset, and the computed slot
rects / hover result at each slot's centre — the inventory-hover re-validation), `--diag [--seconds N]` (what the overlay sees through its own read path: chain, vitals, buffs, game window vs
focused window/pid, item under the cursor — first thing to run when a feature "does nothing"),
`--entity`, `--find`/`--find-entities`/`--find-terrain`/`--find-map`, `--tiles`, `--rarity`,
`--info`, `--inventory` (player inventory + item structure: lists every inventory with box dims, dumps
each item's slot/rarity/identified/art/stack/components, `--inv N` for one inventory by id, `--itemmods`
for explicit/implicit mod ids + rolled values; self-validates the drift-prone vec hops) + `--itemdump
<hexItemAddr>` (deep single-item probe: Mods rarity/identified + per-affix id/value + Mods.dat row scan,
LocalStats statIndex→value, Sockets contents), `--watch` (area-change logger),
`--dump`, `--presence` (walk-stable before/after diff to
find a buffed scalar), `--devtree` (browser-based live memory/UI/entity explorer at
`localhost:7778` — `DevTree/DevTreeServer.cs` + `DevTreeHtml.cs`; the PoE2 stand-in for ExileApi's
DevTree), and `--atlas-probe` (one-shot ATLAS PROJECTION recovery/validation — run with the Atlas map
open after a patch: re-locates the node class + canvas, validates every offset (PASS/⚠DRIFT), and prints
the derived projection + paste-ready offsets; the `--atlas-{xform,canvas,nodes2,readnodes,corr}` probes
remain for deep re-discovery), and `--atlas-graph` (validates the node GRAPH — per-node grid coords
`AtlasNode.GridPos +0x320` + the connection-edge `StdVector` `AtlasGraph.ConnectionsVec` on the canvas
`+0x5A8`; brute-scans for both so it self-heals on drift — the basis for node-to-node atlas pathfinding).

**Atlas overlay projection** (✓ live, pan + zoom): atlas nodes are UiElements; a node's screen pos is
`screen = (UIscale × zoom) × relPos + offset` — relPos `+0x118` (read live; PAN is baked in), zoom =
node/canvas scale `+0x130` (read live), UIscale = winH/1600, offset calibrated once (F10/F11). NOT a
perspective homography. Calibration is a scale+translate RANSAC fit (`AtlasHomography`); the linear part
is rescaled by liveZoom/calibZoom each frame. See `resources/atlas-research-notes.md` "FULLY SOLVED".

## Key facts (validated live; re-verify per patch)

- Chain: AOB "Game States" → GameState → InGameState (active state) → `AreaInstance @ +0x290` →
  `LocalPlayer @ +0x5B8`.
- AreaInstance: AreaInfo `+0xA0` (code), AreaLevel `+0xC4`, AreaHash `+0x11C`, AwakeEntities std::map
  `+0x6D8` / Sleeping `+0x6E8`, TerrainStruct `+0x8B8` (walkable `+0xD0`, BytesPerRow `+0x130`). The
  entity-map/player/terrain/ServerData block shifted +0x18 in the 2026-06-25 patch (validated live).
- Entity: Details `+0x08`, ComponentList `+0x10`; component map via ComponentLookUp StdBucket.
  Rarity = ObjectMagicProperties `+0x144`; hostility = Positioned.Reaction `+0x1E0` (friendly = bit
  pattern `(b&0x7F)==1`); grid = Render world `+0x138` / 10.87; Life HP `+0x1A8` / Mana `+0x1F8` / ES
  `+0x230`; Player name `+0x1B0`, level `+0x204`.
- Map UI: UiRoot `InGameState +0x2F0`; UiElement Self `+0x08`, Children `+0x10`, Parent `+0xB8`, Flags `+0x168`
  (visible = bit `0x0B`), ScaleIndex `+0x172`, RelativePos `+0x100`, LocalScaleMul `+0x118`, Size `+0x270`, Text `+0x360`
  (the position/scale/size block moved −0x18 and Text −0x30 in the patch before 2026-09-24 — `Research --invui --floats`
  re-derives them from the flask bar). Item-slot elements (flask bar, inventory, stash, ritual tiles) hold the item
  entity at `+0x4E0`. MapUiElement Shift `+0x350`, DefaultShift `+0x358` (= (0,-20)), Zoom `+0x390`.
- Inventory (✓ live, Research `--inventory`): `AreaInstance +0x598` → ServerData → `+0x48` PlayerServerData
  vec `[0]` → ServerDataStructure → `+0x320` PlayerInventories vec (InventoryArrayStruct stride `0x18`:
  `+0x00` id, `+0x08` → InventoryStruct, `+0x10` = ptr−0x10 fingerprint). ServerData `+0x21E0` =
  std::wstring **current league name** (✓ live 2026-06-22, Research `--league`) — verbatim
  poe.ninja/poe2scout `Value` incl. the "HC " prefix (e.g. "HC Runes of Aldur"), so it auto-detects the
  HC vs SC price league. InventoryStruct: TotalBoxes(X,Y)
  `+0x150`, ItemList vec(ptr→InventoryItemStruct, len X·Y) `+0x170`. InventoryItemStruct: Item entity
  `+0x00`, Slot `+0x08`. Item = Entity; Mods rarity `+0x94`/identified `+0x90`, affix vecs Implicit `+0xA0`/
  Explicit `+0xB8`/Enchant `+0xD0` (ModArrayStruct stride `0x40`, `+0x28` → Mods.dat row → first qword →
  UTF-16 internal mod id); Stack count `+0x18`; RenderItem art `+0x28`.
- **Still TBD:** camera world→screen matrix (for world-space nameplates); friendly area Name string.

## Releasing

**The GitHub release build is triggered ONLY by pushing a `v*` tag** — NOT by pushing to `main`
(`.github/workflows/release.yml`: `on: push: tags: ["v*"]`). A version bump + commit + push to `main`
will NOT build or publish anything on its own. To cut a release:
1. Bump `<Version>` in `src/POE2Radar.Overlay/POE2Radar.Overlay.csproj` and commit.
2. Push the branch, then create a **lightweight** tag matching the version and push it:
   `git tag v0.15.3 && git push origin v0.15.3` (lightweight = points straight at the commit, the
   existing convention; the workflow re-derives the baked-in `Version` from the tag name).
The workflow publishes a self-contained single-file win-x64 exe, zips it, and creates/updates the
GitHub Release. **Don't push a release tag unless asked** — tagging is what ships a build to users.

## Dependencies
- `SkiaSharp` (overlay rendering). Targets `net10.0`, x64, Windows + Linux.
