using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Web;

namespace POE2Radar.Overlay;

/// <summary>
/// <c>--demo</c>: serve the dashboard with sample state and NO game attach — for working on the web UI (and
/// for trying the settings pages before launching PoE2). Settings are the real config next to the exe; the
/// rule/landmark stores use a throwaway directory so demo edits never touch them. Sends no input.
/// </summary>
internal static class DemoDashboard
{
    public static int Run()
    {
        var settings = RadarSettings.Load();
        var dir = Path.Combine(Path.GetTempPath(), "poe2radar-demo");
        Directory.CreateDirectory(dir);
        var hidden = new HiddenEntities(Path.Combine(dir, "hidden_entities.json"));
        var rules = new DisplayRules(Path.Combine(dir, "display_rules.json"));
        if (rules.Count == 0) rules.Replace(DisplayRules.BuildDefault(settings.Styles, settings.ShowMonsters, Array.Empty<WatchedEntry>()));
        var landmarks = new LandmarkStore(Path.Combine(dir, "landmarks.json"));
        var selected = new List<(string Id, int Slot)>();

        var state = new RadarState(true, 0xC0FFEE, 68, false, 1f, new System.Numerics.Vector2(400, 300),
            Array.Empty<Poe2Live.EntityDot>(), Array.Empty<Poe2Live.Landmark>(), 74f, 41f, 88f, settings.AutoFlaskEnabled, "armed",
            "MapHideoutFelled", "", 71, 2.8f, 0.9f, Array.Empty<MonolithMarker>(), 143f,
            BuffKeeper: settings.BuffKeeper.Enabled, BuffNote: "armed", TradeOpen: 2, ChatNote: "sent");

        var start = DateTime.UtcNow;
        object Trade() => new
        {
            enabled = settings.Trade.Enabled,
            log = new { path = "~/.local/share/Steam/steamapps/common/Path of Exile 2/logs/Client.txt", status = "tailing", lines = 1284 },
            afk = false,
            sessions = new object[]
            {
                new { id = 1, direction = "Incoming", player = "BuyerOne", guild = (string?)null, state = "InArea", inArea = true,
                      item = "Doomsday, Heavy Belt", amount = 3.0, currency = "Divine Orb", league = "Standard", stashTab = "sale",
                      left = 3, top = 5, note = "", created = start.AddSeconds(-42), whispers = Array.Empty<string>(), repeats = 1 },
                new { id = 2, direction = "Outgoing", player = "SellerThree", guild = (string?)"GG", state = "New", inArea = false,
                      item = "2 Greater Jeweller's Orb", amount = 12.0, currency = "Exalted Orb", league = "Standard", stashTab = (string?)null,
                      left = (int?)null, top = (int?)null, note = "", created = start.AddMinutes(-5), whispers = new[] { "sure, come over" }, repeats = 0 },
            },
            summary = new
            {
                today = new { sold = 3, bought = 1, earnedEx = 610.0, spentEx = 45.0, profitEx = 565.0, unconverted = 0, profitText = "3.1 div" },
                week = new { sold = 19, bought = 4, earnedEx = 2300.0, spentEx = 260.0, profitEx = 2040.0, unconverted = 1, profitText = "11.3 div" },
                all = new { sold = 40, bought = 9, earnedEx = 4700.0, spentEx = 560.0, profitEx = 4140.0, unconverted = 2, profitText = "23 div" },
            },
            history = Enumerable.Range(0, 14).Select(i => new
            {
                id = "h" + i, utc = start.AddHours(-i * 5.5), direction = i % 3 == 2 ? "Bought" : "Sold",
                player = new[] { "BuyerOne", "Zed_Ex", "MapLord", "Kalguuran" }[i % 4],
                item = new[] { "Heavy Belt", "Sapphire Ring", "Waystone (Tier 15)", "Greater Orb of Augmentation" }[i % 4],
                amount = 1.0 + i % 5, currency = i % 2 == 0 ? "Divine Orb" : "Exalted Orb", league = "Standard",
                valueEx = (double?)((1.0 + i % 5) * (i % 2 == 0 ? 180 : 1)),
            }),
            historyCount = 14,
            historyError = (string?)null,
        };
        object Buffs() => new
        {
            readable = true, armed = settings.BuffKeeper.Enabled, note = "armed",
            buffs = new object[]
            {
                new { name = "arcane_surge", timeLeft = (float?)3.4f, total = (float?)8f, charges = 1 },
                new { name = "blood_boil", timeLeft = (float?)null, total = (float?)null, charges = 5 },
                new { name = "flask_effect_life", timeLeft = (float?)1.2f, total = (float?)3f, charges = 1 },
            },
            notes = settings.BuffKeeper.Rules.Select((r, i) => r.Enabled ? (i % 2 == 0 ? "active 3.4s" : "missing → recast") : "rule disabled").ToArray(),
            chat = "sent",
        };

        using var api = new ApiServer(() => state, settings, () => selected, id => { }, () => selected.Clear(), hidden, rules, landmarks,
            () => new[] { "Metadata/Terrain/Doodads/Boss/ArenaEntrance" }, () => new[] { "MonsterFireAura", "AbyssLightless" },
            () => new { loaded = true, league = "Standard", count = 3120, status = "demo", exPerDivine = 180.0, lastFetchUtc = start },
            () => new { located = false, note = "demo — atlas is read live in game" }, _ => { }, _ => { },
            () => new { current = UpdateChecker.Current, latest = UpdateChecker.Current, updateAvailable = false, url = UpdateChecker.ReleasesPage },
            settings.ApiPort)
        {
            BuffsProvider = Buffs,
            TradeProvider = Trade,
            TradeCommand = _ => new { ok = true },
        };
        api.Start();
        Console.WriteLine($"Demo dashboard on http://localhost:{settings.ApiPort}/ — Ctrl+C to stop.");
        var stop = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
        stop.Wait();
        return 0;
    }
}
