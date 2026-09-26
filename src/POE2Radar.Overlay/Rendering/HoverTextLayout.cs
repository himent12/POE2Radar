namespace POE2Radar.Overlay;

/// <summary>Render-thread-owned single-entry cache. Cursor movement does not invalidate text layout.</summary>
internal sealed class HoverTextLayout
{
    internal sealed record Layout(string[] Rows, int MaxLength);
    private string? _text, _detail;
    private int _maxChars;
    private Layout? _layout;

    public Layout Get(string text, string detail, int maxChars)
    {
        maxChars = Math.Max(1, maxChars);
        if (_layout != null && text == _text && detail == _detail && maxChars == _maxChars) return _layout;
        var rows = new List<string>();
        Add(text);
        foreach (var line in detail.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Add(line);
        _text = text; _detail = detail; _maxChars = maxChars;
        return _layout = new(rows.ToArray(), rows.Max(r => r.Length));

        void Add(string line)
        {
            while (line.Length > maxChars)
            {
                var split = line.LastIndexOf(' ', maxChars - 1, maxChars);
                if (split <= 0) split = maxChars;
                rows.Add(line[..split]);
                line = line[split..].TrimStart();
            }
            rows.Add(line);
        }
    }
}
