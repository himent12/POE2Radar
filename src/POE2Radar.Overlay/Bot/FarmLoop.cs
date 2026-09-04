using POE2Radar.Core.Game;
using NumVec2 = System.Numerics.Vector2;

namespace POE2Radar.Overlay.Navigation;

/// <summary>
/// Farm loop: clear the farm area (map-clear), open a town portal, walk into it, walk to the town
/// waypoint, take the waypoint back to the farm area as a NEW instance, clear again — forever.
///
/// <para>Pure phase machine: <see cref="Next"/> is a function of the snapshot and returns the phase to be
/// in, the nav target to walk to (an entity id the route planner resolves), and the one-shot input the
/// owner should perform this tick. Every phase has a timeout that falls back to the phase that can
/// recover it (portal despawned → cast again; waypoint menu without the area → click the waypoint again).
/// Tests never need a live client.</para>
/// </summary>
public static class FarmLoop
{
    public enum Phase { Off, Clearing, OpenPortal, EnterPortal, TownWaypoint, WaypointMenu, Returning }

    public enum Input { None, CastPortal, ClickWaypointArea }

    /// <summary>Portal the player casts (the town-side "return" portal is a different object).</summary>
    public const string PortalMetadata = "MiscellaneousObjects/TownPortal";
    public const string ReturnPortalMetadata = "MiscellaneousObjects/ReturnToLastTownPortal";
    public const string WaypointMetadata = "MiscellaneousObjects/Waypoint";

    // ── Timing (ms). ──
    public const int PortalCastRetryMs = 4000;   // no portal appeared → cast again
    public const int EnterPortalTimeoutMs = 25000; // walking into the portal took too long → recast
    public const int WaypointTimeoutMs = 60000;   // could not reach/use the waypoint → re-pick it
    public const int MenuTimeoutMs = 5000;        // waypoint menu without the area name → click the waypoint again
    public const int ReturnTimeoutMs = 40000;     // area did not change after the menu click → back to the waypoint
    public const int MenuRetryMs = 3000;          // menu still open this long after the click → click again
    public const float PortalSearchCells = 30f;   // a freshly cast portal is right next to the player

    public readonly record struct Snapshot(
        Phase Phase,
        DateTime PhaseSince,
        DateTime NowUtc,
        string AreaCode,
        string FarmAreaCode,
        bool InTown,
        bool ClearDone,          // map-clear reports the zone cleared (sustained by the owner)
        IReadOnlyList<Poe2Live.EntityDot> Entities,
        NumVec2 Player,
        bool MenuAreaVisible,    // the waypoint menu currently shows the farm area's name (render probe)
        DateTime LastInputUtc,   // when the owner last performed CastPortal / ClickWaypointArea
        bool PortalKeyBound = true);

    public readonly record struct Step(Phase Phase, string? TargetId, Input Input, string Note, bool Reset = false)
    {
        public bool PhaseChanged(Phase from) => Phase != from;
    }

    public static Step Next(in Snapshot s)
    {
        var inFarm = string.Equals(s.AreaCode, s.FarmAreaCode, StringComparison.OrdinalIgnoreCase);
        var elapsed = s.NowUtc - s.PhaseSince;
        var sinceInput = s.NowUtc - s.LastInputUtc;

        // Location overrides: wherever the machine thinks it is, the zone decides.
        if (inFarm && s.Phase is Phase.TownWaypoint or Phase.WaypointMenu or Phase.Returning or Phase.Off)
            return new(Phase.Clearing, null, Input.None, "clearing", Reset: true);
        if (s.InTown && s.Phase is Phase.Clearing or Phase.OpenPortal or Phase.EnterPortal or Phase.Off)
            return new(Phase.TownWaypoint, WaypointId(s.Entities), Input.None, "town → waypoint", Reset: true);
        if (!inFarm && !s.InTown && s.Phase is Phase.Clearing or Phase.OpenPortal or Phase.EnterPortal or Phase.Off)
            return new(Phase.OpenPortal, null, Input.None, "wrong zone → portal home", Reset: s.Phase != Phase.OpenPortal);

        switch (s.Phase)
        {
            case Phase.Clearing:
                if (s.ClearDone) return new(Phase.OpenPortal, null, Input.None, "cleared → portal", Reset: true);
                return new(Phase.Clearing, null, Input.None, "clearing");

            case Phase.OpenPortal:
            {
                if (!s.PortalKeyBound) return new(Phase.OpenPortal, null, Input.None, "set a portal key (dashboard)");
                if (TryNearestPortal(s.Entities, s.Player, out var portal))
                    return new(Phase.EnterPortal, "e:" + portal.Id, Input.None, "→ portal", Reset: true);
                var cast = sinceInput.TotalMilliseconds >= PortalCastRetryMs;
                return new(Phase.OpenPortal, null, cast ? Input.CastPortal : Input.None, cast ? "casting portal" : "waiting for portal");
            }

            case Phase.EnterPortal:
            {
                if (!TryNearestPortal(s.Entities, s.Player, out var portal) || elapsed.TotalMilliseconds > EnterPortalTimeoutMs)
                    return new(Phase.OpenPortal, null, Input.None, "portal gone → recast", Reset: true);
                return new(Phase.EnterPortal, "e:" + portal.Id, Input.None, "→ portal");
            }

            case Phase.TownWaypoint:
            {
                var wp = WaypointId(s.Entities);
                if (s.MenuAreaVisible) return new(Phase.WaypointMenu, null, Input.None, "waypoint menu", Reset: true);
                if (elapsed.TotalMilliseconds > WaypointTimeoutMs) return new(Phase.TownWaypoint, wp, Input.None, "→ waypoint (retry)", Reset: true);
                return new(Phase.TownWaypoint, wp, Input.None, wp is null ? "looking for the waypoint" : "→ waypoint");
            }

            case Phase.WaypointMenu:
            {
                if (!s.MenuAreaVisible)
                    return elapsed.TotalMilliseconds > MenuTimeoutMs
                        ? new(Phase.TownWaypoint, WaypointId(s.Entities), Input.None, "menu closed → waypoint", Reset: true)
                        : new(Phase.WaypointMenu, null, Input.None, "waypoint menu");
                if (s.LastInputUtc >= s.PhaseSince) return new(Phase.Returning, null, Input.None, "→ new instance", Reset: true);
                return new(Phase.WaypointMenu, null, Input.ClickWaypointArea, "selecting farm area");
            }

            case Phase.Returning:
                if (elapsed.TotalMilliseconds > ReturnTimeoutMs)
                    return new(Phase.TownWaypoint, WaypointId(s.Entities), Input.None, "no zone change → waypoint", Reset: true);
                // Menu still open well after the click: the label click did not take — click again.
                if (s.MenuAreaVisible && elapsed.TotalMilliseconds > MenuRetryMs)
                    return new(Phase.WaypointMenu, null, Input.None, "menu still open → click again", Reset: true);
                return new(Phase.Returning, null, Input.None, "→ new instance");

            default:
                return new(inFarm ? Phase.Clearing : s.InTown ? Phase.TownWaypoint : Phase.OpenPortal, null, Input.None, "starting", Reset: true);
        }
    }

    /// <summary>Nearest player-cast town portal within <see cref="PortalSearchCells"/> of the player.</summary>
    public static bool TryNearestPortal(IReadOnlyList<Poe2Live.EntityDot> entities, NumVec2 player, out Poe2Live.EntityDot portal)
    {
        portal = default;
        var best = PortalSearchCells * PortalSearchCells;
        var found = false;
        foreach (var e in entities)
        {
            if (!IsPortal(e)) continue;
            var d = NumVec2.DistanceSquared(e.Grid, player);
            if (d > best) continue;
            best = d; portal = e; found = true;
        }
        return found;
    }

    public static bool IsPortal(in Poe2Live.EntityDot e)
        => e.Metadata.Contains(PortalMetadata, StringComparison.OrdinalIgnoreCase)
           && !e.Metadata.Contains(ReturnPortalMetadata, StringComparison.OrdinalIgnoreCase);

    /// <summary>The town waypoint entity's nav id, or null when it is not in the entity list yet.</summary>
    public static string? WaypointId(IReadOnlyList<Poe2Live.EntityDot> entities)
    {
        foreach (var e in entities)
            if (e.Metadata.Contains(WaypointMetadata, StringComparison.OrdinalIgnoreCase)) return "e:" + e.Id;
        return null;
    }
}
