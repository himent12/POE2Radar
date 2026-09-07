# Ranged boss combat

F12 → Settings → Combat / Bot → **Boss combat · ranged uniques** controls the
policy. It runs only while combat is locally armed (F4, or an existing bot mode),
Keep distance is positive, and a living hostile unique is within Combat range.
F4 alone now includes boss repositioning. Disarming the active mode stops its
casts, movement and dodge; F9 shuts down and releases held inputs. HTTP tuning
cannot arm automation. Turning the boss policy off restores ordinary combat.

## Decisions and tuning

- **Keep distance** sets the inner bow range; release hysteresis adds up to four
  cells. The outer range is 78% of Combat range. Terrain-valid lateral candidates
  compete with retreat candidates, with preference for wall clearance and distance
  from nearby enemies. Unknown terrain produces a wait, never a blind retreat.
- **Damage burst %** defaults to 6% within 500 ms. An observed loss can interrupt a
  cast and request a dodge toward a checked escape point. Substantial ES loss is
  scaled against life; tiny incidental shields and mana spending cannot trigger it.
  Zero disables this trigger. This is a reaction to damage already received.
- **Minimum dodge gap** defaults to 1800 ms. It is a cooldown, not a periodic roll
  or an attack prediction. The dodge key is released after 70 ms, direction keys
  after 250 ms, then the configured Dodge recovery owns input (at least 400 ms).
- **Quiet opening** defaults to 1200 ms with stable player and boss positions, useful range,
  clear terrain and no recent damage. New holds above 450 ms or cast intervals
  above 650 ms require this opening. Existing ordinary casts finish; damage,
  newly low life, hazard entry, target loss or invalid bindings can interrupt.
  Threatened rotations reassess after one attack, and boss combat suppresses the
  per-skill unconditional post-combo dodge. Stored manual timings remain unchanged.
- **Missing boss grace** defaults to 8000 ms. A disappearing or unreadable boss
  remains pinned rather than becoming a kill or a stalled mapping target. Known
  zero HP ends tracking; area changes clear it. Four seconds without observed
  damage switches to bounded short probes labelled “targetability unknown.”
- **Rudja oil margin** defaults to six grid cells around the specifically observed
  `Metadata/Effects/Spells/crossbow_oilgrenade/RudjaOilGround` entity. This is an
  estimated exclusion margin, not a decoded hitbox. Paths may exit a patch but
  cannot enter or deepen exposure.
- **Dodge travel distance** defaults to forty grid cells, based on the live Ranger
  roll trace. The full estimated path and landing must be clear; otherwise the
  policy repositions without rolling. Tune this for movement speed; it is not
  an exact physics prediction.
- **Escape Shot distance** is an estimated backward landing distance (default ten
  cells). Boss combat requires Target aim and a clear backward path/landing.
  Low-resource priority guards remain eligible while repositioning or waiting;
  ordinary buffs wait for attack openings, and all skills yield to a dodge.

Movement, casts, dodge and interaction input have mutually exclusive ownership.
Bindings are checked against the current skill bar and saved game controls,
including mouse-mode Move Only and the actual dodge key. Mouse-mode dodge never
presses the letters WASD, which may be attack bindings. Rebinding/disarm/target
loss releases the captured old keys. Escape, guard and Snipe selection still obey
all configured resource, target and cooldown conditions.

Normal packs retain the mapping policy. Lifeless monster-shaped arena visuals
are no longer attack/stall targets; unknown life is not proof of death. Navigation
also bounds the first smoothed click waypoint by look-ahead and promotes the
current route target, preventing off-screen clicks and stale first routes.

## Reader evidence and limits

Live observations on standalone 0.5.5, 2026-09-07 confirmed entity identities,
rarity, HP, player resources, positions, terrain and Rudja oil metadata. Actor,
Animated and Targetable components exist, but their attack/animation/targetability
semantics were **not validated**. The old Targetable offsets produced the same
bytes on players, monsters, corpses and objects; no policy uses them.

There is no universal animation, projectile, telegraph, facing, attack-phase or
immunity decoder. Devourer poison and generic ServerEffect/GroundEffect objects
are unsupported hazards. Terrain line checks approximate visibility; they are
not projectile collision or height tests. Quiet openings can still be dangerous,
and damage reactions cannot prevent a first hit. Missing phases that last longer
than the grace period, new entity IDs, adds and multi-boss encounters need more
encounter-specific validation. Dodge dispatch is bounded but game acceptance is
not confirmed by an animation reader, so no automatic retry loop is used.

A read-only capture for future validation:

```sh
dotnet run --project src/POE2Radar.Research -c Release -- --combat-observe --seconds 30
```

After the normal attach preamble this emits JSON lines at roughly 100 ms intervals
for at most 120 seconds, with nearby entities/resources and bounded raw component
bytes. Raw bytes are research evidence, not interpreted danger signals. Keep
captures local; do not paste addresses into product offsets without validation.

## Prior implementation review

Commit `12060bd` reverted the broad RollArbiter/BossFight work to `a2406a9`; its
message gives no more specific root cause. The reviewed `f971fb3` implementation
combined a four-second roll timer with a resource-loss trigger. Later history
records run/roll cancelling casts, eaten-roll retries and held-run flapping. This
implementation does not restore that timer or arbiter: a pure observed-state
policy and a bounded input owner are tested separately from the live executor.

## Validation

Regression coverage includes interrupted channels/modifiers, input release and
recovery, binding changes, target disappearance/reacquisition, death/area changes,
short/long skill windows, Escape Shot landings, terrain/hazards, resource changes,
roll suppression, mapping eligibility and navigation regressions.

The level-19 bow Ranger's actual live bar was imported through the existing build
scan: MMB Bow Shot, RMB Lightning Arrow, Q Escape Shot, W Stormcaller Arrow,
E Snipe, R Ice-Tipped Arrows, LMB Move Only and Space dodge. Existing four skill
cards retained their manual timing; newly discovered Snipe and Ice-Tipped Arrows
used catalogue timings. No game binds, equipment, currency or permanent choices
were changed. The metadata `MeleeBowPlayer` is the catalogue's ranged Bow Shot.

Controlled level-3 Mud Burrow mapping observed pack kills and resumed movement;
one 40-second segment recorded eleven alive-to-dead transitions with minimum
life 98.36%. The first Devourer trial killed the boss in about eleven seconds,
with range adjustments and combat recovery. Its 7.4% first hit did not meet the
initial 12% trigger; this motivated a 6% default for the next trial. Minimum life
was 74.95% including lingering poison after the kill. This is low-level functional
validation, not evidence of safety in progression or endgame bosses.

The second Devourer trial (6% trigger, latest position/opening policy) recorded one
11% damage reaction, one Space dodge dispatch, roughly forty cells of displacement
through the 650 ms recovery, resumed attacks about 1.2 seconds after the hit, and
boss death about 14.7 seconds into the approach/fight recording. Minimum life was
85.63%, with full recovery afterward and no auto-flasks. There were no periodic
or repeated dodge presses. A brief unavailable-target sample occurred before the
observed zero-HP sample; this is not validation of all Devourer burrow phases.
The displacement prompted a final full-roll-distance path/landing guard, covered
by a confined-arena regression test; that additional guard was not live-tested
in another boss encounter. Escape Shot was not triggered in these healthy runs.

Final checks: 343 tests passed; Release solution build succeeded with zero warnings
and errors; whitespace check passed. The new full-roll setting was checked through
the local API for clamping and restoration without changing the rotation or
arming any mode. Temporary event settings were restored, the game's configuration
file hash remained unchanged, and the Ranger was returned to town at full life
with all automation disarmed.
