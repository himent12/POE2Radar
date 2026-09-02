using POE2Radar.Core.Game;

namespace POE2Radar.Core.Pathfinding;

/// <summary>
/// Isometric projection from grid coordinates to map-screen coordinates. PoE renders the
/// map with a 38.7° camera tilt and a player-centered frame; the equation matches the
/// open-source Radar plugin so overlays line up with the in-game player blip.
///
/// <para>Convention: <c>delta</c> is the cell offset from the player to the target
/// (target.grid - player.grid). The result is added to the map center.</para>
/// </summary>
public static class MapProjection
{
    private const double CameraAngleRad = 38.7 * Math.PI / 180.0;
    public static readonly float CameraCos = (float)Math.Cos(CameraAngleRad);
    public static readonly float CameraSin = (float)Math.Sin(CameraAngleRad);

    /// <summary>
    /// Convert a grid delta (target - player) into the equivalent screen-space delta on the
    /// map at the given <paramref name="mapScale"/>. Optional <paramref name="deltaWorldZ"/>
    /// adds elevation; pass 0 for flat terrain (acceptable until height data is wired up).
    /// </summary>
    public static Vector2 GridDeltaToMapDelta(Vector2 delta, float mapScale, float deltaWorldZ = 0f)
    {
        var dz = deltaWorldZ / GridConstants.GridToWorld;
        return new Vector2
        {
            X = mapScale * (delta.X - delta.Y) * CameraCos,
            Y = mapScale * (dz - (delta.X + delta.Y)) * CameraSin,
        };
    }

    /// <summary>
    /// Project a grid cell to the map by adding its delta-from-player to the map center.
    /// Both <paramref name="mapCenter"/> and the result are in window-relative pixels.
    /// </summary>
    public static Vector2 GridToMapPoint(
        Vector2 gridCell,
        Vector2 playerGrid,
        Vector2 mapCenter,
        float   mapScale,
        float   deltaWorldZ = 0f)
    {
        var d = new Vector2 { X = gridCell.X - playerGrid.X, Y = gridCell.Y - playerGrid.Y };
        var md = GridDeltaToMapDelta(d, mapScale, deltaWorldZ);
        return new Vector2 { X = mapCenter.X + md.X, Y = mapCenter.Y + md.Y };
    }

    /// <summary>
    /// Project a world-space point through the camera WorldToScreen matrix (16 floats, row-major)
    /// into window-relative pixels. Returns false when the point is behind the camera or the
    /// matrix is missing.
    /// </summary>
    public static bool TryWorldToScreen(ReadOnlySpan<float> m, float wx, float wy, float wz,
        float width, float height, out float screenX, out float screenY)
    {
        screenX = screenY = 0;
        if (m.Length < 16) return false;
        var cw = wx * m[3] + wy * m[7] + wz * m[11] + m[15];
        if (cw <= 0.0001f) return false;
        var cxp = wx * m[0] + wy * m[4] + wz * m[8] + m[12];
        var cyp = wx * m[1] + wy * m[5] + wz * m[9] + m[13];
        screenX = (cxp / cw / 2f + 0.5f) * width;
        screenY = (0.5f - cyp / cw / 2f) * height;
        return true;
    }
}
