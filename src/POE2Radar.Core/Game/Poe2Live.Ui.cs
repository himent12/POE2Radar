using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace POE2Radar.Core.Game;

public sealed partial class Poe2Live
{
    private readonly List<nint> _mapEls = new();

    private nint _mapCacheKey = -1;

    /// <summary>
    /// Map UI state. The MapUiElements (DefaultShift=(0,-20), Zoom=0.5) are discovered once per area
    /// and cached — per frame we only read their flags/shift/zoom (cheap). The game exposes several:
    /// some are always-on, some always-off, and one is the minimap viewport whose visible bit Tab
    /// toggles. We gate "map open" on a *genuine toggler* — an element observed BOTH visible and
    /// hidden — so a permanently-hidden element can't masquerade as the toggle signal (the bug that
    /// pinned this to "closed" once the UI began exposing 4 elements instead of 2). Projection
    /// params (shift/zoom) come from a currently-visible toggler. Until the first toggle is observed
    /// this area, fall back to "more than the always-on baseline visible" (&gt;=2).
    /// </summary>
    public MapUi ReadMap(nint inGameState, nint areaInstance)
    {
        if (areaInstance != _mapCacheKey || _mapEls.Count == 0)
        {
            _mapCacheKey = areaInstance;
            _mapEls.Clear();
            _everHidden.Clear();
            _everVisible.Clear();
            DiscoverMapElements(inGameState);
        }

        var visibleCount = 0;
        var any = false; MapUi anyUi = default;
        var sawToggler = false; var togglerVisible = false; var haveTogglerUi = false; MapUi togglerUi = default;
        foreach (var el in _mapEls)
        {
            if (!TryReadMapElement(el, out var vis, out var sx, out var sy, out var zoom)) continue;
            if (vis) { _everVisible.Add(el); visibleCount++; } else _everHidden.Add(el);
            if (!any) { any = true; anyUi = new MapUi(vis, sx, sy, zoom); }

            // A genuine toggler has been seen in BOTH states; permanently-on/off elements never qualify.
            if (_everVisible.Contains(el) && _everHidden.Contains(el))
            {
                sawToggler = true;
                if (vis) togglerVisible = true;
                if (vis || !haveTogglerUi) { togglerUi = new MapUi(vis, sx, sy, zoom); haveTogglerUi = true; }
            }
        }
        if (!any) return default;

        if (sawToggler)
            return new MapUi(togglerVisible, togglerUi.ShiftX, togglerUi.ShiftY, togglerUi.Zoom);

        // No toggle observed yet this area: the open minimap lights up one element beyond the
        // always-on baseline, so >=2 visible ≈ open. Superseded as soon as a real toggle is seen.
        return new MapUi(visibleCount >= 2, anyUi.ShiftX, anyUi.ShiftY, anyUi.Zoom);
    }

    private void DiscoverMapElements(nint inGameState)
    {
        var uiRoot = Ptr(inGameState + Poe2.InGameState.UiRoot);
        if (uiRoot == 0) return;
        var queue = new Queue<nint>(); queue.Enqueue(uiRoot);
        var visited = new HashSet<nint>();
        var body = new byte[Poe2.MapUiElement.Zoom + 8];
        while (queue.Count > 0 && visited.Count < 30000)
        {
            var el = queue.Dequeue();
            if (el == 0 || !visited.Add(el)) continue;

            var first = Ptr(el + Poe2.UiElement.Children);
            if (first != 0 && _reader.TryReadStruct<nint>(el + Poe2.UiElement.Children + 8, out var lastC))
            {
                var n = ((long)lastC - (long)first) / 8;
                if (n is > 0 and <= 8192) EnqueueChildren(queue, first, n);
            }

            if (_reader.TryReadBytes(el, body) < body.Length) continue;
            if (BitConverter.ToSingle(body, Poe2.MapUiElement.DefaultShift) != 0f) continue;
            if (BitConverter.ToSingle(body, Poe2.MapUiElement.DefaultShift + 4) != -20f) continue;
            var zoom = BitConverter.ToSingle(body, Poe2.MapUiElement.Zoom);
            if (zoom is <= 0.05f or >= 8f) continue;
            _mapEls.Add(el);
        }
    }

    // Flags … Zoom span of a map element: read in ONE read per element per frame (was 5 reads: DefaultShift.Y,
    // Shift.X, Shift.Y, Zoom, Flags). Also keeps Shift X/Y from one copy. Field-by-field is the fallback.
    private static readonly int MapBlockStart = Math.Min(Math.Min(Poe2.UiElement.Flags, Poe2.MapUiElement.Shift),
        Math.Min(Poe2.MapUiElement.DefaultShift, Poe2.MapUiElement.Zoom));

    private static readonly int MapBlockLen = Math.Max(Math.Max(Poe2.UiElement.Flags + 4, Poe2.MapUiElement.Shift + 8),
        Math.Max(Poe2.MapUiElement.DefaultShift + 8, Poe2.MapUiElement.Zoom + 4)) - MapBlockStart;

    private readonly byte[] _mapBlock = new byte[MapBlockLen];

    internal bool TryReadMapElement(nint el, out bool visible, out float shiftX, out float shiftY, out float zoom)
    {
        if (MapBlockLen <= MaxUiBlock && _reader.TryReadBytes(el + MapBlockStart, _mapBlock) == MapBlockLen)
        {
            ReadOnlySpan<byte> b = _mapBlock;
            visible = false; shiftX = shiftY = zoom = 0;
            if (BitConverter.ToSingle(b[(Poe2.MapUiElement.DefaultShift + 4 - MapBlockStart)..]) != -20f) return false;
            shiftX = BitConverter.ToSingle(b[(Poe2.MapUiElement.Shift - MapBlockStart)..]);
            shiftY = BitConverter.ToSingle(b[(Poe2.MapUiElement.Shift + 4 - MapBlockStart)..]);
            zoom = BitConverter.ToSingle(b[(Poe2.MapUiElement.Zoom - MapBlockStart)..]);
            visible = (BinaryPrimitives.ReadUInt32LittleEndian(b[(Poe2.UiElement.Flags - MapBlockStart)..])
                       & (1u << Poe2.UiElement.FlagVisibleBit)) != 0;
            return true;
        }
        return TryReadMapElementSlow(el, out visible, out shiftX, out shiftY, out zoom);
    }

    internal bool TryReadMapElementSlow(nint el, out bool visible, out float shiftX, out float shiftY, out float zoom)
    {
        visible = false; shiftX = shiftY = zoom = 0;
        if (!_reader.TryReadStruct<float>(el + Poe2.MapUiElement.DefaultShift + 4, out var dsy) || dsy != -20f) return false;
        _reader.TryReadStruct<float>(el + Poe2.MapUiElement.Shift, out shiftX);
        _reader.TryReadStruct<float>(el + Poe2.MapUiElement.Shift + 4, out shiftY);
        _reader.TryReadStruct<float>(el + Poe2.MapUiElement.Zoom, out zoom);
        visible = IsVisible(el);
        return true;
    }

    /// <summary>Element's own visibility bit (0x0B of Flags). Note: full visibility is hierarchical.</summary>
    public bool IsVisible(nint element)
    {
        if (!_reader.TryReadStruct<uint>(element + Poe2.UiElement.Flags, out var flags)) return false;
        return (flags & (1u << Poe2.UiElement.FlagVisibleBit)) != 0;
    }

    /// <summary>Screen-space rect (pixels) of ANY UiElement, via the GameHelper UiElementBase math:
    /// parent-chain unscaled position × resolution scale. Same geometry as <see cref="Poe2Runeforge"/>
    /// (sans its scroll viewport). <paramref name="winW"/>/<paramref name="winH"/> are the current game
    /// window size. Returns false on a read failure, a degenerate (≤1 px) rect, or when the element's own
    /// visibility bit is clear (so a render-thread caller can read live tag rects and a stale/closed tag
    /// just drops out). Touches no per-entity cache → safe to call from a separate reader stack per frame.</summary>
    public bool TryUiElementRect(nint el, float winW, float winH, out float x, out float y, out float w, out float h,
        string? requireFirstLine = null)
    {
        x = y = w = h = 0f;
        if (el == 0) return false;
        // Flags, ScaleIndex, LocalScaleMul and Size in ONE read (they share a ~0x160-byte span); per-field
        // reads remain the fallback when the block can't be read.
        Span<byte> rb = stackalloc byte[UiRectBlockLen <= MaxUiBlock ? UiRectBlockLen : 1];
        var haveBlock = UiRectBlockLen <= MaxUiBlock && _reader.TryReadBytes(el + UiRectBlockStart, rb) == UiRectBlockLen;
        uint flags;
        if (haveBlock) flags = BinaryPrimitives.ReadUInt32LittleEndian(rb[(Poe2.UiElement.Flags - UiRectBlockStart)..]);
        else if (!_reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out flags)) return false;
        if ((flags & (1u << Poe2.UiElement.FlagVisibleBit)) == 0) return false;   // not (locally) visible
        // Stale-address guard: a loot-tag element captured at world rate can be freed/recycled before this
        // frame. Require the element to STILL show the matched first-line text; a recycled element holding
        // unrelated UI fails this and drops out (no garbage rect → no jitter). Caller passes the matched text.
        if (requireFirstLine is { Length: > 0 } && !WStringFirstLineEquals(el + Poe2.UiElement.Text, requireFirstLine))
            return false;
        byte idx; float mul; System.Numerics.Vector2 sz;
        if (haveBlock)
        {
            idx = rb[Poe2.UiElement.ScaleIndex - UiRectBlockStart];
            mul = MemoryMarshal.Read<float>(rb[(Poe2.UiElement.LocalScaleMul - UiRectBlockStart)..]);
            sz = MemoryMarshal.Read<System.Numerics.Vector2>(rb[(Poe2.UiElement.SizeW - UiRectBlockStart)..]);
        }
        else
        {
            if (!_reader.TryReadStruct<byte>(el + Poe2.UiElement.ScaleIndex, out idx)) return false;
            _reader.TryReadStruct<float>(el + Poe2.UiElement.LocalScaleMul, out mul);
            // Size as ONE atomic 8-byte read (W,H contiguous at SizeW/SizeH) — never split into two reads, or a
            // mid-update read tears W from one frame and H from another.
            _reader.TryReadStruct<System.Numerics.Vector2>(el + Poe2.UiElement.SizeW, out sz);
        }
        var (sw, sh) = UiScaleValue(idx, mul, winW, winH);
        if (sw <= 0f || sh <= 0f) return false;
        var (px, py) = UiUnscaledPos(el, 0, winW, winH);
        if (!float.IsFinite(px) || !float.IsFinite(py)) return false;
        x = px * sw; y = py * sh; w = sz.X * sw; h = sz.Y * sh;
        return w > 1f && h > 1f;
    }

    /// <summary>A UiElement's RelativePos (canvas/parent-relative position) as ONE atomic 8-byte read,
    /// VALIDATED. Used by the render thread to re-read atlas node positions per frame so the overlay tracks
    /// pan smoothly. Returns false — so the caller falls back to the last baked position — when the element
    /// is stale/freed/recycled (its Self pointer no longer matches: atlas nodes scrolled far off-screen can
    /// be virtualized, and reading the freed slot yields garbage that projected to streaking lines) or when
    /// the value is non-finite / implausibly large.</summary>
    public bool TryRelPos(nint el, out float x, out float y)
    {
        x = y = 0f;
        if (el == 0) return false;
        // Liveness guard: a real UiElement's Self (+0x08) points back at itself; a recycled/freed slot won't.
        if (!_reader.TryReadStruct<nint>(el + Poe2.UiElement.Self, out var self) || self != el) return false;
        if (!_reader.TryReadStruct<System.Numerics.Vector2>(el + Poe2.UiElement.RelativePos, out var v)) return false;
        if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || MathF.Abs(v.X) > 200000f || MathF.Abs(v.Y) > 200000f) return false;
        x = v.X; y = v.Y; return true;
    }

    /// <summary>v1 = winW/2560, v2 = winH/1600; ScaleIndex picks which axis scale(s) apply (1→(v1,v1),
    /// 2→(v2,v2), 3→(v1,v2), else uniform mul). Mirrors GameHelper's ScaleValue / Poe2Runeforge.</summary>
    private static (float w, float h) UiScaleValue(byte idx, float mul, float winW, float winH)
    {
        if (mul == 0f) mul = 1f;
        var v1 = winW / (float)Poe2.UiElement.BaseResW;
        var v2 = winH / (float)Poe2.UiElement.BaseResH;
        float w = mul, h = mul;
        switch (idx)
        {
            case 1: w *= v1; h *= v1; break;
            case 2: w *= v2; h *= v2; break;
            case 3: w *= v1; h *= v2; break;
        }
        return (w, h);
    }

    /// <summary>Parent-chain accumulated UNSCALED position: relPos + parent position, plus this element's
    /// PositionModifier when its flag <c>0x0A</c> is set. SIMPLE add (no cross-scale rescale) — this is the
    /// form validated for loot tags by Research <c>--lootcursor</c>. (The runeforge panel's
    /// <see cref="Poe2Runeforge"/> keeps a rescale branch, but its rows share their parents' scale so it
    /// never fires; loot tags DO cross scale indices, where the rescale mis-positioned them.)</summary>
    internal (float x, float y) UiUnscaledPos(nint el, int depth, float winW, float winH)
    {
        // Memo (active only during one hover scan): siblings share every ancestor, so each element's chain
        // position is computed once per scan instead of once per descendant (O(N·depth) → O(N) reads).
        if (_uiPosMemo is { } memo && memo.TryGetValue(el, out var known)) return known;

        // RelativePos, Parent, Flags and PositionModifier sit within ~0xB8 bytes of each other: read them in
        // ONE read (was 3–4 syscalls per ancestor). RelativePos X,Y still come from a single copy (no tearing
        // — see UiUnscaledPosSlow). Falls back to the per-field reads if the block can't be read.
        if (UiPosBlockLen > MaxUiBlock) return UiUnscaledPosSlow(el, depth, winW, winH);
        Span<byte> b = stackalloc byte[UiPosBlockLen];
        if (_reader.TryReadBytes(el + UiPosBlockStart, b) != UiPosBlockLen)
            return UiUnscaledPosSlow(el, depth, winW, winH);
        var rel = MemoryMarshal.Read<System.Numerics.Vector2>(b[(Poe2.UiElement.RelativePos - UiPosBlockStart)..]);
        var parent = PlausiblePtr(BinaryPrimitives.ReadInt64LittleEndian(b[(Poe2.UiElement.Parent - UiPosBlockStart)..]));
        (float, float) result;
        if (parent == 0 || depth >= 64) result = (rel.X, rel.Y);
        else
        {
            var (ppx, ppy) = UiUnscaledPos(parent, depth + 1, winW, winH);
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(b[(Poe2.UiElement.Flags - UiPosBlockStart)..]);
            if ((flags & (1u << Poe2.UiElement.FlagModifyPosBit)) != 0)
            {
                var mod = MemoryMarshal.Read<System.Numerics.Vector2>(b[(Poe2.UiElement.PositionModifier - UiPosBlockStart)..]);
                if (PlausibleOffset(mod)) { ppx += mod.X; ppy += mod.Y; }
            }
            result = (ppx + rel.X, ppy + rel.Y);
        }
        _uiPosMemo?.TryAdd(el, result);
        return result;
    }

    /// <summary>
    /// PositionModifier sanity: in the current patch the modify-position flag is set on nearly every element and
    /// the +0xF0 slot is (0,0) on most of them but holds pointer-like garbage (~3e19) on a few HUD ancestors
    /// (verified live 2026-09-24, Research --invui). A real modifier is a small UI-unit offset, so anything else
    /// is ignored instead of flinging the whole subtree off-screen.
    /// </summary>
    private static bool PlausibleOffset(System.Numerics.Vector2 v)
        => float.IsFinite(v.X) && float.IsFinite(v.Y) && MathF.Abs(v.X) < 10000f && MathF.Abs(v.Y) < 10000f;

    // The contiguous span holding every field UiUnscaledPos needs (derived from the offset table, so a patch
    // that moves a field just moves the block; a span too large to be worth it disables the block path).
    private static readonly int UiPosBlockStart = Math.Min(Math.Min(Poe2.UiElement.RelativePos, Poe2.UiElement.Parent),
        Math.Min(Poe2.UiElement.Flags, Poe2.UiElement.PositionModifier));

    private static readonly int UiPosBlockLen = Math.Max(Math.Max(Poe2.UiElement.RelativePos + 8, Poe2.UiElement.Parent + 8),
        Math.Max(Poe2.UiElement.Flags + 4, Poe2.UiElement.PositionModifier + 8)) - UiPosBlockStart;

    private const int MaxUiBlock = 0x400;   // beyond this a combined read isn't worth it (offsets drifted apart)

    private static readonly int UiRectBlockStart = Math.Min(Math.Min(Poe2.UiElement.Flags, Poe2.UiElement.ScaleIndex),
        Math.Min(Poe2.UiElement.LocalScaleMul, Poe2.UiElement.SizeW));

    private static readonly int UiRectBlockLen = Math.Max(Math.Max(Poe2.UiElement.Flags + 4, Poe2.UiElement.ScaleIndex + 1),
        Math.Max(Poe2.UiElement.LocalScaleMul + 4, Poe2.UiElement.SizeW + 8)) - UiRectBlockStart;

    private Dictionary<nint, (float x, float y)>? _uiPosMemo;

    private readonly Dictionary<nint, (float x, float y)> _uiPosMemoStore = new();

    /// <summary>The original per-field parent-chain walk (reference semantics + fallback).</summary>
    internal (float x, float y) UiUnscaledPosSlow(nint el, int depth, float winW, float winH)
    {
        // RelativePos as ONE atomic 8-byte read (X,Y contiguous). Splitting it into two float reads tears
        // X from one frame and Y from the next while the game is repositioning a world-anchored loot label
        // as the player moves — which is exactly what made the chips jitter in motion. Same idiom as the
        // HP-bar Vector3 world read.
        _reader.TryReadStruct<System.Numerics.Vector2>(el + Poe2.UiElement.RelativePos, out var rel);
        var parent = Ptr(el + Poe2.UiElement.Parent);
        if (parent == 0 || depth >= 64) return (rel.X, rel.Y);

        var (ppx, ppy) = UiUnscaledPos(parent, depth + 1, winW, winH);

        if (_reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var flags)
            && (flags & (1u << Poe2.UiElement.FlagModifyPosBit)) != 0)
        {
            _reader.TryReadStruct<System.Numerics.Vector2>(el + Poe2.UiElement.PositionModifier, out var mod);
            if (PlausibleOffset(mod)) { ppx += mod.X; ppy += mod.Y; }
        }
        return (ppx + rel.X, ppy + rel.Y);
    }

    /// <summary>Enqueue an element's child pointers, read in blocks of up to 128 per syscall (was one read per
    /// child). Implausible pointers are dropped (the per-child path enqueued them as 0, which the BFS skips).
    /// A block that can't be read falls back to per-pointer reads, so nothing is lost vs the old path.</summary>
    internal void EnqueueChildren(Queue<nint> queue, nint first, long n)
    {
        Span<nint> block = stackalloc nint[128];
        for (long k = 0; k < n;)
        {
            var count = (int)Math.Min(block.Length, n - k);
            var bytes = MemoryMarshal.AsBytes(block[..count]);
            if (_reader.TryReadBytes(first + (nint)(k * 8), bytes) == bytes.Length)
            {
                for (var i = 0; i < count; i++)
                    if (PlausiblePtr(block[i]) is var c and not 0) queue.Enqueue(c);
            }
            else
            {
                for (var i = 0; i < count; i++) queue.Enqueue(Ptr(first + (nint)((k + i) * 8)));
            }
            k += count;
        }
    }

    /// <summary>Read the post-ritual tribute shop's offered rewards (full item entities) so the overlay can
    /// price each. The reward tiles are item-slot UiElements carrying their item Entity at
    /// <see cref="Poe2Offsets.Ritual.TileSlotItem"/> (+0x4F8); ALL are present with no hover. Returns empty
    /// when the shop is closed — gated on a shop-signature text element, so it's cheap when not shopping.
    /// World thread (resolves item components via the per-area cache). See the ritual-rewards RE notes.</summary>
    public List<RitualReward> ReadRitualRewards(nint inGameState, float winW, float winH)
    {
        var result = new List<RitualReward>();
        var uiRoot = Ptr(inGameState + Poe2.InGameState.UiRoot);
        if (uiRoot == 0) return result;
        const uint visBit = 1u << Poe2.UiElement.FlagVisibleBit;

        // 1) Confirm the shop is open + find a signature text element. BFS the VISIBLE tree (invisible
        //    subtrees pruned → cheap); the signature only renders while the tribute shop is up.
        nint sigEl = 0;
        var queue = new Queue<nint>(); queue.Enqueue(uiRoot);
        var visited = new HashSet<nint>();
        while (queue.Count > 0 && visited.Count < 20000)
        {
            var el = queue.Dequeue();
            if (el == 0 || !visited.Add(el)) continue;
            var visible = _reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var flags) && (flags & visBit) != 0;
            if (!visible && el != uiRoot) continue;             // prune invisible subtree
            if (ChildSpan(el, out var f, out var nn)) EnqueueChildren(queue, f, nn);
            if (sigEl == 0)
            {
                var t = ReadStdWString(el + Poe2.UiElement.Text);
                if (t.Length >= 6 && (t.Contains("Rituals Remaining", StringComparison.OrdinalIgnoreCase)
                                      || t.Contains("tribute to the king", StringComparison.OrdinalIgnoreCase)))
                    sigEl = el;
            }
        }
        if (sigEl == 0) return result;   // shop closed

        // 2) Walk UP from the signature element; at each ancestor look among its DIRECT children for the
        //    reward grid (a container of item-slot tiles). Grid + signature share the shop-window ancestor;
        //    the flask bar — the only other multi-slot item grid — is NOT under that window, so it's excluded.
        var cur = sigEl;
        nint grid = 0;
        for (var up = 0; up < 8 && grid == 0; up++)
        {
            grid = FindRewardGrid(cur);
            var parent = Ptr(cur + Poe2.UiElement.Parent);
            if (parent == 0) break;
            cur = parent;
        }
        if (grid == 0 || !ChildSpan(grid, out var gf, out var gn)) return result;

        // 3) Read each tile's item entity (+0x4F8) → identity, and its live screen rect.
        for (long i = 0; i < gn; i++)
        {
            var tile = Ptr(gf + (nint)(i * 8));
            var item = TileItem(tile);
            if (item == 0) continue;
            var (rarity, art, identified, name) = ReadIdentityFromItem(item);
            if (!TryUiElementRect(tile, winW, winH, out var x, out var y, out var w, out var h)) continue;
            result.Add(new RitualReward(rarity, art, name, identified, x, y, w, h));
        }
        return result;
    }

    /// <summary>A reward-grid container among <paramref name="parent"/>'s DIRECT children: the child whose
    /// own children are mostly item-slot tiles (item entity at +0x4F8). Returns the best (≥2 tiles) or 0.</summary>
    private nint FindRewardGrid(nint parent)
    {
        if (!ChildSpan(parent, out var first, out var n)) return 0;
        nint best = 0; var bestItems = 0;
        for (long i = 0; i < n; i++)
        {
            var c = Ptr(first + (nint)(i * 8));
            if (!ChildSpan(c, out var cf, out var cn) || cn is < 1 or > 16) continue;
            var items = 0;
            for (long k = 0; k < cn; k++) if (TileItem(Ptr(cf + (nint)(k * 8))) != 0) items++;
            if (items >= 2 && items > bestItems && items * 2 >= cn) { best = c; bestItems = items; }
        }
        return best;
    }

    /// <summary>The item Entity held by an item-slot tile (+0x4F8), or 0 when empty / not an item element
    /// (validated by requiring a RenderItem component).</summary>
    private nint TileItem(nint tile)
    {
        if (tile == 0) return 0;
        var item = Ptr(tile + Poe2.Ritual.TileSlotItem);
        return item != 0 && ResolveComponent(item, "RenderItem") != 0 ? item : 0;
    }

    /// <summary>
    /// Find a VISIBLE UI element whose text contains <paramref name="needle"/> (case-insensitive) and return
    /// its screen rect — e.g. the death screen's "Resurrect at Checkpoint" button so the bot can click it.
    /// BFS over the visible tree (invisible subtrees pruned); bounded to 20k nodes. Render thread safe.
    /// </summary>
    public bool TryFindVisibleTextRect(nint inGameState, string needle, float winW, float winH,
        out float x, out float y, out float w, out float h, out string text)
    {
        x = y = w = h = 0f; text = "";
        var uiRoot = Ptr(inGameState + Poe2.InGameState.UiRoot);
        if (uiRoot == 0) return false;
        const uint visBit = 1u << Poe2.UiElement.FlagVisibleBit;
        var queue = new Queue<nint>(); queue.Enqueue(uiRoot);
        var visited = new HashSet<nint>();
        while (queue.Count > 0 && visited.Count < 20000)
        {
            var el = queue.Dequeue();
            if (el == 0 || !visited.Add(el)) continue;
            var visible = _reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var flags) && (flags & visBit) != 0;
            if (!visible && el != uiRoot) continue;
            if (ChildSpan(el, out var first, out var nn)) EnqueueChildren(queue, first, nn);
            var t = ReadStdWString(el + Poe2.UiElement.Text);
            if (t.Length < needle.Length || !t.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
            // The text element may be a zero-size label inside the button — use the first ancestor with a real size.
            var cur = el;
            for (var up = 0; up < 4; up++)
            {
                if (TryUiElementRect(cur, winW, winH, out x, out y, out w, out h) && w >= 8f && h >= 8f) { text = t; return true; }
                cur = Ptr(cur + Poe2.UiElement.Parent);
                if (cur == 0) break;
            }
        }
        return false;
    }

    private bool ChildSpan(nint el, out nint first, out long n)
    {
        first = Ptr(el + Poe2.UiElement.Children); n = 0;
        if (first == 0) return false;
        if (!_reader.TryReadStruct<nint>(el + Poe2.UiElement.ChildrenEnd, out var last)) return false;
        n = ((long)last - (long)first) / 8;
        return n is > 0 and <= 4000;
    }

    /// <summary>BFS the world-anchored ground-label layer (the <c>ItemsOnGroundLabelElement</c>, resolved by
    /// <see cref="ResolveGroundLabelContainer"/>) for VISIBLE, text-bearing elements and return each one's
    /// address + the FIRST LINE of its text. <b>Scoped to that container's subtree</b> — NOT the whole UI
    /// tree — so a value chip can only land on a real on-ground loot tag, never on an unrelated panel
    /// (stash/vendor/reward) whose text happens to match a priced item name. Invisible subtrees are PRUNED.
    /// The caller matches each first line to a priced item by NAME (a loot tag's text IS the item name, so no
    /// item-entity link is needed) and reads the live rect via <see cref="TryUiElementRect"/>. Empty when not
    /// in game or no items are on the ground. Bounded by <paramref name="maxNodes"/>. Allocates a fresh
    /// list/queue/set per call → meant to run THROTTLED on the world thread, not per frame.</summary>
    public List<(nint El, string Text)> ScanLootLabels(nint inGameState, int maxNodes = 20000)
    {
        var result = new List<(nint, string)>();
        var container = ResolveGroundLabelContainer(inGameState);
        if (container == 0) return result;
        const uint visBit = 1u << Poe2.UiElement.FlagVisibleBit;

        var queue = new Queue<nint>(); queue.Enqueue(container);
        var visited = new HashSet<nint>();
        while (queue.Count > 0 && visited.Count < maxNodes)
        {
            var el = queue.Dequeue();
            if (el == 0 || !visited.Add(el)) continue;
            var visible = _reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var flags) && (flags & visBit) != 0;
            if (!visible && el != container) continue;   // prune the invisible subtree (container always descended)

            var first = Ptr(el + Poe2.UiElement.Children);
            if (first != 0 && _reader.TryReadStruct<nint>(el + Poe2.UiElement.ChildrenEnd, out var last))
            {
                var n = ((long)last - (long)first) / 8;
                if (n is > 0 and <= 8192) EnqueueChildren(queue, first, n);
            }

            var text = ReadStdWString(el + Poe2.UiElement.Text);
            if (text.Length < 2) continue;
            var nl = text.IndexOf('\n');
            var firstLine = (nl >= 0 ? text[..nl] : text).Trim();
            if (firstLine.Length >= 2) result.Add((el, firstLine));
        }
        return result;
    }

    /// <summary>Read the item currently under the cursor in an item UI (inventory/stash/vendor/reward): the
    /// innermost VISIBLE UiElement whose screen rect holds the cursor and that carries an item entity at
    /// +0x4F8 (<see cref="Poe2Offsets.Ritual.TileSlotItem"/> — the universal item-slot field). Reads its
    /// identity (<see cref="ReadIdentityFromItem"/>) + stack count and returns the slot's screen rect for the
    /// overlay to anchor a price chip to. Returns null when nothing item-like is hovered. One pruned visible-
    /// tree walk (invisible subtrees skipped; per element just a +0x4F8 read, rects only for item slots) —
    /// meant to run THROTTLED on the world thread. <paramref name="curX"/>/<paramref name="curY"/> are
    /// overlay-client pixels (TryUiElementRect space).</summary>
    private readonly Queue<nint> _hoverQueue = new();
    private readonly HashSet<nint> _hoverVisited = new();
    private nint _hoverSlot, _hoverRoot;
    private long _hoverSlotScanAt;
    public long HoverCacheHits { get; private set; }
    public int HoverScanNodes { get; private set; }
    public long HoverScanReads { get; private set; }
    public double HoverScanMilliseconds { get; private set; }

    public HoveredItem? ReadHoveredItem(nint inGameState, float winW, float winH, float curX, float curY, int maxNodes = 40000)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var reads = _reader.ReadCount;
        HoverScanNodes = 0;
        _uiPosMemoStore.Clear();
        _uiPosMemo = _uiPosMemoStore;   // one scan = one consistent snapshot of the UI positions
        try { return ReadHoveredItemCore(inGameState, winW, winH, curX, curY, maxNodes); }
        finally
        {
            _uiPosMemo = null;
            _uiPosMemoStore.Clear();
            HoverScanReads = _reader.ReadCount - reads;
            HoverScanMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
    }

    // A cached slot is only valid while its self pointer and every visible ancestor still lead to this
    // root. Recheck the item pointer/identity/stack on EVERY hit; inventory slots can be replaced in place.
    private bool IsUiVisibleForHover(nint slot)
        => _reader.TryReadStruct<uint>(slot + Poe2.UiElement.Flags, out var flags)
            && (flags & (1u << Poe2.UiElement.FlagVisibleBit)) != 0;

    private bool IsLiveHoverSlot(nint slot, nint root)
    {
        for (var depth = 0; depth < 64 && slot != 0; depth++)
        {
            if (slot == root) return true;
            if (Ptr(slot + Poe2.UiElement.Self) != slot || !IsUiVisibleForHover(slot)) return false;
            slot = Ptr(slot + Poe2.UiElement.Parent);
        }
        return false;
    }

    private HoveredItem? ReadHoveredItemCore(nint inGameState, float winW, float winH, float curX, float curY, int maxNodes = 40000)
    {
        var uiRoot = Ptr(inGameState + Poe2.InGameState.UiRoot);
        if (uiRoot == 0) return null;
        const uint visBit = 1u << Poe2.UiElement.FlagVisibleBit;

        nint bestEl = 0, bestItem = 0; var bestArea = float.MaxValue;

        if (maxNodes <= 0 || !float.IsFinite(curX) || !float.IsFinite(curY)) return null;
        if (_hoverSlot != 0 && _hoverRoot == uiRoot
            && System.Diagnostics.Stopwatch.GetElapsedTime(_hoverSlotScanAt).TotalMilliseconds < 500
            && IsLiveHoverSlot(_hoverSlot, uiRoot)
            && TryUiElementRect(_hoverSlot, winW, winH, out var hx, out var hy, out var hw, out var hh)
            && curX >= hx && curX <= hx + hw && curY >= hy && curY <= hy + hh)
        {
            var current = ItemFromHoverSlot(_hoverSlot, scan: true);
            if (current != 0 && ResolveComponent(current, "RenderItem") != 0)
            {
                bestEl = _hoverSlot; bestItem = current; HoverCacheHits++;
            }
        }
        if (bestItem == 0)
        {
            var queue = _hoverQueue; queue.Clear(); queue.Enqueue(uiRoot);
            var visited = _hoverVisited; visited.Clear();
            Span<nint> childPointers = stackalloc nint[128];
            while (queue.Count > 0 && visited.Count < maxNodes)
            {
                var el = queue.Dequeue();
                if (el == 0 || !visited.Add(el)) continue;
                var visible = _reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var flags) && (flags & visBit) != 0;
                if (!visible && el != uiRoot) continue;   // prune invisible subtree
                if (ChildSpan(el, out var f, out var nn))
                {
                    // Read adjacent child pointers in blocks instead of making one OS call per child.
                    for (long k = 0; k < nn && queue.Count + visited.Count < maxNodes;)
                    {
                        var count = (int)Math.Min(childPointers.Length, Math.Min(nn - k, maxNodes - queue.Count - visited.Count));
                        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(childPointers[..count]);
                        if (_reader.TryReadBytes(f + (nint)(k * 8), bytes) != bytes.Length) break;
                        for (int i = 0; i < count; i++)
                            if ((ulong)childPointers[i] is >= 0x10000 and <= 0x7FFFFFFFFFFF) queue.Enqueue(childPointers[i]);
                        k += count;
                    }
                }
                if (el == uiRoot) continue;

                // Item slot? (+0x4F8 → item entity, validated by a RenderItem component.) Keep the SMALLEST
                // cursor-containing slot (the innermost — grids nest the slot inside panel elements).
                if (!TryUiElementRect(el, winW, winH, out var rx, out var ry, out var rw, out var rh)
                    || curX < rx || curX > rx + rw || curY < ry || curY > ry + rh || rw * rh >= bestArea) continue;
                var it = ItemFromHoverSlot(el, scan: rw < winW * 0.4f && rh < winH * 0.4f);
                if (it != 0)
                {
                    bestArea = rw * rh; bestEl = el; bestItem = it;
                }
            }

            HoverScanNodes = visited.Count;
            _hoverSlot = bestEl; _hoverRoot = uiRoot;
            _hoverSlotScanAt = System.Diagnostics.Stopwatch.GetTimestamp();
            queue.Clear(); visited.Clear();
        }

        // Some inventory UI variants expose the hovered entity directly through the game tracker.
        if (bestItem == 0) bestItem = ItemFromLiveHoverTracker(uiRoot);

        // Ground drops use a WorldItem wrapper instead of an inventory slot.
        if (bestItem == 0)
        {
            var world = MouseOverEntity(inGameState);
            var wrapper = world == 0 ? 0 : ResolveComponent(world, "WorldItem");
            bestItem = wrapper == 0 ? 0 : Ptr(wrapper + Poe2.WorldItemComponent.ItemEntity);
            if (bestItem == 0 || ResolveComponent(bestItem, "RenderItem") == 0) return null;
        }
        var (rarity, art, identified, name) = ReadIdentityFromItem(bestItem);
        var stackComp = ResolveComponent(bestItem, "Stack");
        var stack = 0; if (stackComp != 0) _reader.TryReadStruct<int>(stackComp + Poe2.StackComponent.Count, out stack);

        float bx = curX + 16, by = curY + 16, bw = 1, bh = 1;
        if (bestEl != 0 && !TryUiElementRect(bestEl, winW, winH, out bx, out by, out bw, out bh)) return null;
        return new HoveredItem(bestItem, rarity, art, identified, name, stack, bx, by, bw, bh);
    }

    /// <summary>The world Entity currently under the cursor (monsters/NPCs/doodads/ground items included), or
    /// 0 when nothing — or only UI — is hovered. Resolves the community hover chain off InGameState (three
    /// pointer hops, see <see cref="Poe2Offsets.MouseOver"/>); validated live 2026-06-29. Cheap enough for the
    /// render loop. Pair with <see cref="ReadItemIdentity"/> / component reads to identify whatever's hovered.</summary>
    public nint MouseOverEntity(nint inGameState)
    {
        if (inGameState == 0) return 0;
        var host = Ptr(inGameState + Poe2.MouseOver.HostFromInGameState);
        if (host == 0) return 0;
        var sub = Ptr(host + Poe2.MouseOver.SubFromHost);
        if (sub == 0) return 0;
        return Ptr(sub + Poe2.MouseOver.EntityFromSub);
    }

    /// <summary>Resolve the world-anchored ground-label container (the <c>ItemsOnGroundLabelElement</c>) by a
    /// FLAGS-FINGERPRINT walk with backtracking from GameUi (<c>InGameState+0x2F0</c>), mirroring
    /// <see cref="Poe2Runeforge"/>'s panel resolution — child indices drift per patch, the Flags "role" bits
    /// don't, so each hop matches <c>(flags &amp; ~visibleBit) == fingerprint</c> and keeps whichever branch
    /// bottoms out at the labels container. The container persists per area but its children populate/empty
    /// as items drop/are looted, so a fresh walk every (throttled) scan self-heals. Returns 0 when the path
    /// can't be matched (not in game / layout changed). See <see cref="Poe2Offsets.GroundLabels"/>.</summary>
    public nint ResolveGroundLabelContainer(nint inGameState)
    {
        var gameUi = Ptr(inGameState + Poe2.InGameState.UiRoot);
        return gameUi == 0 ? 0 : WalkGroundLabels(gameUi, 0);
    }

    private nint WalkGroundLabels(nint parent, int step)
    {
        var fps = Poe2.GroundLabels.ContainerFlagFingerprints;
        const uint visibleMask = 1u << Poe2.UiElement.FlagVisibleBit;
        if (step == fps.Length) return parent;             // matched the full fingerprint path → container
        if (!ChildSpan(parent, out var first, out var n)) return 0;
        var target = fps[step] & ~visibleMask;
        for (long i = 0; i < n; i++)
        {
            var child = Ptr(first + (nint)(i * 8));
            if (child == 0) continue;
            if (!_reader.TryReadStruct<uint>(child + Poe2.UiElement.Flags, out var flags)) continue;
            if ((flags & ~visibleMask) != target) continue;
            var deeper = WalkGroundLabels(child, step + 1);
            if (deeper != 0) return deeper;
        }
        return 0;
    }

    /// <summary>RENDER-RATE live read of one already-known monster's world position + HP, reusing the
    /// component addresses cached by the last <see cref="Entities"/> walk (no component re-resolve, no map
    /// re-enumeration). This is what lets HP bars track a moving monster smoothly at the full frame rate
    /// while the expensive entity enumeration stays at world rate. Two tiny reads (12-byte position, 8-byte
    /// vital). Returns false if the entity isn't in the current area's cache or the position read fails.</summary>
    public bool TryLiveBar(nint entity, out Vector3 world, out int hpCur, out int hpMax)
    {
        world = default; hpCur = 0; hpMax = 0;
        if (!_renderAddr.TryGetValue(entity, out var render) || render == 0) return false;
        if (!_reader.TryReadStruct<Vector3>(render + Poe2.Render.CurrentWorldPosition, out world)) return false;
        if (_lifeAddr.TryGetValue(entity, out var life) && life != 0
            && _reader.TryReadStruct<VitalStruct>(life + _healthOff, out var v)) { hpCur = v.Current; hpMax = v.Max; }
        return true;
    }

    /// <summary>The Render + Life component addresses cached for <paramref name="entity"/> by the most
    /// recent <see cref="Entities"/> walk (0 when not resolved). Lets the world thread CAPTURE these into
    /// an HP-bar spec so the RENDER thread can read the bar's live pos/HP via <see cref="TryLiveBarAt"/>
    /// on its OWN reader stack — no shared per-entity cache between threads. Returns false if no Render.</summary>
    public bool TryBarComponents(nint entity, out nint render, out nint life)
    {
        render = _renderAddr.GetValueOrDefault(entity);
        life = _lifeAddr.GetValueOrDefault(entity);
        return render != 0;
    }

    /// <summary>RENDER-RATE bar read from EXPLICIT component addresses (captured off the world thread's
    /// <see cref="Entities"/> walk via <see cref="TryBarComponents"/>), using THIS instance's reader +
    /// resolved Health offset. Touches no per-entity cache, so a render-thread <see cref="Poe2Live"/> can
    /// drive HP bars without sharing state with the world-thread instance. <paramref name="render"/> must
    /// be non-zero. Returns false on a failed position read.</summary>
    public bool TryLiveBarAt(nint render, nint life, out Vector3 world, out int hpCur, out int hpMax)
    {
        world = default; hpCur = 0; hpMax = 0;
        if (render == 0 || !_reader.TryReadStruct<Vector3>(render + Poe2.Render.CurrentWorldPosition, out world)) return false;
        if (life != 0 && _reader.TryReadStruct<VitalStruct>(life + _healthOff, out var v)) { hpCur = v.Current; hpMax = v.Max; }
        return true;
    }

    /// <summary>WorldToScreen matrix (16 floats, row-major) from Camera@InGameState+0x368. Null if unavailable.</summary>
    public float[]? CameraMatrix(nint inGameState)
    {
        var cam = Ptr(inGameState + Poe2.InGameState.Camera);
        if (cam == 0) return null;
        // Reuse the buffers — this runs every render frame; the result is consumed synchronously.
        if (_reader.TryReadBytes(cam + Poe2.Camera.WorldToScreenMatrix, _camBytes) != 64) return null;
        System.Buffer.BlockCopy(_camBytes, 0, _camMatrix, 0, 64);
        return _camMatrix;
    }
}
