using POE2Radar.Overlay.Input;
using Xunit;

namespace POE2Radar.Tests;

public sealed class ChatSenderTests
{
    /// <summary>Records every input call as a string and advances a fake clock on Sleep.</summary>
    private sealed class FakeInput : IChatInput
    {
        public readonly List<string> Log = new();
        public long Now;
        public bool TypeOk = true;
        public int ModifiersHeldPolls;
        public Action? OnEnter;
        public void TapKey(ushort vk) { Log.Add(vk switch { 0x0D => "Enter", 0x1B => "Esc", _ => $"vk{vk:X2}" }); if (vk == 0x0D) OnEnter?.Invoke(); }
        public bool TypeText(string text) { Log.Add("type:" + text); return TypeOk; }
        public void Sleep(int ms) { Log.Add($"sleep:{ms}"); Now += ms; }
        public long NowMs => Now;
        public bool ModifiersHeld() => ModifiersHeldPolls-- > 0;
    }

    [Fact]
    public void Sends_enter_type_enter_with_delays()
    {
        var input = new FakeInput();
        using var s = new ChatSender(input, () => true, startWorker: false);
        Assert.True(s.Enqueue("/hideout"));
        s.RunPending();
        Assert.Equal(["Enter", "sleep:40", "type:/hideout", "sleep:20", "Enter"], input.Log);
        Assert.Equal("sent: /hideout", s.LastResult);
    }

    [Fact]
    public void Multi_line_text_is_separate_messages_at_least_250ms_apart()
    {
        var input = new FakeInput();
        using var s = new ChatSender(input, () => true, startWorker: false);
        Assert.True(s.Enqueue("/hideout\r\n\n  @Bob ty  \n"));
        Assert.Equal(2, s.Pending);
        s.RunPending();
        Assert.Equal(["Enter", "sleep:40", "type:/hideout", "sleep:20", "Enter",
                      "sleep:250",
                      "Enter", "sleep:40", "type:@Bob ty", "sleep:20", "Enter"], input.Log);
    }

    [Fact]
    public void Gap_counts_time_already_elapsed()
    {
        var input = new FakeInput();
        using var s = new ChatSender(input, () => true, startWorker: false);
        s.Enqueue("a"); s.RunPending();
        input.Now += 200; input.Log.Clear();
        s.Enqueue("b"); s.RunPending();
        Assert.Equal("sleep:50", input.Log[0]);
        input.Now += 1000; input.Log.Clear();
        s.Enqueue("c"); s.RunPending();
        Assert.Equal("Enter", input.Log[0]);
    }

    [Fact]
    public void Closed_gate_types_nothing_and_reports()
    {
        var input = new FakeInput();
        using var s = new ChatSender(input, () => false, startWorker: false);
        s.Enqueue("/hideout");
        s.RunPending();
        Assert.Empty(input.Log);
        Assert.StartsWith("dropped \"/hideout\": game not focused", s.LastResult);
    }

    [Fact]
    public void Focus_lost_after_opening_chat_does_not_type()
    {
        var input = new FakeInput();
        var focused = true;
        input.OnEnter = () => focused = false;
        using var s = new ChatSender(input, () => focused, startWorker: false);
        s.Enqueue("/hideout");
        s.RunPending();
        Assert.Equal(["Enter", "sleep:40"], input.Log);
        Assert.Contains("focus lost", s.LastResult);
    }

    [Fact]
    public void Untypeable_text_is_cancelled_with_escape()
    {
        var input = new FakeInput { TypeOk = false };
        using var s = new ChatSender(input, () => true, startWorker: false);
        s.Enqueue("@Ωmega hi");
        s.RunPending();
        Assert.Equal(["Enter", "sleep:40", "type:@Ωmega hi", "sleep:20", "Esc"], input.Log);
        Assert.StartsWith("cancelled", s.LastResult);
    }

    [Fact]
    public void Waits_for_hotkey_modifiers_to_be_released()
    {
        var input = new FakeInput { ModifiersHeldPolls = 3 };
        using var s = new ChatSender(input, () => true, startWorker: false);
        s.Enqueue("/hideout");
        s.RunPending();
        Assert.Equal(["sleep:20", "sleep:20", "sleep:20", "Enter"], input.Log.Take(4));
    }

    [Fact]
    public void Stuck_modifiers_drop_the_line()
    {
        var input = new FakeInput { ModifiersHeldPolls = int.MaxValue };
        using var s = new ChatSender(input, () => true, startWorker: false);
        s.Enqueue("/hideout");
        s.RunPending();
        Assert.DoesNotContain("Enter", input.Log);
        Assert.Contains("modifier keys held", s.LastResult);
    }

    [Fact]
    public void Queue_is_capped_and_multi_line_is_all_or_nothing()
    {
        var input = new FakeInput();
        using var s = new ChatSender(input, () => true, startWorker: false);
        for (var i = 0; i < ChatSender.MaxPending; i++) Assert.True(s.Enqueue($"line {i}"));
        Assert.False(s.Enqueue("one too many"));
        Assert.Contains("queue full", s.LastResult);
        s.RunPending();
        Assert.Equal(ChatSender.MaxPending, input.Log.Count(l => l.StartsWith("type:")));

        input.Log.Clear();
        for (var i = 0; i < ChatSender.MaxPending - 1; i++) s.Enqueue($"x{i}");
        Assert.False(s.Enqueue("a\nb"));
        Assert.Equal(ChatSender.MaxPending - 1, s.Pending);
        Assert.False(s.Enqueue("  \n "));
    }

    [Fact]
    public void Input_exceptions_never_escape_and_next_line_still_sends()
    {
        var input = new FakeInput();
        var calls = 0;
        using var s = new ChatSender(input, () => ++calls == 1 ? throw new InvalidOperationException("boom") : true, startWorker: false);
        s.Enqueue("a\nb");
        s.RunPending();
        Assert.Contains("type:b", input.Log);
        Assert.Equal("sent: b", s.LastResult);
    }

    [Fact]
    public void Worker_thread_sends_and_dispose_stops_it()
    {
        var input = new FakeInput();
        var s = new ChatSender(input, () => true);
        Assert.True(s.Enqueue("/hideout\n/kingsmarch"));
        Assert.True(s.WaitIdle(5000));
        Assert.False(s.IsSending);
        lock (input.Log) Assert.Equal(["type:/hideout", "type:/kingsmarch"], input.Log.Where(l => l.StartsWith("type:")));
        s.Dispose();
        Assert.False(s.Enqueue("after dispose"));
    }
}
