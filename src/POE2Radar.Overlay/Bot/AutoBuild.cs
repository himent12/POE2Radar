using System.Text.Json;
using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;

namespace POE2Radar.Overlay.Input;

public sealed record BuildSuggestion(string Metadata, string Role, bool Supported, string Reason, CombatSkill Skill,
    string BindingSource = "Unassigned", bool Recommended = false);
public sealed record BuildProposal(CharacterLoadout Character, string Identity, string Archetype,
    float CombatRange, float KeepDistance, IReadOnlyList<BuildSuggestion> Suggestions, IReadOnlyList<string> Warnings,
    GameInputSnapshot? Input = null);
public sealed record BuildSelection(string Metadata, int Key, int Modifiers = 0, int Slot = 0);

/// <summary>Explainable rotation planning from gem facts and confirmed character bindings. Never arms input.</summary>
public static class AutoBuild
{
    public static CombatSkill Clone(CombatSkill skill) => JsonSerializer.Deserialize<CombatSkill>(JsonSerializer.Serialize(skill))!;

    public static BuildProposal Propose(CharacterLoadout loadout, IReadOnlyList<CombatSkill> existing, GameInputSnapshot? input = null)
    {
        var identity = loadout.League + ":" + loadout.Character;
        var warnings = loadout.Warnings.ToList();
        if (input is not null) warnings.AddRange(input.Warnings);
        if (loadout.SkillBar is not { Complete: true })
            warnings.Add(loadout.SkillBar?.Warning ?? "Keys are read from game settings. Confirm skill-to-slot assignments when the live bar is unavailable.");
        warnings.Add("Timings use base skill data and conservative ranges. Support gems, passive modifiers, actual costs, DPS and skill readiness are not measured; review timing for your build.");
        if (loadout.Equipment.Any(e => e.InventoryId is 15 or 16))
            warnings.Add("Second weapon set detected. Skills requiring it need manual weapon-set configuration in game; active set is not read.");
        var weapon = loadout.Equipment.FirstOrDefault(e => e.InventoryId == 3)?.Metadata ?? "";
        var bow = weapon.Contains("/Bows/", StringComparison.Ordinal);
        var crossbow = weapon.Contains("/Crossbows/", StringComparison.Ordinal);
        var caster = weapon.Contains("/Wands/", StringComparison.Ordinal) || weapon.Contains("/Staves/", StringComparison.Ordinal);
        var range = bow || crossbow || caster ? 35f : 12f;
        var archetype = bow ? "Bow" : crossbow ? "Crossbow" : caster ? "Caster weapon" : weapon.Length == 0 ? "Unknown weapon" : "Melee";
        var suggestions = new List<BuildSuggestion>();
        foreach (var item in loadout.Skills.DistinctBy(s => s.Metadata))
        {
            var gem = SkillCatalog.Find(item.Metadata);
            var effect = gem?.Effects.FirstOrDefault(e => e.Manual && !e.Reserves && !e.Types.Contains("CrossbowAmmoSkill"));
            var skill = new CombatSkill { Key = 0, Name = item.Name, SourceMetadata = item.Metadata,
                SourceCharacter = identity, Enabled = false, Range = range, CooldownMs = 600 };
            var role = "Unsupported";
            var reason = "Unknown skill. Add a custom rule in Skill rules; no rotation behavior is guessed.";
            var types = effect?.Types ?? Array.Empty<string>();
            bool Has(params string[] values) => values.Any(types.Contains);
            if (gem is not null)
            {
                if (gem.GemType == "support")
                { role = "Support"; reason = "Support gem modifies another skill and has no independent key press."; }
                else if (gem.Effects.Any(e => e.Types.Contains("CrossbowAmmoSkill")))
                { role = "Ammunition"; reason = "Loads crossbow ammunition. Reload/bolt state and the separate fire action are required; configure a custom sequence."; }
                else if (effect is null)
                { role = "Persistent / triggered"; reason = "Reservation, persistent minion, or triggered effect. It is not repeatedly cast by the rotation."; }
                else if (Has("Bow") && !bow)
                    reason = "This skill requires a bow in weapon set 1; the scan did not find one.";
                else if (Has("Grenade", "CrossbowSkill") && !crossbow)
                    reason = "This skill requires a crossbow in weapon set 1; the scan did not find one.";
                else if (effect.CostKinds.Any(k => !k.StartsWith("Mana", StringComparison.Ordinal)))
                    reason = "Uses " + string.Join(", ", effect.CostKinds) + ". These costs require custom resource rules.";
                else if (Has("ActiveBlock"))
                    reason = "Reactive block or parry: it needs incoming-hit timing and is not a repeatable attack. Configure a defensive rule manually.";
                else if (effect.Id is not ("SnipePlayer" or "IceTippedArrowsPlayer" or "UnearthPlayer" or "LightningSpearPlayer" or "ExplosiveSpearPlayer") && (gem.Tags.Contains("conditional") || Has("TargetsDestructibleCorpses", "TargetsDestructibleRareCorpses", "CanTargetUnusableCorpse",
                    "ConsumesCharges", "RequiresCharges", "Shapeshift", "Bear", "Wolf", "Wyvern", "RequiresCombo", "HasUsageCondition",
                    "Offering", "Link", "ConsumesRage", "SkillConsumesBleeding", "SkillConsumesFreeze", "SkillConsumesParried", "ConsumesFullyBrokenArmour")))
                    reason = "Requires a corpse, ally, charges, combo, ailment, or form state that the reader does not yet verify. Configure a custom rule.";
                else if (Has("CommandsMinions"))
                    reason = "Minion command targeting varies by skill. Configure its ally/enemy target and conditions manually.";
                else if (Has("Movement", "Travel", "Blink", "Jumping") && !item.Metadata.EndsWith("SkillGemEscapeShot", StringComparison.Ordinal))
                    reason = "Movement direction and destination need a skill-specific rule; it is excluded from automatic attacks.";
                else
                {
                    skill.CooldownMs = Math.Clamp(Math.Max(effect.CastMs, effect.CooldownMs), 400, 60000);
                    skill.RepeatGapMs = Math.Clamp(effect.CastMs, 150, 2000);
                    skill.MinManaPct = effect.CostKinds.Any(k => k.StartsWith("Mana", StringComparison.Ordinal)) ? 15 : 0;
                    skill.Range = Has("Melee") ? 12 : Has("Projectile", "RangedAttack", "Spell", "SummonsTotem") ? 35 : range;
                    if (Has("Channel")) skill.HoldMs = Math.Clamp(effect.CastMs * 2, 600, 2000);
                    if (item.Metadata.EndsWith("SkillGemEscapeShot", StringComparison.Ordinal))
                    {
                        role = "Emergency escape"; skill.Priority = true; skill.HpBelowPct = 40;
                        skill.CooldownMs = Math.Max(5000, skill.CooldownMs); skill.AimMode = "Target";
                        if (loadout.Vitals is { HasEs: true } pools && pools.EsUnreserved >= pools.HpUnreserved * .25f)
                        { skill.EsBelowPct = 25; skill.AnyLowResource = true; }
                        reason = "Escape Shot jumps backward: aim at the enemy. Escape on low life or substantial depleted ES.";
                    }
                    else if (effect.Id == "UnearthPlayer")
                    {
                        role = "Main attack"; skill.Range = 25;
                        reason = "Damage enemies with the cone. Corpses optionally produce constructs; a corpse is not required to attack.";
                    }
                    else if (effect.Id == "IceTippedArrowsPlayer")
                    {
                        role = "Empower attack"; skill.Priority = true;
                        skill.CooldownMs = Math.Max(12000, skill.CooldownMs);
                        skill.AimMode = "Cursor";
                        reason = "Empower the next bow attack. Wait for the normal cooldown; frenzy charges only bypass that cooldown.";
                    }
                    else if (Has("Guard"))
                    {
                        role = "Defence"; skill.Priority = true; skill.HpBelowPct = 45;
                        skill.RequireTarget = false; skill.MinTargets = 0; skill.AimMode = "Cursor";
                        skill.CooldownMs = Math.Max(5000, skill.CooldownMs);
                        reason = "Guard: use on low life, with no enemy or cursor movement required.";
                    }
                    else if (Has("AppliesCurse", "Warcry", "Mark"))
                    {
                        role = "Debuff / warcry"; skill.CooldownMs = Math.Max(6000, skill.CooldownMs);
                        reason = "Refresh periodically near enemies; tune the interval to the modified duration.";
                    }
                    else if (Has("SummonsTotem", "CreatesMinion") || effect.Manual && Has("Minion"))
                    {
                        role = "Summon"; skill.CooldownMs = Math.Max(5000, skill.CooldownMs);
                        reason = "Manual summon near enemies. Refresh interval is estimated; summon counts are not read.";
                    }
                    else if (Has("Attack", "Damage", "DamageOverTime"))
                    {
                        role = item.Metadata.Contains("SkillGemPlayerDefault", StringComparison.Ordinal) ? "Low-mana fallback" : "Main attack";
                        reason = Has("Melee") ? "Direct melee attack with close positioning." : "Direct ranged attack or spell with distance from enemies.";
                        if (role == "Low-mana fallback") { skill.ManaBelowPct = 15; skill.MinManaPct = 0; }
                        if (Has("Channel")) reason += " Hold briefly to channel; tune the hold duration for your supports.";
                        if (effect.Id == "SnipePlayer") reason += " Freeze is an optional payoff. Perfect-release timing is not measured; this uses a fixed hold.";
                        if (effect.Id is "LightningSpearPlayer" or "ExplosiveSpearPlayer") reason += " Frenzy charges provide an optional enhancement; the attack works without them.";
                        else if (Has("DamageOverTime", "Orb", "Wall", "HasSeals"))
                        { skill.CooldownMs = Math.Max(3000, skill.CooldownMs); reason += " Refresh periodically to avoid repeatedly replacing a duration effect or spending unbuilt seals."; }
                    }
                    else { role = "Utility"; reason = "Utility effect needs custom conditions; excluded from automatic rotation."; }
                }
            }
            var supported = role is "Main attack" or "Low-mana fallback" or "Emergency escape" or "Defence" or "Debuff / warcry" or "Summon" or "Empower attack";
            var source = "Unassigned";
            var prior = existing.FirstOrDefault(s => s.SourceMetadata == item.Metadata && s.SourceCharacter == identity);
            if (supported && prior is not null)
            {
                // Preserve the user's tuned rules, not just the key; rescanning must not erase their work.
                skill = Clone(prior);
                if (prior.SourceSlot is >= 1 and <= 13)
                {
                    var binding = input?.Complete == true ? input.Find("use_bound_skill" + prior.SourceSlot) : null;
                    skill.Key = binding?.Usable == true ? binding.Key : 0;
                    skill.Modifiers = binding?.Usable == true ? binding.Modifiers : 0;
                    source = skill.Key > 0 ? "Game settings · confirmed slot " + prior.SourceSlot : "Confirmed slot unavailable";
                }
                else source = "Saved manual binding";
                skill.Enabled = GameBinding.IsSupported(skill.Key, skill.Modifiers);
            }
            // A manually confirmed fallback must not inherit a live guard from an older scan.
            skill.SourceLiveBinding = false;
            if (loadout.SkillBar is { Complete: true } bar)
            {
                // The live bar takes precedence over remembered slots after a user rearranges skills.
                var matches = bar.Slots.Where(s => effect is not null && s.EffectId == effect.Id).ToArray();
                var assigned = matches.FirstOrDefault(s => s.Slot == prior?.SourceSlot
                        && input?.Find("use_bound_skill" + s.Slot)?.Usable == true)
                    ?? matches.FirstOrDefault(s => input?.Find("use_bound_skill" + s.Slot)?.Usable == true);
                var binding = assigned is not null && input?.Complete == true ? input.Find("use_bound_skill" + assigned.Slot) : null;
                skill.SourceSlot = assigned?.Slot ?? 0;
                skill.Key = binding?.Usable == true ? binding.Key : 0;
                skill.Modifiers = binding?.Usable == true ? binding.Modifiers : 0;
                skill.Enabled = supported && skill.Key > 0;
                skill.SourceLiveBinding = skill.Enabled;
                source = assigned is null ? "Not assigned on the live bar" : "Live skill bar · slot " + assigned.Slot;
                if (matches.Length > 1) reason += " Assigned more than once; using the remembered slot or first usable binding.";
            }
            suggestions.Add(new(item.Metadata, role, supported, reason, skill, source));
        }
        // A zero-cost basic attack remains usable for builds with no spender at all.
        if (!suggestions.Any(s => s.Role == "Main attack"))
            for (var i = 0; i < suggestions.Count; i++)
                if (suggestions[i].Role == "Low-mana fallback")
                { suggestions[i].Skill.ManaBelowPct = 0; suggestions[i] = suggestions[i] with { Role = "Main attack" }; }
        // Do not kite melee attacks out of range merely because a ranged utility spell was detected.
        var attacks = suggestions.Where(s => s.Role == "Main attack").ToArray();
        if (attacks.Length > 0) range = attacks.Min(s => s.Skill.Range > 0 ? s.Skill.Range : range);
        else if (suggestions.Any(s => s.Role == "Summon")) { range = 35; archetype = "Summoner"; }
        var keepDistance = range >= 25 ? 12f : 0f;
        if (loadout.Vitals is { } v && v.HasEs && v.EsUnreserved >= v.HpUnreserved / 2) archetype += " / life + ES";
        var ordered = suggestions.OrderByDescending(s => s.Skill.Priority).ThenBy(s => s.Role == "Low-mana fallback").ToArray();
        var count = 0;
        for (var i = 0; i < ordered.Length; i++)
            ordered[i] = ordered[i] with { Recommended = ordered[i].Supported
                && (loadout.SkillBar is not { Complete: true } || ordered[i].Skill.Enabled) && count++ < 13 };
        if (!suggestions.Any(s => s.Role is "Main attack" or "Summon"))
            warnings.Add("No automatic attack or summon rule is available. Configure the specialized skills manually before using this build.");
        if (suggestions.Count(s => s.Supported) > 13) warnings.Add("Select at most thirteen skills; the remaining skills are shown for review.");
        return new(loadout, identity, archetype, range, keepDistance, ordered, warnings.Distinct().ToArray(), input);
    }

    public static bool TrySelect(BuildProposal proposal, IReadOnlyList<(string Metadata, int Key)> bindings,
        out List<CombatSkill> selected, out string error)
        => TrySelect(proposal, bindings.Select(b => new BuildSelection(b.Metadata, b.Key)).ToArray(), out selected, out error);

    public static bool TrySelect(BuildProposal proposal, IReadOnlyList<BuildSelection> bindings,
        out List<CombatSkill> selected, out string error)
    {
        selected = new(); error = "";
        if (!proposal.Character.Complete) { error = "Character scan is incomplete."; return false; }
        if (bindings.Count is < 1 or > 13) { error = "Select one to thirteen supported skills."; return false; }
        var keys = new HashSet<(int, int)>();
        var ids = new HashSet<string>();
        var main = false;
        foreach (var entry in bindings)
        {
            var candidate = proposal.Suggestions.FirstOrDefault(s => s.Metadata == entry.Metadata);
            var key = entry.Key; var modifiers = entry.Modifiers;
            if (proposal.Character.SkillBar is { Complete: true } liveBar)
            {
                var gem = SkillCatalog.Find(entry.Metadata);
                var effect = gem?.Effects.FirstOrDefault(e => e.Manual && !e.Reserves && !e.Types.Contains("CrossbowAmmoSkill"));
                var actual = liveBar.Slots.Where(s => s.EffectId == effect?.Id && (entry.Slot == 0 || entry.Slot == s.Slot))
                    .Any(s => proposal.Input?.Complete == true && proposal.Input.Find("use_bound_skill" + s.Slot) is { Usable: true } b
                        && b.Key == key && b.Modifiers == modifiers);
                if (!actual) { error = "Selected key does not match this skill on the live bar. Scan again after assigning it in game."; selected.Clear(); return false; }
            }
            if (entry.Slot != 0)
            {
                var detected = proposal.Input?.Complete == true ? proposal.Input.Find("use_bound_skill" + entry.Slot) : null;
                if (entry.Slot is < 1 or > 13 || detected is null || !detected.Usable || detected.Key != key || detected.Modifiers != modifiers)
                { selected.Clear(); error = "Selected slot is unreadable or its binding changed. Scan again."; return false; }
            }
            if (candidate is null || !candidate.Supported || !ids.Add(entry.Metadata)
                || !GameBinding.IsSupported(key, modifiers) || !keys.Add((key, modifiers)))
            { selected.Clear(); error = "Unsupported, duplicate or invalid binding. Confirm each actual game key."; return false; }
            if (proposal.Input is { Complete: true } input && input.Bindings.Any(b => b.Key == key && b.Modifiers == modifiers
                && (b.Action.StartsWith("move_", StringComparison.Ordinal) || b.Action.StartsWith("use_flask_in_slot", StringComparison.Ordinal)
                    || b.Action is "weapon_swap" or "use_dodge_roll" or "use_dodge_roll_no_sprint")))
            { selected.Clear(); error = "A skill binding conflicts with movement, dodge, weapon swap or flasks."; return false; }
            var skill = Clone(candidate.Skill);
            skill.Key = key; skill.Modifiers = modifiers; skill.SourceSlot = entry.Slot; skill.Enabled = true;
            if (skill.SourceLiveBinding && skill.SourceSlot == 0)
                skill.SourceSlot = proposal.Character.SkillBar!.Slots.First(s => SkillCatalog.Find(entry.Metadata)!.Effects.Any(e => e.Id == s.EffectId)
                    && proposal.Input!.Find("use_bound_skill" + s.Slot) is { } b && b.Key == key && b.Modifiers == modifiers).Slot;
            selected.Add(skill);
            main |= candidate.Role is "Main attack" or "Summon" or "Low-mana fallback";
        }
        if (!main) { selected.Clear(); error = "Select and bind a supported main attack, basic attack or summon."; return false; }
        // A fallback must still work when the user excludes all mana spenders from this selection.
        if (!selected.Any(s => s.MinManaPct > 0 && proposal.Suggestions.Any(p => p.Metadata == s.SourceMetadata
                && p.Role is "Main attack" or "Summon")))
            foreach (var skill in selected.Where(s => s.SourceMetadata.Contains("SkillGemPlayerDefault", StringComparison.Ordinal))) skill.ManaBelowPct = 0;
        return true;
    }

    public static bool BindingsMatch(IReadOnlyList<CombatSkill> skills, SkillBarSnapshot? bar, GameInputSnapshot? input)
    {
        foreach (var skill in skills.Where(s => s.Enabled && s.SourceLiveBinding))
        {
            if (bar is not { Complete: true } || input is not { Complete: true }) return false;
            var slot = bar.Slots.FirstOrDefault(s => s.Slot == skill.SourceSlot);
            var key = input.Find("use_bound_skill" + skill.SourceSlot);
            if (slot is null || key is not { Usable: true } || key.Key != skill.Key || key.Modifiers != skill.Modifiers
                || SkillCatalog.Find(skill.SourceMetadata)?.Effects.Any(e => e.Manual && e.Id == slot.EffectId) != true) return false;
        }
        return true;
    }
}
