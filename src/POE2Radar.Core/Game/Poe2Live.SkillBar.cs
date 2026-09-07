namespace POE2Radar.Core.Game;

public sealed record SkillBarSlot(int Slot, string EffectId, string ActionId, uint SkillId);
public sealed record SkillBarSnapshot(bool Complete, IReadOnlyList<SkillBarSlot> Slots, string Warning = "");

public sealed partial class Poe2Live
{
    /// <summary>Reads all thirteen assignments, including the hidden secondary bar. Never infers gem order.</summary>
    public SkillBarSnapshot ReadSkillBar()
    {
        SkillBarSnapshot Failed() => new(false, Array.Empty<SkillBarSlot>(), "Live skill bar is unreadable. Scan in game or confirm bindings manually.");
        if (!TryResolve(out var igs, out var area, out var player)) return Failed();
        var root = Ptr(igs + Poe2.InGameState.UiRoot);
        var hud = NamedChild(root, "HUD");
        var right = NamedChild(hud, "HUDRight");
        var bar = NamedChild(right, "skills_bar");
        if (!SkillBarChildren(bar, out var children) || children.Length < Poe2.SkillBar.SlotCount) return Failed();
        var slots = new List<SkillBarSlot>();
        for (var i = 0; i < Poe2.SkillBar.SlotCount; i++)
        {
            var button = children[i];
            if (Ptr(button + Poe2.UiElement.Self) != button || Ptr(button + Poe2.UiElement.Parent) != bar
                || !_reader.TryReadStruct<nint>(button + Poe2.SkillBar.ButtonSkill, out var skill)
                || !_reader.TryReadStruct<uint>(button + Poe2.SkillBar.ButtonSkillId, out var id)) return Failed();
            var effect = ""; var action = "";
            if (skill != 0)
            {
                if (!_reader.TryReadStruct<uint>(skill + Poe2.SkillInstance.Id, out var activeId) || id != activeId) return Failed();
                action = SkillIdString(Ptr(Ptr(skill + Poe2.SkillInstance.ActionRow)));
                if (action.Length == 0) return Failed();
                if (!_reader.TryReadStruct<nint>(skill + Poe2.SkillInstance.GrantedPerLevel, out var levelRow)) return Failed();
                if (levelRow != 0)
                {
                    effect = SkillIdString(Ptr(Ptr(levelRow)));
                    if (effect.Length == 0) return Failed();
                }
                else if (action is not ("Move" or "DoNothing" or "Interaction")) return Failed();
            }
            else if (id != 0) return Failed();
            // Reject an assignment changed while resolving its pointer chain.
            if (Ptr(button + Poe2.SkillBar.ButtonSkill) != skill) return Failed();
            slots.Add(new(i + 1, effect, action, id));
        }
        if (!TryResolve(out _, out var finalArea, out var finalPlayer) || finalArea != area || finalPlayer != player) return Failed();
        return new(true, slots);
    }

    private string SkillIdString(nint address)
    {
        if (address == 0) return "";
        var value = _reader.ReadStringUtf16(address, 160);
        return value.Length is > 0 and < 160 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? value : "";
    }

    private nint NamedChild(nint parent, string name)
    {
        if (!SkillBarChildren(parent, out var children)) return 0;
        nint found = 0;
        foreach (var child in children)
        {
            if (Ptr(child + Poe2.UiElement.Self) != child) continue;
            if (ReadStdWString(child + Poe2.UiElement.Identifier) != name) continue;
            if (found != 0) return 0; // ambiguous structure after a patch
            found = child;
        }
        return found;
    }

    private bool SkillBarChildren(nint element, out nint[] children)
    {
        children = Array.Empty<nint>();
        if (element == 0 || Ptr(element + Poe2.UiElement.Self) != element
            || !LoadoutVector(element + Poe2.UiElement.Children, 8, 256, out var vector, out var count)) return false;
        children = new nint[count];
        for (var i = 0; i < count; i++)
            if (!_reader.TryReadStruct<nint>(vector.First + i * 8, out children[i])) return false;
        return true;
    }
}
