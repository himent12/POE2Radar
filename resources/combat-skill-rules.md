# Conditional combat skills

Open F12 → Settings → Combat / Bot. Add up to eight skill cards, select the
actual bound key, then choose a starting preset. Presets configure conditions,
not game abilities: bind the intended escape, guard or recovery skill yourself.
Changes do not arm combat. Existing focus, in-game and local kill-switch gates remain.

## Examples

- **Low-life escape:** life below 35%, priority, aim away from an enemy, 5s cooldown.
- **Emergency guard:** life below 45%, no enemy required, leave cursor alone.
- **Mana recovery:** mana below 30%, no enemy required, 8s cooldown.
- **Shield recovery:** ES below 30%; requires an actual ES pool.
- **Low life OR shield guard:** life below 35% OR ES below 25%.
- **Escape when surrounded:** four or more enemies within 12 grid cells.
- **Pack clear:** three or more enemies in range.
- **Rare / boss skill:** rare or unique target, at least 25% mana.
- **Finisher:** known enemy life below 20%.

## Combining rules

Zero disables a percentage condition. Low-resource thresholds are strict
(below 30% does not include 30%); minimum mana is inclusive. Normally all
conditions must match. **Any low resource (OR)** joins only enabled low-life,
low-mana and low-ES triggers. Minimum mana, enemy count, rarity, target health,
range and cooldown always remain additional requirements. Missing vitals block
resource-triggered casts; missing ES does not count as depleted ES.

For recovery without enemies: disable Require enemy target, set Min enemies to
0, choose Self / no re-aim, and disable enemy-dependent conditions. No re-aim
does not target the player automatically; it leaves the mouse cursor alone.
Away aiming requires an enemy direction and does not guarantee a safe landing.

Cooldown starts at the first dispatched key press, not when a cast is queued.
Cancelling before that press does not spend cooldown or advance the rotation.
Later taps in the same combo do not restart the cooldown. Queued casts are
cancelled across area changes, including cursor-only recovery skills.

Priority skills are checked before the normal rotation and do not advance its
cursor. They respect cooldowns and wait for an active combo to finish; they are
not guaranteed instant interrupts. Multiple priority skills use list order.
Every cast, including a single tap, owns movement/aim immediately, releases held
movement keys, and blocks flee/kite and interaction input through its configured
cast-recovery window. Set Cast interval to cover the skill animation; it is an
estimate, not a measured game cast time. Post-cast delay is added after recovery.

Names, keys, order, enable switches and readable condition summaries are on each
card. Combo timing is expandable: repeat taps, tap interval, hold duration,
post-cast delay and optional dodge. The in-game Insert Skills tab exposes compact
controls; use the dashboard for presets, OR mode, aiming and full conditions.
