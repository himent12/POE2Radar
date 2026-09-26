using POE2Radar.Core.Native;

namespace POE2Radar.Overlay.Input;

/// <summary>The input primitives <see cref="ChatSender"/> needs — injected so tests can record the exact sequence.</summary>
public interface IChatInput
{
    void TapKey(ushort vk);
    bool TypeText(string text);
    void Sleep(int ms);
    long NowMs { get; }
    /// <summary>Ctrl/Shift/Alt physically held — the user's hotkey chord would otherwise modify our keys.</summary>
    bool ModifiersHeld();
}

/// <summary>Real game input via <see cref="GameHost"/>.</summary>
public sealed class GameChatInput : IChatInput
{
    public void TapKey(ushort vk) => GameHost.TapKey(vk);
    public bool TypeText(string text) => GameHost.TypeText(text);
    public void Sleep(int ms) => Thread.Sleep(ms);
    public long NowMs => Environment.TickCount64;
    public bool ModifiersHeld()
        => GameHost.IsKeyDown(Hotkey.VkCtrl) || GameHost.IsKeyDown(Hotkey.VkShift) || GameHost.IsKeyDown(Hotkey.VkAlt);
}

/// <summary>
/// Sends chat lines to the game from ONE background thread (typing takes ~100 ms per line and must never
/// block the render/world loops). Per line: Enter (open chat) → wait → type → wait → Enter (send), at most one
/// line per <see cref="MinGapMs"/>. The <c>canSend</c> gate (game foreground + in-game) is re-checked before
/// opening chat and again before typing, so nothing is ever typed into another application; a failed gate
/// drops the line. Lines with untypeable characters are cancelled with Escape rather than sent garbled
/// (a mangled whisper name could reach the wrong player).
/// </summary>
public sealed class ChatSender : IDisposable
{
    public const int MaxPending = 8;
    public const int OpenDelayMs = 40, TypeDelayMs = 20, MinGapMs = 250, ModifierWaitMs = 1000;
    private const ushort VkEnter = 0x0D, VkEscape = 0x1B;

    private readonly IChatInput _input;
    private readonly Func<bool> _canSend;
    private readonly Queue<string> _queue = new();
    private readonly object _gate = new();
    private readonly Thread? _thread;
    private bool _busy, _stop;
    private long _lastSentMs = long.MinValue;
    private volatile string _lastResult = "";

    /// <param name="startWorker">false = no thread; tests drive <see cref="RunPending"/> synchronously.</param>
    public ChatSender(IChatInput input, Func<bool> canSend, bool startWorker = true)
    {
        _input = input;
        _canSend = canSend;
        if (!startWorker) return;
        _thread = new Thread(Worker) { IsBackground = true, Name = "ChatSender" };
        _thread.Start();
    }

    /// <summary>Outcome of the last send/drop, for the UI ("sent: /hideout", "dropped: game not focused").</summary>
    public string LastResult => _lastResult;

    public int Pending { get { lock (_gate) return _queue.Count; } }

    /// <summary>Lines queued or mid-send. Poll hotkeys only while false: on Linux the typed characters are real
    /// key presses, so a bare-letter binding could re-trigger itself from its own text.</summary>
    public bool IsSending { get { lock (_gate) return _busy || _queue.Count > 0; } }

    /// <summary>Queue <paramref name="text"/> (each non-blank line = one message). All-or-nothing: false when
    /// it would exceed <see cref="MaxPending"/>, when empty, or after Dispose.</summary>
    public bool Enqueue(string text)
        => Enqueue(text.Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0).ToList());

    public bool Enqueue(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return false;
        lock (_gate)
        {
            if (_stop) return false;
            if (_queue.Count + lines.Count > MaxPending)
            {
                _lastResult = $"dropped: chat queue full ({_queue.Count} pending)";
                return false;
            }
            foreach (var l in lines) _queue.Enqueue(l);
            Monitor.PulseAll(_gate);
        }
        return true;
    }

    /// <summary>Block until the queue is empty and nothing is mid-send (tests / orderly shutdown).</summary>
    public bool WaitIdle(int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        lock (_gate)
        {
            while (_queue.Count > 0 || _busy)
            {
                var left = deadline - Environment.TickCount64;
                if (left <= 0) return false;
                Monitor.Wait(_gate, (int)left);
            }
        }
        return true;
    }

    /// <summary>Send everything queued on the calling thread (only for <c>startWorker: false</c>).</summary>
    internal void RunPending()
    {
        while (TryDequeue(out var line)) Process(line);
    }

    private bool TryDequeue(out string line)
    {
        lock (_gate)
        {
            if (_queue.Count == 0) { line = ""; return false; }
            line = _queue.Dequeue();
            return true;
        }
    }

    private void Worker()
    {
        while (true)
        {
            string line;
            lock (_gate)
            {
                while (_queue.Count == 0 && !_stop) Monitor.Wait(_gate);
                if (_stop) return;
                line = _queue.Dequeue();
                _busy = true;
            }
            Process(line);
            lock (_gate) { _busy = false; Monitor.PulseAll(_gate); }
        }
    }

    private void Process(string line)
    {
        try { _lastResult = SendOne(line); }
        catch (Exception ex) { _lastResult = $"error: {ex.Message}"; }
    }

    private string SendOne(string line)
    {
        var wait = _lastSentMs == long.MinValue ? 0 : MinGapMs - (_input.NowMs - _lastSentMs);
        if (wait > 0) _input.Sleep((int)wait);
        // A chat hotkey like Ctrl+F5 is usually still held when we get here; Ctrl+Enter / Shift+letters would
        // do something else in-game, so wait (bounded) for the user to let go.
        var waited = 0;
        while (_input.ModifiersHeld())
        {
            if (waited >= ModifierWaitMs) return $"dropped \"{Short(line)}\": modifier keys held";
            _input.Sleep(20);
            waited += 20;
        }
        if (!_canSend()) return $"dropped \"{Short(line)}\": game not focused / not in game";
        _input.TapKey(VkEnter);
        _input.Sleep(OpenDelayMs);
        // Focus can change during the open delay; never type into another window. (The in-game chat box may
        // be left open in that case — harmless, the user closes it.)
        if (!_canSend()) return $"dropped \"{Short(line)}\": focus lost";
        var ok = _input.TypeText(line);
        _input.Sleep(TypeDelayMs);
        _input.TapKey(ok ? VkEnter : VkEscape);
        _lastSentMs = _input.NowMs;
        return ok ? $"sent: {Short(line)}" : $"cancelled \"{Short(line)}\": characters not typeable on this keyboard layout";
    }

    private static string Short(string s) => s.Length <= 60 ? s : s[..57] + "...";

    public void Dispose()
    {
        lock (_gate)
        {
            _stop = true;
            _queue.Clear();
            Monitor.PulseAll(_gate);
        }
        if (_thread != null && _thread != Thread.CurrentThread) _thread.Join(2000);
    }
}
