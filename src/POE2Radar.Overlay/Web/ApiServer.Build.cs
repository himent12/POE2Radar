using System.Net;
using System.Text.Json;
using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Input;

namespace POE2Radar.Overlay.Web;

public sealed partial class ApiServer
{
    public Func<CharacterLoadout>? LoadoutProvider { get; init; }
    private BuildProposal? _buildPreview;
    private string _buildToken = "";
    private DateTime _buildExpires;
    private BuildSettings? _buildBackup;
    private string _buildApplied = "";
    private sealed record BuildSettings(List<CombatSkill> Skills, float Range, float KeepDistance);

    private BuildSettings CaptureBuildSettings() => new(
        JsonSerializer.Deserialize<List<CombatSkill>>(JsonSerializer.Serialize(_settings.CombatSkills, Json), Json) ?? new(),
        _settings.CombatRange, _settings.CombatKeepDistance);

    private static string LoadoutKey(CharacterLoadout c) => JsonSerializer.Serialize(new
    {
        c.Character,
        c.League,
        c.Level,
        Hp = c.Vitals?.HpUnreserved,
        Mana = c.Vitals?.ManaUnreserved,
        Es = c.Vitals?.EsUnreserved,
        Equipment = c.Equipment.OrderBy(x => x.InventoryId).ThenBy(x => x.Metadata),
        Skills = c.Skills.OrderBy(x => x.Metadata)
    }, Json);

    private void HandleBuild(HttpListenerContext ctx)
    {
        void Reply(int status, object value) => Write(ctx, status, JsonSerializer.Serialize(value, Json));
        if (!IsLoopbackHost(ctx.Request)) { Reply(403, new { error = "forbidden host" }); return; }
        if (ctx.Request.HttpMethod == "GET")
        {
            if (LoadoutProvider is null) { Reply(503, new { error = "Character reader unavailable." }); return; }
            _buildPreview = AutoBuild.Propose(LoadoutProvider(), _settings.CombatSkills);
            _buildToken = Guid.NewGuid().ToString("N");
            _buildExpires = DateTime.UtcNow.AddMinutes(2);
            Reply(200, new { token = _buildToken, proposal = _buildPreview });
            return;
        }
        if (ctx.Request.HttpMethod != "POST") { Reply(405, new { error = "method not allowed" }); return; }
        if (!string.Equals(ctx.Request.ContentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
        { Reply(415, new { error = "Content-Type must be application/json." }); return; }
        using var body = JsonDocument.Parse(ReadBody(ctx));
        var root = body.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { Reply(400, new { error = "Expected an object." }); return; }
        if (_state().CombatAssist) { Reply(409, new { error = "Disarm combat/bot locally before replacing or restoring a build." }); return; }
        if (root.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.String && action.GetString() == "undo")
        {
            if (_buildBackup is null || JsonSerializer.Serialize(CaptureBuildSettings(), Json) != _buildApplied)
            { Reply(409, new { error = "No unchanged generated build to undo. Manual edits will not be overwritten." }); return; }
            _settings.CombatSkills = _buildBackup.Skills;
            _settings.CombatRange = _buildBackup.Range;
            _settings.CombatKeepDistance = _buildBackup.KeepDistance;
            _settings.Save();
            _buildBackup = null;
            Reply(200, new { ok = true });
            return;
        }
        if (!root.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String
            || token.GetString() != _buildToken || _buildPreview is null || DateTime.UtcNow > _buildExpires)
        { Reply(409, new { error = "Preview expired. Scan again." }); return; }
        if (!_buildPreview.Character.Complete || LoadoutProvider is null)
        { Reply(409, new { error = "Character scan is incomplete." }); return; }
        var current = LoadoutProvider();
        if (!current.Complete || LoadoutKey(current) != LoadoutKey(_buildPreview.Character))
        { Reply(409, new { error = "Character or equipment changed. Scan again." }); return; }
        if (!root.TryGetProperty("bindings", out var bindings) || bindings.ValueKind != JsonValueKind.Array || bindings.GetArrayLength() > 8)
        { Reply(400, new { error = "Select one to eight supported skills." }); return; }
        var requested = new List<(string Metadata, int Key)>();
        foreach (var binding in bindings.EnumerateArray())
        {
            if (binding.ValueKind != JsonValueKind.Object || !binding.TryGetProperty("metadata", out var metadata)
                || metadata.ValueKind != JsonValueKind.String || !binding.TryGetProperty("key", out var keyValue)
                || keyValue.ValueKind != JsonValueKind.Number || !keyValue.TryGetInt32(out var key))
            { Reply(400, new { error = "Invalid skill binding." }); return; }
            requested.Add((metadata.GetString()!, key));
        }
        if (!AutoBuild.TrySelect(_buildPreview, requested, out var selected, out var error))
        { Reply(400, new { error }); return; }
        _buildBackup = CaptureBuildSettings();
        _settings.CombatSkills = selected;
        _settings.CombatRange = _buildPreview.CombatRange;
        _settings.CombatKeepDistance = _buildPreview.KeepDistance;
        _settings.Save();
        _buildApplied = JsonSerializer.Serialize(CaptureBuildSettings(), Json);
        _buildPreview = null;
        Reply(200, new { ok = true, note = "Build saved. Arm locally with F4 (combat) or F3 (bot)." });
    }
}
