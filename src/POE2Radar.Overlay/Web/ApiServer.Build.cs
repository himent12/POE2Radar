using System.Net;
using System.Text.Json;
using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Input;

namespace POE2Radar.Overlay.Web;

public sealed partial class ApiServer
{
    public Func<CharacterLoadout>? LoadoutProvider { get; init; }
    public Func<GameInputSnapshot> InputProvider { get; init; } = GameInputConfig.Read;
    internal Func<RadarSettings, string?> BuildStore { get; init; } = settings => settings.TrySave(out var error) ? null : error;
    private string _buildBaseSettings = "";
    private BuildProposal? _buildPreview;
    private string _buildToken = "";
    private DateTime _buildExpires;
    private BuildSettings? _buildBackup;
    private string _buildApplied = "";
    private sealed record BuildSettings(List<CombatSkill> Skills, float Range, float KeepDistance,
        string MoveMethod, int MoveW, int MoveA, int MoveS, int MoveD, int MoveClick, int MoveRun, int Dodge, int Life, int Mana,
        Dictionary<string, List<CombatSkill>> Profiles);

    private BuildSettings CaptureBuildSettings() => new(
        JsonSerializer.Deserialize<List<CombatSkill>>(JsonSerializer.Serialize(_settings.CombatSkills, Json), Json) ?? new(),
        _settings.CombatRange, _settings.CombatKeepDistance, _settings.MoveMethod,
        _settings.MoveKeyW, _settings.MoveKeyA, _settings.MoveKeyS, _settings.MoveKeyD,
        _settings.MoveClickKey, _settings.MoveRunKey,
        _settings.CombatDodgeKey, _settings.LifeKey, _settings.ManaKey,
        JsonSerializer.Deserialize<Dictionary<string, List<CombatSkill>>>(JsonSerializer.Serialize(_settings.BuildProfiles, Json), Json) ?? new());

    private void RestoreBuildSettings(BuildSettings value)
    {
        _settings.CombatSkills = value.Skills; _settings.CombatRange = value.Range;
        _settings.CombatKeepDistance = value.KeepDistance; _settings.MoveMethod = value.MoveMethod;
        _settings.MoveKeyW = value.MoveW; _settings.MoveKeyA = value.MoveA;
        _settings.MoveKeyS = value.MoveS; _settings.MoveKeyD = value.MoveD;
        _settings.MoveClickKey = value.MoveClick; _settings.MoveRunKey = value.MoveRun;
        _settings.CombatDodgeKey = value.Dodge; _settings.LifeKey = value.Life; _settings.ManaKey = value.Mana;
        _settings.BuildProfiles = value.Profiles;
    }

    private static GameBinding? MoveBinding(GameInputSnapshot input, SkillBarSnapshot bar) => bar.Slots
        .Where(s => s.ActionId == "Move").Select(s => input.Find("use_bound_skill" + s.Slot))
        .FirstOrDefault(b => b is { Usable: true, Modifiers: 0 });

    private void ImportBuildControls(GameInputSnapshot input, CharacterLoadout character)
    {
        int Key(string action, int fallback) => input.Find(action) is { Usable: true, Modifiers: 0 } b ? b.Key : fallback;
        _settings.CombatDodgeKey = Key("use_dodge_roll", _settings.CombatDodgeKey);
        _settings.MoveRunKey = _settings.CombatDodgeKey;
        _settings.LifeKey = Key("use_flask_in_slot1", _settings.LifeKey);
        _settings.ManaKey = Key("use_flask_in_slot2", _settings.ManaKey);
        _settings.MoveMethod = input.Mode == "WASD" ? "WASD" : "Click";
        if (input.Mode == "Mouse") _settings.MoveClickKey = MoveBinding(input, character.SkillBar!)!.Key;
        if (input.Mode == "WASD")
        {
            _settings.MoveKeyW = Key("move_up", _settings.MoveKeyW);
            _settings.MoveKeyA = Key("move_left", _settings.MoveKeyA);
            _settings.MoveKeyS = Key("move_down", _settings.MoveKeyS);
            _settings.MoveKeyD = Key("move_right", _settings.MoveKeyD);
        }
    }

    private static string LoadoutKey(CharacterLoadout c) => JsonSerializer.Serialize(new
    {
        c.Character,
        c.League,
        c.Level,
        Hp = c.Vitals?.HpUnreserved,
        Mana = c.Vitals?.ManaUnreserved,
        Es = c.Vitals?.EsUnreserved,
        Equipment = c.Equipment.OrderBy(x => x.InventoryId).ThenBy(x => x.Metadata),
        Skills = c.Skills.OrderBy(x => x.Metadata),
        c.SkillBar
    }, Json);

    private void HandleBuild(HttpListenerContext ctx)
    {
        void Reply(int status, object value) => Write(ctx, status, JsonSerializer.Serialize(value, Json));
        if (!IsLoopbackHost(ctx.Request)) { Reply(403, new { error = "forbidden host" }); return; }
        if (ctx.Request.HttpMethod == "GET")
        {
            if (LoadoutProvider is null) { Reply(503, new { error = "Character reader unavailable." }); return; }
            var character = LoadoutProvider();
            var identity = character.League + ":" + character.Character;
            var saved = _settings.BuildProfiles?.GetValueOrDefault(identity) ?? new();
            var existing = _settings.CombatSkills.Concat(saved).DistinctBy(s => (s.SourceCharacter, s.SourceMetadata)).ToArray();
            _buildPreview = AutoBuild.Propose(character, existing, InputProvider());
            _buildBaseSettings = JsonSerializer.Serialize(CaptureBuildSettings(), Json);
            _buildToken = Guid.NewGuid().ToString("N");
            _buildExpires = DateTime.UtcNow.AddMinutes(2);
            Reply(200, new { token = _buildToken, proposal = _buildPreview, expiresAt = _buildExpires });
            return;
        }
        if (ctx.Request.HttpMethod != "POST") { Reply(405, new { error = "method not allowed" }); return; }
        if (!string.Equals(ctx.Request.ContentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
        { Reply(415, new { error = "Content-Type must be application/json." }); return; }
        using var body = JsonDocument.Parse(ReadBody(ctx));
        var root = body.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { Reply(400, new { error = "Expected an object." }); return; }
        if (_state() is { CombatAssist: true } or { Bot: true } or { PathMove: true } or { AutoFlask: true }
            or { MapClear: true } or { FarmLoop: true } or { QuestFollow: true })
        { Reply(409, new { error = "Disarm combat, bot, movement and auto-flasks locally before replacing or restoring a build." }); return; }
        if (root.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.String && action.GetString() == "undo")
        {
            if (_buildBackup is null || JsonSerializer.Serialize(CaptureBuildSettings(), Json) != _buildApplied)
            { Reply(409, new { error = "No unchanged generated build to undo. Manual edits will not be overwritten." }); return; }
            var beforeUndo = CaptureBuildSettings();
            RestoreBuildSettings(_buildBackup);
            if (BuildStore(_settings) is { } undoSaveError)
            { RestoreBuildSettings(beforeUndo); Reply(500, new { error = "Could not save restored build: " + undoSaveError }); return; }
            _buildBackup = null;
            Reply(200, new { ok = true });
            return;
        }
        if (!root.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String
            || token.GetString() != _buildToken || _buildPreview is null || DateTime.UtcNow > _buildExpires)
        { Reply(409, new { error = "Preview expired. Scan again." }); return; }
        if (!_buildPreview.Character.Complete || LoadoutProvider is null)
        { Reply(409, new { error = "Character scan is incomplete." }); return; }
        if (JsonSerializer.Serialize(CaptureBuildSettings(), Json) != _buildBaseSettings)
        { Reply(409, new { error = "Settings changed after this preview. Scan again to preserve your edits." }); return; }
        var currentInput = InputProvider();
        if (currentInput.Fingerprint != _buildPreview.Input?.Fingerprint || currentInput.Complete != _buildPreview.Input?.Complete)
        { Reply(409, new { error = "Game key bindings changed. Scan again." }); return; }
        var current = LoadoutProvider();
        if (!current.Complete || LoadoutKey(current) != LoadoutKey(_buildPreview.Character))
        { Reply(409, new { error = "Character, equipment or skill bar changed. Scan again." }); return; }
        if (!root.TryGetProperty("bindings", out var bindings) || bindings.ValueKind != JsonValueKind.Array || bindings.GetArrayLength() > 13)
        { Reply(400, new { error = "Select one to thirteen supported skills." }); return; }
        var requested = new List<BuildSelection>();
        foreach (var binding in bindings.EnumerateArray())
        {
            if (binding.ValueKind != JsonValueKind.Object || !binding.TryGetProperty("metadata", out var metadata)
                || metadata.ValueKind != JsonValueKind.String || !binding.TryGetProperty("key", out var keyValue)
                || keyValue.ValueKind != JsonValueKind.Number || !keyValue.TryGetInt32(out var key))
            { Reply(400, new { error = "Invalid skill binding." }); return; }
            var modifiers = 0; var slot = 0;
            if ((binding.TryGetProperty("modifiers", out var modValue) && (modValue.ValueKind != JsonValueKind.Number || !modValue.TryGetInt32(out modifiers)))
                || (binding.TryGetProperty("slot", out var slotValue) && (slotValue.ValueKind != JsonValueKind.Number || !slotValue.TryGetInt32(out slot))))
            { Reply(400, new { error = "Invalid slot or modifier." }); return; }
            requested.Add(new(metadata.GetString()!, key, modifiers, slot));
        }
        if (!AutoBuild.TrySelect(_buildPreview, requested, out var selected, out var error))
        { Reply(400, new { error }); return; }
        var importControls = root.TryGetProperty("importControls", out var importValue) && importValue.ValueKind == JsonValueKind.True;
        if (importControls && !currentInput.Complete)
        { Reply(400, new { error = "Cannot import controls from an incomplete configuration." }); return; }
        if (importControls)
        {
            if (currentInput.Mode == "Mouse" && (current.SkillBar is not { Complete: true } bar || MoveBinding(currentInput, bar) is null))
            { Reply(400, new { error = "Mouse movement import requires a readable live bar with Move Only assigned to a single key." }); return; }
            var required = new List<string> { "use_dodge_roll", "use_flask_in_slot1", "use_flask_in_slot2" };
            if (currentInput.Mode == "WASD") required.AddRange(new[] { "move_up", "move_left", "move_down", "move_right" });
            if (required.Any(a => currentInput.Find(a) is not { Usable: true, Modifiers: 0 }))
            { Reply(400, new { error = "Movement, dodge and flask import requires bound single keys. Keep controls unchanged or adjust them manually." }); return; }
        }
        var backup = CaptureBuildSettings();
        _settings.CombatSkills = selected;
        var attackRanges = selected.Where(s => _buildPreview.Suggestions.Any(p => p.Metadata == s.SourceMetadata && p.Role is "Main attack" or "Summon"))
            .Select(s => s.Range > 0 ? s.Range : _buildPreview.CombatRange).ToArray();
        _settings.CombatRange = attackRanges.Length > 0 ? attackRanges.Min() : _buildPreview.CombatRange;
        _settings.CombatKeepDistance = _settings.CombatRange >= 25 ? 12 : 0;
        if (importControls) ImportBuildControls(currentInput, current);
        _settings.BuildProfiles ??= new();
        // Archive all currently tuned character rules before replacing the active rotation.
        void Remember(string identity, IEnumerable<CombatSkill> skills) => _settings.BuildProfiles[identity] = skills
            .Concat(_settings.BuildProfiles.GetValueOrDefault(identity) ?? new()).DistinctBy(s => s.SourceMetadata)
            .Select(AutoBuild.Clone).Take(128).ToList();
        foreach (var group in backup.Skills.Where(s => s.SourceCharacter.Length > 0).GroupBy(s => s.SourceCharacter)) Remember(group.Key, group);
        Remember(_buildPreview.Identity, selected);
        if (BuildStore(_settings) is { } saveError)
        { RestoreBuildSettings(backup); Reply(500, new { error = "Could not save build: " + saveError }); return; }
        _buildBackup = backup;
        _buildApplied = JsonSerializer.Serialize(CaptureBuildSettings(), Json);
        _buildPreview = null;
        Reply(200, new { ok = true, note = "Build saved. Arm locally with F4 (combat) or F3 (bot)." });
    }
}
