# Auto build: character rotations

F12 → Settings → Combat / Bot → **Scan & propose build**.

The read-only scan collects the character, equipment, resource pools, equipped
skill gems and live skill-bar assignments. The planner uses an embedded PoE2 gem/effect catalogue, distinguishes
active skills from persistent/triggered effects and support gems, and proposes
combat rules with explanations. Nothing is armed by scanning or saving.

## Key detection

The scanner finds `poe2_production_Config.ini` in Windows Documents/OneDrive or
Linux Steam/Proton prefixes, including additional Steam libraries and standalone
compatdata directories. `POE2_CONFIG_PATH` overrides discovery with an explicit
full file path. Conflicting installations are reported rather than choosing the
newest file. No game configuration is ever written.

`[UI] use_wasd_to_move` selects `[ACTION_KEYS]` or `[WASD_ACTION_KEYS]`.
The reader understands unbound keys and Shift/Ctrl/Alt combinations, including
the secondary bar. Invalid or missing slot entries prevent automatic import.

The live reader resolves `HUD → HUDRight → skills_bar` by element names and
reads all 13 slot assignments, including the hidden Ctrl bar. Weapon-granted
skills absent from gem inventories are recovered through unique catalogue effect matches. Each button's skill
ID is cross-checked against its active skill, then the granted-effect ID is joined
to the equipped gem catalogue. Configured keys and modifiers fill automatically.
Gem inventory order is never treated as bar order. Rearrangements override saved
slots while preserving tuned timings. Unassigned skills are left unselected.
If the live structure is unavailable, manual binding remains available with a warning.

Validated on the standalone 0.5.5 client (2026-09-07): all 13 slots on Ranger,
Witch, Huntress and Monk characters, including duplicate assignments and
weapon-granted Chaos Bolt. A temporary Q reassignment was detected and restored. Offsets live in `Poe2Offsets.SkillBar`
and `Poe2Offsets.SkillInstance`; `--skill-bar` prints the read-only snapshot.

An optional, explicitly listed import copies movement mode/keys, dodge/run and
flask keys. Modifier combinations for movement/flasks are not supported by their
runtime, so the controls import is disabled for these configurations; skill chords
remain available. Mouse movement imports the live Move Only slot and refuses the import if the
bar is unreadable or has no supported single-key Move Only binding. Controller gameplay and
character-specific weapon-set assignments are not detected by this reader.

## Planning behavior

- Direct melee attacks use close positioning; ranged attacks/spells use distance.
  Mixed melee/ranged selections keep the bot within melee range.
- Base cast times set the cast interval; base cooldowns set the minimum repeat
  interval. Channels receive a short hold, and duration effects refresh periodically.
- Guards and Escape Shot get low-life priority rules. Escape Shot aims at the enemy
  because the leap goes backward. Small ES pools do not trigger repeated escapes.
- Marks, curses and warcries refresh periodically. Manual summons get a refresh
  rule and may serve as the build's primary combat action.
- Recognized zero-cost basic attacks provide a low-mana fallback, or an unrestricted
  main attack when no spender is available.
- Supports, persistent reservations and automatically triggered effects are not
  repeatedly pressed. Crossbow ammunition, ally/corpse targeting, combo/charge/form
  requirements and unknown skills remain explicit manual-configuration cases.

The catalogue covers 1,191 gem entries; **that does not mean 1,191 automatically
supported rotations**. Facts come from [RePoE's PoE2 data](https://github.com/repoe-fork/poe2).
The exact revision and output checksum are recorded in
[`auto-build/catalog-source.json`](auto-build/catalog-source.json).
Regenerate with `python3 resources/auto-build/generate_catalog.py <revision>`;
review and test changed classifications after patches.

## Apply, profiles and undo

Select one to thirteen supported skills, with a main attack, basic attack or manual summon.
Duplicate key combinations and conflicts with detected movement/dodge/flasks are
rejected. Q and Ctrl+Q are distinct bindings and have independent cooldowns.
The runtime releases modifier keys on cancellation and pauses generated rules
when the character identity does not match. Live-generated rules also pause when
their bar assignments or configuration no longer match (checked once per second).

Applying replaces the rotation, combat range and keep-distance, plus the listed
controls if import is checked. Disarm input locally before applying. The API
cannot arm automation. Previews expire after two minutes and are rejected if
identity, equipment, resource capacities, skill-bar assignments, bindings or relevant settings changed.
Changing current HP/mana alone does not invalidate the preview.

Tuned skill timings and conditions survive rescans and character switches.
Profiles live in the ordinary radar settings file. Undo restores the previous
rotation, ranges, imported controls and profiles in the same session, and refuses
to overwrite later manual edits. Settings writes use temporary-file replacement;
a failed build save restores the in-memory configuration and reports an error.

## Validation and remaining limits

Automated tests cover parser modes/chords, invalid configurations, skill
classification, character binding reuse, cooldown separation, modifier ordering,
and the real HTTP scan/apply/undo flow (including stale previews and write failure).
The dashboard scan, duplicate-key validation, save, remembered slots and undo can
be exercised against fixture characters without dispatching game input.

Live validation on 2026-09-07 also exercised the Ranger's generated six-skill
rotation in the Manor Ramparts. Four normal enemies were killed, and the character
finished with full life and mana. Rebinding Q paused generated combat within the
one-second check interval; restoring the assignment cleared the mismatch. The
Witch, Huntress and Monk received read-only scans and successful build-selection
checks, not combat trials. The Witch exposed a zero-ES offset-validation bug:
the corrected reader now reports no ES rather than mistaking a neighboring
100-point field for the energy-shield pool.

This is not a universal optimizer. Weapon-set eligibility and skill readiness
are not yet measured. Support effects, passive modifiers,
actual resource costs, DPS, summon counts, crossbow reload state and conditional
skill prerequisites are not measured. Base cast times and estimated ranges must
be tuned for the character. Persistent-only minion builds and specialized skills
need manual rules. The limited live trial above does not validate every skill,
weapon set, encounter or character build.

Read-only diagnostics:

```sh
dotnet run --project src/POE2Radar.Research -c Release -- --input-config
dotnet run --project src/POE2Radar.Research -c Release -- --pid <PID> --loadout
dotnet run --project src/POE2Radar.Research -c Release -- --pid <PID> --skill-bar
```

`--input-config` works without the game running; `--loadout` validates the existing
memory snapshot against a running client. Neither sends combat input.
