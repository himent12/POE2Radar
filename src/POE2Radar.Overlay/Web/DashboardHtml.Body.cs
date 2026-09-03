namespace POE2Radar.Overlay.Web;

/// <summary>Dashboard page — HTML body (tabs, cards, forms).</summary>
internal static partial class DashboardHtml
{
    private const string Body = """
<body>
<a id="updateBanner" href="#" target="_blank" rel="noopener" hidden
   style="display:none;align-items:center;gap:10px;padding:9px 16px;margin:0;background:#e0b341;color:#1a1400;font-weight:600;text-decoration:none">
  <span>&#x2B06; Update available</span><span id="updateMsg" style="font-weight:400"></span><span style="margin-left:auto;text-decoration:underline">Download &rarr;</span>
</a>
<div class="shell">
  <header>
    <div class="mark">
      <h1>POE2RADAR</h1>
    </div>
    <div class="hgap"></div>
    <div class="area-chip" id="areaChip">— <b>·</b></div>
    <div class="conn" id="conn"><span class="dot"></span><span id="connTxt">offline</span></div>
  </header>

  <div class="body">
    <aside>
      <div class="vital">
        <div class="vlabel"><span>Life</span><span class="num" id="hpNum">—</span></div>
        <div class="bar hp"><i id="hpBar" style="width:0"></i></div>
      </div>
      <div class="vital">
        <div class="vlabel"><span>Energy Shield</span><span class="num" id="esNum">—</span></div>
        <div class="bar es"><i id="esBar" style="width:0"></i></div>
      </div>
      <div class="vital">
        <div class="vlabel"><span>Mana</span><span class="num" id="mpNum">—</span></div>
        <div class="bar mana"><i id="mpBar" style="width:0"></i></div>
      </div>

      <div class="sect">Zone</div>
      <div class="kv"><span>Area</span><span id="kAreaName">—</span></div>
      <div class="kv"><span>Area code</span><span id="kArea">—</span></div>
      <div class="kv"><span>Act / Level</span><span id="kAlvl">—</span></div>
      <div class="kv"><span>Map open</span><span id="kMap">—</span></div>
      <div class="kv"><span>Auto-flask</span><span id="kFlask">—</span></div>
      <div class="kv"><span>Bot</span><span id="kBot">—</span></div>
      <div class="kv"><span>Map clear</span><span id="kClear">—</span></div>
      <div class="kv"><span>Combat assist</span><span id="kCombat">—</span></div>
      <div class="kv"><span>Quest follow</span><span id="kQuest">—</span></div>
      <div class="kv"><span>Path move</span><span id="kMove">—</span></div>
      <div id="zoneNotes" class="znotes" hidden></div>

      <div class="sect">Census</div>
      <div class="tally">
        <div class="t"><div class="n" id="cEnt">0</div><div class="l">Entities</div></div>
        <div class="t"><div class="n" id="cPoi">0</div><div class="l">Points of Int.</div></div>
        <div class="t"><div class="n" id="cMon">0</div><div class="l">Monsters</div></div>
        <div class="t"><div class="n" id="cLm">0</div><div class="l">Landmarks</div></div>
      </div>

      <div id="monoCard" hidden>
        <div class="sect">Monolith Rewards</div>
        <div id="monoList" class="znotes" style="display:block"></div>
      </div>

      <div style="height:24px"></div>
    </aside>

    <main>
      <div class="tabs">
        <button class="tab on" data-tab="filters">Rules</button>
        <button class="tab" data-tab="landmarks">Landmarks</button>
        <button class="tab" data-tab="atlas">Atlas</button>
        <button class="tab" data-tab="value">Item Value</button>
        <button class="tab" data-tab="settings">Settings</button>
      </div>

      <section class="view" data-view="filters">
        <div class="panel-grid">
          <div class="card" style="grid-column:1/-1">
            <h3>Display Rules <span class="tag">&middot; one ordered ruleset &mdash; first match wins</span></h3>
            <div class="row"><div class="rl hint-row">The single source of truth for how every entity draws. Each entity is matched <b>top&ndash;to&ndash;bottom</b>; the <b>first enabled rule that matches</b> decides everything &mdash; its icon &amp; color, whether it&rsquo;s hidden, whether it shows an HP bar, and whether it&rsquo;s auto-pathed. Reorder with &#9650;/&#9660; to change precedence. A rule matches on any mix of <i>type, metadata terms, monster mods (auras/buffs), rarity, reaction, life, chest/POI/encounter state</i>; a blank condition means &ldquo;any&rdquo;. No more conflicting filters &mdash; if two rules could match, the higher one wins.</div></div>
            <div id="drList"></div>
            <div class="controls" style="margin:8px 0 0">
              <button class="addbtn" id="drPick" style="width:auto;margin:0;padding:9px 16px">+ Add from game data…</button>
              <button class="addbtn" id="drAdd" style="width:auto;margin:0;padding:9px 16px">+ Add blank rule</button>
            </div>
          </div>
          <div class="card" style="grid-column:1/-1">
            <h3>Hidden <span class="tag">&middot; cull entirely from radar, list &amp; nav</span></h3>
            <div class="row"><div class="rl hint-row">A stronger cut than a Hide rule: entities whose metadata contains a pattern (or matches a <code>*</code>/<code>?</code> glob) are removed <i>everywhere</i> &mdash; overlay, entity list, and navigation &mdash; before the display rules even run.</div></div>
            <div id="hideList" class="controls" style="margin:8px 0 14px"></div>
            <div class="controls" style="margin:0">
              <input type="search" id="hidePattern" placeholder="pattern or glob to hide (e.g. AbyssCrack, *Daemon*)">
              <button class="addbtn" id="hideAdd" style="width:auto;margin:0;padding:8px 16px">+ Hide</button>
            </div>
          </div>
        </div>
        <div style="margin-top:18px; height:14px"><span class="saved" id="savedMsgF">&#10003; saved to config</span></div>
      </section>

      <section class="view" data-view="landmarks" hidden>
        <div class="panel-grid">
          <div class="card" style="grid-column:1/-1">
            <h3>Landmarks <span class="tag">&middot; curated map labels &mdash; view, fix, share</span></h3>
            <div class="row"><div class="rl hint-row">The built-in &ldquo;known&rdquo; map features (boss arenas, exits, loot, waypoints&hellip;), labelled per area. Rename a wrong label, add your own, or hide a bad entry. <b>Export</b> a corrected list to share or submit for baking into a release; <b>Import</b> to load one. (For how a tile <i>draws</i> — icon/color/hide — use a Tile rule on the Rules tab; this is just the labels.)</div></div>
            <div class="controls" style="margin:6px 0 12px">
              <input type="search" id="lmSearch" placeholder="filter by area / tile / label…">
              <button class="chip on" id="lmAreaOnly">This area only</button>
              <span style="flex:1"></span>
              <button class="addbtn" id="lmImport" style="width:auto;margin:0;padding:8px 14px">Import…</button>
              <button class="addbtn" id="lmExport" style="width:auto;margin:0;padding:8px 14px">Export</button>
            </div>
            <div id="lmList"></div>
            <div class="mechrow">
              <div class="top">
                <input class="mname" id="lmArea" placeholder="area (e.g. P2_3, or *)" style="max-width:150px">
                <input class="mname" id="lmPat" placeholder="tile path / pattern">
                <input class="mname" id="lmLabel" placeholder="label">
                <button class="addbtn" id="lmAdd" style="width:auto;margin:0;padding:8px 16px">+ Add</button>
              </div>
            </div>
          </div>
        </div>
        <div style="margin-top:18px; height:14px"><span class="saved" id="savedMsgL">&#10003; saved to config</span></div>
      </section>

      <section class="view" data-view="atlas" hidden>
        <div class="panel-grid">
          <div class="card" style="grid-column:1/-1">
            <h3 style="display:flex;align-items:center;gap:10px">Atlas
              <span class="tag" id="atlasStatus">&mdash;</span>
              <span style="flex:1"></span>
              <button class="chip" id="atlasRefresh" title="Re-read the open Atlas">&#8635; Refresh</button>
              <button class="chip" id="atlasHelp" title="How it works" style="width:28px;padding:6px 0;text-align:center">?</button>
            </h3>

            <!-- help popover (collapsed by default) -->
            <div id="atlasHelpBox" hidden class="hint-row" style="margin:0 0 10px;padding:9px 11px;border:1px solid var(--line);border-radius:6px;line-height:1.6">
              Open the Atlas in-game, then <b>Refresh</b>. Each row is a map type or rolled content read from memory.
              Per row toggle <b>&#9745; Highlight</b> (ring it in-game), <b style="color:#3ddc97">&#8674; Nav</b> (draw a route to it),
              <b style="color:#e0b341">&#10148; Arrow</b> (edge pointer when off-screen) &mdash; independent. Click any column header to sort.
              Hover a tile in-game + press <b>F10</b> to inspect it.
            </div>

            <!-- quick presets -->
            <div class="controls" id="atlasPresets" style="gap:6px;margin:0 0 8px;flex-wrap:wrap">
              <span class="hint-row" style="opacity:.7;margin-right:2px">Quick&nbsp;set:</span>
              <button class="chip" data-preset="citadels">&#9733; Citadels</button>
              <button class="chip" data-preset="deadly">&#9760; Deadly Boss</button>
              <button class="chip" data-preset="bosses">Bosses</button>
              <button class="chip" data-preset="towers">Towers</button>
              <button class="chip" data-preset="uniques">Uniques</button>
            </div>

            <!-- display options (#3 declutter / #5 content icons) — persisted via /api/settings -->
            <div class="controls" id="atlasOpts" style="gap:14px;margin:0 0 8px;flex-wrap:wrap;font-size:12px">
              <label title="Hide maps you've already completed (declutter)"><input type="checkbox" data-atset="atlasHideCompleted"> Hide completed</label>
              <label title="Hide maps you can run right now"><input type="checkbox" data-atset="atlasHideAccessible"> Hide accessible</label>
              <label title="Draw in-game content art above tracked + fogged maps"><input type="checkbox" data-atset="atlasShowContentIcons"> Content icons</label>
              <label title="Content icon size (px)">Icon size <input type="number" data-atset="atlasContentIconSize" min="12" max="64" step="1" style="width:56px"></label>
              <label title="Spacing of the directional arrows along routes">Arrow spacing <input type="number" data-atset="atlasRouteArrowSpacing" min="1.5" max="18" step="0.5" style="width:56px"></label>
            </div>

            <!-- active rules (removable chips) -->
            <div id="atlasActive" style="margin:0 0 8px"></div>

            <!-- group filter + search -->
            <div class="controls" style="gap:6px;margin:0 0 8px;flex-wrap:wrap">
              <button class="chip on" data-group="all">All</button>
              <button class="chip" data-group="Kind">Kind</button>
              <button class="chip" data-group="Type">Type</button>
              <button class="chip" data-group="Content">Content</button>
              <button class="chip" data-group="Map">Map</button>
              <span style="flex:1"></span>
              <button class="chip" id="atlasHlSelOnly">Active only</button>
              <button class="chip" id="atlasHlClear">Clear all</button>
              <input type="search" id="atlasHlFilter" placeholder="search&hellip;" style="width:160px">
            </div>

            <div id="atlasHlTable" style="max-height:460px;overflow:auto;border:1px solid var(--line);border-radius:6px">
              <span class="hint-row" style="padding:8px;display:block">Open the Atlas in-game + Refresh to list filters.</span>
            </div>
          </div>

          <!-- #7 colour groups: a named set of map names that all draw in one ring/label colour. -->
          <div class="card" style="grid-column:1/-1">
            <h3 style="display:flex;align-items:center;gap:10px">Map colour groups
              <span class="hint-row" style="opacity:.7;font-weight:400">recolour a whole category at once (Citadels, Halls, Uniques&hellip;)</span>
              <span style="flex:1"></span>
              <button class="chip" id="atlasGroupAdd">+ Add group</button>
            </h3>
            <div id="atlasGroups"></div>
          </div>
        </div>
      </section>

      <section class="view" data-view="settings" hidden>
        <div class="panel-grid">
          <div class="card">
            <h3>Radar Display</h3>
            <div class="row"><div class="rl">Show terrain<small>walkable-terrain bitmap</small></div>
              <label class="sw"><input type="checkbox" data-set="showTerrain"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Show player blip<small>blue dot marking your own position</small></div>
              <label class="sw"><input type="checkbox" data-set="showPlayerBlip"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Always show overlay<small>draw even when PoE2 isn&rsquo;t focused (e.g. while tweaking this dashboard); auto-flask stays focus-gated</small></div>
              <label class="sw"><input type="checkbox" data-set="alwaysShowOverlay"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Hide junk entities<small>suppress cosmetic / FX / daemon dots</small></div>
              <label class="sw"><input type="checkbox" data-set="hideJunk"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Navigation paths<small>draw A&#42; routes to selected landmarks</small></div>
              <label class="sw"><input type="checkbox" data-set="showPath"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Curated landmark names<small>community labels (boss / reward / exits)</small></div>
              <label class="sw"><input type="checkbox" data-set="useCuratedLandmarks"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Overlay FPS cap<small>lower = less load on the game; 60 is smooth for a radar (15&ndash;360)</small></div>
              <input class="numin" type="number" step="1" min="15" max="360" data-set="fpsCap"></div>
          </div>
          <div class="card">
            <h3>Monster HP Bars <span class="tag">&middot; by rarity</span></h3>
            <div class="row"><div class="rl hint-row">Toggle the bar on/off per rarity with the <b>On</b> checkbox &mdash; uncheck all to disable HP bars entirely, or leave only the rarities you want. The rest sets the bar <i>geometry</i> per rarity.</div></div>
            <div class="hpgrid">
              <span class="hph">On</span><span class="hph">Rarity</span><span class="hph">Width</span><span class="hph">Border</span><span class="hph">Thick</span>
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
            <div class="row"><div class="rl hint-row">Bar fill follows the monster icon color; set border color &amp; thickness per rarity (thickness 0 = no border). Offset Y negative = above the mob.</div></div>
          </div>
          <div class="card">
            <h3>Terrain <span class="tag">&middot; walkable overlay</span></h3>
            <div class="row"><div class="rl">Interior fill<small>wash over walkable cells</small></div>
              <span class="trow-ctl">
                <input type="color" class="i-color" data-tcolor="interiorColor">
                <input type="range" class="op" min="0" max="100" data-topacity="interiorOpacity">
                <span class="opv" data-topv="interiorOpacity">—</span></span></div>
            <div class="row"><div class="rl" style="color:var(--poi)">Wall edge<small>outlines around rooms</small></div>
              <span class="trow-ctl">
                <input type="color" class="i-color" data-tcolor="edgeColor">
                <input type="range" class="op" min="0" max="100" data-topacity="edgeOpacity">
                <span class="opv" data-topv="edgeOpacity">—</span></span></div>
            <div class="row"><div class="rl hint-row">Edits rebuild the terrain bitmap; use &ldquo;Show terrain&rdquo; above to hide it entirely.</div></div>
          </div>
          <div class="card">
            <h3>Map Calibration</h3>
            <div class="row"><div class="rl">Scale multiplier<small>projection scale of the map overlay</small></div>
              <input class="numin" type="number" step="0.01" data-set="scaleMul"></div>
            <div class="row"><div class="rl">Offset X</div><input class="numin" type="number" step="1" data-set="offX"></div>
            <div class="row"><div class="rl">Offset Y</div><input class="numin" type="number" step="1" data-set="offY"></div>
            <div class="row"><div class="rl hint-row">Adjust here &mdash; changes apply live (no in-game hotkeys).</div></div>
          </div>
          <div class="card">
            <h3>Auto-Flask</h3>
            <div class="row"><div class="rl">Life flask triggers on<small>which pool the life flask key watches &mdash; ES is ignored if your build has none</small></div>
              <select class="numin selin" data-set="lifeFlaskMode">
                <option value="Health">Health %</option>
                <option value="EnergyShield">Energy Shield %</option>
                <option value="Either">Either (HP or ES)</option>
              </select></div>
            <div class="row"><div class="rl">Life threshold %<small>tap life flask below this Life %</small></div>
              <input class="numin" type="number" step="1" min="0" max="100" data-set="lifeThresholdPct"></div>
            <div class="row"><div class="rl">ES threshold %<small>tap life flask below this Energy Shield % (ES / Either modes)</small></div>
              <input class="numin" type="number" step="1" min="0" max="100" data-set="esThresholdPct"></div>
            <div class="row"><div class="rl">Mana threshold %<small>tap mana flask below this Mana %</small></div>
              <input class="numin" type="number" step="1" min="0" max="100" data-set="manaThresholdPct"></div>
            <div class="row"><div class="rl">Life flask key</div>
              <input class="numin keyin" type="text" maxlength="1" data-set="lifeKey"></div>
            <div class="row"><div class="rl">Mana flask key</div>
              <input class="numin keyin" type="text" maxlength="1" data-set="manaKey"></div>
            <div class="row"><div class="rl">Life cooldown<small>min ms between life taps</small></div>
              <input class="numin" type="number" step="100" min="0" data-set="lifeCooldownMs"></div>
            <div class="row"><div class="rl">Mana cooldown<small>min ms between mana taps</small></div>
              <input class="numin" type="number" step="100" min="0" data-set="manaCooldownMs"></div>
            <div class="row"><div class="rl hint-row">F8 toggles auto-flask in-game. Status: <span id="flaskState">&mdash;</span></div></div>
          </div>
          <div class="card">
            <h3>Combat / Bot</h3>
            <div class="row"><div class="rl hint-row">F3 toggles the bot master in-game (quest follow + path move + combat). F6 while the bot is on picks one quest (last press wins). F2 toggles map-clear. F4 toggles combat assist independently. F5 toggles path move. Arm bits cannot be armed from this page. Bot: <span id="botState">&mdash;</span></div></div>
            <div class="row"><div class="rl">Attack range<small>grid units; tap only if a hostile monster is this close</small></div>
              <input class="numin" type="number" step="1" min="1" max="200" data-set="combatRange"></div>
              <div class="row"><div class="rl">Engage range<small>grid units; the bot stops walking to fight only while a hostile is this close (skills still fire out to the attack range, and the bot keeps walking toward farther mobs)</small></div>
              <input class="numin" type="number" step="1" min="1" max="200" data-set="combatEngageRange"></div>
              <div class="row"><div class="rl">Flee below HP %<small>stop attacking and run away from the pack when life drops under this (0 = never flee, always attack)</small></div>
              <input class="numin" type="number" step="1" min="0" max="100" data-set="combatFleeHpPct"></div>
              <div class="row"><div class="rl">Resume at HP %<small>go back to attacking once life is back at this</small></div>
              <input class="numin" type="number" step="1" min="0" max="100" data-set="combatFleeRecoverPct"></div>
              <div class="row"><div class="rl">Flee distance<small>grid cells to run from the pack (most open direction)</small></div>
              <input class="numin" type="number" step="1" min="1" max="200" data-set="combatFleeDistance"></div>
              <div class="row"><div class="rl">Auto-respawn<small>when the character dies, tap the resurrect key until alive (bot / clear / combat / move must be armed)</small></div>
                <input type="checkbox" data-set="autoRespawn"></div>
              <div class="row"><div class="rl">Dodge key<small>used by "dodge after" skill steps (PoE2 default: Space)</small></div>
                <select class="numin selin" data-set="combatDodgeKey">
                  <option value="32">Space</option>
                  <option value="16">Shift</option>
                  <option value="17">Ctrl</option>
                  <option value="18">Alt</option>
                </select></div>
              <div class="row"><div class="rl">Respawn key<small>fallback only — the bot clicks the "Resurrect at Checkpoint" button it finds on screen; this key is tapped if the button cannot be located</small></div>
                <select class="numin selin" data-set="respawnKey">
                  <option value="32">Space</option>
                  <option value="13">Enter</option>
                </select></div>
              <div class="row"><div class="rl">Fight stall<small>ms; 0 = never give up. Default 6000: a monster that takes NO damage for this long is immune (essence-imprisoned, shielded) and gets skipped</small></div>
              <input class="numin" type="number" step="100" min="0" max="60000" data-set="combatStallMs"></div>
              <div class="row"><div class="rl">Ignore after stall<small>ms to ignore stalled monsters before trying them again</small></div>
              <input class="numin" type="number" step="1000" min="1000" max="300000" data-set="combatIgnoreMs"></div>
            <div class="row"><div class="rl">Target<small>which hostile the rotation aims at</small></div>
              <select class="numin selin" data-set="combatTargetMode">
                <option value="Nearest">Nearest</option>
                <option value="Rarity">Rarity (unique &gt; rare &gt; magic)</option>
                <option value="LowestHp">Weakest (lowest life)</option>
                <option value="HighestHp">Tank (highest life)</option>
              </select></div>
            <div class="row"><div class="rl">Rotation<small>round-robin cycles the list; priority always fires the first ready slot</small></div>
              <select class="numin selin" data-set="combatRotationMode">
                <option value="RoundRobin">Round-robin</option>
                <option value="Priority">Priority</option>
              </select></div>
            <div class="row"><div class="rl">Keep distance<small>grid units; ranged builds back away while a hostile is closer than this (0 = melee, never)</small></div>
              <input class="numin" type="number" step="1" min="0" max="200" data-set="combatKeepDistance"></div>
            <div class="row"><div class="rl">Skill rotation<small>ordered slots. Range 0 uses the attack range; Min mobs gates AoE; Life &lt; fires only under that %; Rare = rare/unique only</small></div></div>
            <div class="skhead"><span></span><span>Key</span><span>CD ms</span><span>Range</span><span>Min mobs</span><span>Life &lt; %</span><span>Rare</span><span>On</span></div>
            <div id="combatSkills"></div>
            <div class="row"><button type="button" class="addbtn" id="combatSkillAdd">Add skill</button></div>
            <div class="row"><div class="rl hint-row">F4 toggles combat assist in-game. It cannot be armed from this page. Status: <span id="combatState">&mdash;</span></div></div>
            <div class="row"><div class="rl hint-row">When armed, each zone auto-selects a nav target from the area's zone notes (or a Transition / waypoint / boss landmark) and reuses the existing A* route. F6 cycles a single quest target (last press is the one the bot follows; it will not jump to the other F6 picks). Click a legend row to pin that one. F7 clears the pin and returns to auto-pick. A unique monster (boss) that spawns is targeted immediately. On arrival, taps interact/use. F3 toggles quest follow in-game. It cannot be armed from this page. Status: <span id="questFollowState">&mdash;</span></div></div>
            <div class="row"><div class="rl">Use / interact<small>key or mouse button tapped on arrival at the quest target</small></div>
              <select class="numin selin" data-set="questUseKey">
                <option value="1">Left mouse</option>
                <option value="2">Right mouse</option>
                <option value="70">F</option>
                <option value="84">T</option>
              </select></div>
            <div class="row"><div class="rl">Use radius<small>grid cells; tap interact when this close to the quest target</small></div>
              <input class="numin" type="number" step="0.5" min="0" max="64" data-set="questUseRadius"></div>
            <div class="row"><div class="rl">Use cooldown<small>min ms between interact taps</small></div>
              <input class="numin" type="number" step="10" min="0" data-set="questUseCooldownMs"></div>
            <div class="row"><div class="rl">Move method<small>WASD taps, or click-to-move toward the next waypoint of the first selected path</small></div>
              <select class="numin selin" data-set="moveMethod">
                <option value="WASD">WASD</option>
                <option value="Click">Click-to-move</option>
              </select></div>
            <div class="row"><div class="rl">Click button<small>mouse button used when method is Click</small></div>
              <select class="numin selin" data-set="moveClickKey">
                <option value="1">Left mouse</option>
                <option value="2">Right mouse</option>
                <option value="4">Middle mouse</option>
              </select></div>
            <div class="row"><div class="rl">Arrive radius<small>grid cells; stop when this close to the remaining waypoints</small></div>
              <input class="numin" type="number" step="0.5" min="0" max="64" data-set="moveArriveRadius"></div>
            <div class="row"><div class="rl">Cooldown<small>min ms between click taps (WASD keys are held, not tapped)</small></div>
              <input class="numin" type="number" step="10" min="0" data-set="moveCooldownMs"></div>
              <div class="row"><div class="rl">Play in background<small>keep the bot going while PoE2 is not the active window. Windows: input is posted to the game window. Linux: run the game under gamescope (Steam launch options: <code>gamescope -f -- %command%</code>) so it has its own always-focused display; the bot sends input there</small></div>
                <input type="checkbox" data-set="playInBackground"></div>
              <div class="row"><div class="rl">Input display (Linux)<small>nested X display of the gamescope session, e.g. <code>:1</code>. <code>auto</code> scans for one hosting the game. Blank = main display (Wine ignores keys while unfocused)</small></div>
                <input class="numin textin" type="text" maxlength="16" data-set="inputDisplay"></div>
              <div class="row"><div class="rl">Run while moving<small>hold the run key whenever the bot is travelling</small></div>
              <input type="checkbox" data-set="moveRunEnabled"></div>
              <div class="row"><div class="rl">Run key<small>PoE2 default: Space</small></div>
              <select class="numin selin" data-set="moveRunKey">
                <option value="32">Space</option>
                <option value="16">Shift</option>
                <option value="17">Ctrl</option>
                <option value="18">Alt</option>
              </select></div>
              <div class="row"><div class="rl">Look-ahead<small>grid cells; steer at the farthest visible waypoint within this — cuts corners instead of stair-stepping</small></div>
              <input class="numin" type="number" step="1" min="1" max="60" data-set="moveLookAhead"></div>
              <div class="row"><div class="rl">Diagonals<small>hold two keys at once for 8-way movement</small></div>
              <input type="checkbox" data-set="moveDiagonals"></div>
              <div class="row"><div class="rl">Axis rotation<small>degrees; rotate the grid→WASD mapping if the character walks off at an angle</small></div>
              <input class="numin" type="number" step="15" min="-180" max="180" data-set="moveAxisRotationDeg"></div>
            <div class="row"><div class="rl">Move key W<small>grid +Y</small></div>
              <input class="numin keyin" type="text" maxlength="1" data-set="moveKeyW"></div>
            <div class="row"><div class="rl">Move key A<small>grid −X</small></div>
              <input class="numin keyin" type="text" maxlength="1" data-set="moveKeyA"></div>
            <div class="row"><div class="rl">Move key S<small>grid −Y</small></div>
              <input class="numin keyin" type="text" maxlength="1" data-set="moveKeyS"></div>
            <div class="row"><div class="rl">Move key D<small>grid +X</small></div>
              <input class="numin keyin" type="text" maxlength="1" data-set="moveKeyD"></div>
            <div class="row"><div class="rl hint-row">F2 toggles map-clear in-game (walks unexplored walkable cells; unique bosses and live hostiles first; pauses quest follow while on). It cannot be armed from this page. Status: <span id="mapClearState">&mdash;</span></div></div>
            <div class="row"><div class="rl">Clear stamp radius<small>grid cells marked visited around the player each tick while map-clear is on</small></div>
              <input class="numin" type="number" step="1" min="4" max="64" data-set="mapClearStampRadius"></div>
              <div class="row"><div class="rl">Clear aggro range<small>grid units; only chase non-unique hostiles this close (0 = any distance); farther packs are reached by the frontier walk</small></div>
              <input class="numin" type="number" step="1" min="0" max="500" data-set="mapClearAggroRange"></div>
              <div class="row"><div class="rl">Clear stuck timeout<small>ms without getting closer to the current target (while not fighting) before it is skipped</small></div>
              <input class="numin" type="number" step="500" min="1000" max="120000" data-set="mapClearStuckMs"></div>
            <div class="row"><div class="rl hint-row">F5 toggles path move in-game (F3 quest follow also arms it). It cannot be armed from this page. Status: <span id="pathMoveState">&mdash;</span></div></div>
          </div>
        </div>
        <div style="margin-top:18px; height:14px"><span class="saved" id="savedMsg">&#10003; saved to config</span></div>
      </section>

      <section class="view" data-view="value" hidden>
        <div class="panel-grid">
          <!-- Shared pricing config: applies to BOTH the ground overlay and the hover chip. -->
          <div class="card" style="grid-column:1/-1">
            <h3>General Pricing <span class="tag">&middot; poe.ninja</span></h3>
            <div class="row"><div class="rl hint-row">These apply to <b>everything priced</b> below &mdash; ground loot, hover, monolith &amp; ritual rewards. Prices come from poe.ninja for the detected league.</div></div>
            <div class="row"><div class="rl">Price league<small>leave blank to auto-detect your league (HC/SC/Standard) from the game</small></div>
              <input class="numin" type="text" id="giLeague" data-gi="league" placeholder="auto-detect" style="width:200px"></div>
            <div class="row"><div class="rl">Low-listing warning<small>flag a price backed by fewer than N live listings with a &ldquo;?&rdquo; (possible mislisting). 0 = never flag</small></div>
              <input class="numin" type="number" step="1" min="0" data-gi="minQuantity"></div>
            <div class="row"><div class="rl hint-row">Pricing status: <span id="priceStatus" style="color:var(--ink-dim)">&mdash;</span></div></div>
          </div>

          <!-- Ground loot value labels. -->
          <div class="card">
            <h3>Ground Loot</h3>
            <div class="row"><div class="rl">Show ground loot value<small>draw a value label over dropped items on the map</small></div>
              <label class="sw"><input type="checkbox" data-gi="enabled"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl hint-row">Show a label for these categories:</div></div>
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
            <div class="row"><div class="rl hint-row">Minimum value to show, per bucket (Ex) &mdash; drops below the floor are hidden:</div></div>
            <div class="row"><div class="rl">Uniques min<small>hide uniques under this (Ex)</small></div>
              <input class="numin" type="number" step="0.1" min="0" data-gi="uniqueMinEx"></div>
            <div class="row"><div class="rl">Currency min<small>hide currency under this (Ex)</small></div>
              <input class="numin" type="number" step="0.1" min="0" data-gi="currencyMinEx"></div>
            <div class="row"><div class="rl">Other min<small>runes / essences / fragments / … (Ex)</small></div>
              <input class="numin" type="number" step="0.1" min="0" data-gi="otherMinEx"></div>
            <div class="row"><div class="rl">Highlight threshold<small>border/emphasis at or above this value (Ex)</small></div>
              <input class="numin" type="number" step="1" min="0" data-gi="highlightMinEx"></div>
            <div class="row"><div class="rl hint-row">Unidentified uniques reveal their NAME + value; everything else (identified uniques, currency, runes, essences, …) shows the value only.</div></div>
          </div>

          <!-- Hover price chip (any item UI). -->
          <div class="card">
            <h3>On Hover</h3>
            <div class="row"><div class="rl">Show item value on hover<small>a price chip beside the game tooltip in inventory / stash / vendor / reward UIs</small></div>
              <label class="sw"><input type="checkbox" data-hv="enabled"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Highlight threshold<small>emphasize the chip at or above this (stack) value (Ex)</small></div>
              <input class="numin" type="number" step="1" min="0" data-hv="highlightMinEx"></div>
            <div class="row"><div class="rl hint-row">Hovering is explicit intent, so this ignores the ground category toggles &amp; value floors &mdash; any priced item shows. Stacks show the per-unit price and the stack total.</div></div>
          </div>

          <!-- Monolith (expedition) reward overlay — a value/pricing feature, grouped here. -->
          <div class="card">
            <h3>Monolith Rewards <span class="tag">&middot; expedition</span></h3>
            <div class="row"><div class="rl">Enabled<small>read + price runeshape-monolith rewards</small></div>
              <label class="sw"><input type="checkbox" data-mono="enabled"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Min value to show / auto-path<small>hide the monolith entirely (icon, panel, auto-nav) below this (Ex). 0 = show every monolith</small></div>
              <input class="numin" type="number" step="1" min="0" data-mono="minValueEx"></div>
            <div class="row"><div class="rl">Highlight threshold<small>green value tier at or above this (Ex)</small></div>
              <input class="numin" type="number" step="1" min="0" data-mono="highlightMinEx"></div>
            <div class="row"><div class="rl">Hide collected<small>drop monoliths whose reward was already claimed</small></div>
              <label class="sw"><input type="checkbox" data-mono="hideCollected"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Show reward panel<small>the in-overlay nearby-monolith reward list</small></div>
              <label class="sw"><input type="checkbox" data-mono="showPanel"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Show map label<small>draw value + top reward at the icon</small></div>
              <label class="sw"><input type="checkbox" data-mono="showMapLabel"><span class="track"></span><span class="knob"></span></label></div>
          </div>

          <!-- Currency Exchange depth panel — value/pricing feature, grouped here. -->
          <div class="card">
            <h3>Currency Exchange <span class="tag">&middot; Kalguur market</span></h3>
            <div class="row"><div class="rl">Enabled<small>show the order-book depth panel when the exchange is open</small></div>
              <label class="sw"><input type="checkbox" data-ce="enabled"><span class="track"></span><span class="knob"></span></label></div>
            <div class="row"><div class="rl">Max rows<small>ladder rows to show per side</small></div>
              <input class="numin" type="number" step="1" min="1" max="64" data-ce="maxRows"></div>
            <div class="row"><div class="rl hint-row">When the in-game Currency Exchange is open, a top-right panel lists the best offered/wanted ratios + depth (the best row of each side is highlighted).</div></div>
          </div>
        </div>
        <div style="margin-top:18px; height:14px"><span class="saved" id="savedMsg2">&#10003; saved to config</span></div>
      </section>

    </main>
  </div>
</div>

""";
}
