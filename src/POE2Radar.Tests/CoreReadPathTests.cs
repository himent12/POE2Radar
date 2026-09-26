using System.Runtime.InteropServices;
using System.Text;
using POE2Radar.Core;
using POE2Radar.Core.Game;
using Xunit;

namespace POE2Radar.Tests;

/// <summary>
/// The optimized Core read paths (bulk bucket read + validated name-map cache, one-read std::wstring header,
/// per-type metadata cache, lazy TryResolve, block-read UI parent chain, batched child enqueue) must return
/// exactly what the original per-field reads return. Fake game structures are laid out in THIS process's
/// unmanaged memory and read back through the real MemoryReader (process_vm_readv / ReadProcessMemory on self).
/// </summary>
public sealed unsafe class CoreReadPathTests : IDisposable
{
    private readonly List<nint> _allocs = new();
    private readonly ProcessHandle _proc = ProcessHandle.ForCurrentProcess();
    private readonly Poe2Live _live;

    public CoreReadPathTests() => _live = new Poe2Live(new MemoryReader(_proc), 0);

    public void Dispose()
    {
        foreach (var a in _allocs) NativeMemory.Free((void*)a);
        _proc.Dispose();
    }

    private nint Alloc(int bytes)
    {
        var p = (nint)NativeMemory.AllocZeroed((nuint)bytes);
        _allocs.Add(p);
        return p;
    }

    private static void W64(nint at, long v) => *(long*)at = v;
    private static void W32(nint at, int v) => *(int*)at = v;
    private static void WF(nint at, float v) => *(float*)at = v;

    private nint Utf8(string s)
    {
        var b = Encoding.UTF8.GetBytes(s + "\0");
        var p = Alloc(Math.Max(b.Length, 64));
        Marshal.Copy(b, 0, p, b.Length);
        return p;
    }

    /// <summary>Write an MSVC std::wstring at <paramref name="at"/> (inline when len &lt; 8, else heap).</summary>
    private void WString(nint at, string s)
    {
        W64(at + 0x10, s.Length);
        W64(at + 0x18, s.Length < 8 ? 7 : s.Length);
        if (s.Length < 8)
        {
            for (var i = 0; i < s.Length; i++) *(char*)(at + i * 2) = s[i];
            return;
        }
        var heap = Alloc((s.Length + 1) * 2);
        for (var i = 0; i < s.Length; i++) *(char*)(heap + i * 2) = s[i];
        W64(at, heap);
    }

    /// <summary>An entity whose ComponentLookUp bucket holds <paramref name="bucket"/> (name, index) entries
    /// (a null name → an implausible name pointer) and whose ComponentList has <paramref name="compCount"/> slots.</summary>
    private (nint entity, nint lookup, nint bucket, nint[] comps) Entity((string? name, int index)[] bucket, int compCount,
        nint lookup = 0, nint bucketMem = 0, string meta = "Metadata/Monsters/Test/Thing")
    {
        var comps = new nint[compCount];
        var list = Alloc(compCount * 8);
        for (var i = 0; i < compCount; i++) { comps[i] = Alloc(16); W64(list + i * 8, comps[i]); }

        if (lookup == 0)
        {
            lookup = Alloc(0x40);
            bucketMem = Alloc(bucket.Length * 16);
            for (var i = 0; i < bucket.Length; i++)
            {
                W64(bucketMem + i * 16, bucket[i].name is { } n ? Utf8(n) : 0x10);
                W32(bucketMem + i * 16 + 8, bucket[i].index);
            }
            W64(lookup + Poe2.ComponentLookUp.NameAndIndexBucket, bucketMem);
            W64(lookup + Poe2.ComponentLookUp.NameAndIndexBucket + 8, bucketMem + bucket.Length * 16);
        }
        var details = Alloc(0x40);
        WString(details + Poe2.EntityDetails.Name, meta);
        W64(details + Poe2.EntityDetails.ComponentLookUpPtr, lookup);
        var entity = Alloc(0x40);
        W64(entity + Poe2.Entity.EntityDetailsPtr, details);
        W64(entity + Poe2.Entity.ComponentList, list);
        W64(entity + Poe2.Entity.ComponentList + 8, list + compCount * 8);
        W64(entity + Poe2.Entity.ComponentList + 16, list + compCount * 8);
        return (entity, lookup, bucketMem, comps);
    }

    private static readonly string[] Probe =
        { "Render", "Life", "Positioned", "MinimapIcon", "ObjectMagicProperties", "Chest", "Dup", "Missing", "Stack" };

    [Fact]
    public void ResolveComponent_matches_per_entry_scan_and_caches_per_lookup()
    {
        var bucket = new (string? name, int index)[]
        {
            ("Render", 0), ("Positioned", 1), ("Life", 2),
            ("Dup", 9),           // out of range for a 6-slot list → the second "Dup" must win
            ("Dup", 4),
            (null, 5),            // implausible name pointer → reads as ""
            ("MinimapIcon", -1),  // negative index → never matches
            ("ObjectMagicProperties", 3),
            ("AVeryLongComponentNameThatExceedsThirtyTwoBytes", 5),
        };
        var (e1, lookup, bucketMem, comps) = Entity(bucket, 6);
        foreach (var n in Probe)
            Assert.Equal(_live.ResolveComponentSlow(e1, n), _live.ResolveComponent(e1, n));
        Assert.Equal(comps[4], _live.ResolveComponent(e1, "Dup"));
        Assert.Equal(1, _live.CachedLookupMaps);

        // A second entity of the same type (shared lookup) with FEWER components: range checks are per entity.
        var (e2, _, _, _) = Entity(bucket, 3, lookup, bucketMem);
        foreach (var n in Probe)
            Assert.Equal(_live.ResolveComponentSlow(e2, n), _live.ResolveComponent(e2, n));
        Assert.Equal(0, _live.ResolveComponent(e2, "Dup"));
        Assert.Equal(1, _live.CachedLookupMaps);

        // The bucket changes in place → the cached map is invalidated by its byte check.
        W32(bucketMem + 2 * 16 + 8, 5);   // Life → index 5
        Assert.Equal(comps[5], _live.ResolveComponent(e1, "Life"));
        Assert.Equal(_live.ResolveComponentSlow(e1, "Life"), _live.ResolveComponent(e1, "Life"));
    }

    [Fact]
    public void ResolveComponent_does_not_cache_a_map_with_an_unreadable_name()
    {
        var bucket = new (string? name, int index)[] { ("Render", 0), ("Life", 1) };
        var (e, _, bucketMem, _) = Entity(bucket, 2);
        W64(bucketMem + 16, 0x10000);    // plausible but unmapped name pointer → read fails
        foreach (var n in Probe)
            Assert.Equal(_live.ResolveComponentSlow(e, n), _live.ResolveComponent(e, n));
        Assert.Equal(0, _live.CachedLookupMaps);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("seven77")]
    [InlineData("eight888")]
    [InlineData("Metadata/Characters/Int/IntFour")]
    public void StdWString_header_read_matches_field_reads(string text)
    {
        var at = Alloc(0x20);
        if (text.Length > 0) WString(at, text);
        Assert.Equal(_live.ReadStdWStringSlow(at), _live.ReadStdWString(at));
        Assert.Equal(text, _live.ReadStdWString(at));
    }

    [Fact]
    public void StdWString_inline_stops_at_nul_and_rejects_bad_lengths()
    {
        var at = Alloc(0x20);
        W64(at + 0x10, 5);
        *(char*)at = 'a'; *(char*)(at + 2) = 'b'; *(char*)(at + 4) = '\0'; *(char*)(at + 6) = 'z';
        Assert.Equal("ab", _live.ReadStdWString(at));
        Assert.Equal(_live.ReadStdWStringSlow(at), _live.ReadStdWString(at));
        W64(at + 0x10, 5000);
        Assert.Equal("", _live.ReadStdWString(at));
        W64(at + 0x10, 20); W64(at, 0x20);   // heap string with an implausible pointer
        Assert.Equal("", _live.ReadStdWString(at));
    }

    [Theory]
    [InlineData("Chaos Orb", "Chaos Orb", true)]
    [InlineData("  Chaos Orb \nx12", "Chaos Orb", true)]
    [InlineData("Chaos Orb x", "Chaos Orb", false)]
    [InlineData("Orb", "Orb", true)]
    [InlineData("Orb\n", "Orb", true)]
    [InlineData("", "Orb", false)]
    [InlineData("Exalted Orb\r\nstack", "Exalted Orb", true)]
    public void First_line_compare_matches_string_path(string text, string expected, bool equal)
    {
        var at = Alloc(0x20);
        if (text.Length > 0) WString(at, text);
        var t = _live.ReadStdWStringSlow(at);
        var nl = t.IndexOf('\n');
        var reference = string.Equals((nl >= 0 ? t[..nl] : t).Trim(), expected, StringComparison.Ordinal);
        Assert.Equal(equal, reference);
        Assert.Equal(reference, _live.WStringFirstLineEquals(at, expected));
    }

    [Fact]
    public void Metadata_cache_follows_the_string_header()
    {
        var (e, _, _, _) = Entity(new (string?, int)[] { ("Render", 0) }, 1, meta: "Metadata/Monsters/A/First");
        Assert.Equal("Metadata/Monsters/A/First", _live.ReadMetadata(e));
        Assert.Equal("Metadata/Monsters/A/First", _live.ReadMetadata(e));
        var details = *(nint*)(e + Poe2.Entity.EntityDetailsPtr);
        WString(details + Poe2.EntityDetails.Name, "Metadata/Monsters/B/Second");   // new heap buffer → new header
        Assert.Equal("Metadata/Monsters/B/Second", _live.ReadMetadata(e));
    }

    [Fact]
    public void TryResolve_prefers_the_active_state_then_falls_back_to_state_slots()
    {
        var (player, _, _, _) = Entity(new (string?, int)[] { ("Render", 0) }, 1, meta: "Metadata/Characters/Str/StrFour");
        var ai = Alloc(Poe2.AreaInstance.LocalPlayer + 0x10);
        W64(ai + Poe2.AreaInstance.LocalPlayer, player);
        var igs = Alloc(Poe2.InGameState.AreaInstanceData + 0x10);
        W64(igs + Poe2.InGameState.AreaInstanceData, ai);

        var gs = Alloc(Poe2.GameState.States + Poe2.GameState.StateSlotCount * Poe2.GameState.StateSlotStride + 0x10);
        var slot = Alloc(8);
        W64(slot, gs);
        var live = new Poe2Live(new MemoryReader(_proc), slot);

        // Active-state vector → igs.
        var vec = Alloc(8); W64(vec, igs);
        W64(gs + Poe2.GameState.CurrentStatePtr, vec);
        Assert.True(live.TryResolve(out var g, out var a, out var p));
        Assert.Equal((igs, ai, player), (g, a, p));

        // Active state is a non-game state → States[3] holds the in-game state.
        var bogus = Alloc(Poe2.InGameState.AreaInstanceData + 0x10);
        W64(vec, bogus);
        W64(gs + Poe2.GameState.States + 3 * Poe2.GameState.StateSlotStride, igs);
        Assert.True(live.TryResolve(out g, out a, out p));
        Assert.Equal((igs, ai, player), (g, a, p));

        // Nothing valid.
        W64(gs + Poe2.GameState.States + 3 * Poe2.GameState.StateSlotStride, 0);
        Assert.False(live.TryResolve(out g, out a, out p));
        Assert.Equal((0, 0, 0), ((long)g, (long)a, (long)p));
    }

    [Fact]
    public void Ui_parent_chain_block_read_matches_field_reads()
    {
        const int size = 0x400;
        nint El(nint parent, float rx, float ry, bool modify, float mx, float my)
        {
            var el = Alloc(size);
            W64(el + Poe2.UiElement.Self, el);
            W64(el + Poe2.UiElement.Parent, parent);
            WF(el + Poe2.UiElement.RelativePos, rx); WF(el + Poe2.UiElement.RelativePos + 4, ry);
            W32(el + Poe2.UiElement.Flags, modify ? 1 << Poe2.UiElement.FlagModifyPosBit : 0);
            WF(el + Poe2.UiElement.PositionModifier, mx); WF(el + Poe2.UiElement.PositionModifier + 4, my);
            return el;
        }
        var root = El(0, 10f, 20f, false, 0, 0);
        var mid = El(root, 5f, -3f, true, 100f, 200f);
        var leaf = El(mid, 1.5f, 2.5f, true, 7f, 9f);
        var bad = El(0x20, 3f, 4f, true, 1f, 1f);   // implausible parent pointer → treated as root
        foreach (var el in new[] { root, mid, leaf, bad })
            Assert.Equal(_live.UiUnscaledPosSlow(el, 0, 1920, 1080), _live.UiUnscaledPos(el, 0, 1920, 1080));
        Assert.Equal((10f + 100f + 7f + 5f + 1.5f, 20f + 200f + 9f - 3f + 2.5f), _live.UiUnscaledPos(leaf, 0, 1920, 1080));
    }

    [Theory]
    [InlineData(true, -20f)]
    [InlineData(false, -20f)]
    [InlineData(true, 0f)]
    public void Map_element_block_read_matches_field_reads(bool visible, float defaultShiftY)
    {
        var el = Alloc(0x400);
        W32(el + Poe2.UiElement.Flags, visible ? 1 << Poe2.UiElement.FlagVisibleBit : 0x10);
        WF(el + Poe2.MapUiElement.DefaultShift + 4, defaultShiftY);
        WF(el + Poe2.MapUiElement.Shift, 12.5f); WF(el + Poe2.MapUiElement.Shift + 4, -7f);
        WF(el + Poe2.MapUiElement.Zoom, 0.75f);
        var fast = _live.TryReadMapElement(el, out var v1, out var x1, out var y1, out var z1);
        var slow = _live.TryReadMapElementSlow(el, out var v2, out var x2, out var y2, out var z2);
        Assert.Equal((slow, v2, x2, y2, z2), (fast, v1, x1, y1, z1));
    }

    [Fact]
    public void Batched_child_enqueue_matches_per_child_reads()
    {
        var kids = new List<long>();
        for (var i = 0; i < 300; i++) kids.Add(i % 17 == 0 ? 0x30 : 0x100000 + i * 0x40);   // some implausible
        var first = Alloc(kids.Count * 8);
        for (var i = 0; i < kids.Count; i++) W64(first + i * 8, kids[i]);
        var q = new Queue<nint>();
        _live.EnqueueChildren(q, first, kids.Count);
        Assert.Equal(kids.Where(k => k >= 0x10000).Select(k => (nint)k), q);
    }
}
