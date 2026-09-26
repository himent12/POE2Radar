using System.Text;

namespace POE2Radar.Overlay.Trade;

/// <summary>
/// Follows Client.txt on its own background thread and raises <see cref="OnLine"/> per complete line.
/// A file that already exists when first seen is joined at its END (history is never replayed); a file
/// that appears after we saw it missing is read from the start. Truncation reopens from 0. The path is
/// re-queried every poll, so a changed setting or a late-launched game is picked up. Never throws out of
/// the thread; <see cref="Status"/> carries the last problem.
/// </summary>
public sealed class ClientLogTailer : IDisposable
{
    private const int MaxBytesPerPoll = 4 << 20; // a huge new file is drained over several polls, not in one stall
    private readonly Func<string?> _pathProvider;
    private readonly int _pollMs;
    private readonly ManualResetEventSlim _stop = new();
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private readonly byte[] _bytes = new byte[64 * 1024];
    private readonly char[] _chars = new char[64 * 1024 + 4];
    private readonly StringBuilder _partial = new();
    private Thread? _thread;
    private FileStream? _stream;
    private string? _path;
    private long _pos = -1; // -1 = join at end on next open
    private volatile string _status = "not started";
    private long _lines;

    public ClientLogTailer(Func<string?> pathProvider, int pollMs = 250)
    {
        _pathProvider = pathProvider;
        _pollMs = Math.Max(10, pollMs);
    }

    /// <summary>Raised on the tailer thread, one call per line (no trailing CR/LF). Handler exceptions are swallowed.</summary>
    public event Action<string>? OnLine;
    public string Status => _status;
    public string? CurrentPath => Volatile.Read(ref _path);
    public long LinesRead => Interlocked.Read(ref _lines);

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "ClientLogTailer" };
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            do
            {
                try { Poll(); }
                catch (Exception ex)
                {
                    // Keep _pos: a transient IO error resumes where we left off rather than skipping or replaying.
                    _status = "error: " + ex.Message;
                    Close();
                }
            } while (!_stop.Wait(_pollMs));
        }
        catch (Exception ex) { _status = "stopped: " + ex.Message; }
        finally { Close(); }
    }

    private void Poll()
    {
        string? path;
        try { path = _pathProvider(); }
        catch (Exception ex) { _status = "path error: " + ex.Message; return; }
        if (path != _path)
        {
            Close();
            Volatile.Write(ref _path, path);
            _pos = -1;
            ResetText();
        }
        if (string.IsNullOrWhiteSpace(path)) { _status = "no Client.txt path"; return; }
        if (!File.Exists(path))
        {
            Close();
            _pos = 0; // whatever appears at this path from now on is new
            ResetText();
            _status = "waiting for " + path;
            return;
        }

        if (_stream == null)
        {
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            if (_pos < 0) _pos = _stream.Length;
        }
        if (_stream.Length < _pos || new FileInfo(path).Length < _pos)
        {
            // Truncated or rotated under us: start over on whatever is there now.
            Close();
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            _pos = 0;
            ResetText();
        }

        _stream.Position = _pos;
        var budget = MaxBytesPerPoll;
        int n;
        while (budget > 0 && !_stop.IsSet && (n = _stream.Read(_bytes, 0, Math.Min(_bytes.Length, budget))) > 0)
        {
            _pos += n;
            budget -= n;
            var c = _decoder.GetChars(_bytes, 0, n, _chars, 0, flush: false); // holds split UTF-8 sequences
            _partial.Append(_chars, 0, c);
            EmitLines();
        }
        _status = "tailing " + path;
    }

    private void EmitLines()
    {
        var start = 0;
        for (var i = 0; i < _partial.Length; i++)
        {
            if (_partial[i] != '\n') continue;
            var end = i > start && _partial[i - 1] == '\r' ? i - 1 : i;
            if (end > start) Raise(_partial.ToString(start, end - start));
            start = i + 1;
        }
        _partial.Remove(0, start); // an unterminated tail waits for its newline
    }

    private void Raise(string line)
    {
        Interlocked.Increment(ref _lines);
        if (OnLine is not { } handler) return;
        // Per-subscriber so one faulty handler can't starve the others.
        foreach (var d in handler.GetInvocationList())
        {
            try { ((Action<string>)d)(line); }
            catch (Exception ex) { _status = "handler error: " + ex.Message; }
        }
    }

    private void ResetText()
    {
        _decoder.Reset();
        _partial.Clear();
    }

    private void Close()
    {
        try { _stream?.Dispose(); } catch (Exception) { }
        _stream = null;
    }

    public void Dispose()
    {
        if (_stop.IsSet) return;
        _stop.Set();
        var t = _thread;
        if (t == null) Close();
        // Only free the wait handle once the thread is gone; a still-running poll would fault on it.
        if (t == null || (t != Thread.CurrentThread && t.Join(2000))) _stop.Dispose();
    }
}
