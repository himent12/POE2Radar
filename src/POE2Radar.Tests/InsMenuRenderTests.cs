using POE2Radar.Core.Game;
using POE2Radar.Overlay;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Draw;
using Xunit;

namespace POE2Radar.Tests;

/// <summary>Headless render of the INSERT menu: every tab draws without throwing, registers its click
/// rects, and (when POE2RADAR_PREVIEW_DIR is set) writes a PNG per tab for eyeballing.</summary>
public sealed class InsMenuRenderTests
{
    private static RenderContext Ctx(int tab) => new(
        InGame: true, Active: true, WindowWidth: 1280, WindowHeight: 800,
        PlayerGrid: default, PlayerWorld: null, Map: default,
        Entities: Array.Empty<Poe2Live.EntityDot>(), Landmarks: Array.Empty<Poe2Live.Landmark>(),
        AreaHash: 1, Terrain: null, ScaleMul: 1f, OffsetX: 0f, OffsetY: 0f,
        HpPct: 72f, ManaPct: 41f, EsPct: 88f, FlaskNote: "life@68%",
        AreaCode: "G2_5", CharLevel: 47, CameraMatrix: null,
        HideJunk: false, ShowPath: true, UseCuratedLandmarks: true,
        ShowMonsters: true, ShowTerrain: true, ShowPlayerBlip: true,
        HpBarNormal: true, HpBarMagic: true, HpBarRare: true, HpBarUnique: true,
        SelectedPaths: Array.Empty<SelectedPath>(), Legend: Array.Empty<LegendEntry>(),
        NavMenuExpanded: false, NavMenuCorner: "TopLeft",
        Styles: new RadarStyles(), HpBars: new HpBarSettings(), HpBarTargets: null,
        TerrainStyle: new TerrainSettings(),
        AutoFlask: true, BotEnabled: true, BotNote: "armed",
        CombatAssist: true, CombatNote: "fired (fighting 3)",
        QuestFollow: false, QuestFollowNote: "paused (clear)",
        PathMove: true, PathMoveNote: "W",
        MapClear: true, MapClearNote: "→ unexplored (112,88)",
        InsMenu: new InsMenuData(
            Tab: tab, CombatRange: 35, CombatEngageRange: 20, CombatFleeHpPct: 35, CombatFleeRecoverPct: 60,
            CombatFleeDistance: 25, CombatStallMs: 0, MapClearStampRadius: 16, MapClearAggroRange: 80,
            MapClearStuckMs: 8000, MoveMethod: "WASD", MoveArriveRadius: 3, LifeThresholdPct: 65,
            ManaThresholdPct: 30, SkillCount: 4, Fps: 144, WorldMs: 3.2f, RenderMs: 1.1f,
            CharName: "Exile", VisitedCells: 18342,
            Skills: new List<CombatSkill>
            {
                new() { Key = 0x51, CooldownMs = 400 },
                new() { Key = 0x57, CooldownMs = 1200, Range = 20, MinTargets = 3 },
                new() { Key = 0x45, CooldownMs = 8000, RareOnly = true },
                new() { Key = 0x52, CooldownMs = 4000, HpBelowPct = 50, Enabled = false },
            },
            TargetMode: "Rarity", RotationMode: "RoundRobin", KeepDistance: 0, MoveCooldownMs: 80, HostilesNear: 3));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Every_tab_renders_and_registers_click_rects(int tab)
    {
        using var win = OverlayWindow.CreateHeadless(1280, 800);
        using var r = new OverlayRenderer(win);
        r.RenderInsMenuPreview(Ctx(tab), new Color4(0.12f, 0.10f, 0.09f, 1f));

        var actions = r.LegendRowRects.Select(x => x.Action).ToList();
        Assert.Contains("ins:panel", actions);
        Assert.Contains("ins:close", actions);
        Assert.Contains("ins:tab:0", actions);
        Assert.Contains("ins:tab:4", actions);
        switch (tab)
        {
            case 0: Assert.Contains("ins:toggle:bot", actions); Assert.Contains("ins:toggle:flask", actions); Assert.Contains("ins:flag:autoRespawn", actions); break;
            case 1: Assert.Contains("ins:slider:combatRange", actions); Assert.Contains("ins:adj:combatRange:1", actions); Assert.Contains("ins:set:combatTargetMode:Rarity", actions); break;
            case 2: Assert.Contains("ins:skill:add", actions); Assert.Contains("ins:skill:adj:0:cd:50", actions); Assert.Contains("ins:skill:flip:1:rareOnly", actions); Assert.Contains("ins:skill:del:2", actions); Assert.Contains("ins:skill:adj:0:repeat:1", actions); Assert.Contains("ins:skill:flip:0:dodgeAfter", actions); break;
            case 3: Assert.Contains("ins:slider:mapClearAggroRange", actions); break;
            case 4: Assert.Contains("ins:set:moveMethod:Click", actions); Assert.Contains("ins:slider:moveLookAhead", actions); Assert.Contains("ins:flag:moveRunEnabled", actions); break;
        }

        var dir = Environment.GetEnvironmentVariable("POE2RADAR_PREVIEW_DIR");
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"ins-menu-tab{tab}.png"), win.SnapshotPng());
        }
    }
}
