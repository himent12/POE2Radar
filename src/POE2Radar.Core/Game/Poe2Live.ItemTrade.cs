namespace POE2Radar.Core.Game;

public sealed record ItemTradeMod(string Kind, string Text);
public sealed record ItemTradeProfile(string Name, Poe2Live.Rarity Rarity, bool Identified,
    IReadOnlyList<ItemTradeMod> Mods, bool Complete);

public sealed partial class Poe2Live
{
    /// <summary>Read actual affix rolls, using the same verified vectors as the inventory research probe.</summary>
    public ItemTradeProfile ReadItemTradeProfile(HoveredItem item)
    {
        var result = new List<ItemTradeMod>();
        var component = ResolveComponent(item.Item, "Mods");
        var complete = component != 0 && item.Identified;
        if (complete)
        foreach (var (kind, offset) in new[] { ("implicit", Poe2.ModsComponent.ImplicitMods), ("explicit", Poe2.ModsComponent.ExplicitMods) })
        {
            if (!_reader.TryReadStruct<StdVector>(component + offset, out var vector)) { complete = false; continue; }
            var size = (long)vector.Last - (long)vector.First;
            if (size == 0) continue;
            if (vector.First == 0 || size < 0 || size > 0x800 || size % Poe2.ModsComponent.ModArrayStride != 0)
            { complete = false; continue; }
            for (long i = 0; i < size; i += Poe2.ModsComponent.ModArrayStride)
            {
                var entry = vector.First + (nint)i;
                var row = Ptr(entry + Poe2.ModsComponent.ModRecordPtr);
                var namePtr = row == 0 ? 0 : Ptr(row + Poe2.ModsComponent.ModRecordIdPtr);
                var id = namePtr == 0 ? "" : _reader.ReadStringUtf16(namePtr, 128);
                if (id.Length == 0) { complete = false; continue; }
                var values = new List<int>();
                if (!_reader.TryReadStruct<StdVector>(entry, out var vals)) { complete = false; continue; }
                var bytes = (long)vals.Last - (long)vals.First;
                if (bytes < 0 || bytes > 32 || bytes % 4 != 0) { complete = false; continue; }
                for (long v = 0; v < bytes; v += 4)
                    if (_reader.TryReadStruct<int>(vals.First + (nint)v, out var value)) values.Add(value);
                    else complete = false;
                if (values.Count == 0 && _reader.TryReadStruct<int>(entry + 0x18, out var first)) values.Add(first);
                if (values.Count == 0) { complete = false; continue; }
                foreach (var line in ItemModTranslator.Shared.RenderMod(id, values)) result.Add(new(kind, line));
            }
        }
        return new(item.Name ?? "", item.Rarity, item.Identified, result, complete);
    }

    /// <summary>Describe any item entity (rarity / art / identified / base name / stack) as a <see cref="HoveredItem"/>
    /// with no screen box — e.g. to price an item straight from an inventory list.</summary>
    public HoveredItem DescribeItem(nint item)
    {
        var (rarity, art, identified, name) = ReadIdentityFromItem(item);
        var stackComp = ResolveComponent(item, "Stack");
        var stack = 0; if (stackComp != 0) _reader.TryReadStruct<int>(stackComp + Poe2.StackComponent.Count, out stack);
        return new HoveredItem(item, rarity, art, identified, name, stack, 0, 0, 1, 1);
    }

    /// <summary>An item entity's metadata path (e.g. Metadata/Items/Maps/…) — identifies the base class.</summary>
    public string ItemMetadata(nint item) => item == 0 ? "" : ReadMetadata(item);

    private nint ItemFromLiveHoverTracker(nint uiRoot)
    {
        var host = Ptr(uiRoot + Poe2.HoverTracker.FromUiRoot);
        var tracker = host == 0 ? 0 : Ptr(host + Poe2.HoverTracker.WorldTracker);
        var hovered = tracker == 0 ? 0 : Ptr(tracker + Poe2.HoverTracker.HoveredEntity);
        if (hovered == 0) return 0;
        if (ReadMetadata(hovered).StartsWith("Metadata/Items/", StringComparison.Ordinal)
            && ResolveComponent(hovered, "RenderItem") != 0) return hovered;
        var descriptor = Ptr(hovered);
        if (descriptor != 0 && ReadMetadata(descriptor).StartsWith("Metadata/Items/", StringComparison.Ordinal)
            && ResolveComponent(descriptor, "RenderItem") != 0) return descriptor;
        return 0;
    }

    private nint ItemFromHoverSlot(nint slot, bool scan)
    {
        nint Validate(nint candidate, bool metadata)
        {
            if (candidate == 0) return 0;
            if (metadata && !ReadMetadata(candidate).StartsWith("Metadata/Items/", StringComparison.Ordinal)) return 0;
            return ResolveComponent(candidate, "RenderItem") != 0 ? candidate : 0;
        }
        var direct = Ptr(slot + Poe2.Ritual.TileSlotItem);
        if (Validate(direct, false) is var known && known != 0) return known;
        if (direct != 0 && Validate(Ptr(direct), true) is var wrapped && wrapped != 0) return wrapped;
        if (!scan) return 0;
        // Inventory widgets can hold an InventoryItem descriptor (+0 -> entity), rather than the
        // ritual widget's direct entity. Only inspect a small cursor-containing widget, never a panel.
        Span<byte> body = stackalloc byte[0x300];
        if (_reader.TryReadBytes(slot + 0x300, body) != body.Length) return 0;
        for (var offset = 0; offset < body.Length; offset += 8)
        {
            var pointer = (nint)BitConverter.ToInt64(body.Slice(offset, 8));
            if ((ulong)pointer is < 0x10000 or > 0x7FFFFFFFFFFF) continue;
            var item = Validate(pointer, true);
            if (item != 0) return item;
            item = Validate(Ptr(pointer), true);
            if (item != 0) return item;
        }
        return 0;
    }
}
