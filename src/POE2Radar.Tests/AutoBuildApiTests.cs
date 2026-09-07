using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Web;
using Xunit;

namespace POE2Radar.Tests;

public sealed class AutoBuildApiTests
{
    private sealed class Fixture : IDisposable
    {
        public CharacterLoadout Character = AutoBuildCatalogTests.Character("SkillGemSpark") with { SkillBar = new(true,
            new[] { new SkillBarSlot(4, "SparkPlayer", "Spark", 1), new SkillBarSlot(1, "", "Move", 2) }) };
        public GameInputSnapshot Input = GameInputConfig.Parse(GameInputConfigTests.Config().Replace("use_bound_skill1=0", "use_bound_skill1=1"));
        public RadarState State = RadarState.Empty;
        public RadarSettings Settings = new() { MoveMethod = "WASD", LifeKey = 51 };
        public bool FailSave;
        public int Saves;
        public readonly ApiServer Server;
        public readonly HttpClient Client;
        public Fixture()
        {
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
            Server = new(() => State, Settings, () => Array.Empty<(string, int)>(), _ => { }, () => { },
                null!, null!, null!, () => Array.Empty<string>(), () => Array.Empty<string>(), port: port)
            {
                LoadoutProvider = () => Character, InputProvider = () => Input,
                BuildStore = _ => { Saves++; return FailSave ? "test disk full" : null; }
            };
            Server.Start();
            Client = new() { BaseAddress = new Uri($"http://localhost:{port}"), Timeout = TimeSpan.FromSeconds(5) };
        }
        public async Task<string> Scan()
        {
            using var response = await Client.GetAsync("/api/build"); response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(json.RootElement.GetProperty("proposal").GetProperty("input").GetProperty("complete").GetBoolean());
            return json.RootElement.GetProperty("token").GetString()!;
        }
        public Task<HttpResponseMessage> Apply(string token, bool controls = true) => Client.PostAsJsonAsync("/api/build", new
        {
            token, importControls = controls,
            bindings = new[] { new { metadata = Character.Skills[0].Metadata, key = 81, modifiers = 0, slot = 4 } }
        });
        public void Dispose() { Client.Dispose(); Server.Dispose(); }
    }

    [Fact]
    public async Task Apply_imports_real_controls_persists_profile_does_not_arm_and_undo_restores_everything()
    {
        using var f = new Fixture();
        var token = await f.Scan();
        using var applied = await f.Apply(token);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal("Click", f.Settings.MoveMethod); Assert.Equal(49, f.Settings.LifeKey);
        Assert.Equal(4, Assert.Single(f.Settings.CombatSkills).SourceSlot);
        Assert.True(f.Settings.BuildProfiles.ContainsKey("League:Hero"));
        Assert.False(f.Settings.CombatAssistEnabled); Assert.False(f.Settings.BotEnabled);
        using var replay = await f.Apply(token); Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        using var undo = await f.Client.PostAsJsonAsync("/api/build", new { action = "undo" });
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        Assert.Equal("WASD", f.Settings.MoveMethod); Assert.Equal(51, f.Settings.LifeKey);
        Assert.Empty(f.Settings.BuildProfiles); Assert.Empty(f.Settings.CombatSkills);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 4)]
    public async Task Mouse_import_uses_the_actual_move_only_assignment(int moveSlot, int expectedKey)
    {
        using var f = new Fixture();
        f.Input = GameInputConfig.Parse(GameInputConfigTests.Config().Replace("use_bound_skill1=0", "use_bound_skill1=1")
            .Replace("use_bound_skill2=0", "use_bound_skill2=4"));
        f.Character = f.Character with { SkillBar = new(true, new[] {
            new SkillBarSlot(4, "SparkPlayer", "Spark", 1), new SkillBarSlot(moveSlot, "", "Move", 2) }.Where(s => s.Slot > 0).ToArray()) };
        var token = await f.Scan();
        using var result = await f.Apply(token);
        Assert.Equal(moveSlot == 0 ? HttpStatusCode.BadRequest : HttpStatusCode.OK, result.StatusCode);
        if (moveSlot > 0) Assert.Equal(expectedKey, f.Settings.MoveClickKey);
        else Assert.Equal(0, f.Saves);
    }

    [Theory]
    [InlineData("keys")]
    [InlineData("equipment")]
    [InlineData("character")]
    [InlineData("skillbar")]
    [InlineData("settings")]
    [InlineData("armed")]
    public async Task Stale_preview_or_armed_input_refuses_mutation(string change)
    {
        using var f = new Fixture(); var token = await f.Scan();
        switch (change)
        {
            case "keys": f.Input = GameInputConfig.Parse(GameInputConfigTests.Config(q: 69)); break;
            case "equipment": f.Character = f.Character with { Equipment = Array.Empty<LoadoutItem>() }; break;
            case "character": f.Character = f.Character with { Character = "Other" }; break;
            case "skillbar": f.Character = f.Character with { SkillBar = new(true, new[] { new SkillBarSlot(4, "SparkPlayer", "Spark", 1) }) }; break;
            case "settings": f.Settings.CombatRange = 23; break;
            case "armed": f.State = f.State with { AutoFlask = true }; break;
        }
        using var response = await f.Apply(token);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Equal(0, f.Saves);
        Assert.Empty(f.Settings.CombatSkills);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mouse_import_without_a_readable_bar_does_not_guess_left_click(bool unreadable)
    {
        using var f = new Fixture();
        f.Character = f.Character with { SkillBar = unreadable ? new(false, Array.Empty<SkillBarSlot>(), "Unreadable") : null };
        var token = await f.Scan();
        using var refused = await f.Apply(token);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(0, f.Saves);
        Assert.Equal("WASD", f.Settings.MoveMethod);
        using var manual = await f.Apply(token, controls: false);
        Assert.Equal(HttpStatusCode.OK, manual.StatusCode);
        Assert.False(Assert.Single(f.Settings.CombatSkills).SourceLiveBinding);
    }

    [Fact]
    public async Task Save_failure_rolls_back_rotation_controls_and_profile()
    {
        using var f = new Fixture(); var token = await f.Scan(); f.FailSave = true;
        using var response = await f.Apply(token);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("WASD", f.Settings.MoveMethod); Assert.Equal(51, f.Settings.LifeKey);
        Assert.Empty(f.Settings.CombatSkills); Assert.Empty(f.Settings.BuildProfiles);
    }

    [Fact]
    public async Task Malformed_modifier_returns_400_and_server_keeps_serving()
    {
        using var f = new Fixture(); var token = await f.Scan();
        using var response = await f.Client.PostAsJsonAsync("/api/build", new
        { token, bindings = new[] { new { metadata = f.Character.Skills[0].Metadata, key = 81, modifiers = "Ctrl" } } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEmpty(await f.Scan()); Assert.Equal(0, f.Saves);
    }

    [Fact]
    public async Task Undo_refuses_to_overwrite_later_manual_edits()
    {
        using var f = new Fixture(); using var applied = await f.Apply(await f.Scan());
        applied.EnsureSuccessStatusCode(); f.Settings.CombatSkills[0].CooldownMs = 1234;
        using var undo = await f.Client.PostAsJsonAsync("/api/build", new { action = "undo" });
        Assert.Equal(HttpStatusCode.Conflict, undo.StatusCode);
        Assert.Equal(1234, f.Settings.CombatSkills[0].CooldownMs);
    }
}
