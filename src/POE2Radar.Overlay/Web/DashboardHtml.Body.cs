namespace POE2Radar.Overlay.Web;

/// <summary>Dashboard page — HTML body: left nav rail + hash-routed pages (Overview, Trade, Macros, Radar,
/// Atlas, Item Value, Settings). Element ids / data-attributes are the contract with the scripts.</summary>
internal static partial class DashboardHtml
{
    private const string Body = """
<body>
<svg width="0" height="0" style="position:absolute" aria-hidden="true">
  <defs>
    <symbol id="i-overview" viewBox="0 0 24 24"><rect x="3" y="3" width="7" height="9" rx="1.5"/><rect x="14" y="3" width="7" height="5" rx="1.5"/><rect x="14" y="12" width="7" height="9" rx="1.5"/><rect x="3" y="16" width="7" height="5" rx="1.5"/></symbol>
    <symbol id="i-trade" viewBox="0 0 24 24"><path d="M4 8h15"/><path d="M15 4l4 4-4 4"/><path d="M20 16H5"/><path d="M9 12l-4 4 4 4"/></symbol>
    <symbol id="i-macros" viewBox="0 0 24 24"><rect x="2.5" y="6" width="19" height="12" rx="2"/><path d="M6.5 10h1M10.5 10h1M14.5 10h1M18 10h-.5M7 14h10"/></symbol>
    <symbol id="i-radar" viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><circle cx="12" cy="12" r="4.5"/><path d="M12 12l6-6"/><circle cx="16.5" cy="14.5" r=".6"/></symbol>
    <symbol id="i-atlas" viewBox="0 0 24 24"><path d="M9 4L3 6.5v13.5l6-2.5 6 2.5 6-2.5V4l-6 2.5L9 4z"/><path d="M9 4v13.5M15 6.5V20"/></symbol>
    <symbol id="i-value" viewBox="0 0 24 24"><path d="M20.4 13.4l-7 7a2 2 0 01-2.8 0L3 12.8V3h9.8l7.6 7.6a2 2 0 010 2.8z"/><circle cx="7.8" cy="7.8" r="1.4"/></symbol>
    <symbol id="i-settings" viewBox="0 0 24 24"><path d="M4 6h9M17 6h3M4 12h3M11 12h9M4 18h11M19 18h1"/><circle cx="15" cy="6" r="2"/><circle cx="9" cy="12" r="2"/><circle cx="17" cy="18" r="2"/></symbol>
    <symbol id="i-flask" viewBox="0 0 24 24"><path d="M9 3h6M10 3v5.5L5 18a2 2 0 001.8 3h10.4A2 2 0 0019 18l-5-9.5V3"/><path d="M7.5 15h9"/></symbol>
    <symbol id="i-buff" viewBox="0 0 24 24"><path d="M12 3l2.6 5.3 5.9.9-4.3 4.1 1 5.8L12 16.4l-5.2 2.7 1-5.8L3.5 9.2l5.9-.9L12 3z"/></symbol>
    <symbol id="i-chat" viewBox="0 0 24 24"><path d="M20 15a2 2 0 01-2 2H8l-4 4V5a2 2 0 012-2h12a2 2 0 012 2v10z"/></symbol>
    <symbol id="i-link" viewBox="0 0 24 24"><path d="M14 4h6v6"/><path d="M20 4l-9 9"/><path d="M18 14v5a1 1 0 01-1 1H5a1 1 0 01-1-1V7a1 1 0 011-1h5"/></symbol>
  </defs>
</svg>
<div class="app">
  <nav class="rail" aria-label="Sections">
    <div class="brand">
      <svg viewBox="0 0 32 32" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true"><circle cx="16" cy="16" r="12.5"/><circle cx="16" cy="16" r="6.5" opacity=".55"/><path d="M16 16l8.5-8.5" stroke-linecap="round"/><circle cx="21" cy="19.5" r="1.6" fill="currentColor" stroke="none"/></svg>
      <div><b>POE2 Radar</b><small id="verTxt">&nbsp;</small></div>
    </div>
    <a class="nav" href="#overview" data-page="overview"><svg><use href="#i-overview"/></svg>Overview</a>
    <a class="nav" href="#trade" data-page="trade"><svg><use href="#i-trade"/></svg>Trade<span class="nbadge" id="navTradeBadge" hidden>0</span></a>
    <a class="nav" href="#macros" data-page="macros"><svg><use href="#i-macros"/></svg>Macros</a>
    <div class="navlbl">Overlay</div>
    <a class="nav" href="#radar" data-page="radar"><svg><use href="#i-radar"/></svg>Radar</a>
    <a class="nav" href="#atlas" data-page="atlas"><svg><use href="#i-atlas"/></svg>Atlas</a>
    <a class="nav" href="#value" data-page="value"><svg><use href="#i-value"/></svg>Item Value</a>
    <a class="nav" href="#settings" data-page="settings"><svg><use href="#i-settings"/></svg>Settings</a>
    <div class="rail-foot">
      <div class="conn" id="conn"><span class="dot"></span><span id="connTxt">Offline</span></div>
      <div class="rail-area" id="areaChip">&mdash;</div>
      <div class="rail-sub" id="railSub">&nbsp;</div>
    </div>
  </nav>

  <main class="content">
    <a id="updateBanner" href="#" target="_blank" rel="noopener" hidden>
      <span>&#x2B06; Update available</span><span id="updateMsg"></span><span style="margin-left:auto">Download &rarr;</span>
    </a>

    <!-- ═════════════ OVERVIEW ═════════════ -->
    <section class="page" data-page="overview">
      <header class="phead">
        <div><h1>Overview</h1><p>Live character, zone and module status at a glance.</p></div>
        <div class="aside"><span class="pill" id="ovGame">&mdash;</span></div>
      </header>

      <div class="grid">
        <div class="tile span-3">
          <div class="tlbl"><span class="sdot hp"></span>Life</div>
          <div class="tval" id="hpNum">&mdash;</div>
          <div class="bar hp"><i id="hpBar"></i></div>
        </div>
        <div class="tile span-3">
          <div class="tlbl"><span class="sdot es"></span>Energy Shield</div>
          <div class="tval" id="esNum">&mdash;</div>
          <div class="bar es"><i id="esBar"></i></div>
        </div>
        <div class="tile span-3">
          <div class="tlbl"><span class="sdot mana"></span>Mana</div>
          <div class="tval" id="mpNum">&mdash;</div>
          <div class="bar mana"><i id="mpBar"></i></div>
        </div>
        <div class="tile span-3">
          <div class="tlbl">Area</div>
          <div class="tval sm" id="kAreaName">&mdash;</div>
          <div class="tsub"><span id="kAlvl">&mdash;</span> &middot; <span class="mono" id="kArea">&mdash;</span></div>
        </div>
      </div>

      <div class="grid stretch">
        <div class="card module span-3">
          <div class="mhead"><span class="micon"><svg><use href="#i-flask"/></svg></span><span class="mtitle">Auto-flask</span><span class="pill" id="mFlaskPill">&mdash;</span></div>
          <div class="mnote" id="kFlask">&mdash;</div>
          <div class="mfoot"><kbd>F8</kbd> arm / disarm in game</div>
        </div>
        <div class="card module span-3">
          <div class="mhead"><span class="micon"><svg><use href="#i-buff"/></svg></span><span class="mtitle">Buff keeper</span><span class="pill" id="mBuffPill">&mdash;</span></div>
          <div class="mnote" id="mBuffNote">&mdash;</div>
          <div class="mfoot"><kbd id="mBuffHk">F4</kbd> arm in game &middot; <a href="#macros">Edit rules &rarr;</a></div>
        </div>
        <div class="card module span-3">
          <div class="mhead"><span class="micon"><svg><use href="#i-trade"/></svg></span><span class="mtitle">Trade</span><span class="pill" id="mTradePill">&mdash;</span></div>
          <div class="mnote" id="mTradeNote">&mdash;</div>
          <div class="mfoot"><span id="mTradeOpen">0 open requests</span> &middot; <a href="#trade">Open &rarr;</a></div>
        </div>
        <div class="card module span-3">
          <div class="mhead"><span class="micon"><svg><use href="#i-chat"/></svg></span><span class="mtitle">Chat</span><span class="pill" id="mChatPill">&mdash;</span></div>
          <div class="mnote" id="mChatNote">&mdash;</div>
          <div class="mfoot">Last result &middot; <a href="#macros">Edit commands &rarr;</a></div>
        </div>
      </div>

      <div class="grid stretch">
        <div class="tile span-4">
          <div class="tlbl">Trade profit today</div>
          <div class="tval" id="ovProfit">&mdash;</div>
          <div class="tsub" id="ovProfitSub">&mdash;</div>
          <div class="kv" style="margin-top:12px"><span>Last 7 days</span><span id="ovProfitWeek">&mdash;</span></div>
          <div class="kv"><span>All time</span><span id="ovProfitAll">&mdash;</span></div>
        </div>
        <div class="card span-4">
          <h3>Census <span class="tag">this area</span></h3>
          <div class="minis" style="grid-template-columns:repeat(2,minmax(0,1fr))">
            <div class="mini"><div class="n" id="cEnt">0</div><div class="l">Entities</div></div>
            <div class="mini"><div class="n" id="cMon">0</div><div class="l">Monsters</div></div>
            <div class="mini"><div class="n" id="cPoi">0</div><div class="l">Points of interest</div></div>
            <div class="mini"><div class="n" id="cLm">0</div><div class="l">Landmarks</div></div>
          </div>
        </div>
        <div class="card span-4">
          <h3>Overlay</h3>
          <div class="kv"><span>Render</span><span id="kFps">&mdash;</span></div>
          <div class="kv"><span>World pass</span><span id="kWorld">&mdash;</span></div>
          <div class="kv"><span>Map open</span><span id="kMap">&mdash;</span></div>
        </div>
      </div>

      <div class="grid">
        <div class="card span-7">
          <h3>Hotkeys <span class="tag">active only while PoE2 is focused</span></h3>
          <div class="hkgrid" id="hkList"></div>
        </div>
        <div class="card span-5">
          <h3>Bookmarks <span class="grow"></span><a class="btn ghost sm" href="#macros">Edit</a></h3>
          <div id="bmQuick"><div class="empty">Loading&hellip;</div></div>
        </div>
      </div>

      <div class="grid">
        <div class="card span-6" id="monoCard" hidden>
          <h3>Monolith rewards <span class="tag">expedition &middot; this area</span></h3>
          <div id="monoList"></div>
        </div>
        <div class="card span-6" id="zoneCard" hidden>
          <h3>Zone notes</h3>
          <div id="zoneNotes" class="znotes"></div>
        </div>
      </div>
    </section>

    <!-- ═════════════ TRADE ═════════════ -->
    <section class="page" data-page="trade" hidden>
      <header class="phead">
        <div><h1>Trade</h1><p>Trade whispers read from the game's Client.txt, with a profit tracker.</p></div>
        <div class="aside"><span class="pill" id="trAfk" hidden>AFK</span><span class="pill" id="trLogPill">&mdash;</span></div>
      </header>
      <div class="note-box" id="trLogInfo" style="margin-bottom:16px">&mdash;</div>

      <div class="grid stretch" id="trSummary">
        <div class="tile sumtile span-4" data-sum="today"><div class="tlbl">Today</div><div class="tval">&mdash;</div></div>
        <div class="tile sumtile span-4" data-sum="week"><div class="tlbl">Last 7 days</div><div class="tval">&mdash;</div></div>
        <div class="tile sumtile span-4" data-sum="all"><div class="tlbl">All time</div><div class="tval">&mdash;</div></div>
      </div>

      <div class="grid">
        <div class="card span-5">
          <h3>Daily profit <span class="tag">last 14 days &middot; exalted</span><span class="grow"></span><span class="tag" id="trChartTotal"></span></h3>
          <div class="chart" id="trChart"></div>
        </div>
        <div class="card span-7">
          <h3>Live requests <span class="tag" id="trReqCount"></span></h3>
          <div class="hint" style="margin:-4px 0 6px">Chat actions (invite, trade, thanks, busy) are only available in game on the overlay's trade panel. Dismissing here just removes the card.</div>
          <div id="trReqs"><div class="empty">No open requests.</div></div>
        </div>
      </div>

      <div class="card" style="margin-bottom:16px">
        <h3>History <span class="tag" id="trHistCount"></span><span class="grow"></span><button class="btn danger sm" id="trClear">Clear history</button></h3>
        <div id="trHistErr"></div>
        <div class="tablewrap" style="max-height:440px" id="trHist"><div class="empty">No completed trades yet.</div></div>
      </div>

      <div class="grid">
        <div class="card span-6">
          <h3>Trade assistant</h3>
          <div class="row"><div class="rl">Enabled<small>read trade whispers from Client.txt</small></div>
            <label class="sw"><input type="checkbox" data-tr="enabled"><span class="track"></span><span class="knob"></span></label></div>
          <div class="row"><div class="rl">Show in-game panel<small>request cards with invite / trade / thanks buttons</small></div>
            <label class="sw"><input type="checkbox" data-tr="showPanel"><span class="track"></span><span class="knob"></span></label></div>
          <div class="row"><div class="rl">Panel position<small>fraction of the game window</small></div>
            <span class="trow-ctl"><span class="flbl">X</span><input type="range" min="0" max="95" data-trpct="panelX" style="width:90px"><span class="opv" data-trpv="panelX">&mdash;</span>
            <span class="flbl">Y</span><input type="range" min="0" max="95" data-trpct="panelY" style="width:90px"><span class="opv" data-trpv="panelY">&mdash;</span></span></div>
          <div class="row"><div class="rl">Track history<small>record completed trades for the profit tracker</small></div>
            <label class="sw"><input type="checkbox" data-tr="trackHistory"><span class="track"></span><span class="knob"></span></label></div>
          <div class="row"><div class="rl">Kick after trade<small>&ldquo;Thanks&rdquo; on a completed incoming trade also kicks the buyer from the party</small></div>
            <label class="sw"><input type="checkbox" data-tr="kickAfterTrade"><span class="track"></span><span class="knob"></span></label></div>
          <div class="row"><div class="rl">Expire after<small>minutes without activity before a request drops off</small></div>
            <input class="numin" type="number" min="1" max="1440" step="1" data-tr="expireMinutes"></div>
          <div class="row"><div class="rl">Max requests<small>cards kept on the panel</small></div>
            <input class="numin" type="number" min="1" max="50" step="1" data-tr="maxSessions"></div>
          <div class="field" style="margin-top:10px"><span>Client.txt path</span>
            <input type="text" data-tr="clientLogPath" placeholder="leave empty to auto-detect" spellcheck="false"></div>
          <div class="hint" style="margin-top:6px">Leave empty to auto-detect from the running game. Point it at <code>logs/Client.txt</code> or the game folder.</div>
        </div>
        <div class="card span-6">
          <h3>Reply messages <span class="tag">sent from the in-game panel buttons</span></h3>
          <div class="field" style="margin-bottom:12px"><span>Thanks</span><input type="text" data-tr="thanksMessage" maxlength="200"></div>
          <div class="field" style="margin-bottom:12px"><span>Busy</span><input type="text" data-tr="busyMessage" maxlength="200"></div>
          <div class="field" style="margin-bottom:12px"><span>Sold</span><input type="text" data-tr="soldMessage" maxlength="200"></div>
          <div class="field"><span>Still interested?</span><input type="text" data-tr="stillInterestedMessage" maxlength="200"></div>
        </div>
      </div>
    </section>

    <!-- ═════════════ MACROS ═════════════ -->
    <section class="page" data-page="macros" hidden>
      <header class="phead">
        <div><h1>Macros</h1><p>Buff keeper and chat-command hotkeys. Nothing here fires unless PoE2 is focused.</p></div>
      </header>

      <div class="grid">
        <div class="card span-8">
          <h3>Buff keeper <span class="pill" id="bkArmed">&mdash;</span><span class="tag" id="bkNote"></span></h3>
          <div class="hint" style="margin:-2px 0 12px">Recasts self-buffs when they go missing, run low, or on a timer. A buff name matches part of the internal buff id (pick from the live list); <code>|</code> separates alternatives. Rules run top to bottom (first = highest priority), one key per tick.
            Arm with <kbd id="bkHkHint">F4</kbd> or the Insert menu; arming isn't possible from the dashboard.</div>
          <div class="fgrid" style="grid-template-columns:repeat(3,minmax(0,1fr)); margin-bottom:14px">
            <label class="field"><span>Arm / disarm hotkey</span><input class="kcap" id="bkToggle" readonly placeholder="click, then press a key"></label>
            <label class="field"><span>Min gap between any two presses (ms)</span><input class="numin" style="width:100%;text-align:left" type="number" min="50" max="5000" step="10" id="bkGap"></label>
          </div>
          <div id="bkWarn"></div>
          <div id="bkOffBanner"></div>
          <div id="bkUnread"></div>
          <div id="bkRules"></div>
          <button class="addbtn" id="bkAdd">+ Add rule</button>
        </div>
        <div class="span-4 sticky">
          <div class="card">
            <h3>Live buffs <span class="tag" id="lbCount"></span></h3>
            <div id="lbList"><div class="empty">Loading&hellip;</div></div>
          </div>
        </div>
      </div>

      <div class="grid">
        <div class="card span-8">
          <h3>Chat commands <span class="grow"></span><label class="inl">Enabled <span class="sw"><input type="checkbox" id="cmdEnabled"><span class="track"></span><span class="knob"></span></span></label></h3>
          <div class="ph">
            <span><code>@char</code> / <code>{char}</code> your character</span>
            <span><code>@last</code> / <code>{last}</code> last whisper partner</span>
            <span><code>{league}</code> current league</span>
            <span><code>{area}</code> current area</span>
            <span>Several lines = several chat messages</span>
          </div>
          <div id="cmdWarn"></div>
          <div class="cmdrow rowhead"><span>On</span><span>Name</span><span>Hotkey</span><span>Chat text</span><span></span></div>
          <div id="cmdList"></div>
          <button class="addbtn" id="cmdAdd">+ Add command</button>
        </div>
        <div class="card span-4">
          <h3>Item inspect</h3>
          <div class="hint" style="margin:-2px 0 10px">Hover an item in game and press the hotkey to open it on the web.</div>
          <label class="field" style="margin-bottom:12px"><span>Open on PoE2 Wiki</span><input class="kcap" data-cmdhk="inspectWikiHotkey" readonly placeholder="unbound"></label>
          <label class="field"><span>Open on poe2db</span><input class="kcap" data-cmdhk="inspectDbHotkey" readonly placeholder="unbound"></label>
          <div class="hint" style="margin-top:14px">Hotkey fields: click, then press a combo. <kbd>Backspace</kbd> clears. Reserved: F6&ndash;F10, F12, Insert, Ctrl+D and the buff-keeper toggle.</div>
        </div>
        <div class="card span-12">
          <h3>Bookmarks <span class="tag">open a website, optionally on a hotkey</span></h3>
          <div class="ph"><span><code>{league}</code> price league</span><span><code>{item}</code> hovered item</span><span><code>{char}</code> your character</span><span>Only http(s) links are kept</span></div>
          <div class="bmrow rowhead"><span>On</span><span>Name</span><span>Folder</span><span>URL</span><span>Hotkey</span><span></span><span></span></div>
          <div id="bmList"></div>
          <button class="addbtn" id="bmAdd">+ Add bookmark</button>
        </div>
      </div>
    </section>

    <!-- ═════════════ RADAR ═════════════ -->
    <section class="page" data-page="radar" hidden>
      <header class="phead">
        <div><h1>Radar</h1><p>How entities and map landmarks draw on the overlay.</p></div>
      </header>
      <div class="seg" role="tablist">
        <button data-sub="rules" class="on">Display rules</button>
        <button data-sub="landmarks">Landmarks</button>
        <button data-sub="hidden">Hidden</button>
      </div>

      <div class="card" data-subview="rules">
        <h3>Display rules <span class="tag">one ordered ruleset &mdash; first match wins</span><span class="grow"></span>
          <button class="btn primary sm" id="drPick">+ Add from game data&hellip;</button>
          <button class="btn sm" id="drAdd">+ Add blank rule</button></h3>
        <div class="hint" style="margin:-2px 0 12px">Each entity is matched <b>top to bottom</b>; the <b>first enabled rule that matches</b> decides its icon and color, whether it's hidden, whether it gets an HP bar, and whether it's auto-pathed. Reorder with &#9650;/&#9660; to change precedence. A rule matches on any mix of <i>type, metadata terms, monster mods, rarity, reaction, life, chest/POI/encounter state</i>; a blank condition means &ldquo;any&rdquo;.</div>
        <div id="drList"></div>
      </div>

      <div class="card" data-subview="landmarks" hidden>
        <h3>Landmarks <span class="tag">curated map labels &mdash; view, fix, share</span></h3>
        <div class="hint" style="margin:-2px 0 12px">The built-in &ldquo;known&rdquo; map features (boss arenas, exits, loot, waypoints&hellip;), labelled per area. Rename a wrong label, add your own, or hide a bad entry. <b>Export</b> a corrected list to share; <b>Import</b> to load one. To change how a tile <i>draws</i>, use a Tile rule under Display rules.</div>
        <div class="controls">
          <input type="search" id="lmSearch" placeholder="Filter by area, tile or label&hellip;">
          <button class="chip on" id="lmAreaOnly">This area only</button>
          <span class="grow"></span>
          <button class="btn" id="lmImport">Import&hellip;</button>
          <button class="btn" id="lmExport">Export</button>
        </div>
        <div id="lmList"></div>
        <div class="lmadd">
          <input id="lmArea" placeholder="area (e.g. P2_3, or *)" style="max-width:170px">
          <input id="lmPat" placeholder="tile path / pattern">
          <input id="lmLabel" placeholder="label">
          <button class="btn primary" id="lmAdd">+ Add</button>
        </div>
      </div>

      <div class="card" data-subview="hidden" hidden>
        <h3>Hidden <span class="tag">cull entirely from radar, list and navigation</span></h3>
        <div class="hint" style="margin:-2px 0 12px">A stronger cut than a Hide rule: entities whose metadata contains a pattern (or matches a <code>*</code>/<code>?</code> glob) are removed <i>everywhere</i> before the display rules run.</div>
        <div id="hideList" class="chips hidechips" style="margin:0 0 14px"></div>
        <div class="controls" style="margin:0">
          <input type="search" id="hidePattern" placeholder="pattern or glob to hide (e.g. AbyssCrack, *Daemon*)">
          <button class="btn primary" id="hideAdd">+ Hide</button>
        </div>
      </div>
    </section>

    <!-- ═════════════ ATLAS ═════════════ -->
    <section class="page" data-page="atlas" hidden>
      <header class="phead">
        <div><h1>Atlas</h1><p>Track, route to and highlight maps on the in-game Atlas.</p></div>
        <div class="aside"><span class="tag hint" id="atlasStatus">&mdash;</span>
          <button class="btn" id="atlasRefresh" title="Re-read the open Atlas">&#8635; Refresh</button>
          <button class="btn ghost" id="atlasHelp" title="How it works">?</button></div>
      </header>
      <div class="card" style="margin-bottom:16px">
        <div id="atlasHelpBox" hidden class="note-box" style="margin:0 0 12px;line-height:1.6">
          Open the Atlas in game, then <b>Refresh</b>. Each row is a map type or rolled content read from memory.
          Per row toggle <b>&#9745; Highlight</b> (ring it in game), <b style="color:var(--good)">&#8674; Nav</b> (draw a route to it) and
          <b style="color:var(--accent)">&#10148; Arrow</b> (edge pointer when off-screen) &mdash; independently. Click a column header to sort.
          Hover a tile in game and press <kbd>F10</kbd> to inspect it.
        </div>
        <div class="controls" id="atlasPresets" style="gap:6px;margin:0 0 12px">
          <span class="flbl" style="margin-right:4px">Quick set</span>
          <button class="chip" data-preset="citadels">&#9733; Citadels</button>
          <button class="chip" data-preset="deadly">&#9760; Deadly Boss</button>
          <button class="chip" data-preset="bosses">Bosses</button>
          <button class="chip" data-preset="towers">Towers</button>
          <button class="chip" data-preset="uniques">Uniques</button>
        </div>
        <div class="controls" id="atlasOpts" style="gap:18px;margin:0 0 12px">
          <label title="Hide maps you've already completed"><input type="checkbox" data-atset="atlasHideCompleted"> Hide completed</label>
          <label title="Hide maps you can run right now"><input type="checkbox" data-atset="atlasHideAccessible"> Hide accessible</label>
          <label title="Draw in-game content art above tracked + fogged maps"><input type="checkbox" data-atset="atlasShowContentIcons"> Content icons</label>
          <label title="Content icon size (px)">Icon size <input type="number" data-atset="atlasContentIconSize" min="12" max="64" step="1" style="width:64px"></label>
          <label title="Spacing of the directional arrows along routes">Arrow spacing <input type="number" data-atset="atlasRouteArrowSpacing" min="1.5" max="18" step="0.5" style="width:64px"></label>
        </div>
        <div id="atlasActive" style="margin:0 0 10px"></div>
        <div class="controls" style="gap:6px;margin:0 0 10px">
          <button class="chip on" data-group="all">All</button>
          <button class="chip" data-group="Kind">Kind</button>
          <button class="chip" data-group="Type">Type</button>
          <button class="chip" data-group="Content">Content</button>
          <button class="chip" data-group="Map">Map</button>
          <span class="grow"></span>
          <button class="chip" id="atlasHlSelOnly">Active only</button>
          <button class="chip" id="atlasHlClear">Clear all</button>
          <input type="search" id="atlasHlFilter" placeholder="Search&hellip;" style="max-width:200px">
        </div>
        <div id="atlasHlTable" style="max-height:520px;overflow:auto;border:1px solid var(--line);border-radius:6px">
          <div class="empty">Open the Atlas in game, then Refresh to list filters.</div>
        </div>
      </div>
      <div class="card">
        <h3>Map colour groups <span class="tag">recolour a whole category at once (Citadels, Halls, Uniques&hellip;)</span><span class="grow"></span>
          <button class="btn sm" id="atlasGroupAdd">+ Add group</button></h3>
        <div id="atlasGroups"></div>
      </div>
    </section>

    <!-- ═════════════ ITEM VALUE ═════════════ -->
    <section class="page" data-page="value" hidden>
      <header class="phead">
        <div><h1>Item Value</h1><p>poe.ninja prices on ground loot, hovered items, monoliths and the currency exchange; waystone mod checks.</p></div>
      </header>
      <div class="panel-grid">
        <div class="card" style="grid-column:1/-1">
          <h3>General pricing <span class="tag">poe.ninja</span></h3>
          <div class="row"><div class="rl">Price league<small>leave blank to auto-detect your league (HC/SC/Standard) from the game</small></div>
            <input class="numin" type="text" id="giLeague" data-gi="league" placeholder="auto-detect" style="width:220px;text-align:left"></div>
          <div class="row"><div class="rl">Low-listing warning<small>flag a price backed by fewer than N live listings with a &ldquo;?&rdquo;. 0 = never flag</small></div>
            <input class="numin" type="number" step="1" min="0" data-gi="minQuantity"></div>
          <div class="row"><div class="rl">Pricing status</div><span id="priceStatus" style="color:var(--ink-dim);text-align:right">&mdash;</span></div>
        </div>

        <div class="card">
          <h3>Ground loot</h3>
          <div class="row"><div class="rl">Show ground loot value<small>draw a value label over dropped items on the map</small></div>
            <label class="sw"><input type="checkbox" data-gi="enabled"><span class="track"></span><span class="knob"></span></label></div>
          <div class="row" style="border:none;padding-bottom:4px"><div class="rl hint-row">Label these categories:</div></div>
          <div class="chips" id="giCats">
            <span class="chip" data-gicat="Uniques">Uniques</span>
            <span class="chip" data-gicat="Currency">Currency</span>
            <span class="chip" data-gicat="Runes">Runes</span>
            <span class="chip" data-gicat="SoulCores">Soul Cores</span>
            <span class="chip" data-gicat="UncutGems">Uncut Gems</span>
            <span class="chip" data-gicat="Essences">Essences</span>
            <span class="chip" data-gicat="Fragments">Fragments</span>
            <span class="chip" data-gicat="Tablets">Tablets</span>
            <span class="chip" data-gicat="Delirium">Delirium</span>
            <span class="chip" data-gicat="Idols">Idols</span>
            <span class="chip" data-gicat="Abyss">Abyss</span>
            <span class="chip" data-gicat="Ritual">Ritual</span>
            <span class="chip" data-gicat="Breach">Breach</span>
            <span class="chip" data-gicat="Expedition">Expedition</span>
          </div>
          <div class="row"><div class="rl">Uniques min<small>hide uniques under this (ex)</small></div>
            <input class="numin" type="number" step="0.1" min="0" data-gi="uniqueMinEx"></div>
          <div class="row"><div class="rl">Currency min<small>hide currency under this (ex)</small></div>
            <input class="numin" type="number" step="0.1" min="0" data-gi="currencyMinEx"></div>
          <div class="row"><div class="rl">Other min<small>runes / essences / fragments / &hellip; (ex)</small></div>
            <input class="numin" type="number" step="0.1" min="0" data-gi="otherMinEx"></div>
          <div class="row"><div class="rl">Highlight threshold<small>emphasis at or above this value (ex)</small></div>
            <input class="numin" type="number" step="1" min="0" data-gi="highlightMinEx"></div>
          <div class="row"><div class="rl hint-row">Unidentified uniques reveal their name + value; everything else shows the value only.</div></div>
        </div>

        <div class="card">
          <h3>On hover</h3>
          <div class="row"><div class="rl">Show item value on hover<small>market estimate under hovered items in inventory / stash / vendor / rewards, or beside ground drops</small></div>
            <label class="sw"><input type="checkbox" data-hv="enabled"><span class="track"></span><span class="knob"></span></label></div>
          <div class="row"><div class="rl">Highlight threshold<small>emphasize the chip at or above this (stack) value (ex)</small></div>
            <input class="numin" type="number" step="1" min="0" data-hv="highlightMinEx"></div>
          <div class="row"><div class="rl">Price check hotkey<small>hover an item and press it: poe.ninja estimate vs. the cheapest live listings, plus a suggested price</small></div>
            <input class="numin" type="text" id="pcHotkey" style="width:130px"></div>
          <div class="row"><div class="rl hint-row">Hovering is explicit intent, so this ignores the ground-loot categories and floors. Stacks show unit and total estimates with league, cache age, listing confidence and recent change. Rare and magic gear check online trade listings after a brief hover, using readable modifier rolls within &plusmn;20% (DPS, defences, item level, sockets, quality and corruption aren't weighed separately). Lookups are cached for ten minutes and respect trade rate limits. Press the price-check hotkey while hovering for the full comparison panel in game; its <b>Open on trade site</b> button opens the matched search.</div></div>
        </div>

        <div class="card">
          <h3>Waystone checker</h3>
          <div class="row"><div class="rl">Enabled<small>when you hover a waystone in game, mod lines matching a pattern below are flagged</small></div>
            <label class="sw"><input type="checkbox" id="mcEnabled"><span class="track"></span><span class="knob"></span></label></div>
          <div class="field" style="margin-top:10px"><span>Dangerous mods &mdash; one pattern per line</span>
            <textarea id="mcList" rows="9" spellcheck="false" style="font-family:var(--mono);font-size:12.5px"></textarea></div>
          <div class="hint" style="margin-top:6px">Case-insensitive text match; prefix a line with <code>re:</code> for a regular expression (e.g. <code>re:reduced .* recovery</code>). Up to 100 patterns.</div>
        </div>

        <div class="card">
          <h3>Monolith rewards <span class="tag">expedition</span></h3>
          <div class="row"><div class="rl">Enabled<small>read + price runeshape-monolith rewards</small></div>
            <label class="sw"><input type="checkbox" data-mono="enabled"><span class="track"></span><span class="knob"></span></label></div>
          <div class="row"><div class="rl">Min value to show / auto-path<small>hide the monolith (icon, panel, auto-nav) below this (ex). 0 = show all</small></div>
            <input class="numin" type="number" step="1" min="0" data-mono="minValueEx"></div>
          <div class="row"><div class="rl">Highlight threshold<small>green value tier at or above this (ex)</small></div>
            <input class="numin" type="number" step="1" min="0" data-mono="highlightMinEx"></div>
          <div class="row"><div class="rl">Hide collected<small>drop monoliths whose reward was already claimed</small></div>
            <label class="sw"><input type="checkbox" data-mono="hideCollected"><span class="track"></span><span class="knob"></span></label></div>
          <div class="row"><div class="rl">Show reward panel<small>the in-overlay nearby-monolith reward list</small></div>
            <label class="sw"><input type="checkbox" data-mono="showPanel"><span class="track"></span><span class="knob"></span></label></div>
          <div class="row"><div class="rl">Show map label<small>draw value + top reward at the icon</small></div>
            <label class="sw"><input type="checkbox" data-mono="showMapLabel"><span class="track"></span><span class="knob"></span></label></div>
        </div>

        <div class="card">
          <h3>Currency exchange <span class="tag">order-book depth</span></h3>
          <div class="row"><div class="rl">Enabled<small>show the depth panel when the exchange is open</small></div>
            <label class="sw"><input type="checkbox" data-ce="enabled"><span class="track"></span><span class="knob"></span></label></div>
          <div class="row"><div class="rl">Max rows<small>ladder rows to show per side</small></div>
            <input class="numin" type="number" step="1" min="1" max="64" data-ce="maxRows"></div>
          <div class="row"><div class="rl hint-row">When the in-game Currency Exchange is open, a top-right panel lists the best offered/wanted ratios with depth (the best row of each side is highlighted).</div></div>
        </div>
      </div>
    </section>

    <!-- ═════════════ SETTINGS ═════════════ -->
    <section class="page" data-page="settings" hidden>
      <header class="phead">
        <div><h1>Settings</h1><p>Radar display, HP bars, terrain, calibration and auto-flask. Changes apply live.</p></div>
      </header>
      <div class="panel-grid">
        <div class="card">
          <h3>Radar display</h3>
          <div class="row"><div class="rl">Show terrain<small>walkable-terrain bitmap</small></div>
            <label class="sw"><input type="checkbox" data-set="showTerrain"><span class="track"></span><span class="knob"></span></label></div>
          <div class="row"><div class="rl">Show player blip<small>dot marking your own position</small></div>
            <label class="sw"><input type="checkbox" data-set="showPlayerBlip"><span class="track"></span><span class="knob"></span></label></div>
          <div class="row"><div class="rl">Always show overlay<small>draw even when PoE2 isn't focused; auto-flask stays focus-gated</small></div>
            <label class="sw"><input type="checkbox" data-set="alwaysShowOverlay"><span class="track"></span><span class="knob"></span></label></div>
          <div class="row"><div class="rl">Reduce motion<small>still the in-game menus: no spinning sigils, motes or entrance animations</small></div>
            <label class="sw"><input type="checkbox" data-set="reduceMotion"><span class="track"></span><span class="knob"></span></label></div>
          <div class="row"><div class="rl">Hide junk entities<small>suppress cosmetic / FX / daemon dots</small></div>
            <label class="sw"><input type="checkbox" data-set="hideJunk"><span class="track"></span><span class="knob"></span></label></div>
          <div class="row"><div class="rl">Navigation paths<small>draw A&#42; routes to selected landmarks</small></div>
            <label class="sw"><input type="checkbox" data-set="showPath"><span class="track"></span><span class="knob"></span></label></div>
          <div class="row"><div class="rl">Curated landmark names<small>community labels (boss / reward / exits)</small></div>
            <label class="sw"><input type="checkbox" data-set="useCuratedLandmarks"><span class="track"></span><span class="knob"></span></label></div>
          <div class="row"><div class="rl">Overlay FPS cap<small>lower = less load on the game (15&ndash;360)</small></div>
            <input class="numin" type="number" step="1" min="15" max="360" data-set="fpsCap"></div>
        </div>
        <div class="card">
          <h3>Monster HP bars <span class="tag">by rarity</span></h3>
          <div class="hint" style="margin:-2px 0 6px">Tick <b>On</b> per rarity (untick all to disable HP bars). Fill follows the monster icon color; border thickness 0 = no border.</div>
          <div class="hpgrid">
            <span class="hph">On</span><span class="hph">Rarity</span><span class="hph">Width</span><span class="hph">Color</span><span class="hph">Border</span>
            <input type="checkbox" data-set="hpBarNormal">
            <span class="hpr">Normal</span>
            <input class="numin" type="number" step="1" min="4" data-hp="widthNormal">
            <input type="color" class="i-color" data-hpcolor="borderColorNormal">
            <input class="numin" type="number" step="0.5" min="0" max="20" data-hp="borderNormal">
            <input type="checkbox" data-set="hpBarMagic">
            <span class="hpr" style="color:var(--magic)">Magic</span>
            <input class="numin" type="number" step="1" min="4" data-hp="widthMagic">
            <input type="color" class="i-color" data-hpcolor="borderColorMagic">
            <input class="numin" type="number" step="0.5" min="0" max="20" data-hp="borderMagic">
            <input type="checkbox" data-set="hpBarRare">
            <span class="hpr" style="color:var(--rare)">Rare</span>
            <input class="numin" type="number" step="1" min="4" data-hp="widthRare">
            <input type="color" class="i-color" data-hpcolor="borderColorRare">
            <input class="numin" type="number" step="0.5" min="0" max="20" data-hp="borderRare">
            <input type="checkbox" data-set="hpBarUnique">
            <span class="hpr" style="color:var(--unique)">Unique</span>
            <input class="numin" type="number" step="1" min="4" data-hp="widthUnique">
            <input type="color" class="i-color" data-hpcolor="borderColorUnique">
            <input class="numin" type="number" step="0.5" min="0" max="20" data-hp="borderUnique">
          </div>
          <div class="hpshared">
            <label>Height<input class="numin" type="number" step="1" min="1" max="30" data-hp="height"></label>
            <label>Offset X<input class="numin" type="number" step="1" data-hp="offsetX"></label>
            <label>Offset Y<input class="numin" type="number" step="1" data-hp="offsetY"></label>
          </div>
          <div class="hint" style="margin-top:8px">Offset Y negative = above the monster.</div>
        </div>
        <div class="card">
          <h3>Terrain <span class="tag">walkable overlay</span></h3>
          <div class="row"><div class="rl">Interior fill<small>wash over walkable cells</small></div>
            <span class="trow-ctl">
              <input type="color" class="i-color" data-tcolor="interiorColor">
              <input type="range" class="op" min="0" max="100" data-topacity="interiorOpacity">
              <span class="opv" data-topv="interiorOpacity">&mdash;</span></span></div>
          <div class="row"><div class="rl">Wall edge<small>outlines around rooms</small></div>
            <span class="trow-ctl">
              <input type="color" class="i-color" data-tcolor="edgeColor">
              <input type="range" class="op" min="0" max="100" data-topacity="edgeOpacity">
              <span class="opv" data-topv="edgeOpacity">&mdash;</span></span></div>
          <div class="row"><div class="rl hint-row">Edits rebuild the terrain bitmap; use &ldquo;Show terrain&rdquo; to hide it entirely.</div></div>
        </div>
        <div class="card">
          <h3>Map calibration</h3>
          <div class="row"><div class="rl">Scale multiplier<small>projection scale of the map overlay</small></div>
            <input class="numin" type="number" step="0.01" data-set="scaleMul"></div>
          <div class="row"><div class="rl">Offset X</div><input class="numin" type="number" step="1" data-set="offX"></div>
          <div class="row"><div class="rl">Offset Y</div><input class="numin" type="number" step="1" data-set="offY"></div>
          <div class="row"><div class="rl hint-row">Changes apply live.</div></div>
        </div>
        <div class="card">
          <h3>Auto-flask <span class="pill" id="flaskPill">&mdash;</span></h3>
          <div class="hint" style="margin:-2px 0 6px">Armed with <kbd>F8</kbd> in game only &mdash; the dashboard can tune it but never arm it. Status: <span id="flaskState">&mdash;</span></div>
          <div class="row"><div class="rl">Life flask triggers on<small>which pool the life flask watches &mdash; ES is ignored if your build has none</small></div>
            <select class="numin selin" data-set="lifeFlaskMode">
              <option value="Health">Health %</option>
              <option value="EnergyShield">Energy Shield %</option>
              <option value="Either">Either (HP or ES)</option>
            </select></div>
          <div class="row"><div class="rl">Life threshold %<small>tap the life flask below this Life %</small></div>
            <input class="numin" type="number" step="1" min="0" max="100" data-set="lifeThresholdPct"></div>
          <div class="row"><div class="rl">ES threshold %<small>below this Energy Shield % (ES / Either modes)</small></div>
            <input class="numin" type="number" step="1" min="0" max="100" data-set="esThresholdPct"></div>
          <div class="row"><div class="rl">Mana threshold %<small>tap the mana flask below this Mana %</small></div>
            <input class="numin" type="number" step="1" min="0" max="100" data-set="manaThresholdPct"></div>
          <div class="row"><div class="rl">Life flask key</div>
            <input class="numin keyin" type="text" maxlength="1" data-set="lifeKey"></div>
          <div class="row"><div class="rl">Mana flask key</div>
            <input class="numin keyin" type="text" maxlength="1" data-set="manaKey"></div>
          <div class="row"><div class="rl">Life cooldown<small>min ms between life taps</small></div>
            <input class="numin" type="number" step="100" min="0" data-set="lifeCooldownMs"></div>
          <div class="row"><div class="rl">Mana cooldown<small>min ms between mana taps</small></div>
            <input class="numin" type="number" step="100" min="0" data-set="manaCooldownMs"></div>
        </div>
      </div>
    </section>
  </main>
</div>
<div class="saved" id="savedMsg" role="status" aria-live="polite">&#10003; Saved</div>
<datalist id="buffNames"></datalist>

""";
}
