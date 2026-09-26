using System.Collections.Concurrent;
using System.Text;
using POE2Radar.Overlay.Trade;
using Xunit;

namespace POE2Radar.Tests;

public sealed class ClientLogTailerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "poe2tail-" + Guid.NewGuid().ToString("N"));
    private readonly string _file;
    private readonly ConcurrentQueue<string> _lines = new();

    public ClientLogTailerTests()
    {
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "Client.txt");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private ClientLogTailer Start(Func<string?>? path = null)
    {
        var t = new ClientLogTailer(path ?? (() => _file), pollMs: 15);
        t.OnLine += _lines.Enqueue;
        t.Start();
        return t;
    }

    private void Append(string text) => Append(Encoding.UTF8.GetBytes(text));

    private void Append(byte[] bytes)
    {
        using var fs = new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        fs.Write(bytes);
    }

    private static bool WaitFor(Func<bool> cond, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (cond()) return true;
            Thread.Sleep(10);
        }
        return cond();
    }

    private static void WaitTailing(ClientLogTailer t) =>
        Assert.True(WaitFor(() => t.Status.StartsWith("tailing", StringComparison.Ordinal)), t.Status);

    [Fact]
    public void Starts_at_end_and_emits_only_new_complete_lines()
    {
        File.WriteAllText(_file, "old line 1\nold line 2\n");
        using var t = Start();
        WaitTailing(t);
        Append("new 1\r\nnew 2\npartial");
        Assert.True(WaitFor(() => _lines.Count >= 2));
        Thread.Sleep(80);
        Assert.Equal(["new 1", "new 2"], _lines.ToArray());
        Append(" done\n");
        Assert.True(WaitFor(() => _lines.Count >= 3));
        Assert.Equal("partial done", _lines.ToArray()[2]);
    }

    [Fact]
    public void Decodes_utf8_split_across_writes()
    {
        File.WriteAllText(_file, "");
        using var t = Start();
        WaitTailing(t);
        var bytes = Encoding.UTF8.GetBytes("Grüße ✓ 名前\n");
        var split = Array.IndexOf(bytes, (byte)0xE2) + 1; // inside the 3-byte check mark
        Append(bytes[..split]);
        Thread.Sleep(80);
        Append(bytes[split..]);
        Assert.True(WaitFor(() => _lines.Count >= 1));
        Assert.Equal("Grüße ✓ 名前", Assert.Single(_lines));
    }

    [Fact]
    public void Truncation_restarts_from_zero()
    {
        File.WriteAllText(_file, new string('x', 200) + "\n");
        using var t = Start();
        WaitTailing(t);
        Append("a\n");
        Assert.True(WaitFor(() => _lines.Count >= 1));
        using (var fs = new FileStream(_file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            fs.SetLength(0);
        Thread.Sleep(80);
        Append("after truncate\n");
        Assert.True(WaitFor(() => _lines.Contains("after truncate")));
    }

    [Fact]
    public void File_appearing_later_is_read_from_start()
    {
        using var t = Start();
        Assert.True(WaitFor(() => t.Status.StartsWith("waiting", StringComparison.Ordinal)), t.Status);
        File.WriteAllText(_file, "first\nsecond\n");
        Assert.True(WaitFor(() => _lines.Count >= 2));
        Assert.Equal(["first", "second"], _lines.ToArray());
    }

    [Fact]
    public void Path_resolved_later_joins_existing_file_at_end()
    {
        File.WriteAllText(_file, "history\n");
        string? path = null;
        using var t = Start(() => Volatile.Read(ref path));
        Assert.True(WaitFor(() => t.Status.StartsWith("no ", StringComparison.Ordinal)), t.Status);
        Volatile.Write(ref path, _file);
        WaitTailing(t);
        Append("fresh\n");
        Assert.True(WaitFor(() => _lines.Count >= 1));
        Assert.Equal("fresh", Assert.Single(_lines));
        Assert.Equal(_file, t.CurrentPath);
    }

    [Fact]
    public void Handler_and_provider_exceptions_do_not_kill_the_thread()
    {
        File.WriteAllText(_file, "");
        var calls = 0;
        using var t = new ClientLogTailer(() => Interlocked.Increment(ref calls) == 1 ? throw new IOException("boom") : _file, 15);
        t.OnLine += l => { if (l == "bad") throw new InvalidOperationException("handler"); };
        t.OnLine += _lines.Enqueue;
        t.Start();
        Assert.True(WaitFor(() => t.Status.StartsWith("tailing", StringComparison.Ordinal)), t.Status);
        Append("bad\ngood\n");
        Assert.True(WaitFor(() => _lines.Contains("good")));
        Assert.Equal(2, t.LinesRead);
    }

    [Fact]
    public void Dispose_is_idempotent_and_releases_the_file()
    {
        File.WriteAllText(_file, "");
        var t = Start();
        WaitTailing(t);
        t.Dispose();
        t.Dispose();
        File.Delete(_file);
        Assert.False(File.Exists(_file));
    }
}
