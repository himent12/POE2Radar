using System.Globalization;
using System.Text.RegularExpressions;

namespace POE2Radar.Overlay.Trade;

/// <summary>One recognised Client.txt line. <see cref="Time"/> is the log's local wall-clock time.</summary>
public abstract record LogEvent(DateTime Time);
public sealed record WhisperReceived(DateTime Time, string From, string? Guild, string Message) : LogEvent(Time);
public sealed record WhisperSent(DateTime Time, string To, string Message) : LogEvent(Time);
public sealed record PlayerJoined(DateTime Time, string Player) : LogEvent(Time);
public sealed record PlayerLeft(DateTime Time, string Player) : LogEvent(Time);
public sealed record AreaEntered(DateTime Time, string Name) : LogEvent(Time);
public sealed record AreaGenerated(DateTime Time, int Level, string Code) : LogEvent(Time);
public sealed record TradeAccepted(DateTime Time) : LogEvent(Time);
public sealed record TradeCancelled(DateTime Time) : LogEvent(Time);
public sealed record AfkChanged(DateTime Time, bool On) : LogEvent(Time);

/// <summary>
/// Pure Client.txt line parser (English client). Everything is anchored on the
/// <c>[LEVEL Client PID]</c> tag rather than column counts, because the numeric columns between the
/// timestamp and the tag have varied between PoE builds.
/// </summary>
public static partial class ClientLogParser
{
    [GeneratedRegex(@"^(?<ts>\d{4}/\d{2}/\d{2} \d{2}:\d{2}:\d{2})\s+(?:\S+\s+)*?\[(?<lvl>[A-Z]+)\s+Client\s+\d+\]\s?(?<msg>.*)$")]
    private static partial Regex LineRx();
    // Names are a single token; the optional <TAG> is the sender's guild.
    [GeneratedRegex(@"^@(?<dir>From|To)\s+(?:<(?<guild>[^>]*)>\s+)?(?<name>[^\s:<>]+):\s?(?<text>.*)$")]
    private static partial Regex WhisperRx();
    [GeneratedRegex(@"^(?<name>[^\s:<>]+) has (?<what>joined|left) the area\.$")]
    private static partial Regex JoinLeaveRx();
    [GeneratedRegex(@"^You have entered (?<area>.+?)\.?$")]
    private static partial Regex EnteredRx();
    [GeneratedRegex(@"^Generating level (?<lvl>\d+) area ""(?<code>[^""]+)""")]
    private static partial Regex GeneratingRx();

    public static LogEvent? Parse(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var m = LineRx().Match(line.TrimEnd('\r', '\n'));
        if (!m.Success) return null;
        if (!DateTime.TryParseExact(m.Groups["ts"].Value, "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var time))
            return null;
        time = DateTime.SpecifyKind(time, DateTimeKind.Local);
        var msg = m.Groups["msg"].Value;

        if (msg.StartsWith('@'))
        {
            var w = WhisperRx().Match(msg);
            if (!w.Success) return null;
            var name = w.Groups["name"].Value;
            var text = w.Groups["text"].Value.Trim();
            if (w.Groups["dir"].Value == "From")
            {
                var guild = w.Groups["guild"].Success ? w.Groups["guild"].Value : null;
                return new WhisperReceived(time, name, string.IsNullOrEmpty(guild) ? null : guild, text);
            }
            return new WhisperSent(time, name, text);
        }

        // System notices are ": text"; other chat channels (#global, $trade, %party, &guild, local) are ignored.
        if (!msg.StartsWith(": ", StringComparison.Ordinal))
        {
            var g = GeneratingRx().Match(msg);
            return g.Success
                ? new AreaGenerated(time, int.Parse(g.Groups["lvl"].Value, CultureInfo.InvariantCulture), g.Groups["code"].Value)
                : null;
        }
        var sys = msg[2..].Trim();
        if (sys.StartsWith("Trade accepted", StringComparison.Ordinal)) return new TradeAccepted(time);
        if (sys.StartsWith("Trade cancelled", StringComparison.Ordinal)) return new TradeCancelled(time);
        if (sys.StartsWith("AFK mode is now ON", StringComparison.Ordinal)) return new AfkChanged(time, true);
        if (sys.StartsWith("AFK mode is now OFF", StringComparison.Ordinal)) return new AfkChanged(time, false);
        var jl = JoinLeaveRx().Match(sys);
        if (jl.Success)
            return jl.Groups["what"].Value == "joined"
                ? new PlayerJoined(time, jl.Groups["name"].Value)
                : new PlayerLeft(time, jl.Groups["name"].Value);
        var en = EnteredRx().Match(sys);
        return en.Success ? new AreaEntered(time, en.Groups["area"].Value) : null;
    }
}
