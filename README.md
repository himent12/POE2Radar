# POE2Radar

An external **map overlay + trade companion for Path of Exile 2**.

It attaches to the PoE2 client, reads game state directly out of process memory (no injection, no
hooks), and draws a terrain + entity overlay on top of the game's map. It also covers the everyday
quality-of-life tools of overlays like PoE Overlay II: price checks, a trade panel and earnings tracker,
waystone checks, chat-command hotkeys, bookmarks, and a buff keeper that recasts a buff when it runs out.
It does **not** bot. Nothing moves your character, targets monsters or runs skill rotations.

> ⚠️ **Use at your own risk.** This reads another process's memory and can send keystrokes to the
> game. Automating input may violate Path of Exile's Terms of Service and could put your account at
> risk. This is a personal/educational tool — you are responsible for how you use it.

## Features

### Map & radar
- **Map overlay**: when the in-game map is open, draws the walkable terrain plus entity dots, centred on
  the player.
- **Entity radar**: enemies by rarity, NPCs, chests, area transitions, other players, and **POIs**
  (anything the game flags with a minimap icon). Optional world-space **monster HP bars**.
- **Tile landmarks**: boss arenas, transitions and other static features from the terrain data, labelled
  with community-curated names as soon as you enter an area.
- **Navigation**: pick any landmark, POI or entity and the overlay draws a smoothed A* route to it. The
  route shows on the map when it's open and as ground waypoints when it's closed. **F6** adds the nearest
  target, **F7** clears. Routes are drawn only; nothing walks them for you.
- **Atlas overlay**: highlights and labels nodes by content or map type, points off-screen arrows at
  tracked maps, and routes the shortest hop path to them.
- **Monster threat detection**: flags dangerous rare/magic monster mods and auras.
- **Custom icons & display rules**: set each rule's icon shape, colour, size and opacity in the dashboard.
  Drop your own `*.svg` into `icons/` next to the exe to add or override icons.

### Trade & items
- **Price check (Ctrl+D)**: hover any item (inventory, stash, vendor, ground) and press **Ctrl+D**. A panel
  opens with the poe.ninja estimate next to the cheapest live trade listings (price, seller, how long ago),
  where the prices spread, and a suggested list price to sell fast.
  - Rares and magics are appraised first: every affix gets its tier on that base and a grade from Vendor to
    Top tier, based on what the slot is bought for (movement speed on boots, DPS on weapons, +levels and spirit,
    life and resistance totals). Filler like thorns or stun threshold is dimmed. The trade search then looks
    for items at least about as good on those stats and prices yours at the cheapest genuine match.
  - Uniques are searched by name; currency and other items by base type. Only instant-buyout listings count.
  - **Open on trade site** opens the exact search; **Refresh** re-checks. Esc or the × closes it.
  - The hotkey is configurable (dashboard → Item Value).
- **Price on hover**: a quick poe.ninja estimate under the item tooltip. Rare/magic gear shows its grade
  and selling points after a short hover. Gear worth something is then compared with live listings; vendor
  trash is flagged without a trade lookup.
- **Loot values**: prices drops on the ground and on their loot tags, and reveals which unique an
  unidentified unique is.
- **Trade panel**: reads the game's `Client.txt`, so incoming buy whispers and your own purchase whispers
  appear as cards on screen. Each card shows who is in your area, the item, price and stash position, with
  **Invite · Trade · Thanks · Kick · Sold** buttons (or **Hideout · Trade · Thanks** for purchases). Each
  click types one chat command.
- **Earnings tracker**: completed trades are logged with totals for today, the last 7 days and all time,
  converted to exalted where a price is known. History, a profit chart and trade-message settings are on
  the dashboard's **Trade** page.
- **Waystone checker**: hover a waystone to see any mods on your dangerous-mod list (plain text, or
  `re:` + regex). Edit the list on the dashboard.
- **Item inspect**: **Alt+W** opens the hovered item on the PoE2 wiki, **Alt+G** on poe2db.
- **League reward values**: value chips on Ritual tribute-shop tiles, Runeshape monolith rewards shown on
  the map before you open them, Runeforge prices, and a Currency Exchange order-book depth panel.

### Macros (opt-in input)
- **Buff keeper**: re-activates an ability whose buff has run out. Each rule watches a buff by name and
  presses its skill key when the buff is **missing**, **about to expire**, or simply every N seconds
  (**interval**). Rules can wait for hostiles nearby and skip town/hideout. Off by default. Arm or disarm
  with **F4** (configurable) or the Insert menu. The Insert menu and dashboard list the buffs on you right
  now, and clicking one creates a rule for it.
- **Chat commands**: bind any chat line to a hotkey (**F5 → `/hideout`** by default). Placeholders:
  `@char`, `@last` (last whisper partner), `{league}`, `{area}`. Multiple lines send multiple messages.
- **Bookmarks**: open websites (trade, poe.ninja, poe2db, wiki, your own) from the dashboard or a hotkey.
- **Auto-flask**: presses the life/mana flask below a Life, Energy Shield or mana threshold. **F8** toggles
  it.

Every input is gated: PoE2 must be the foreground window, you must be in game (and alive, for flasks and
buffs), and each action has a cooldown. The arm switches can't be flipped over HTTP.

### UI
- **Insert**: in-game control center with Overview, Flask, Macros, Trade and Radar sections. It uses a
  PoE-style design with warm charcoal and gold, Cinzel headings, and vitals shown as rings.
- **F12 / `http://localhost:7777`**: web dashboard with Overview (vitals, module status, today's profit,
  hotkey cheat-sheet, quick links), Trade, Macros, Radar rules & landmarks, Atlas, Item Value and
  Settings. Writes are loopback-only.

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

### Hotkeys

| Key | Action |
|---|---|
| **Insert** | In-game control center |
| **F12** | Open the web dashboard |
| **F8** | Toggle auto-flask |
| **F4** | Toggle the buff keeper (configurable) |
| **F5** | `/hideout` (default chat command — editable) |
| **Ctrl+D** | Price check the hovered item (estimate vs. live listings) |
| **Alt+W / Alt+G** | Hovered item on the wiki / poe2db |
| **F6 / F7** | Route to the nearest landmark / clear routes |
| **F10** | With the Atlas open: inspect the hovered tile, set a route start/end |
| **F9** | Quit |

Chat commands, bookmarks, inspect keys and the buff-keeper toggle are all rebindable on the dashboard's
**Macros** page. The dashboard also works without the game running: `POE2Radar.Overlay --demo`.

## Architecture

Three projects:

- `src/POE2Radar.Core` — memory plumbing (Win32 `ReadProcessMemory` / Linux `process_vm_readv` against
  Proton), the PoE2 offset table (`Game/Poe2Offsets.cs`), and the live read layer (`Game/Poe2Live.cs`).
- `src/POE2Radar.Overlay` — the overlay: attaches, AOB-resolves the game roots, runs the tick loop,
  renders a Skia overlay (Win32 layered window or X11 ARGB), serves the API + dashboard, tails
  `Client.txt` for the trade panel, and (opt-in) drives flask / buff-keeper / chat-macro input.
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
