using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;

namespace POE2Radar.Overlay.Web;

/// <summary>Immutable snapshot published by the tick loop for the API to serve.</summary>
public sealed record RadarState(
    bool InGame,
    uint AreaHash,
    int AreaLevel,
    bool MapVisible,
    float Zoom,
    System.Numerics.Vector2 Player,
    IReadOnlyList<Poe2Live.EntityDot> Entities,
    IReadOnlyList<Poe2Live.Landmark> Landmarks,
    float HpPct,
    float ManaPct,
    float EsPct,
    bool AutoFlask,
    string FlaskNote,
    string AreaCode,
    string CharName,
    int CharLevel,
    // Threading validation timers: the last world-pass duration (background thread) and the last
    // render-frame duration (render thread), in milliseconds. Surfaced via /state for stress-testing.
    float WorldMs = 0,
    float RenderMs = 0,
    // Runeshape monoliths in the current area (slot count, anchor, best value + full priced reward set) —
    // served to the dashboard's Monolith Rewards card. Empty when none / feature disabled.
    IReadOnlyList<MonolithMarker>? Monoliths = null,
    // Measured effective render FPS (rolling window) — for verifying the overlay actually hits FpsCap.
    float Fps = 0,
    // Currency-exchange live order book (when the exchange panel is open): per-side ladder rows + summary.
    bool ExchangeOpen = false,
    string ExchangeSummary = "",
    IReadOnlyList<ExchangeRow>? ExchangeOffered = null,
    IReadOnlyList<ExchangeRow>? ExchangeWanted = null,
    int ExchangeHaveQty = 0,
    string ExchangeFillNote = "",
    bool CombatAssist = false,
    string CombatNote = "",
    bool QuestFollow = false,
    string QuestFollowNote = "",
    bool PathMove = false,
    string PathMoveNote = "",
    bool Bot = false,
    string BotNote = "",
    bool MapClear = false,
    string MapClearNote = "",
    bool FarmLoop = false,
    string FarmNote = "")
{
    public static readonly RadarState Empty =
        new(false, 0, 0, false, 0, System.Numerics.Vector2.Zero,
            Array.Empty<Poe2Live.EntityDot>(), Array.Empty<Poe2Live.Landmark>(), 100, 100, 100, false, "", "", "", 0);
}
