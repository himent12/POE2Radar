using System.Text.RegularExpressions;

namespace POE2Radar.Core.Game;

public sealed record LoadoutItem(int InventoryId, string Slot, string Metadata, string Name);
public sealed record CharacterLoadout(bool Complete, string Character, string League, int Level,
    Poe2Live.Vitals? Vitals, IReadOnlyList<LoadoutItem> Equipment, IReadOnlyList<LoadoutItem> Skills,
    IReadOnlyList<string> Warnings, SkillBarSnapshot? SkillBar = null);

public sealed partial class Poe2Live
{
    /// <summary>On-demand, read-only snapshot. Call on the API reader's owning thread, never the render reader.</summary>
    public CharacterLoadout ReadLoadout()
    {
        var equipment = new List<LoadoutItem>();
        var skills = new List<LoadoutItem>();
        var warnings = new List<string>();
        var character = "";
        var league = "";
        var level = 0;
        Vitals? vitals = null;
        SkillBarSnapshot? bar = null;
        CharacterLoadout Result(bool complete) => new(complete, character, league, level, vitals, equipment, skills, warnings, bar);
        if (!TryResolve(out _, out var area, out var player))
        {
            warnings.Add("Load a character into a zone before scanning.");
            return Result(false);
        }
        character = PlayerName(player);
        league = LeagueName(area);
        level = PlayerLevel(player);
        vitals = PlayerVitals(player);
        var server = Ptr(area + Poe2.AreaInstance.ServerDataPtr);
        if (server == 0 || !LoadoutVector(server + Poe2.ServerData.PlayerServerDataVec, 8, 32, out var players, out var playerCount) || playerCount == 0)
        {
            warnings.Add("Player inventory root is unreadable; offsets may have changed.");
            return Result(false);
        }
        var data = Ptr(players.First);
        if (data == 0 || !LoadoutVector(data + Poe2.ServerData.PlayerInventoriesVec,
                Poe2.ServerData.InvArrayStride, 512, out var inventories, out var count))
        {
            warnings.Add("Inventory list is unreadable.");
            return Result(false);
        }
        var complete = true;
        var sawSkills = false;
        var sawWeapon = false;
        for (var i = 0; i < count; i++)
        {
            var entry = inventories.First + i * Poe2.ServerData.InvArrayStride;
            if (!_reader.TryReadStruct<int>(entry + Poe2.ServerData.InvArrayId, out var id)) { complete = false; continue; }
            var slot = id switch
            {
                2 => "Body armour",
                3 => "Weapon set 1",
                4 => "Offhand set 1",
                5 => "Helmet",
                6 => "Amulet",
                7 => "Ring 1",
                8 => "Ring 2",
                9 => "Gloves",
                10 => "Boots",
                11 => "Belt",
                12 => "Flasks",
                15 => "Weapon set 2",
                16 => "Offhand set 2",
                47 => "Equipped skill gems",
                75 => "Granted skills",
                _ => ""
            };
            if (slot.Length == 0) continue;
            sawSkills |= id == 47;
            sawWeapon |= id == 3;
            var inventory = Ptr(entry + Poe2.ServerData.InvArrayPtr);
            if (inventory == 0 || !LoadoutVector(inventory + Poe2.Inventory.ItemListVec, 8, 1024, out var items, out var itemCount))
            { complete = false; warnings.Add(slot + " could not be read."); continue; }
            var seen = new HashSet<nint>();
            for (var j = 0; j < itemCount; j++)
            {
                if (!_reader.TryReadStruct<nint>(items.First + j * 8, out var record)) { complete = false; continue; }
                if (record == 0) continue;
                var entity = Ptr(record + Poe2.InventoryItem.Item);
                if (entity == 0) { complete = false; continue; }
                if (!seen.Add(entity)) continue;
                var metadata = ReadMetadata(entity);
                if (!metadata.StartsWith("Metadata/Items/", StringComparison.Ordinal)) { complete = false; continue; }
                var component = ResolveComponent(entity, "Base");
                var row = component == 0 ? 0 : Ptr(component + Poe2.BaseComponent.NameRow);
                var namePtr = row == 0 ? 0 : Ptr(row + Poe2.BaseComponent.RowDisplayName);
                var name = namePtr == 0 ? "" : _reader.ReadStringUtf16(namePtr, 100);
                if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
                    name = Regex.Replace(metadata[(metadata.LastIndexOf('/') + 1)..].Replace("SkillGem", ""), "([a-z])([A-Z])", "$1 $2");
                var item = new LoadoutItem(id, slot, metadata, name);
                if (id is 47 or 75)
                {
                    if (ResolveComponent(entity, "SkillGem") != 0 && !metadata.EndsWith("SkillGemUnusable", StringComparison.Ordinal)) skills.Add(item);
                }
                else equipment.Add(item);
            }
        }
        bar = ReadSkillBar();
        if (bar.Complete)
        {
            // Weapon-granted skills can exist on the bar without an inventory-75 SkillGem entity.
            foreach (var effectId in bar.Slots.Select(s => s.EffectId).Where(s => s.Length > 0).Distinct())
            {
                if (skills.Any(s => SkillCatalog.Find(s.Metadata)?.Effects.Any(e => e.Id == effectId) == true)) continue;
                if (SkillCatalog.FindByEffect(effectId) is { } known)
                    skills.Add(new(0, "Live granted skill", known.Metadata, known.Gem.Name));
                else
                    warnings.Add("Bound effect " + effectId + " has no unique gem catalogue match; configure it manually.");
            }
        }
        if (!TryResolve(out _, out var finalArea, out var finalPlayer) || finalArea != area || finalPlayer != player)
        { warnings.Add("Character or area changed during the scan. Scan again."); complete = false; }
        if (!sawSkills || !sawWeapon) { warnings.Add("Expected equipment/skill inventories are missing."); complete = false; }
        if (vitals is null) { warnings.Add("Life/mana/ES pools are unreadable."); complete = false; }
        if (character.Length == 0 || league.Length == 0) { warnings.Add("Character identity is unreadable."); complete = false; }
        if (!complete) warnings.Add("Incomplete scan: applying an automatic build is blocked.");
        return Result(complete);
    }

    private bool LoadoutVector(nint address, int stride, int maxCount, out StdVector vector, out int count)
    {
        count = 0;
        if (!_reader.TryReadStruct(address, out vector)) return false;
        if (vector.First == 0) return vector.Last == 0 && vector.End == 0;
        var size = (long)vector.Last - (long)vector.First;
        if ((ulong)vector.First < 0x10000 || (ulong)vector.End > 0x7FFFFFFFFFFF || size < 0
            || size > (long)stride * maxCount || size % stride != 0 || vector.End < vector.Last) return false;
        count = (int)(size / stride);
        return true;
    }
}
