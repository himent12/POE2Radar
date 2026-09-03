using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;

namespace POE2Radar.Overlay.Web;

public sealed partial class ApiServer
{
    /// <summary>The navigation selection as a list of {id, slot} objects (for the GET/POST payloads).</summary>
    private object[] NavSelection()
        => _navGet().Select(s => (object)new { id = s.Id, slot = s.Slot }).ToArray();

    /// <summary>Apply a posted nav command: {"toggle":"&lt;id&gt;"} toggles that target; {"clear":true}
    /// clears the whole selection. Anything else is ignored. Draw-only — sends nothing to the game.</summary>
    private void ApplyNav(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        if (root.TryGetProperty("clear", out var clear) && clear.ValueKind == JsonValueKind.True)
        {
            _navClear();
            return;
        }
        if (root.TryGetProperty("toggle", out var toggle) && toggle.ValueKind == JsonValueKind.String)
        {
            var id = toggle.GetString();
            if (!string.IsNullOrEmpty(id)) _navToggle(id);
        }
    }

    /// <summary>Apply a posted hidden-list command: {"add":"&lt;pattern&gt;"} adds a cull pattern,
    /// {"remove":"&lt;pattern&gt;"} removes one, {"clear":true} clears all. A pattern may be a literal
    /// substring or a <c>*</c>/<c>?</c> glob. Affects only what the overlay draws/serves — never the game.</summary>
    private void ApplyHidden(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        if (root.TryGetProperty("clear", out var clear) && clear.ValueKind == JsonValueKind.True)
        {
            _hidden.Clear();
            return;
        }
        if (root.TryGetProperty("add", out var add) && add.ValueKind == JsonValueKind.String)
        {
            var p = add.GetString();
            if (!string.IsNullOrWhiteSpace(p)) _hidden.Add(p);
        }
        if (root.TryGetProperty("remove", out var remove) && remove.ValueKind == JsonValueKind.String)
        {
            var p = remove.GetString();
            if (!string.IsNullOrWhiteSpace(p)) _hidden.Remove(p);
        }
    }

    /// <summary>Replace the entire ordered display ruleset from a POST <c>{"rules":[...]}</c> — the
    /// dashboard owns the array and re-posts it on every edit (add / remove / reorder / toggle / field
    /// change), the same whole-object pattern <c>styles</c> uses. Each rule is sanitized. Also accepts
    /// <c>{"clear":true}</c>. Render-only — never touches the game.</summary>
    private void ApplyDisplayRules(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        if (root.TryGetProperty("clear", out var cl) && TryBool(cl, out var c) && c)
        {
            _displayRules.Replace(Array.Empty<DisplayRule>());
            return;
        }
        if (!root.TryGetProperty("rules", out var arr) || arr.ValueKind != JsonValueKind.Array) return;

        var list = JsonSerializer.Deserialize<List<DisplayRule>>(arr.GetRawText(), Json) ?? new();
        if (list.Count > 300) list = list.GetRange(0, 300); // sanity cap
        foreach (var r in list) SanitizeRule(r);
        _displayRules.Replace(list);
    }

    /// <summary>Apply a Landmarks-tab command to the curated-label overlay:
    /// <list type="bullet">
    /// <item>{"set":{area,pattern,label}} — add / rename (string label) or suppress (null/blank label)</item>
    /// <item>{"remove":{area,pattern}} — delete the user entry (reverts to the baked label, if any)</item>
    /// <item>{"import":{area:{pattern:label|null}}} — replace the whole user overlay</item>
    /// </list>
    /// Edits curated labels only — never the game.</summary>
    private void ApplyLandmarks(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        if (root.TryGetProperty("import", out var imp) && imp.ValueKind == JsonValueKind.Object)
        {
            try { _landmarkStore.Import(JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string?>>>(imp.GetRawText())); }
            catch { /* ignore malformed import */ }
            return;
        }
        if (root.TryGetProperty("set", out var set) && set.ValueKind == JsonValueKind.Object)
        {
            var area = Str(set, "area"); var pattern = Str(set, "pattern");
            var label = set.TryGetProperty("label", out var lv) && lv.ValueKind == JsonValueKind.String ? lv.GetString() : null;
            label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();   // blank → suppress
            if (!string.IsNullOrWhiteSpace(area) && !string.IsNullOrWhiteSpace(pattern))
                _landmarkStore.Set(area!.Trim(), pattern!.Trim(), label);
        }
        if (root.TryGetProperty("remove", out var rem) && rem.ValueKind == JsonValueKind.Object)
        {
            var area = Str(rem, "area"); var pattern = Str(rem, "pattern");
            if (!string.IsNullOrWhiteSpace(area) && !string.IsNullOrWhiteSpace(pattern))
                _landmarkStore.Remove(area!.Trim(), pattern!.Trim());
        }
    }
}
