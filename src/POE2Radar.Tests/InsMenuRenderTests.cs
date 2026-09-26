using POE2Radar.Core.Game;
using POE2Radar.Overlay;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Draw;
using POE2Radar.Overlay.Input;
using Xunit;

namespace POE2Radar.Tests;

/// <summary>Headless render of the INSERT menu: every tab draws without throwing, registers its click
/// rects, and (when POE2RADAR_PREVIEW_DIR is set) writes a PNG per tab for eyeballing.</summary>
public sealed class InsMenuRenderTests
{
    internal static RenderContext Ctx(int tab) => new(
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
        AutoFlask: true,
        Status: Chips,
        InsMenu: new InsMenuData(
            Tab: tab, Settings: SampleSettings(), Fps: 144, WorldMs: 3.2f, RenderMs: 1.1f,
            CharName: "Exile", Version: "1.0.0", Status: Chips, BuffKeeperArmed: true,
            Buffs: new Poe2Live.BuffInfo[]
            {
                new("arcane_surge", 3.4f, 8f, 1), new("blood_boil", float.PositiveInfinity, 0f, 5),
                new("flask_effect_life", 1.2f, 3f, 1),
            },
            BuffNotes: new[] { "active 3.4s", "missing → recast", "OFF (rule disabled)" },
            ChatNote: "sent",
            Trade: new TradeMenuInfo("tailing Client.txt", true,
                new[] { ("Today", "3 sold · 1 bought · +2.4 div"), ("Last 7 days", "19 sold · 4 bought · +11 div"), ("All time", "40 sold · 9 bought · +23 div") },
                new[] { "+ 1 div  Heavy Belt  (BuyerOne)", "− 45 ex  Sapphire Ring  (SellerTwo)" })),
        Trades: SampleTrades);

    internal static readonly TradeCard[] SampleTrades =
    {
        new(1, true, "BuyerOne", "Doomsday, Heavy Belt", "3 div", "~540 ex", "\"sale\" · 3,5", "InArea", true, "42s", 1, ""),
        new(2, true, "Slowpoke_Exile", "Sapphire Ring", "45 ex", "", "\"~b/o\" · 1,1", "Invited", false, "2m", 0, "can you do 40?"),
        new(3, false, "SellerThree", "2 Greater Jeweller's Orb", "12 ex", "", "", "New", false, "5m", 0, ""),
        new(4, true, "Done_Deal", "Ruby Ring", "1 div", "~180 ex", "", "Completed", false, "9m", 0, ""),
    };

    internal static RadarSettings SampleSettings()
    {
        var s = new RadarSettings();
        s.BuffKeeper.Rules = new()
        {
            new() { Enabled = true, Name = "Arcane Surge", BuffName = "arcane_surge", Key = 0x54, Trigger = BuffKeeper.TriggerExpiring },
            new() { Enabled = true, Name = "Herald", BuffName = "herald_of_ice", Key = 0x52, Trigger = BuffKeeper.TriggerMissing },
            new() { Enabled = false, Name = "War cry", Key = 0x45, Trigger = BuffKeeper.TriggerInterval },
        };
        return s;
    }

    internal static readonly StatusChip[] Chips =
    {
        new("Flask", true, "life@68%", "F8"),
        new("Buffs", true, "armed", "F4"),
    };

    public static IEnumerable<object[]> Tabs() => Enumerable.Range(0, OverlayRenderer.InsTabCount).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(Tabs))]
    public void Every_tab_renders_and_registers_click_rects(int tab)
    {
        using var win = OverlayWindow.CreateHeadless(1280, 800);
        using var r = new OverlayRenderer(win);
        r.RenderInsMenuPreview(Ctx(tab), new Color4(0.12f, 0.10f, 0.09f, 1f));

        var actions = r.LegendRowRects.Select(x => x.Action).ToList();
        Assert.Contains("ins:panel", actions);
        Assert.Contains("ins:close", actions);
        for (var i = 0; i < OverlayRenderer.InsTabCount; i++) Assert.Contains("ins:tab:" + i, actions);
        switch (tab)
        {
            case 0: Assert.Contains("ins:toggle:flask", actions); Assert.Contains("ins:open:dashboard", actions); break;
            case 1: Assert.Contains("ins:slider:lifeThresholdPct", actions); Assert.Contains("ins:key:lifeKey:1", actions); Assert.Contains("ins:set:lifeFlaskMode:Either", actions); break;
            case 2: Assert.Contains("ins:toggle:buffs", actions); Assert.Contains("ins:buff:flip:0", actions); Assert.Contains("ins:buff:key:1:1", actions);
                Assert.Contains("ins:buff:trig:2", actions); Assert.Contains("ins:buff:add:blood_boil", actions); Assert.Contains("ins:cmd:flip:0", actions); break;
            case 3: Assert.Contains("ins:flag:tradeEnabled", actions); Assert.Contains("ins:slider:tradePanelY", actions); break;
            case 4: Assert.Contains("ins:flag:showMonsters", actions); Assert.Contains("ins:slider:fpsCap", actions);
                Assert.Contains("ins:flag:reduceMotion", actions); break;
        }
        // Every slider/adjust action resolves to a registered spec (a typo would silently do nothing).
        foreach (var a in actions.Where(a => a.StartsWith("ins:slider:") || a.StartsWith("ins:adj:")))
            Assert.True(InsSliderSpec.All.ContainsKey(a.Split(':')[2]), a);

        var dir = Environment.GetEnvironmentVariable("POE2RADAR_PREVIEW_DIR");
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"ins-menu-tab{tab}.png"), win.SnapshotPng());
        }
    }
}
