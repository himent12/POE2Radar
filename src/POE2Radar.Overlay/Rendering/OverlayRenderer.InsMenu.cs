using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Draw;
using NumVec2 = System.Numerics.Vector2;

namespace POE2Radar.Overlay;

/// <summary>
/// Insert-key menu — the "grimoire" (<c>OverlayRenderer.Ui.cs</c>): the game dimmed under a slow sigil, a framed
/// window with a sidebar (spinning logo, rune-marked section list, key reference) and a main area with a Cinzel
/// heading, a status pill, and cards of plain rows — diamond switches, hairline sliders, segmented choices and
/// keycaps. Laid out at a fixed design size and scaled to the window (<see cref="BeginScaled"/>).
///
/// Click grammar (<see cref="_legendRowRects"/> → <c>RadarApp.OnOverlayClick</c>):
/// <c>ins:panel</c> (swallow) · <c>ins:close</c> · <c>ins:tab:N</c> · <c>ins:toggle:&lt;module&gt;</c> ·
/// <c>ins:flag:&lt;setting&gt;</c> · <c>ins:slider:&lt;key&gt;</c> (value from click X) ·
/// <c>ins:adj:&lt;key&gt;:&lt;delta&gt;</c> · <c>ins:set:&lt;key&gt;:&lt;value&gt;</c> ·
/// <c>ins:key:&lt;key&gt;:&lt;±1&gt;</c> (cycle a key binding) · <c>ins:open:dashboard</c> ·
/// <c>ins:buff:flip|trig:i</c> · <c>ins:buff:key:i:&lt;±1&gt;</c> · <c>ins:buff:add:&lt;buff name&gt;</c> ·
/// <c>ins:cmd:flip:i</c>. Trade-panel buttons use <c>trade:&lt;id&gt;:&lt;action&gt;</c> (OverlayRenderer.Trade).
/// </summary>
public sealed partial class OverlayRenderer
{
    private const float MW = 1240f, MH = 780f, SideW = 260f;

    private static readonly (string Name, string Rune, string Runes)[] Tabs =
    {
        ("Overview", "ᛟ", "ᛟᚢᛖᚱᚹᛁᛖᚹ"), ("Flask", "ᚠ", "ᚠᛚᚨᛊᚲ"), ("Macros", "ᛗ", "ᛗᚨᚲᚱᛟᛊ"),
        ("Trade", "ᛏ", "ᛏᚱᚨᛞᛖ"), ("Radar", "ᚱ", "ᚱᚨᛞᚨᚱ"),
    };

    /// <summary>Number of INSERT-menu sections (RadarApp clamps tab clicks to this).</summary>
    public const int InsTabCount = 5;

    /// <summary>Index of the Trade section (RadarApp only builds the tracker stats while it is showing).</summary>
    public const int InsTradeTab = 3;

    /// <summary>Headless draw of just the INSERT menu onto the window surface (preview/tests).</summary>
    internal void RenderInsMenuPreview(RenderContext ctx, Color4 background)
    {
        EnsureResources();
        var rt = _window.RenderTarget;
        rt.BeginDraw();
        rt.Clear(background);
        _legendRowRects.Clear();
        DrawInsMenu(rt, ctx);
        rt.EndDraw();
    }

    // Entrance timing: the section's cards rise in again whenever the menu opens or the tab changes.
    private float _insLastDrawAt = -1e6f, _insTabAt;
    private int _insShownTab = -1;
    private float _insT;

    private void DrawInsMenu(DrawTarget rt, RenderContext ctx)
    {
        if (ctx.InsMenu is not { } m) return;
        EnsureInsResources();
        BeginUiFrame(ctx);
        var tab = Math.Clamp(m.Tab, 0, Tabs.Length - 1);
        if (Now - _insLastDrawAt > 0.3f || tab != _insShownTab) { _insTabAt = Now; _insShownTab = tab; }
        _insLastDrawAt = Now;
        _insT = Since(_insTabAt);

        Backdrop(rt, ctx.WindowWidth, ctx.WindowHeight);
        var s = FitScale(ctx.WindowWidth, ctx.WindowHeight, MW, MH);
        BeginScaled(rt, MathF.Round((ctx.WindowWidth - MW * s) * 0.5f), MathF.Round((ctx.WindowHeight - MH * s) * 0.5f), s);

        var panel = new RawRectF(0f, 0f, MW, MH);
        Frame(rt, panel);
        _legendRowRects.Add((panel, "ins:panel"));
        DrawSidebar(rt, ctx, m, tab);

        // ── Header: heading + its runes, status pill, close ──
        const float x1 = SideW + 40f, x2 = MW - 40f, hcy = 32f + 22f;
        var (name, _, runes) = Tabs[tab];
        var head = BeginRise(rt, _insT, 0f);
        var hx = TT(rt, name, _tfH1!, x1, hcy, UWhite, 0.1f);
        TT(rt, runes, _tfRune!, hx + 16f, hcy, UFaint, 0.4f);
        EndRise(rt, head);
        CloseButton(rt, x2 - 22f, hcy, "ins:close", 44f);
        var pill = string.IsNullOrEmpty(m.CharName) ? "In game" : $"{m.CharName} · level {ctx.CharLevel}";
        var pw = rt.MeasureText(pill, _tfMedium!) + 24f + 15f;
        var pr = new RawRectF(x2 - 44f - 16f - pw, hcy - 16f, x2 - 44f - 16f, hcy + 16f);
        rt.DrawRectangle(pr, B(ULine), 1f);
        var dot = new NumVec2(pr.Left + 15.5f, hcy);
        var p = Pulse();
        rt.FillRadialGlow(dot, 8f, A(UOn, 0.35f + 0.3f * p));
        rt.FillEllipse(new Ellipse(dot, 3.5f, 3.5f), B(A(UOn, 0.55f + 0.45f * p)));
        T(rt, pill, _tfMedium!, pr.Left + 27f, hcy, ULabel);

        // ── Content ──
        var body = new RawRectF(x1, 100f, x2, MH - 32f);
        switch (tab)
        {
            case 0: DrawOverview(rt, ctx, m, body); break;
            case 1: DrawFlaskTab(rt, ctx, m.Settings, body); break;
            case 2: DrawMacrosTab(rt, m, body); break;
            case 3: DrawTradeTab(rt, m, body); break;
            default: DrawRadarTab(rt, m.Settings, body); break;
        }
        EndScaled(rt);
    }

    private void DrawSidebar(DrawTarget rt, RenderContext ctx, InsMenuData m, int tab)
    {
        VLine(rt, SideW, 1f, MH - 1f, USide);
        const float cx = SideW * 0.5f;
        Sigil(rt, cx, 32f + 42f, 84f);
        // Wordmark with the design's slow flicker.
        var f = Now % 5f / 5f;
        var flick = f switch { < 0.45f => 1f - f / 0.45f * 0.18f, < 0.5f => 0.82f - (f - 0.45f) / 0.05f * 0.22f,
            < 0.55f => 0.6f + (f - 0.5f) / 0.05f * 0.3f, _ => 0.9f + (f - 0.55f) / 0.45f * 0.1f };
        TTC(rt, "POE2 RADAR", _tfWordmark!, cx, 32f + 84f + 24f, A(UWhite, flick), 0.2f);
        TTC(rt, "ᚱᚨᛞᚨᚱ", _tfRuneSm!, cx, 32f + 84f + 52f, UDim, 0.5f);

        var y = 214f;
        for (var i = 0; i < Tabs.Length; i++)
        {
            var r = new RawRectF(22f, y, SideW - 22f, y + 52f);
            var active = i == tab;
            var hover = active ? 0f : HoverWash(rt, r, "ins:tab:" + i, 0.05f);
            var sel = Anim("sel:ins:tab:" + i, active, 0.2f);
            if (sel > 0.01f)
            {
                rt.FillRectangle(r, B(A(UWhite, 0.07f * sel)));
                rt.FillRectangle(new RawRectF(r.Left, r.Top, r.Left + 2f, r.Bottom), B(A(UWhite, sel)));
            }
            var lit = MathF.Max(sel, hover);
            var rc = new NumVec2(r.Left + 12f + 15f, y + 26f);
            rt.DrawDashedCircle(rc, 13f, Mix(UEdge, UWhite, sel), 1f, 2f, 4f);
            TC(rt, Tabs[i].Rune, _tfRune!, rc.X, rc.Y, Mix(UGrey, UWhite, lit));
            TT(rt, Tabs[i].Name.ToUpperInvariant(), _tfNav!, r.Left + 54f, y + 26f, Mix(UMuted, UWhite, lit), 0.12f);
            bool? armed = i switch { 1 => ctx.AutoFlask, 2 => m.BuffKeeperArmed, 3 => m.Settings.Trade.Enabled, _ => null };
            if (armed is { } on)
            {
                var d = new NumVec2(r.Right - 16f, y + 26f);
                if (on) { rt.FillRadialGlow(d, 7f, A(UOn, 0.4f)); rt.FillEllipse(new Ellipse(d, 3f, 3f), B(UOn)); }
                else rt.DrawEllipse(new Ellipse(d, 3f, 3f), B(UDim), 1f);
            }
            _legendRowRects.Add((r, "ins:tab:" + i));
            y += 56f;
        }

        // ── Key reference ──
        var keys = new (string What, string Key)[]
        {
            ("Price check", m.Settings.HoverPrice.PriceCheckHotkey), ("Auto-flask on/off", "F8"), ("Route to nearest", "F6"),
            ("Clear routes", "F7"), ("Atlas tile info", "F10"), ("Dashboard", "F12"), ("Close", "Ins"),
        };
        const float rowH = 26f;
        var box = new RawRectF(22f, MH - 32f - (14f * 2f + 22f + keys.Length * rowH), SideW - 22f, MH - 32f);
        rt.DrawRectangle(box, B(USide), 1f);
        Caps(rt, "Invocations", box.Left + 14f, box.Top + 14f + 6f, UDim, 0.2f);
        var ky = box.Top + 14f + 22f + rowH * 0.5f;
        foreach (var (what, key) in keys)
        {
            T(rt, what, _tfMedium!, box.Left + 14f, ky, ULabel);
            Keycap(rt, key, box.Right - 14f - KeycapWidth(rt, key), ky);
            ky += rowH;
        }
    }

    private const float ColGap = 20f;

    // ═════════════════════════════ Overview ═════════════════════════════
    private void DrawOverview(DrawTarget rt, RenderContext ctx, InsMenuData m, RawRectF b)
    {
        var s = m.Settings;
        var cw = (b.Width - 32f) / 3f;
        var esOff = s.LifeFlaskMode == "Health";
        var g = BeginRise(rt, _insT, 0.05f);
        VitalCard(rt, new RawRectF(b.Left, b.Top, b.Left + cw, b.Top + 150f), "Life", ctx.HpPct, ULife,
            esOff || s.LifeFlaskMode == "Either" ? s.LifeThresholdPct : 0f, ctx.AutoFlask);
        EndRise(rt, g);
        g = BeginRise(rt, _insT, 0.12f);
        VitalCard(rt, new RawRectF(b.Left + cw + 16f, b.Top, b.Left + cw * 2f + 16f, b.Top + 150f), "Energy shield", ctx.EsPct, UTeal,
            esOff ? 0f : s.EsThresholdPct, ctx.AutoFlask);
        EndRise(rt, g);
        g = BeginRise(rt, _insT, 0.19f);
        VitalCard(rt, new RawRectF(b.Right - cw, b.Top, b.Right, b.Top + 150f), "Mana", ctx.ManaPct, UBlue,
            s.ManaThresholdPct, ctx.AutoFlask);
        EndRise(rt, g);

        // ── Automation rows ──
        var y = b.Top + 150f + 24f;
        g = BeginRise(rt, _insT, 0.22f);
        Caps(rt, "Automation", b.Left, y + 8f);
        var link = "Open dashboard →";
        var lw = rt.MeasureText(link, _tfMedium!);
        T(rt, link, _tfMedium!, b.Right - 6f - lw, y + 8f, UWhite);
        _legendRowRects.Add((new RawRectF(b.Right - 12f - lw, y - 8f, b.Right, y + 24f), "ins:open:dashboard"));
        EndRise(rt, g);
        y += 22f;

        var bk = s.BuffKeeper;
        var buffNote = m.Status.FirstOrDefault(c => c.Name == "Buffs").Note;
        var trade = m.Status.FirstOrDefault(c => c.Name == "Trade");
        var cmds = s.Commands.Commands.Count(c => c.Enabled && c.Hotkey.Length > 0);
        y = AutoRow(rt, b, y, 0, "Auto-flask", ctx.AutoFlask, ctx.FlaskNote, "F8", "ins:toggle:flask");
        y = AutoRow(rt, b, y, 1, "Buff keeper", m.BuffKeeperArmed, buffNote, bk.ToggleHotkey, "ins:toggle:buffs");
        y = AutoRow(rt, b, y, 2, "Trade assistant", trade.Name is not null && trade.On, trade.Name is null ? "reads buyer whispers" : trade.Note, null, null);
        y = AutoRow(rt, b, y, 3, "Chat commands", cmds > 0, string.IsNullOrEmpty(m.ChatNote) ? $"{cmds} on keys" : $"last: {m.ChatNote}", null, null,
            cmds > 0 ? $"{cmds} ON KEYS" : "NONE");
        g = BeginRise(rt, _insT, 0.57f);
        HLine(rt, b.Left, b.Right, y, URule);
        EndRise(rt, g);

        // ── Performance strip ──
        var py = b.Bottom - 34f;
        var stats = new (string Label, string Value, Color4 Col)[]
        {
            ("Frame rate", $"{m.Fps} fps", UWhite), ("Frame time", $"{m.RenderMs:0.0} ms", UWhite),
            ("World scan", $"{m.WorldMs:0.0} ms", m.WorldMs > 25f ? URed : UWhite), ("Version", m.Version, UMuted),
        };
        g = BeginRise(rt, _insT, 0.6f);
        HLine(rt, b.Left, b.Right, py - 30f, URule);
        var sw = b.Width / stats.Length;
        for (var i = 0; i < stats.Length; i++)
        {
            var sx = b.Left + sw * i;
            Caps(rt, stats[i].Label, sx, py - 8f, UDim);
            T(rt, stats[i].Value, _tfValue!, sx, py + 14f, stats[i].Col);
        }
        EndRise(rt, g);
    }

    /// <summary>A vitals card in the shape of the design's currency cards: orb, caps label, big Cinzel percentage,
    /// a meter with the flask threshold marked, and the auto-flask state underneath.</summary>
    private void VitalCard(DrawTarget rt, RawRectF r, string label, float pct, Color4 col, float threshold, bool flask)
    {
        CardBox(rt, r);
        const float pad = 18f;
        Orb(rt, r.Left + pad + 24f, r.Top + pad + 24f, 15f, col);
        var tx = r.Left + pad + 48f + 12f;
        Caps(rt, label, tx, r.Top + pad + 10f);
        var num = $"{pct:0}";
        T(rt, num, _tfStat!, tx, r.Top + pad + 36f, UWhite);
        T(rt, "%", _tfStatUnit!, tx + rt.MeasureText(num, _tfStat!) + 5f, r.Top + pad + 38f, UMuted);

        var my = r.Top + 96f;
        var mx1 = r.Left + pad; var mx2 = r.Right - pad;
        rt.FillRectangle(new RawRectF(mx1, my - 1f, mx2, my + 1f), B(ULine));
        var fx = mx1 + (mx2 - mx1) * Math.Clamp(pct, 0f, 100f) / 100f;
        if (fx > mx1)
        {
            rt.FillRectangle(new RawRectF(mx1, my - 1.5f, fx, my + 1.5f), B(A(col, 0.95f)));
            rt.FillRadialGlow(new NumVec2(fx, my), 10f, A(col, 0.5f));
        }
        if (threshold > 0f)
        {
            var tpx = mx1 + (mx2 - mx1) * Math.Clamp(threshold, 0f, 100f) / 100f;
            VLine(rt, tpx, my - 7f, my + 7f, A(UWhite, 0.7f));
            Diamond(rt, tpx, my - 9f, 2.6f, UWhite);
        }
        var (foot, fc) = threshold <= 0f ? ("not watched by the flask", UDim)
            : flask ? ($"▲ flask below {threshold:0}%", UOn) : ($"flask off · would drink below {threshold:0}%", UMuted);
        T(rt, foot, _tfSmall!, r.Left + pad, r.Bottom - 22f, fc);
    }

    /// <summary>One automation row (the design's "recently appraised" row): diamond, name, a state chip, a short
    /// note, and — for the armable modules — the kill-switch key and a switch.</summary>
    private float AutoRow(DrawTarget rt, RawRectF b, float y, int index, string name, bool on, string? note, string? hotkey, string? action, string? chip = null)
    {
        const float h = 48f;
        var cy = y + h * 0.5f;
        var g = BeginRise(rt, _insT, 0.25f + index * 0.08f);
        HoverWash(rt, new RawRectF(b.Left, y, b.Right, y + h), "auto:" + name);
        HLine(rt, b.Left, b.Right, y, URule);
        DiamondMark(rt, b.Left + 15f, cy, on ? UWhite : UDim);
        T(rt, name, _tfName!, b.Left + 36f, cy, on ? UText : UMuted);
        var cx = b.Left + 230f;
        var ce = Chip(rt, chip ?? (on ? "Armed" : "Off"), cx, cy, on ? ChipStyle.Live : ChipStyle.Dim);
        var right = b.Right - 8f;
        if (action is not null)
        {
            Switch(rt, right, cy, on, action);
            right -= SwitchW + 14f;
            _legendRowRects.Add((new RawRectF(b.Left, y, b.Right, y + h), action));
        }
        if (hotkey is { Length: > 0 })
        {
            var kx = right - KeycapWidth(rt, hotkey);
            Keycap(rt, hotkey, kx, cy);
            right = kx - 14f;
        }
        if (!string.IsNullOrEmpty(note)) T(rt, Fit(rt, note, _tfSmall!, right - ce - 24f), _tfSmall!, ce + 16f, cy, UDim);
        EndRise(rt, g);
        return y + h;
    }

    // ═════════════════════════════ Flask ═════════════════════════════
    private void DrawFlaskTab(DrawTarget rt, RenderContext ctx, RadarSettings s, RawRectF b)
    {
        var colW = (b.Width - ColGap) * 0.5f;
        var lx = b.Left; var rx = b.Left + colW + ColGap;
        const float slider = 58f;
        var cardH = CardPad * 2f + CardHead + Row * 3f + slider * 3f;

        var iw = colW - CardPad * 2f;
        var g = BeginRise(rt, _insT, 0f);
        var y = Card(rt, new RawRectF(lx, b.Top, lx + colW, b.Top + cardH), "Life flask");
        var x = lx + CardPad;
        y = Toggle(rt, x, y, iw, "Auto-flask", string.IsNullOrEmpty(ctx.FlaskNote) ? null : ctx.FlaskNote, ctx.AutoFlask, "ins:toggle:flask", "F8");
        y = Choice(rt, x, y, iw, "Drink when low on", null, s.LifeFlaskMode ?? "Health", "lifeFlaskMode",
            new[] { ("Health", "Life"), ("EnergyShield", "Shield"), ("Either", "Either") });
        y = KeyPicker(rt, x, y, iw, "Flask key", null, s.LifeKey, "lifeKey");
        y = Slider(rt, x, y, iw, "Life below", null, s.LifeThresholdPct, "lifeThresholdPct");
        y = Slider(rt, x, y, iw, "Energy shield below", null, s.EsThresholdPct, "esThresholdPct");
        Slider(rt, x, y, iw, "Wait between drinks", null, s.LifeCooldownMs, "lifeCooldownMs");
        EndRise(rt, g);

        g = BeginRise(rt, _insT, 0.08f);
        var ry = Card(rt, new RawRectF(rx, b.Top, rx + colW, b.Top + cardH), "Mana flask");
        x = rx + CardPad;
        ry = KeyPicker(rt, x, ry, iw, "Flask key", null, s.ManaKey, "manaKey");
        ry = Slider(rt, x, ry, iw, "Mana below", null, s.ManaThresholdPct, "manaThresholdPct");
        ry = Slider(rt, x, ry, iw, "Wait between drinks", null, s.ManaCooldownMs, "manaCooldownMs");
        ry += 18f;
        Caps(rt, "Right now", x, ry);
        ry += 20f;
        ry = Meter(rt, x, ry, iw, "Life", ctx.HpPct, s.LifeThresholdPct, ULife);
        ry = Meter(rt, x, ry, iw, "Energy shield", ctx.EsPct, s.LifeFlaskMode == "Health" ? 0f : s.EsThresholdPct, UTeal);
        Meter(rt, x, ry, iw, "Mana", ctx.ManaPct, s.ManaThresholdPct, UBlue);
        EndRise(rt, g);

        TC(rt, "Only drinks while PoE2 is in front and you're alive.", _tfSmall!, b.Left + b.Width * 0.5f, b.Top + cardH + 26f, UDim);
    }

    /// <summary>Compact vital meter: label + value over a thin bar with the threshold marked.</summary>
    private float Meter(DrawTarget rt, float x, float y, float w, string label, float pct, float threshold, Color4 col)
    {
        T(rt, label, _tfMedium!, x, y + 8f, ULabel);
        TR(rt, $"{pct:0}%", _tfValue!, x + w, y + 8f, UWhite);
        var my = y + 24f;
        rt.FillRectangle(new RawRectF(x, my - 1f, x + w, my + 1f), B(ULine));
        rt.FillRectangle(new RawRectF(x, my - 1.5f, x + w * Math.Clamp(pct, 0f, 100f) / 100f, my + 1.5f), B(col));
        if (threshold > 0f) Diamond(rt, x + w * Math.Clamp(threshold, 0f, 100f) / 100f, my, 3f, UWhite);
        return y + 36f;
    }

    // ═════════════════════════════ Macros ═════════════════════════════
    private void DrawMacrosTab(DrawTarget rt, InsMenuData m, RawRectF b)
    {
        var s = m.Settings;
        var bk = s.BuffKeeper;
        var leftW = MathF.Round((b.Width - ColGap) * 0.56f);
        var lx = b.Left; var rx = b.Left + leftW + ColGap; var rw = b.Right - rx;

        var g = BeginRise(rt, _insT, 0f);
        var y = Card(rt, new RawRectF(lx, b.Top, lx + leftW, b.Bottom), "Buff keeper");
        var x = lx + CardPad; var iw = leftW - CardPad * 2f;
        var armNote = m.Status.FirstOrDefault(c => c.Name == "Buffs").Note;
        y = Toggle(rt, x, y, iw, "Recast buffs when they run out", string.IsNullOrEmpty(armNote) ? null : armNote,
            m.BuffKeeperArmed, "ins:toggle:buffs", bk.ToggleHotkey);
        var shown = 0;
        for (var i = 0; i < bk.Rules.Count && y + RuleH <= b.Bottom - 44f; i++, shown++)
        {
            var note = m.BuffNotes is { } notes && i < notes.Count ? notes[i] : "";
            y = BuffRuleRow(rt, x, y, iw, i, bk.Rules[i], note);
        }
        if (bk.Rules.Count == 0) T(rt, "No rules yet — pick a buff on the right.", _tfBody!, x, y + 22f, UDim);
        else if (shown < bk.Rules.Count) T(rt, $"+{bk.Rules.Count - shown} more on the dashboard", _tfSmall!, x, y + 16f, UDim);
        T(rt, "Buff names, timers and enemy checks are edited on the dashboard.", _tfSmall!, x, b.Bottom - CardPad - 4f, UDim);
        EndRise(rt, g);

        var buffH = CardPad * 2f + CardHead + 6 * 34f;
        g = BeginRise(rt, _insT, 0.08f);
        var by = Card(rt, new RawRectF(rx, b.Top, rx + rw, b.Top + buffH), "On you now");
        var bx = rx + CardPad; var bw = rw - CardPad * 2f;
        if (m.Buffs is null) T(rt, "Can't read buffs right now.", _tfSmall!, bx, by + 16f, UDim);
        else if (m.Buffs.Count == 0) T(rt, "No buffs active.", _tfSmall!, bx, by + 16f, UDim);
        else
            foreach (var bf in m.Buffs.Take(6))
            {
                BuffRow(rt, bx, by, bw, bf);
                by += 34f;
            }
        EndRise(rt, g);

        g = BeginRise(rt, _insT, 0.16f);
        var cy = Card(rt, new RawRectF(rx, b.Top + buffH + ColGap, rx + rw, b.Bottom), "Chat commands");
        var cmds = s.Commands.Commands;
        for (var i = 0; i < cmds.Count && cy + 42f <= b.Bottom - CardPad + 4f; i++)
        {
            var c = cmds[i];
            var r = new RawRectF(bx, cy, bx + bw, cy + 42f);
            HoverWash(rt, new RawRectF(r.Left - 8f, r.Top, r.Right + 8f, r.Bottom), "ins:cmd:flip:" + i);
            var ccy = cy + 21f;
            var kx = c.Hotkey.Length > 0 ? Keycap(rt, c.Hotkey, bx, ccy) + 12f : bx;
            T(rt, Fit(rt, c.Name.Length > 0 ? c.Name : c.Text, _tfBody!, r.Right - kx - SwitchW - 12f), _tfBody!, kx, ccy, c.Enabled ? ULabel : UDim);
            Switch(rt, r.Right, ccy, c.Enabled, "ins:cmd:flip:" + i);
            HLine(rt, bx, bx + bw, cy + 41.5f, URule);
            _legendRowRects.Add((r, "ins:cmd:flip:" + i));
            cy += 42f;
        }
        EndRise(rt, g);
    }

    private void BuffRow(DrawTarget rt, float x, float y, float w, Core.Game.Poe2Live.BuffInfo b)
    {
        var cy = y + 14f;
        DiamondMark(rt, x + 6f, cy, UMuted);
        T(rt, Fit(rt, b.Name, _tfMedium!, w - 130f), _tfMedium!, x + 20f, cy, UText);
        var t = b.IsInfinite ? "—" : $"{b.TimeLeft:0.0}s";
        if (b.Charges > 1) t = $"{b.Charges}×  " + t;
        TR(rt, t, _tfValue!, x + w - 38f, cy, UWhite);
        rt.FillRectangle(new RawRectF(x + 20f, y + 28f, x + w - 38f, y + 29f), B(URule));
        if (!b.IsInfinite && b.TotalTime > 0f)
        {
            var f = Math.Clamp(b.TimeLeft / b.TotalTime, 0f, 1f);
            rt.FillRectangle(new RawRectF(x + 20f, y + 28f, x + 20f + (w - 58f) * f, y + 29.5f), B(A(UWhite, 0.8f)));
        }
        var add = new RawRectF(x + w - 26f, cy - 12f, x + w, cy + 12f);
        rt.DrawRectangle(add, B(UEdge), 1f);
        TC(rt, "+", _tfBold!, (add.Left + add.Right) * 0.5f, cy - 0.5f, UWhite);
        _legendRowRects.Add((add, "ins:buff:add:" + b.Name));
    }

    private const float RuleH = 78f;

    /// <summary>One buff rule over two lines: name + buff id + switch; key stepper, trigger and live note.</summary>
    private float BuffRuleRow(DrawTarget rt, float x, float y, float w, int i, Input.BuffRule r, string note)
    {
        var cy = y + 20f;
        var title = Fit(rt, r.Name.Length > 0 ? r.Name : r.BuffName.Length > 0 ? r.BuffName : "Rule " + (i + 1), _tfName!, w * 0.5f);
        DiamondMark(rt, x + 6f, cy, r.Enabled ? UWhite : UDim);
        T(rt, title, _tfName!, x + 22f, cy, r.Enabled ? UText : UDim);
        if (r.BuffName.Length > 0 && !title.StartsWith(r.BuffName, StringComparison.Ordinal))
            T(rt, Fit(rt, r.BuffName, _tfSmall!, w * 0.3f), _tfSmall!, x + 22f + rt.MeasureText(title, _tfName!) + 10f, cy, UDim);
        Switch(rt, x + w, cy, r.Enabled, $"ins:buff:flip:{i}");
        _legendRowRects.Add((new RawRectF(x + w - SwitchW - 4f, cy - 16f, x + w + 4f, cy + 16f), $"ins:buff:flip:{i}"));

        var cy2 = y + 54f;
        var left = KeyStepperLeft(rt, x, cy2, r.Key, $"ins:buff:key:{i}:-1", $"ins:buff:key:{i}:1");
        var trig = r.Trigger switch { "Expiring" => "Before it ends", "Interval" => "On a timer", _ => "When missing" };
        var tw = TrackW(rt, trig.ToUpperInvariant(), _tfSeg!, 0.12f) + 24f;
        var tr = new RawRectF(left + 12f, cy2 - 14f, left + 12f + tw, cy2 + 14f);
        rt.DrawRectangle(tr, B(UEdge), 1f);
        TTC(rt, trig.ToUpperInvariant(), _tfSeg!, (tr.Left + tr.Right) * 0.5f, cy2, ULabel, 0.12f);
        _legendRowRects.Add((tr, $"ins:buff:trig:{i}"));
        var hot = note.Contains("recast", StringComparison.OrdinalIgnoreCase);
        TR(rt, Fit(rt, note, _tfSmall!, x + w - tr.Right - 16f), _tfSmall!, x + w, cy2, hot ? UWhite : UDim);
        HLine(rt, x, x + w, y + RuleH - 0.5f, URule);
        return y + RuleH;
    }

    /// <summary>Left-aligned "◂ [key] ▸" starting at <paramref name="x"/>. Returns its right edge.</summary>
    private float KeyStepperLeft(DrawTarget rt, float x, float cy, int vk, string prevAction, string nextAction)
    {
        var total = 22f + 4f + KeycapWidth(rt, KeyLabel(vk), big: true) + 4f + 18f;
        KeyStepper(rt, x + total, cy, vk, prevAction, nextAction);
        return x + total;
    }

    // ═════════════════════════════ Trade ═════════════════════════════
    private void DrawTradeTab(DrawTarget rt, InsMenuData m, RawRectF b)
    {
        var t = m.Settings.Trade;
        var info = m.Trade;

        // Earnings: three stat cards like the design's currency cards.
        var labels = new[] { "Today", "Last 7 days", "All time" };
        var cw = (b.Width - 32f) / 3f;
        for (var i = 0; i < 3; i++)
        {
            var r = new RawRectF(b.Left + (cw + 16f) * i, b.Top, b.Left + (cw + 16f) * i + cw, b.Top + 118f);
            var sg = BeginRise(rt, _insT, 0.05f + i * 0.07f);
            CardBox(rt, r);
            var (profit, detail) = TradeStat(info, labels[i]);
            Caps(rt, labels[i], r.Left + 18f, r.Top + 26f);
            var (num, unit) = SplitAmount(profit);
            T(rt, num, _tfStat!, r.Left + 18f, r.Top + 60f, profit.StartsWith('−') ? URed : UWhite);
            if (unit.Length > 0) T(rt, unit, _tfStatUnit!, r.Left + 18f + rt.MeasureText(num, _tfStat!) + 6f, r.Top + 62f, UMuted);
            T(rt, Fit(rt, detail, _tfSmall!, cw - 36f), _tfSmall!, r.Left + 18f, r.Bottom - 22f, UMuted);
            EndRise(rt, sg);
        }

        var colW = (b.Width - ColGap) * 0.5f;
        var lx = b.Left; var rx = b.Left + colW + ColGap;
        var y0 = b.Top + 118f + ColGap;
        var iw = colW - CardPad * 2f;
        var g = BeginRise(rt, _insT, 0.26f);
        var y = Card(rt, new RawRectF(lx, y0, lx + colW, b.Bottom), "Trade assistant");
        var x = lx + CardPad;
        y = Toggle(rt, x, y, iw, "Read buyer whispers", info is null ? null : info.LogStatus, t.Enabled, "ins:flag:tradeEnabled");
        y = Toggle(rt, x, y, iw, "Show the trade panel", null, t.ShowPanel, "ins:flag:tradeShowPanel");
        y = Toggle(rt, x, y, iw, "Keep a trade history", null, t.TrackHistory, "ins:flag:tradeTrackHistory");
        y = Toggle(rt, x, y, iw, "Kick after saying thanks", null, t.KickAfterTrade, "ins:flag:tradeKickAfter");
        var half = (iw - 24f) * 0.5f;
        Slider(rt, x, y, half, "Panel left", null, t.PanelX, "tradePanelX");
        Slider(rt, x + half + 24f, y, half, "Panel top", null, t.PanelY, "tradePanelY");
        EndRise(rt, g);

        g = BeginRise(rt, _insT, 0.34f);
        var ry = Card(rt, new RawRectF(rx, y0, rx + colW, b.Bottom), "Recent trades");
        x = rx + CardPad;
        if (info is null || info.Recent.Count == 0) T(rt, "Nothing traded yet.", _tfBody!, x, ry + 16f, UDim);
        else
            foreach (var line in info.Recent)
            {
                if (ry + 40f > b.Bottom - CardPad) break;
                var sold = line.StartsWith('+');
                HoverWash(rt, new RawRectF(x - 8f, ry, x + iw + 8f, ry + 40f), "recent:" + line);
                DiamondMark(rt, x + 6f, ry + 20f, sold ? UOn : UMuted);
                T(rt, Fit(rt, line, _tfMedium!, iw - 24f), _tfMedium!, x + 22f, ry + 20f, sold ? UText : UMuted);
                HLine(rt, x, x + iw, ry + 39.5f, URule);
                ry += 40f;
            }
        EndRise(rt, g);
    }

    /// <summary>"+2.4 div" → ("+2.4", "div"); a bare number or "—" has no unit.</summary>
    private static (string Num, string Unit) SplitAmount(string s)
    {
        var i = s.LastIndexOf(' ');
        return i <= 0 ? (s, "") : (s[..i], s[(i + 1)..]);
    }

    /// <summary>Split a tracker line ("3 sold · 1 bought · +2.4 div") into the headline profit + the rest.</summary>
    private static (string Profit, string Detail) TradeStat(TradeMenuInfo? info, string label)
    {
        if (info is null) return ("—", "no trades yet");
        foreach (var (l, v) in info.Stats)
        {
            if (l != label) continue;
            var cut = v.LastIndexOf('·');
            return cut < 0 ? (v, "") : (v[(cut + 1)..].Trim(), v[..cut].Trim().TrimEnd('·').Trim());
        }
        return ("—", "no trades yet");
    }

    // ═════════════════════════════ Radar ═════════════════════════════
    private void DrawRadarTab(DrawTarget rt, RadarSettings s, RawRectF b)
    {
        var colW = (b.Width - ColGap) * 0.5f;
        var lx = b.Left; var rx = b.Left + colW + ColGap;
        var iw = colW - CardPad * 2f;
        var g = BeginRise(rt, _insT, 0f);
        var y = Card(rt, new RawRectF(lx, b.Top, lx + colW, b.Top + CardPad * 2f + CardHead + Row * 5f + 58f * 2f), "Map");
        var x = lx + CardPad;
        y = Toggle(rt, x, y, iw, "Monsters", null, s.ShowMonsters, "ins:flag:showMonsters");
        y = Toggle(rt, x, y, iw, "Terrain", null, s.ShowTerrain, "ins:flag:showTerrain");
        y = Toggle(rt, x, y, iw, "Your position", null, s.ShowPlayerBlip, "ins:flag:showPlayerBlip");
        y = Toggle(rt, x, y, iw, "Route lines", null, s.ShowPath, "ins:flag:showPath");
        y = Toggle(rt, x, y, iw, "Show while PoE2 is in the background", null, s.AlwaysShowOverlay, "ins:flag:alwaysShowOverlay");
        y = Slider(rt, x, y, iw, "Map scale", null, s.ScaleMul, "scaleMul");
        Slider(rt, x, y, iw, "Frame cap", null, s.FpsCap, "fpsCap");
        EndRise(rt, g);

        var barsH = CardPad * 2f + CardHead + Row * 4f;
        g = BeginRise(rt, _insT, 0.08f);
        var ry = Card(rt, new RawRectF(rx, b.Top, rx + colW, b.Top + barsH), "Health bars");
        x = rx + CardPad;
        ry = Toggle(rt, x, ry, iw, "Normal monsters", null, s.HpBarNormal, "ins:flag:hpBarNormal");
        ry = Toggle(rt, x, ry, iw, "Magic monsters", null, s.HpBarMagic, "ins:flag:hpBarMagic");
        ry = Toggle(rt, x, ry, iw, "Rare monsters", null, s.HpBarRare, "ins:flag:hpBarRare");
        Toggle(rt, x, ry, iw, "Unique monsters", null, s.HpBarUnique, "ins:flag:hpBarUnique");
        EndRise(rt, g);

        var valuesTop = b.Top + barsH + ColGap;
        var valuesH = CardPad * 2f + CardHead + Row * 2f;
        g = BeginRise(rt, _insT, 0.16f);
        var vy = Card(rt, new RawRectF(rx, valuesTop, rx + colW, valuesTop + valuesH), "Item values");
        vy = Toggle(rt, x, vy, iw, "Value of loot on the ground", null, s.GroundItems.Enabled, "ins:flag:groundItems");
        Toggle(rt, x, vy, iw, "Value under hovered items", null, s.HoverPrice.Enabled, "ins:flag:hoverPrice");
        EndRise(rt, g);

        var uiTop = valuesTop + valuesH + ColGap;
        g = BeginRise(rt, _insT, 0.24f);
        var uy = Card(rt, new RawRectF(rx, uiTop, rx + colW, uiTop + CardPad * 2f + CardHead + Row), "Interface");
        Toggle(rt, x, uy, iw, "Reduce motion", "no spinning sigils, motes or entrance animations", s.ReduceMotion, "ins:flag:reduceMotion");
        EndRise(rt, g);
    }
}
