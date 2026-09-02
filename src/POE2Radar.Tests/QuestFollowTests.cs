using POE2Radar.Core.Game;
using POE2Radar.Overlay.Navigation;
using NumVec2 = System.Numerics.Vector2;
using Xunit;

namespace POE2Radar.Tests;

public sealed class QuestFollowTests
{
    private static readonly DateTime T0 = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
    private const int VkUse = 0x01;

    private static QuestFollow.LandmarkHint Tile(
        string id, string name, string? curated, string path)
        => new(id, name, curated, path);

    private static QuestFollow.EntityHint Ent(
        string id, string name, string meta,
        bool poi = false, bool unique = false,
        Poe2Live.EntityCategory cat = Poe2Live.EntityCategory.Monster)
        => new(id, name, meta, poi, unique, cat);

    private static readonly QuestFollow.LandmarkHint GrelwoodExit = Tile(
        "t:Metadata/Terrain/Woods/AreaTransitions/Clearfell_OldForest_Transition_01.tdtx:1-y:0@40,10",
        "Clearfell_OldForest_Transition_01",
        "The Grelwood",
        "Metadata/Terrain/Woods/AreaTransitions/Clearfell_OldForest_Transition_01.tdtx:1-y:0");

    private static readonly QuestFollow.LandmarkHint MudBurrow = Tile(
        "t:Metadata/Terrain/Woods/AreaTransitions/FallenTreeWoods_in.tdtx:2-y:1@5,5",
        "FallenTreeWoods_in",
        "Mud Burrow",
        "Metadata/Terrain/Woods/AreaTransitions/FallenTreeWoods_in.tdtx:2-y:1");

    private static readonly QuestFollow.LandmarkHint Beira = Tile(
        "t:Metadata/Terrain/Woods/Slash/HagWitchArena_01.tdtx:5-y:0@20,20",
        "HagWitchArena_01",
        "Beira of the Rotten (10% Cold Res)",
        "Metadata/Terrain/Woods/Slash/HagWitchArena_01.tdtx:5-y:0");

    private static QuestFollow.UseSnapshot UseBase(
        bool armed = true,
        bool focused = true,
        bool inGame = true,
        float playerX = 0,
        float playerY = 0,
        bool hasTarget = true,
        float targetX = 2,
        float targetY = 0,
        float arrive = 6,
        DateTime? now = null,
        DateTime? lastFire = null,
        int cooldownMs = 400,
        int useKey = VkUse)
        => new(
            Armed: armed,
            Focused: focused,
            InGame: inGame,
            PlayerGrid: new NumVec2(playerX, playerY),
            HasTarget: hasTarget,
            TargetGrid: new NumVec2(targetX, targetY),
            ArriveRadius: arrive,
            NowUtc: now ?? T0,
            LastFireUtc: lastFire ?? DateTime.MinValue,
            CooldownMs: cooldownMs,
            UseKey: useKey);

    [Fact]
    public void ExitToken_selects_matching_transition_landmark()
    {
        var id = QuestFollow.PickTarget(
            "G1_2",
            "[Optional] Mud Burrow boss has Lvl 2 Skill\n\nExit > The Grelwood",
            [MudBurrow, Beira, GrelwoodExit],
            []);
        Assert.Equal(GrelwoodExit.Id, id);
    }

    [Fact]
    public void ExitToken_beats_optional_side_area_of_the_same_kind()
    {
        var id = QuestFollow.PickTarget(
            "G1_2",
            "[Optional] Mud Burrow boss has Lvl 2 Skill + Lvl 1 Support\nExit > The Grelwood",
            [MudBurrow, GrelwoodExit],
            []);
        Assert.Equal(GrelwoodExit.Id, id);
    }

    [Fact]
    public void Hillock_unique_when_exit_is_town()
    {
        var hillock = Ent("e:42", "Hillock", "Metadata/Monsters/Hillock", poi: true, unique: true);
        var id = QuestFollow.PickTarget(
            "G1_1",
            "Kill Hillock and Exit > Town\n\nThank you for using Path Of Levelling 2!",
            [],
            [hillock]);
        Assert.Equal("e:42", id);
    }

    [Fact]
    public void Numbered_note_matches_transition_by_curated_name()
    {
        var grim = Tile(
            "t:Metadata/Terrain/Woods/AreaTransitions/OldForestToGrimTangle_02_metatile.tdtx:6-y:2@1,1",
            "OldForestToGrimTangle_02_metatile",
            "The Grim Tangle",
            "Metadata/Terrain/Woods/AreaTransitions/OldForestToGrimTangle_02_metatile.tdtx:6-y:2");
        var hut = Tile(
            "t:Metadata/Terrain/Woods/OldForestWoods/Feature/BurnTheWitch_01.tdtx:0-y:4@2,2",
            "BurnTheWitch_01",
            "Areagne's Hut (Support Gem Level 1 and Flasks)",
            "Metadata/Terrain/Woods/OldForestWoods/Feature/BurnTheWitch_01.tdtx:0-y:4");
        var id = QuestFollow.PickTarget(
            "G1_4",
            "1) The Grim Tangle - enter and get the waypoint\n2) Tree Of Souls + Waypoint\n4) Areagne's Hut - optional potions",
            [hut, grim],
            []);
        Assert.Equal(grim.Id, id);
    }

    [Fact]
    public void Cemetery_typo_in_notes_matches_curated_name()
    {
        var cemetery = Tile(
            "t:Metadata/Terrain/Woods/GrimTangle/areatransitions/GrimtangleTransitionUp_02.tdtx:0-y:1@3,3",
            "GrimtangleTransitionUp_02",
            "Cemetery of the Eternals",
            "Metadata/Terrain/Woods/GrimTangle/areatransitions/GrimtangleTransitionUp_02.tdtx:0-y:1");
        var id = QuestFollow.PickTarget(
            "G1_6",
            "Go roughly up and left\n\nExit > Cemetary Of The Eternals",
            [cemetery],
            []);
        Assert.Equal(cemetery.Id, id);
    }

    [Fact]
    public void Prefers_transition_when_notes_name_both_a_boss_and_an_exit()
    {
        var id = QuestFollow.PickTarget(
            "G1_2",
            "Beira and exit is on your side\nExit > The Grelwood",
            [Beira, GrelwoodExit],
            []);
        Assert.Equal(GrelwoodExit.Id, id);
    }

    [Fact]
    public void Fallback_transition_when_notes_do_not_match()
    {
        var id = QuestFollow.PickTarget(
            "G1_2",
            "wall-follow, good luck",
            [Beira, GrelwoodExit],
            []);
        Assert.Equal(GrelwoodExit.Id, id);
    }

    [Fact]
    public void Fallback_waypoint_when_no_transition()
    {
        var wp = Tile(
            "t:Metadata/MiscellaneousObjects/Waypoint@8,8",
            "Waypoint",
            "Tree Of Souls + Waypoint",
            "Metadata/MiscellaneousObjects/Waypoint");
        var id = QuestFollow.PickTarget("G1_4", "", [wp], []);
        Assert.Equal(wp.Id, id);
    }

    [Fact]
    public void Fallback_unique_boss_when_no_transition_or_waypoint()
    {
        var boss = Ent("e:7", "King In The Mists", "Metadata/Monsters/King", unique: true);
        var id = QuestFollow.PickTarget("G1_12", "", [], [boss]);
        Assert.Equal("e:7", id);
    }

    [Fact]
    public void Town_area_returns_none()
    {
        var id = QuestFollow.PickTarget(
            "G1_town",
            "Exit > The Riverbank",
            [GrelwoodExit],
            []);
        Assert.Null(id);
    }

    [Fact]
    public void Empty_world_returns_none()
    {
        Assert.Null(QuestFollow.PickTarget("G1_2", "Exit > The Grelwood", [], []));
    }

    [Fact]
    public void Checkpoint_token_matches_landmark()
    {
        var maus = Tile(
            "t:Metadata/Terrain/Woods/Graveyard/Feature/mausoleum_01.tdtx:0-y:1@9,9",
            "mausoleum_01",
            "Mausoleum of the Praetor",
            "Metadata/Terrain/Woods/Graveyard/Feature/mausoleum_01.tdtx:0-y:1");
        var id = QuestFollow.PickTarget(
            "G1_7",
            "Checkpoint > Mausoleum Of The Praetor",
            [maus],
            []);
        Assert.Equal(maus.Id, id);
    }

    [Fact]
    public void Entity_poi_matches_note_name()
    {
        var wp = Ent(
            "e:99",
            "Waypoint",
            "Metadata/MiscellaneousObjects/Waypoint",
            poi: true,
            cat: Poe2Live.EntityCategory.Object);
        // No landmark, notes mention the waypoint entity by name after the town skip.
        var id = QuestFollow.PickTarget(
            "G1_5",
            "Kill the boss, TP > Town, then Waypoint > The Grim Tangle",
            [],
            [wp]);
        // "The Grim Tangle" does not match the waypoint entity; fallback is the waypoint itself.
        Assert.Equal("e:99", id);
    }

    [Fact]
    public void Arrived_tapsUse()
    {
        var d = QuestFollow.DecideUse(UseBase(playerX: 0, playerY: 0, targetX: 2, targetY: 0, arrive: 6));
        Assert.True(d.ShouldTap);
        Assert.Equal((ushort)VkUse, d.Vk);
        Assert.Equal("use", d.Note);
    }

    [Fact]
    public void FarFromTarget_skipsUse()
    {
        var d = QuestFollow.DecideUse(UseBase(playerX: 0, playerY: 0, targetX: 40, targetY: 0, arrive: 6));
        Assert.False(d.ShouldTap);
        Assert.Equal(0, d.Vk);
        Assert.Equal("armed", d.Note);
    }

    [Fact]
    public void Use_disarmed_skips()
    {
        var d = QuestFollow.DecideUse(UseBase(armed: false));
        Assert.False(d.ShouldTap);
        Assert.Equal("OFF (F3)", d.Note);
    }

    [Fact]
    public void Use_unfocused_skips()
    {
        var d = QuestFollow.DecideUse(UseBase(focused: false));
        Assert.False(d.ShouldTap);
        Assert.Equal("paused (PoE2 not focused)", d.Note);
    }

    [Fact]
    public void Use_notInGame_skips()
    {
        var d = QuestFollow.DecideUse(UseBase(inGame: false));
        Assert.False(d.ShouldTap);
        Assert.Equal("paused (not in game)", d.Note);
    }

    [Fact]
    public void Use_noTarget_skips()
    {
        var d = QuestFollow.DecideUse(UseBase(hasTarget: false));
        Assert.False(d.ShouldTap);
        Assert.Equal("armed (no target)", d.Note);
    }

    [Fact]
    public void Use_cooldown_skips()
    {
        var d = QuestFollow.DecideUse(UseBase(
            now: T0,
            lastFire: T0.AddMilliseconds(-100),
            cooldownMs: 400));
        Assert.False(d.ShouldTap);
        Assert.Equal("armed", d.Note);
    }
}
