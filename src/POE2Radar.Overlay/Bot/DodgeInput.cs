namespace POE2Radar.Overlay.Input;

/// <summary>One bounded dodge press owns input through recovery. Captures keys at dispatch so rebinding,
/// cancellation, focus loss and shutdown release exactly the keys actually sent.</summary>
public sealed class DodgeInput
{
    private readonly HashSet<ushort> _held = new();
    private DateTime _release, _directionRelease, _until;
    private ushort _dodge;
    public bool Busy { get; private set; }
    public void Start(DateTime now, ushort dodge, IReadOnlyList<ushort> direction, int recoveryMs, Action<ushort> down, Action<ushort> up)
    {
        Cancel(up);
        _dodge = dodge; Busy = true;
        _release = now.AddMilliseconds(70); _directionRelease = now.AddMilliseconds(250);
        _until = now.AddMilliseconds(Math.Max(400, recoveryMs));
        try
        {
            foreach (var key in direction.Append(dodge).Distinct()) { down(key); _held.Add(key); }
        }
        catch { Cancel(up); throw; }
    }
    public bool Tick(DateTime now, Action<ushort> up)
    {
        if (!Busy) return false;
        if (now >= _release && _held.Remove(_dodge)) up(_dodge);
        if (now >= _directionRelease) Release(up);
        if (now >= _until) { Release(up); Busy = false; }
        return Busy;
    }
    public void Cancel(Action<ushort> up) { Release(up); Busy = false; }
    private void Release(Action<ushort> up) { foreach (var key in _held) up(key); _held.Clear(); }
}
