# POE2Radar

An external, mostly read-only **map/radar overlay for Path of Exile 2**.

It attaches to the PoE2 client, reads game state directly out of process memory (no injection, no
hooks), and draws a terrain + entity overlay on top of the game's map — plus an optional auto-flask
quality-of-life feature.

> ⚠️ **Use at your own risk.** This reads another process's memory and can send keystrokes to the
> game. Automating input may violate Path of Exile's Terms of Service and could put your account at
> risk. This is a personal/educational tool — you are responsible for how you use it.

## Features

- **Map overlay** — when the in-game map is open, draws the walkable-terrain mask + entity dots,
  projected player-centered onto the game's map.
- **Entity radar** — alive enemies (red), NPCs, chests, area transitions, other players, and
  **POIs** (anything the game flags with a minimap icon) shown with a ring. Optional world-space
  **HP bars** over monsters.
- **Tile landmarks** — static features pulled from terrain tile data (boss arenas, area
  transitions, …), shown the moment you enter an area, with community-curated friendly names.
- **Atlas overlay** — on the open Atlas, highlights and labels nodes by content/map type, draws
  off-screen arrows to tracked maps, and auto-routes: shortest-hop route lines from where you are
  to every tracked tile, with hop counts. Track boss tiers (Deadly, Twinned, …), special maps
  (Citadels, Towers, Unique maps), and any content type; biome-coloured borders on tracked labels.
- **Loot values** — prices dropped items from poe.ninja and draws the value on the drop / its loot
  tag, including revealing what **unidentified uniques** are. Filter by category and value floor.
- **League reward values** — see what a reward is worth before you commit: value chips on every
  **Ritual** tribute-shop tile, a **Runeshape monolith's** best reward and rune count shown on the
  map *before* you open it, and prices on the **Runeforge** (Runeshape Combinations) panel.
- **Monster threat detection** — flags dangerous rare/magic monster mods and auras.
- **Navigation** — pick any landmark, POI, or entity as a destination and the overlay draws a
  smoothed A* route to it: on the in-game map when it's open, or as waypoints on the world ground
  when it's closed. Multi-select (each route its own color). Auto-nav patterns (e.g. the expedition
  encounter) re-acquire their target automatically in each new zone.
- **Customizable icons & display rules** — per-rule icon shape/color/size/opacity, editable live in
  the dashboard; drop your own `*.svg` into the `icons/` folder next to the exe to add or override
  any icon.
- **Auto-flask** (opt-in input) — presses the life/mana flask key below a Life, Energy Shield, or
  mana threshold (selectable). Hard-gated: only when PoE2 is the foreground window, with cooldowns
  and an **F8 kill-switch**.
- **Combat assist** (opt-in input, **off by default**) — while armed with **F4**, taps the next ready
  skill in a configurable rotation (default **QWER**) when a hostile monster is in grid range. Same
  gates as auto-flask (focused window, in-game, per-skill cooldown). Cannot be armed from the dashboard.
- **Bot** (opt-in, **off by default**) — **F3** is the master kill-switch (quest follow + path move
  along the selected A* route + combat rotation). **F5** toggles path move on its own; **F4** still
  toggles combat independently. Path move is WASD or click-to-move; quest follow taps interact/use
  on arrival. Skill keys, range, and move tunables live in the dashboard Combat / Bot card; arm
  bits cannot be set over HTTP.
- **Web dashboard** (`http://localhost:7777`, or **F12** in-game) — a local control panel: a
  searchable list of every entity/landmark you can click to navigate to, plus settings tabs (radar
  display + icon styling, monster HP bars, atlas tracking, loot-value pricing/league, monster-mod
  rules, auto-flask tuning). Served same-origin only; setting/navigation writes are loopback-gated.
  Read endpoints: `GET /state`, `/entities`, `/landmarks`, `/api/icons`.

This is a Linux-capable fork of [Sikaka/POE2Radar](https://github.com/Sikaka/POE2Radar). On Arch
(and other Linux) it attaches to the Proton/Wine PoE2 process via `process_vm_readv` and draws a
transparent X11 overlay (Proton windows are XWayland even on a Wayland desktop).

## Download (no build required)

**Windows:** grab **`POE2Radar-vX.Y.Z-win-x64.zip`** from
[Releases](https://github.com/himent12/POE2Radar/releases), unzip, and run `POE2Radar.Overlay.exe`
**as Administrator** with PoE2 already running.

**Linux:** grab **`POE2Radar-vX.Y.Z-linux-x64.tar.gz`**, extract, then either:

```
sudo setcap cap_sys_ptrace=ep ./POE2Radar.Overlay
./POE2Radar.Overlay
```

or allow same-user ptrace:

```
sudo sysctl kernel.yama.ptrace_scope=0
```

Start PoE2 first, in **borderless windowed** (exclusive fullscreen hides the overlay). No .NET
install is needed for the self-contained build.

Notes:
- Windows SmartScreen may warn about an unsigned exe — "More info → Run anyway".
- Antivirus may flag it because it reads game memory and (optionally) sends keystrokes.

## Linux (Arch / CachyOS)

Needs the **.NET 10 SDK**, `libX11`, `libXfixes`, `libXtst`, `fontconfig`.

```
sudo pacman -S --needed dotnet-sdk-10.0 libx11 libxfixes libxtst fontconfig
# memory reads: Yama ptrace_scope=1 (Arch default) blocks attaching to Proton
sudo sysctl kernel.yama.ptrace_scope=0
echo 'kernel.yama.ptrace_scope = 0' | sudo tee /etc/sysctl.d/10-ptrace.conf

./run.sh
```

`run.sh` builds Release and launches the overlay. PoE2 must already be running (Steam/Proton) and
you should be in a zone, not at the login screen.

To **exit**: **F9**, or Ctrl+C in the terminal. (The Windows tray icon is Windows-only.)

## Build from source

Requires the **.NET 10 SDK**. Windows or Linux x64.

```
dotnet build POE2Radar.slnx
# launch with PoE2 already running and you in a zone:
#   Windows: src\POE2Radar.Overlay\bin\Debug\net10.0\POE2Radar.Overlay.exe
#   Linux:   ./run.sh
```

Reading another process generally requires Administrator (Windows) or ptrace permission (Linux).

Hotkeys: **F8** toggles auto-flask; **F4** toggles combat assist; **F3** toggles the bot master
(quest follow + path move + combat); **F5** toggles path move; **F9** quits; **F12** opens the web dashboard;
**F6** routes to the nearest landmark/POI and **F7** clears routes; **F10** (with the Atlas open)
inspects the hovered tile and sets a route start/end. All other settings live in the dashboard
(no calibration hotkeys, to avoid accidental presses).

## Architecture

Three projects:

- `src/POE2Radar.Core` — memory plumbing (Win32 `ReadProcessMemory` / Linux `process_vm_readv` against
  Proton), the PoE2 offset table (`Game/Poe2Offsets.cs`), and the live read layer (`Game/Poe2Live.cs`).
- `src/POE2Radar.Overlay` — the radar: attaches, AOB-resolves the game roots, runs the tick loop,
  renders a Skia overlay (Win32 layered window or X11 ARGB), serves the API, and (opt-in) drives
  auto-flask input.
- `src/POE2Radar.Research` — dev-time offset discovery/validation tools (AOB scan, HP value-scan,
  entity/tile/UI probes, an area-change watcher). Never linked into the overlay binary.

## Offsets & patches

PoE2 memory offsets drift with game patches. Validated offsets live in `Game/Poe2Offsets.cs`
(each marked `✓` when confirmed live). After a patch that breaks reads, use the `POE2Radar.Research`
probes to re-discover them. There is no live oracle for PoE2, so validation is value-scan / manual.

## Credits

Memory-layout research and the AOB approach draw heavily on the open-source **GameHelper2** project
(its `GameOffsets` were the starting reference for PoE2's struct shapes). GameHelper2 is not
redistributed here; only independently re-validated offset values are recorded in this repo.

## License

MIT — see [LICENSE](LICENSE).
