using POE2Radar.Core.Game;
using POE2Radar.Overlay.Navigation;
using NumVec2 = System.Numerics.Vector2;
using Xunit;

namespace POE2Radar.Tests;

public sealed class MapEventsTests
{
    private static Poe2Live.EntityDot Ent(uint id, string meta, Poe2Live.EntityCategory cat, float x, float y,
        bool opened = false, bool complete = false, int hp = 10, int hpMax = 10, byte reaction = 0)
        => new(id, 0, new NumVec2(x, y), default, cat, meta, hp, hpMax, false, reaction, Poe2Live.Rarity.Normal, opened, complete);

    [Fact]
    public void Classifies_known_events()
    {
        Assert.Equal(MapEvents.Kind.Strongbox, MapEvents.Classify(Ent(1, "Metadata/Chests/StrongBoxes/Arcanist", Poe2Live.EntityCategory.Chest, 0, 0)));
        Assert.Equal(MapEvents.Kind.Essence, MapEvents.Classify(Ent(2, "Metadata/Terrain/Leagues/Essence/EssenceMonolith", Poe2Live.EntityCategory.Object, 0, 0)));
        Assert.Equal(MapEvents.Kind.Shrine, MapEvents.Classify(Ent(3, "Metadata/Shrines/Shrine", Poe2Live.EntityCategory.Other, 0, 0)));
        Assert.Equal(MapEvents.Kind.Breach, MapEvents.Classify(Ent(4, "Metadata/MiscellaneousObjects/Breach/BreachObject", Poe2Live.EntityCategory.Other, 0, 0)));
        Assert.Equal(MapEvents.Kind.Chest, MapEvents.Classify(Ent(5, "Metadata/Chests/Basic", Poe2Live.EntityCategory.Chest, 0, 0)));
        // A monster with "Essence" in its path is a monster, not a crystal.
        Assert.Equal(MapEvents.Kind.None, MapEvents.Classify(Ent(6, "Metadata/Monsters/EssenceRare", Poe2Live.EntityCategory.Monster, 0, 0)));
    }

    [Fact]
    public void Pending_respects_toggles_completion_and_blacklist()
    {
        var ents = new[]
        {
            Ent(1, "Metadata/Chests/StrongBoxes/Arcanist", Poe2Live.EntityCategory.Chest, 5, 0),
            Ent(2, "Metadata/Chests/StrongBoxes/Arcanist", Poe2Live.EntityCategory.Chest, 6, 0, opened: true),
            Ent(3, "Metadata/Shrines/Shrine", Poe2Live.EntityCategory.Other, 7, 0, complete: true),
            Ent(4, "Metadata/Chests/Basic", Poe2Live.EntityCategory.Chest, 8, 0),
            Ent(5, "Metadata/Shrines/Shrine", Poe2Live.EntityCategory.Other, 9, 0),
        };
        var p = MapEvents.Pending(ents, new MapEvents.Options(Chests: false), blacklist: new HashSet<uint> { 5 });
        Assert.Single(p);
        Assert.Equal(1u, p[0].Id);
        p = MapEvents.Pending(ents, new MapEvents.Options(Chests: true));
        Assert.Equal(3, p.Count);
    }

    [Fact]
    public void Stalled_monster_becomes_click_target()
    {
        var ents = new[] { Ent(9, "Metadata/Monsters/Rare", Poe2Live.EntityCategory.Monster, 3, 3) };
        var p = MapEvents.Pending(ents, new MapEvents.Options(), stalledMonsters: new HashSet<uint> { 9 });
        Assert.Single(p);
        Assert.Equal(MapEvents.Kind.Stalled, p[0].Kind);
        Assert.Empty(MapEvents.Pending(ents, new MapEvents.Options(ClickStalled: false), stalledMonsters: new HashSet<uint> { 9 }));
    }

    [Fact]
    public void Imprisoned_monsters_are_the_ones_beside_an_unopened_crystal()
    {
        var ents = new[]
        {
            Ent(1, "Metadata/Terrain/Leagues/Essence/EssenceMonolith", Poe2Live.EntityCategory.Object, 10, 10),
            Ent(2, "Metadata/Monsters/Rare", Poe2Live.EntityCategory.Monster, 12, 11),
            Ent(3, "Metadata/Monsters/Rare", Poe2Live.EntityCategory.Monster, 30, 30),
            Ent(4, "Metadata/Terrain/Leagues/Essence/EssenceMonolith", Poe2Live.EntityCategory.Object, 30, 30, complete: true),
        };
        var imp = MapEvents.ImprisonedMonsters(ents);
        Assert.Contains(2u, imp);
        Assert.DoesNotContain(3u, imp); // its crystal is already opened
    }

    [Fact]
    public void Clear_picks_event_after_hostiles_before_frontier()
    {
        var walk = new byte[32 * 32]; Array.Fill(walk, (byte)1);
        var visited = new HashSet<long>();
        MapClear.StampVisited(visited, walk, 32, 32, new NumVec2(2, 2), 2);
        var events = new[] { new MapEvents.Event(7, MapEvents.Kind.Strongbox, new NumVec2(10, 10), default, "Strongbox") };
        Assert.Equal("e:7", MapClear.PickTarget("G1_2", new NumVec2(2, 2), walk, 32, 32, visited, [], null, events: events));
        var mobs = new[] { new MapClear.MobHint("e:1", new NumVec2(4, 2), false) };
        Assert.Equal("e:1", MapClear.PickTarget("G1_2", new NumVec2(2, 2), walk, 32, 32, visited, mobs, null, events: events));
        // Out of event range → sweep instead.
        var id = MapClear.PickTarget("G1_2", new NumVec2(2, 2), walk, 32, 32, visited, [], null, events: events, maxEventDistance: 5f);
        Assert.NotEqual("e:7", id);
    }
}
