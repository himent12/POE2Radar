# Auto build: conservative starter profiles

F12 → Settings → Combat / Bot → **Scan & propose build**.

The read-only scan collects character identity/level, available life/mana/ES pools,
equipment inventories (not backpack items), equipped skill gems and weapon-granted
gems. It does not inject, equip items, spend passives or press keys.

Confirm each selected skill's **actual game key**, then **Apply selected build**.
Disarm combat/bot locally first. Applying replaces only the combat skill list,
combat range and keep-distance. Arm locally with F4/F3 afterwards. Bindings are
remembered per character/league and exact gem metadata, including through normal
skill-card edits. A renamed skill does not lose its binding.

**Undo last generated build** restores those three settings in the same session,
but refuses to overwrite later manual changes. Restarting clears the undo backup.
Scanning never changes configuration. Previews expire after two minutes; applying
re-reads character, equipment, gems and pool capacities before accepting.

## Supported starter rules

- Bow + Lightning Arrow / Stormcaller Arrow: main attack, 15% mana reserve.
- Bow + Escape Shot: priority below 40% life; also below 25% ES when the ES pool
  is at least 25% of life. Requires 15% mana. Escape Shot jumps backward: aim AT
  the enemy, not behind the player.
- Granted Bow Shot: fallback below 15% mana.
- Fireball / Spark: conservative ranged-spell rotation.

Unknown skills are displayed but disabled. A supported main attack, unique keys
and a complete scan are required to apply. Guessed keys are never substituted.
Normal runtime safety gates and cooldowns remain in effect.

## Current limits

This is **not unrestricted one-button optimization**. No validated skill-bar/key
reader exists yet; initial binding confirmation is required. Ranges and cooldowns
are starter estimates. Full character stats, affix rolls, support interactions,
skill costs, DPS, passives and active weapon-set switching are not analyzed.
Equipment family and life/mana/ES pools inform recommendations; unsupported
mechanics require manual rules. New catalogue entries should be reviewed and
tested before enabling them.

## Developer probe

Build Research, then run `POE2Radar.Research --pid <PID> --loadout` inside a zone.
It prints the production snapshot and exits nonzero on incomplete reads. Current
Proton validation found Shortbow, Broadhead Quiver, armour/flasks, Stormcaller
Arrow, Escape Shot and granted Bow Shot. No combat input was tested.
