using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;

namespace POE2Radar.Overlay.Input;

public sealed record BuildSuggestion(string Metadata, string Role, bool Supported, string Reason, CombatSkill Skill);
public sealed record BuildProposal(CharacterLoadout Character, string Identity, string Archetype,
    float CombatRange, float KeepDistance, IReadOnlyList<BuildSuggestion> Suggestions, IReadOnlyList<string> Warnings);

/// <summary>Conservative, explainable starter rules; never equips gear, assigns game keys or arms input.</summary>
public static class AutoBuild
{
    public static BuildProposal Propose(CharacterLoadout loadout, IReadOnlyList<CombatSkill> existing)
    {
        var identity = loadout.League + ":" + loadout.Character;
        var warnings = loadout.Warnings.ToList();
        warnings.Add("Skill-bar key bindings are not readable yet. Confirm each detected skill's actual key once; it is remembered per character.");
        warnings.Add("Cooldowns and ranges are starter estimates, not measured DPS, costs or cooldown recovery. Passive tree, support effects and resistance caps are not optimized.");
        if (loadout.Equipment.Any(e => e.InventoryId is 15 or 16))
            warnings.Add("Second weapon set detected. Active weapon-set switching is not inferred; suggestions use set 1.");
        var weapon = loadout.Equipment.FirstOrDefault(e => e.InventoryId == 3)?.Metadata ?? "";
        var bow = weapon.Contains("/Bows/", StringComparison.Ordinal);
        var caster = weapon.Contains("/Wands/", StringComparison.Ordinal) || weapon.Contains("/Staves/", StringComparison.Ordinal);
        var range = bow || caster ? 35f : 12f;
        var keepDistance = bow || caster ? 12f : 0f;
        var archetype = bow ? "Bow" : caster ? "Caster weapon" : weapon.Length == 0 ? "Unknown weapon" : "Close-range starter";
        if (loadout.Vitals is { } v && v.HasEs && v.EsUnreserved >= v.HpUnreserved / 2)
            archetype += " / life + ES";
        var suggestions = new List<BuildSuggestion>();
        foreach (var item in loadout.Skills.DistinctBy(s => s.Metadata))
        {
            var id = item.Metadata[(item.Metadata.LastIndexOf('/') + 1)..];
            var role = "Unsupported";
            var reason = "No reviewed rule for this skill. Kept disabled; configure it manually rather than guessing.";
            var skill = new CombatSkill
            {
                Key = 0,
                Name = item.Name,
                SourceMetadata = item.Metadata,
                SourceCharacter = identity,
                Enabled = false,
                Range = range,
                CooldownMs = 600
            };
            switch (id)
            {
                case "SkillGemStormcallerArrow" when bow:
                case "SkillGemLightningArrow" when bow:
                    role = "Main attack"; skill.Name = item.Name;
                    if (id == "SkillGemStormcallerArrow") skill.CooldownMs = 900;
                    skill.MinManaPct = 15; reason = "Equipped bow + recognized arrow skill: ranged attack; reserve the last 15% mana.";
                    break;
                case "SkillGemEscapeShot" when bow:
                    role = "Emergency escape"; skill.Name = "Escape Shot"; skill.Priority = true;
                    skill.HpBelowPct = 40; skill.MinManaPct = 15; skill.CooldownMs = 5000;
                    // Escape Shot leaps BACK from the direction aimed: aim at danger, not behind the player.
                    skill.AimMode = "Target";
                    if (loadout.Vitals is { HasEs: true } pools && pools.EsUnreserved >= pools.HpUnreserved * 0.25f) { skill.EsBelowPct = 25; skill.AnyLowResource = true; }
                    reason = "Escape Shot retreats backward, so aim AT the enemy. Trigger on low life (or low ES when its pool is at least 25% of life), with mana reserved.";
                    break;
                case "SkillGemPlayerDefaultBow" when bow:
                    role = "Low-mana fallback"; skill.Name = "Bow Shot"; skill.ManaBelowPct = 15;
                    reason = "Granted basic bow attack: fallback below 15% mana instead of repeatedly requesting the spender.";
                    break;
                case "SkillGemFireball":
                case "SkillGemSpark":
                    role = "Main attack"; skill.Range = 35; skill.MinManaPct = 15;
                    reason = "Recognized ranged spell: conservative casting pace with a 15% mana reserve.";
                    break;
            }
            var supported = role != "Unsupported";
            var prior = existing.FirstOrDefault(s => s.SourceMetadata == item.Metadata && s.SourceCharacter == identity);
            if (supported && prior is { Key: >= 1 and <= 255 }) { skill.Key = prior.Key; skill.Enabled = true; }
            suggestions.Add(new(item.Metadata, role, supported, reason, skill));
        }
        if (suggestions.Any(s => s.Role == "Main attack" && s.Skill.Range >= 25f)) { range = 35f; keepDistance = 12f; }
        if (!suggestions.Any(s => s.Role == "Main attack")) warnings.Add("No supported main attack detected. Automatic apply is blocked; existing skills are untouched.");
        if (suggestions.Count > 8) warnings.Add("More than eight detected skills. Select at most eight to apply.");
        return new(loadout, identity, archetype, range, keepDistance,
            suggestions.OrderByDescending(s => s.Skill.Priority).ThenBy(s => s.Role == "Low-mana fallback").ToArray(), warnings);
    }
    public static bool TrySelect(BuildProposal proposal, IReadOnlyList<(string Metadata, int Key)> bindings,
        out List<CombatSkill> selected, out string error)
    {
        selected = new();
        error = "";
        if (!proposal.Character.Complete) { error = "Character scan is incomplete."; return false; }
        if (bindings.Count is < 1 or > 8) { error = "Select one to eight supported skills."; return false; }
        var keys = new HashSet<int>();
        var ids = new HashSet<string>();
        var main = false;
        foreach (var (metadata, key) in bindings)
        {
            var candidate = proposal.Suggestions.FirstOrDefault(s => s.Metadata == metadata);
            if (candidate is null || !candidate.Supported || !ids.Add(metadata)
                || !(key is 1 or 2 or 4 or 5 or 6 or 32 or >= 48 and <= 57 or >= 65 and <= 90) || !keys.Add(key))
            { selected.Clear(); error = "Unsupported, duplicate or invalid binding. Confirm each actual game key."; return false; }
            var skill = System.Text.Json.JsonSerializer.Deserialize<CombatSkill>(System.Text.Json.JsonSerializer.Serialize(candidate.Skill))!;
            skill.Key = key;
            skill.Enabled = true;
            selected.Add(skill);
            main |= candidate.Role == "Main attack";
        }
        if (!main) { selected.Clear(); error = "Select and bind a supported main attack."; return false; }
        return true;
    }
}
