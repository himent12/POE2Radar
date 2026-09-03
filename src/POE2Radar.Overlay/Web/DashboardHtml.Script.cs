namespace POE2Radar.Overlay.Web;

/// <summary>Dashboard page — client script (polling, settings, editors).</summary>
internal static partial class DashboardHtml
{
    private const string Script = """
<script>
const $ = s => document.querySelector(s);
const $$ = s => [...document.querySelectorAll(s)];
let state=null, zone=null;
let activeTab='filters';
let atlasData=null, atlasView='region', atlasSel=new Set(), atlasHl=null, atlasNav=null, atlasArrow=null, atlasHlSelOnly=false, atlasGroup='all';

/* ── tabs ── */
$$('.tab').forEach(t=>t.onclick=()=>{
  activeTab=t.dataset.tab;
  $$('.tab').forEach(x=>x.classList.toggle('on',x===t));
  $$('.view').forEach(v=>v.hidden = v.dataset.view!==activeTab);
  if(activeTab==='settings') loadSettings();
  if(activeTab==='value'){ loadSettings(); pollPrices(); }
  if(activeTab==='filters') loadFilters();
  if(activeTab==='landmarks') loadLandmarks();
  if(activeTab==='atlas'){ if(!atlasData) loadAtlas(); else renderAtlas(); }
});

/* ── polling (left rail vitals/zone/census) ── */
async function getJSON(u){ const r=await fetch(u,{cache:'no-store'}); if(!r.ok) throw 0; return r.json(); }
function setConn(live){ $('#conn').classList.toggle('live',live); $('#connTxt').textContent = live?'live':'offline'; }

async function tick(){
  try{
    state = await getJSON('/state');
    setConn(true);
    try{ zone = await getJSON('/api/zone'); }catch(e){ zone=null; }
    renderState();
    if(activeTab==='value') pollPrices();   // keep the league/status live (prices load a few s after launch)
  }catch(e){ setConn(false); }
}

/* ── settings tab (writes radar/visual + flask via the loopback-gated /api/settings) ── */
async function loadSettings(){
  try{
    const s = await getJSON('/api/settings');
    $$('[data-set]').forEach(el=>{
      const k=el.dataset.set;
      if(el.type==='checkbox') el.checked=!!s[k];
      else if(el.classList.contains('keyin')) el.value=vkToChar(s[k]);
      else if(s[k]!==undefined) el.value=s[k];
    });
    hpBars = s.hpBars || null;
    terrain = s.terrain || null;
    gi = s.groundItems || {};
    hover = s.hoverPrice || {};
    mono = s.monoliths || {};
    ce = s.currencyExchange || {};
    combatSkillsData = Array.isArray(s.combatSkills)
      ? s.combatSkills.map(sk=>({key:sk.key||sk.vk||0x51, cooldownMs:sk.cooldownMs??400, range:sk.range||0}))
      : [];
    renderHpBars(); renderTerrain(); renderGround(); renderHover(); renderMono(); renderExchange(); renderCombatSkills();
  }catch(e){}
}

/* ── ground-item pricing (nested object: POST the whole {groundItems}) ── */
let gi = null;
function renderGround(){
  if(!gi) return;
  $$('[data-gi]').forEach(el=>{
    const k=el.dataset.gi;
    if(el.type==='checkbox') el.checked=!!gi[k];
    else if(gi[k]!==undefined && gi[k]!==null) el.value=gi[k];
  });
  const cats=new Set((gi.categories||[]).map(c=>(c||'').toLowerCase()));
  $$('#giCats .chip').forEach(c=>c.classList.toggle('on', cats.has(c.dataset.gicat.toLowerCase())));
}
function saveGround(){ if(gi) saveSetting('groundItems', gi); }
function wireGround(){
  $$('[data-gi]').forEach(el=>{
    const k=el.dataset.gi;
    if(el.type==='checkbox') el.onchange=()=>{ gi=gi||{}; gi[k]=el.checked; saveGround(); };
    else if(el.type==='text') el.onchange=()=>{ gi=gi||{}; gi[k]=el.value.trim(); saveGround(); };
    else el.onchange=()=>{ const v=parseFloat(el.value); if(!isNaN(v)){ gi=gi||{}; gi[k]=v; saveGround(); } };
  });
  $$('#giCats .chip').forEach(c=>c.onclick=()=>{
    c.classList.toggle('on');
    gi=gi||{};
    gi.categories=$$('#giCats .chip.on').map(x=>x.dataset.gicat);
    saveGround();
  });
}
/* ── hover price chip (nested object: POST the whole {hoverPrice}) ── */
let hover = null;
function renderHover(){
  if(!hover) return;
  $$('[data-hv]').forEach(el=>{
    const k=el.dataset.hv;
    if(el.type==='checkbox') el.checked=!!hover[k];
    else if(hover[k]!==undefined && hover[k]!==null) el.value=hover[k];
  });
}
function saveHover(){ if(hover) saveSetting('hoverPrice', hover); }
function wireHover(){
  $$('[data-hv]').forEach(el=>{
    const k=el.dataset.hv;
    if(el.type==='checkbox') el.onchange=()=>{ hover=hover||{}; hover[k]=el.checked; saveHover(); };
    else el.onchange=()=>{ const v=parseFloat(el.value); if(!isNaN(v)){ hover=hover||{}; hover[k]=v; saveHover(); } };
  });
}
/* ── live pricing status: shows the resolved league + load state, and uses the detected league as the
      placeholder in the (blank = auto-detect) league field so the user can see what auto-detect picked. ── */
async function pollPrices(){
  try{
    const p = await getJSON('/api/prices');
    const st = $('#priceStatus'); const lg = $('#giLeague');
    if(lg && p.league) lg.placeholder = p.league + ' (auto)';
    if(st){
      st.textContent = p.loaded
        ? `${p.league||'?'} — ${p.count||0} items loaded`
        : (p.status||'loading…');
      st.style.color = p.loaded ? 'var(--good, #3ddc97)' : 'var(--ink-dim)';
    }
  }catch(e){}
}
/* ── monolith (expedition) rewards (nested object: POST the whole {monoliths}) ── */
let mono = null;
function renderMono(){
  if(!mono) return;
  $$('[data-mono]').forEach(el=>{
    const k=el.dataset.mono;
    if(el.type==='checkbox') el.checked=!!mono[k];
    else if(mono[k]!==undefined && mono[k]!==null) el.value=mono[k];
  });
}
function saveMono(){ if(mono) saveSetting('monoliths', mono); }
function wireMono(){
  $$('[data-mono]').forEach(el=>{
    const k=el.dataset.mono;
    if(el.type==='checkbox') el.onchange=()=>{ mono=mono||{}; mono[k]=el.checked; saveMono(); };
    else el.onchange=()=>{ const v=parseFloat(el.value); if(!isNaN(v)){ mono=mono||{}; mono[k]=v; saveMono(); } };
  });
}
/* ── currency exchange depth panel (nested object: POST the whole {currencyExchange}) ── */
let ce = null;
function renderExchange(){
  if(!ce) return;
  $$('[data-ce]').forEach(el=>{
    const k=el.dataset.ce;
    if(el.type==='checkbox') el.checked=!!ce[k];
    else if(ce[k]!==undefined && ce[k]!==null) el.value=ce[k];
  });
}
function saveExchange(){ if(ce) saveSetting('currencyExchange', ce); }
function wireExchange(){
  $$('[data-ce]').forEach(el=>{
    const k=el.dataset.ce;
    if(el.type==='checkbox') el.onchange=()=>{ ce=ce||{}; ce[k]=el.checked; saveExchange(); };
    else el.onchange=()=>{ const v=parseFloat(el.value); if(!isNaN(v)){ ce=ce||{}; ce[k]=v; saveExchange(); } };
  });
}
async function saveSetting(key,val){
  try{
    await fetch('/api/settings',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({[key]:val})});
    // Flash every on-page "saved" indicator (one per tab) — only the visible tab's is seen.
    $$('.saved').forEach(m=>{ m.classList.add('show'); clearTimeout(m._t); m._t=setTimeout(()=>m.classList.remove('show'),1100); });
  }catch(e){}
}
function wireSettings(){
  $$('[data-set]').forEach(el=>{
    const k=el.dataset.set;
    if(el.type==='checkbox') el.onchange=()=>saveSetting(k,el.checked);
    else if(el.classList.contains('keyin')) el.onchange=()=>{ const vk=charToVk(el.value); if(vk) saveSetting(k,vk); el.value=vkToChar(vk); };
    else if(el.classList.contains('textin')) el.onchange=()=>saveSetting(k, el.value.trim());
    else if(el.tagName==='SELECT') el.onchange=()=>{ const n=parseInt(el.value,10); saveSetting(k, (!isNaN(n) && String(n)===el.value) ? n : el.value); };
    else el.onchange=()=>{ const v=parseFloat(el.value); if(!isNaN(v)) saveSetting(k,v); };
  });
}
// Flask key inputs accept a single character ('1'-'9', letters) → Win32 VK (== ASCII of uppercase).
const charToVk = s => { const c=(s||'').trim().toUpperCase().charCodeAt(0); return isNaN(c)?0:c; };
const vkToChar = v => v ? String.fromCharCode(v) : '';

/* ── combat-assist rotation (ordered {key, cooldownMs, range}; cannot arm from this page) ── */
let combatSkillsData=[];
const QWER=[0x51,0x57,0x45,0x52];
function saveCombatSkills(){ saveSetting('combatSkills', combatSkillsData); }
function nextQwer(){
  const used=new Set(combatSkillsData.map(s=>s.key));
  return QWER.find(k=>!used.has(k)) || 0x51;
}
function renderCombatSkills(){
  const box=$('#combatSkills'); if(!box) return;
  if(!combatSkillsData.length){
    box.innerHTML='<span class="hint-row" style="opacity:.6">No skills. Add a key (QWER) to rotate through.</span>';
    return;
  }
  box.innerHTML=combatSkillsData.map((sk,i)=>
    '<div class="skrow" data-i="'+i+'">'
    +'<span class="skn">'+(i+1)+'</span>'
    +'<input class="numin keyin sk-key" type="text" maxlength="1" value="'+esc(vkToChar(sk.key))+'" title="skill key">'
    +'<input class="numin sk-cd" type="number" step="50" min="0" value="'+(sk.cooldownMs??400)+'" title="cooldown ms">'
    +'<input class="numin sk-rg" type="number" step="1" min="0" max="200" value="'+(sk.range||0)+'" title="optional range (0 = global)">'
    +'<input class="numin sk-min" type="number" step="1" min="1" max="20" value="'+(sk.minTargets||1)+'" title="only fire with at least this many hostiles in range (AoE)">'
    +'<input class="numin sk-hp" type="number" step="5" min="0" max="100" value="'+(sk.hpBelowPct||0)+'" title="only fire while your life is under this % (0 = always)">'
    +'<input type="checkbox" class="sk-rare" '+(sk.rareOnly?'checked':'')+' title="only against rare / unique">'
    +'<input type="checkbox" class="sk-on" '+(sk.enabled===false?'':'checked')+' title="enabled">'
    +'<button type="button" class="delbtn sk-del">Remove</button></div>'
    +'<div class="skrow skcombo" data-i="'+i+'"><span class="skn"></span>'
    +'<label>×<input class="numin sk-rep" type="number" step="1" min="1" max="10" value="'+(sk.repeat||1)+'" title="tap the key this many times"></label>'
    +'<label>cast ms<input class="numin sk-gap" type="number" step="10" min="60" max="2000" value="'+(sk.repeatGapMs||150)+'" title="ms between repeated casts — also the wait before a dodge so the last cast finishes"></label>'
    +'<label>hold<input class="numin sk-hold" type="number" step="50" min="0" max="10000" value="'+(sk.holdMs||0)+'" title="ms to hold the key (0 = tap)"></label>'
    +'<label><input type="checkbox" class="sk-dodge" '+(sk.dodgeAfter?'checked':'')+'> dodge after</label>'
    +'<label>then wait<input class="numin sk-next" type="number" step="50" min="0" max="10000" value="'+(sk.nextDelayMs||0)+'" title="ms the whole rotation waits after this cast"></label></div>'
  ).join('');
  $$('#combatSkills .skcombo').forEach(row=>{
    const i=+row.dataset.i, sk=combatSkillsData[i]; if(!sk) return;
    const num=(cls,key,lo,hi)=>{ row.querySelector(cls).onchange=e=>{ const v=parseInt(e.target.value,10); if(!isNaN(v)){ sk[key]=Math.max(lo,Math.min(hi,v)); saveCombatSkills(); } }; };
    num('.sk-rep','repeat',1,10); num('.sk-gap','repeatGapMs',30,2000); num('.sk-hold','holdMs',0,10000); num('.sk-next','nextDelayMs',0,10000);
    row.querySelector('.sk-dodge').onchange=e=>{ sk.dodgeAfter=!!e.target.checked; saveCombatSkills(); };
  });
  $$('#combatSkills .skrow:not(.skcombo)').forEach(row=>{
    const i=+row.dataset.i, sk=combatSkillsData[i]; if(!sk) return;
    row.querySelector('.sk-key').onchange=e=>{ const vk=charToVk(e.target.value); if(vk){ sk.key=vk; saveCombatSkills(); } e.target.value=vkToChar(sk.key); };
    row.querySelector('.sk-cd').onchange=e=>{ const v=parseFloat(e.target.value); if(!isNaN(v)){ sk.cooldownMs=Math.max(0,v); saveCombatSkills(); } };
    row.querySelector('.sk-rg').onchange=e=>{ const v=parseFloat(e.target.value); if(!isNaN(v)){ sk.range=Math.max(0,v); saveCombatSkills(); } };
    row.querySelector('.sk-min').onchange=e=>{ const v=parseInt(e.target.value,10); if(!isNaN(v)){ sk.minTargets=Math.max(1,v); saveCombatSkills(); } };
    row.querySelector('.sk-hp').onchange=e=>{ const v=parseFloat(e.target.value); if(!isNaN(v)){ sk.hpBelowPct=Math.max(0,Math.min(100,v)); saveCombatSkills(); } };
    row.querySelector('.sk-rare').onchange=e=>{ sk.rareOnly=!!e.target.checked; saveCombatSkills(); };
    row.querySelector('.sk-on').onchange=e=>{ sk.enabled=!!e.target.checked; saveCombatSkills(); };
    row.querySelector('.sk-del').onclick=()=>{ combatSkillsData.splice(i,1); renderCombatSkills(); saveCombatSkills(); };
  });
}

/* ── icon / HP-bar / mechanics editors (nested objects: POST the whole {styles}/{hpBars}) ── */
let styles=null, hpBars=null, terrain=null;
const ICON_KEYS=[
  ['monsterNormal','Monster · Normal'],['monsterMagic','Monster · Magic'],
  ['monsterRare','Monster · Rare'],['monsterUnique','Monster · Unique'],
  ['player','Player'],['npc','NPC'],['chestRare','Chest · Rare'],
  ['chestUnique','Chest · Unique'],['transition','Transition'],
  ['poi','Point of Interest'],['landmark','Landmark']];
const esc=s=>(s||'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
const pct=o=>Math.round((o==null?1:o)*100);

/* ── SVG icon library (served by /api/icons): drives both the in-page previews and the picker grid. ── */
let ICONS=[]; const ICONMAP={};
async function loadIcons(){
  try{ ICONS=await getJSON('/api/icons')||[]; }catch(e){ ICONS=[]; }
  for(const k in ICONMAP) delete ICONMAP[k];
  ICONS.forEach(d=>ICONMAP[(d.name||'').toLowerCase()]=d);
}
const iconDef=name=>ICONMAP[(name||'').toLowerCase()]||null;
function iconSvg(name,color){
  const d=iconDef(name); if(!d) return '';
  const c=color||'currentColor';
  return `<svg viewBox="${d.viewBox}" preserveAspectRatio="xMidYMid meet">`
    + (d.paths||[]).map(p=>`<path d="${esc(p)}" fill="${c}"/>`).join('') + `</svg>`;
}
function pickerHtml(name,color){
  const d=iconDef(name), nm=d?d.name:(name||'Circle');
  return `<span class="iconpick" data-val="${esc(nm)}"><span class="ipreview" style="color:${color||'var(--ink)'}">`
    + iconSvg(nm,color) + `</span><span class="ipname">${esc(nm)}</span><span class="ipcar">▼</span></span>`;
}
function refreshPicker(pk,name,color){
  const d=iconDef(name), nm=d?d.name:(name||'Circle');
  pk.dataset.val=nm;
  const pv=pk.querySelector('.ipreview'); pv.style.color=color||'var(--ink)'; pv.innerHTML=iconSvg(nm,color);
  pk.querySelector('.ipname').textContent=nm;
}
let _iconPop=null;
function ensureIconPop(){
  if(_iconPop) return _iconPop;
  _iconPop=document.createElement('div'); _iconPop.id='iconPop'; document.body.appendChild(_iconPop);
  document.addEventListener('mousedown',e=>{
    if(_iconPop.classList.contains('open') && !_iconPop.contains(e.target) && !e.target.closest('.iconpick')) _iconPop.classList.remove('open');
  });
  return _iconPop;
}
function openIconPicker(anchor,current,cb){
  const pop=ensureIconPop();
  pop.innerHTML='<div class="ipop-grid">'+ICONS.map(d=>
    `<div class="ipop-cell${d.name.toLowerCase()===(current||'').toLowerCase()?' sel':''}" data-n="${esc(d.name)}" title="${esc(d.name)}">`
    + iconSvg(d.name) + `<span class="cn">${esc(d.name)}</span></div>`).join('')+'</div>';
  pop.querySelectorAll('.ipop-cell').forEach(c=>c.onclick=()=>{ pop.classList.remove('open'); cb(c.dataset.n); });
  pop.classList.add('open');
  const r=anchor.getBoundingClientRect(), pw=pop.offsetWidth, ph=pop.offsetHeight;
  let left=Math.min(r.left, innerWidth-8-pw), top=r.bottom+4;
  if(top+ph>innerHeight-8) top=Math.max(8, r.top-4-ph);
  pop.style.left=Math.max(8,left)+'px'; pop.style.top=top+'px';
}
const saveStyles=()=>{ if(styles) saveSetting('styles',styles); };
const saveHpBars=()=>{ if(hpBars) saveSetting('hpBars',hpBars); };

function renderHpBars(){
  if(!hpBars) return;
  $$('[data-hp]').forEach(el=>{ if(hpBars[el.dataset.hp]!==undefined) el.value=hpBars[el.dataset.hp]; });
  $$('[data-hpcolor]').forEach(el=>{ el.value=hpBars[el.dataset.hpcolor]||'#ffffff'; });
}
function wireHpBars(){
  $$('[data-hp]').forEach(el=>{ el.onchange=()=>{ const v=parseFloat(el.value); if(!isNaN(v)&&hpBars){ hpBars[el.dataset.hp]=v; saveHpBars(); } }; });
  $$('[data-hpcolor]').forEach(el=>{ el.onchange=()=>{ if(hpBars){ hpBars[el.dataset.hpcolor]=el.value; saveHpBars(); } }; });
}

/* ── terrain color/transparency (POSTs the whole {terrain} object; rebuilds the terrain bitmap) ── */
const saveTerrain=()=>{ if(terrain) saveSetting('terrain',terrain); };
function renderTerrain(){
  if(!terrain) return;
  $$('[data-tcolor]').forEach(el=>{ el.value=terrain[el.dataset.tcolor]||'#ffffff'; });
  $$('[data-topacity]').forEach(el=>{ el.value=Math.round((terrain[el.dataset.topacity]??1)*100); });
  $$('[data-topv]').forEach(el=>{ el.textContent=Math.round((terrain[el.dataset.topv]??1)*100)+'%'; });
}
function wireTerrain(){
  $$('[data-tcolor]').forEach(el=>{ el.onchange=()=>{ if(terrain){ terrain[el.dataset.tcolor]=el.value; saveTerrain(); } }; });
  $$('[data-topacity]').forEach(el=>{
    const k=el.dataset.topacity, v=$(`[data-topv="${k}"]`);
    el.oninput=()=>{ if(v) v.textContent=el.value+'%'; };
    el.onchange=()=>{ if(terrain){ terrain[k]=(+el.value)/100; saveTerrain(); } };
  });
}

function iconRow(key,label,o){
  return `<div class="stylerow" data-k="${key}">
    <label class="sw"><input type="checkbox" class="i-en"${o.enabled?' checked':''}><span class="track"></span><span class="knob"></span></label>
    <span class="nm">${label}</span>
    ${pickerHtml(o.shape,o.color)}
    <input type="color" class="i-color" value="${o.color||'#ffffff'}">
    <input type="range" class="op i-op" min="0" max="100" value="${pct(o.opacity)}">
    <span class="opv">${pct(o.opacity)}%</span>
    <input type="number" class="numin sz i-size" step="0.1" min="0.5" value="${o.size}">
  </div>`;
}
function renderIcons(){
  if(!styles){ $('#iconStyles').innerHTML=''; return; }
  $('#iconStyles').innerHTML=ICON_KEYS.map(([k,l])=>iconRow(k,l,styles[k]||{})).join('');
  $$('#iconStyles .stylerow').forEach(row=>{
    const o=styles[row.dataset.k]; if(!o) return;
    const pk=row.querySelector('.iconpick');
    row.querySelector('.i-en').onchange=e=>{ o.enabled=e.target.checked; saveStyles(); };
    pk.onclick=()=>openIconPicker(pk,o.shape,n=>{ o.shape=n; refreshPicker(pk,n,o.color); saveStyles(); });
    row.querySelector('.i-color').onchange=e=>{ o.color=e.target.value; refreshPicker(pk,o.shape,o.color); saveStyles(); };
    const op=row.querySelector('.i-op'), opv=row.querySelector('.opv');
    op.oninput=()=>{ opv.textContent=op.value+'%'; };
    op.onchange=()=>{ o.opacity=(+op.value)/100; saveStyles(); };
    row.querySelector('.i-size').onchange=e=>{ const v=parseFloat(e.target.value); if(!isNaN(v)){ o.size=v; saveStyles(); } };
  });
}

/* Entity categories a mechanic rule can be gated to (value = Poe2Live.EntityCategory name). Empty
   selection = applies to every category. Labels are friendlier than the raw enum names. */
const MECH_CATS=[['Monster','Monsters'],['Chest','Chests'],['Other','Misc / POI'],
  ['Object','Terrain'],['Npc','NPCs'],['Transition','Transitions']];
function mechRow(m,i){
  const cats=m.categories||[];
  return `<div class="mechrow" data-i="${i}">
    <div class="top">
      <label class="sw"><input type="checkbox" class="m-en"${m.enabled?' checked':''}><span class="track"></span><span class="knob"></span></label>
      <input class="mname" placeholder="Name (e.g. Expedition)" value="${esc(m.name)}">
      <button class="delbtn m-del">Remove</button>
    </div>
    <input class="matchin m-match" placeholder="match terms, comma-separated (e.g. Strongbox, StrongBoxes)" value="${esc((m.match||[]).join(', '))}">
    <div class="mcats"><span class="mcats-lbl">Applies to</span>${MECH_CATS.map(([v,l])=>
      `<label class="catchip${cats.includes(v)?' on':''}"><input type="checkbox" class="m-cat" data-cat="${v}"${cats.includes(v)?' checked':''}>${l}</label>`).join('')}
      <span class="mcats-hint">${cats.length?'':'all types'}</span></div>
    <div class="ctl">
      ${pickerHtml(m.shape,m.color)}
      <input type="color" class="m-color" value="${m.color||'#ffffff'}">
      <input type="range" class="op m-op" min="0" max="100" value="${pct(m.opacity)}">
      <span class="opv">${pct(m.opacity)}%</span>
      <input type="number" class="numin sz m-size" step="0.1" min="0.5" value="${m.size}">
    </div>
  </div>`;
}
function renderMechanics(){
  if(!styles){ $('#mechList').innerHTML=''; return; }
  styles.mechanics=styles.mechanics||[];
  $('#mechList').innerHTML=styles.mechanics.map((m,i)=>mechRow(m,i)).join('');
  $$('#mechList .mechrow').forEach(row=>{
    const m=styles.mechanics[+row.dataset.i]; if(!m) return;
    const pk=row.querySelector('.iconpick');
    row.querySelector('.m-en').onchange=e=>{ m.enabled=e.target.checked; saveStyles(); };
    row.querySelector('.mname').onchange=e=>{ m.name=e.target.value; saveStyles(); };
    row.querySelector('.m-match').onchange=e=>{ m.match=e.target.value.split(',').map(s=>s.trim()).filter(Boolean); saveStyles(); };
    row.querySelectorAll('.m-cat').forEach(cb=>{ cb.onchange=()=>{
      m.categories=[...row.querySelectorAll('.m-cat:checked')].map(c=>c.dataset.cat);
      cb.closest('.catchip').classList.toggle('on',cb.checked);
      const h=row.querySelector('.mcats-hint'); if(h) h.textContent=m.categories.length?'':'all types';
      saveStyles(); }; });
    pk.onclick=()=>openIconPicker(pk,m.shape,n=>{ m.shape=n; refreshPicker(pk,n,m.color); saveStyles(); });
    row.querySelector('.m-color').onchange=e=>{ m.color=e.target.value; refreshPicker(pk,m.shape,m.color); saveStyles(); };
    const op=row.querySelector('.m-op'), opv=row.querySelector('.opv');
    op.oninput=()=>{ opv.textContent=op.value+'%'; };
    op.onchange=()=>{ m.opacity=(+op.value)/100; saveStyles(); };
    row.querySelector('.m-size').onchange=e=>{ const v=parseFloat(e.target.value); if(!isNaN(v)){ m.size=v; saveStyles(); } };
    row.querySelector('.m-del').onclick=()=>{ styles.mechanics.splice(+row.dataset.i,1); renderMechanics(); saveStyles(); };
  });
}
/* ── Rules tab: unified Display Rules + Hidden cull patterns ── */
let hidden=[], drules=[];
function flashF(){ const m=$('#savedMsgF'); if(!m) return; m.classList.add('show'); clearTimeout(m._t); m._t=setTimeout(()=>m.classList.remove('show'),1100); }
async function postHidden(body){ try{ await fetch('/api/hidden',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)}); flashF(); }catch(e){} }
async function loadFilters(){
  await loadModVocab();   // populate the mods autocomplete BEFORE rendering rule rows reference it
  await loadDrules();
  try{ const h=await getJSON('/api/hidden'); hidden=h.patterns||[]; }catch(e){ hidden=[]; }
  renderHidden();
}
/* The persistent monster-mod catalog feeds the <datalist> the Mods matcher autocompletes against, so
   you can pick a known aura/buff id instead of recalling it. Refreshed each time the Rules tab loads. */
async function loadModVocab(){
  let mods=[]; try{ const r=await getJSON('/api/mods'); mods=(r&&r.mods)||[]; }catch(_){ mods=[]; }
  let dl=document.getElementById('modVocab');
  if(!dl){ dl=document.createElement('datalist'); dl.id='modVocab'; document.body.appendChild(dl); }
  dl.innerHTML=mods.map(m=>`<option value="${esc(m)}">`).join('');
}

/* ── Display Rules: the unified ordered ruleset. The page holds the array, edits it, and re-POSTs
   the WHOLE list on any change (add / remove / reorder / toggle / field) — same pattern styles used. ── */
const DR_CATS=['Monster','Chest','Npc','Object','Other','Transition','Player','Tile'];
const DR_SELECTS=[['rarity','Rarity',['Normal','Magic','Rare','Unique']],['reaction','Reaction',['Hostile','Friendly']],
  ['life','Life',['Alive','Dead']],['chest','Chest',['Opened','Unopened']],['poi','POI',['Yes','No']],['encounter','Encounter',['Active','Complete']]];
async function saveDrules(){ try{ await fetch('/api/display-rules',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({rules:drules})}); flashF(); }catch(e){} }
async function loadDrules(){ try{ const r=await getJSON('/api/display-rules'); drules=r.rules||[]; }catch(e){ drules=[]; } renderDrules(); }
function drSel(f,l,o,cur){ return `<label class="drsel">${l}<select class="dr-cond" data-f="${f}"><option value=""${!cur?' selected':''}>any</option>`
  +o.map(x=>`<option${cur===x?' selected':''}>${x}</option>`).join('')+`</select></label>`; }
/* Concise matcher→action summary shown on the collapsed row so the list stays scannable. */
function drSummary(r){
  const p=[];
  p.push((r.categories&&r.categories.length)?r.categories.join('/'):'any type');
  if(r.match&&r.match.length) p.push('“'+r.match.join(', ')+'”');
  if(r.mods&&r.mods.length) p.push('mods: '+r.mods.join(', '));
  ['rarity','reaction','life','chest','poi','encounter'].forEach(f=>{ if(r[f]) p.push(r[f]); });
  return esc(p.join(' · '));
}
function drRow(r,i){
  const open=!!r._open, cats=r.categories||[];
  const badges=(r.hide?'<span class="drbadge hide">hide</span>':'')
    +(r.navigable?'<span class="drbadge">path</span>':'');
  const body=open?`<div class="drbody">
      <div class="top"><input class="mname dr-name" value="${esc(r.name)}" placeholder="rule name"></div>
      <input class="matchin dr-match" placeholder="match: metadata terms, comma-separated (blank = any)" value="${esc((r.match||[]).join(', '))}">
      <input class="matchin dr-mods" list="modVocab" placeholder="monster mods: aura/buff terms, comma-separated (e.g. Aura, ManaSiphon) — blank = any" value="${esc((r.mods||[]).join(', '))}">
      <div class="mcats"><span class="mcats-lbl">Type</span>${DR_CATS.map(c=>
        `<label class="catchip${cats.includes(c)?' on':''}"><input type="checkbox" class="dr-cat" data-cat="${c}"${cats.includes(c)?' checked':''}>${c}</label>`).join('')}</div>
      <div class="drconds">${DR_SELECTS.map(([f,l,o])=>drSel(f,l,o,r[f])).join('')}</div>
      <div class="ctl">
        <label class="drflag dr-hideflag" title="hide matching entities entirely"><input type="checkbox" class="dr-hide"${r.hide?' checked':''}> Hide</label>
        ${pickerHtml(r.shape,r.color)}
        <input type="color" class="dr-color" value="${r.color||'#ffffff'}">
        <input type="range" class="op dr-op" min="0" max="100" value="${pct(r.opacity)}"><span class="opv">${pct(r.opacity)}%</span>
        <input type="number" class="numin sz dr-size" step="0.1" min="0.5" value="${r.size}">
        <input class="mname dr-label" style="flex:1;min-width:70px" value="${esc(r.label||'')}" placeholder="label (optional)">
        <label class="drflag" title="qualify as an auto-path navigation target"><input type="checkbox" class="dr-nav"${r.navigable?' checked':''}> Auto-path</label>
      </div>
    </div>`:'';
  return `<div class="mechrow drrow${r.hide?' hideon':''}${open?' open':''}${r.enabled?'':' off'}" data-i="${i}">
    <div class="drhead">
      <label class="sw" title="enabled"><input type="checkbox" class="dr-en"${r.enabled?' checked':''}><span class="track"></span><span class="knob"></span></label>
      <span class="drcaret">${open?'▾':'▸'}</span>
      <span class="drswatch" style="color:${r.color||'#fff'}">${r.hide?'':iconSvg(r.shape,r.color)}</span>
      <span class="drnm">${esc(r.name||'(unnamed)')}</span>
      <span class="drsum">${drSummary(r)}</span>
      <span class="drbadges">${badges}</span>
      <span class="drord"><button class="ordbtn dr-up" title="higher precedence">▲</button><button class="ordbtn dr-dn" title="lower precedence">▼</button></span>
      <button class="delbtn dr-del" title="remove">✕</button>
    </div>
    ${body}
  </div>`;
}
function renderDrules(){
  const host=$('#drList'); if(!host) return;
  host.innerHTML = drules.length ? drules.map(drRow).join('') : '<div class="row"><div class="rl hint-row">No display rules yet. Add one below.</div></div>';
  $$('#drList .drrow').forEach(row=>{
    const i=+row.dataset.i, r=drules[i]; if(!r) return;
    const save=saveDrules;
    // Header (always present): click anywhere except a control toggles expand.
    row.querySelector('.drhead').onclick=e=>{ if(e.target.closest('input,button,select,label,.drord')) return; r._open=!r._open; renderDrules(); };
    row.querySelector('.dr-en').onchange=e=>{ r.enabled=e.target.checked; row.classList.toggle('off',!r.enabled); save(); };
    row.querySelector('.dr-up').onclick=()=>{ if(i>0){ const t=drules[i-1]; drules[i-1]=drules[i]; drules[i]=t; renderDrules(); save(); } };
    row.querySelector('.dr-dn').onclick=()=>{ if(i<drules.length-1){ const t=drules[i+1]; drules[i+1]=drules[i]; drules[i]=t; renderDrules(); save(); } };
    row.querySelector('.dr-del').onclick=()=>{ drules.splice(i,1); renderDrules(); save(); };
    if(!r._open) return; // body controls only exist when expanded
    const pk=row.querySelector('.iconpick');
    row.querySelector('.dr-name').onchange=e=>{ r.name=e.target.value; save(); };
    row.querySelector('.dr-match').onchange=e=>{ r.match=e.target.value.split(',').map(s=>s.trim()).filter(Boolean); save(); };
    row.querySelector('.dr-mods').onchange=e=>{ r.mods=e.target.value.split(',').map(s=>s.trim()).filter(Boolean); save(); };
    row.querySelectorAll('.dr-cat').forEach(cb=>cb.onchange=()=>{ r.categories=[...row.querySelectorAll('.dr-cat:checked')].map(c=>c.dataset.cat); cb.closest('.catchip').classList.toggle('on',cb.checked); save(); });
    row.querySelectorAll('.dr-cond').forEach(sel=>sel.onchange=()=>{ r[sel.dataset.f]=sel.value||null; save(); });
    row.querySelector('.dr-hide').onchange=e=>{ r.hide=e.target.checked; row.classList.toggle('hideon',r.hide); save(); };
    pk.onclick=()=>openIconPicker(pk,r.shape,n=>{ r.shape=n; refreshPicker(pk,n,r.color); save(); });
    row.querySelector('.dr-color').onchange=e=>{ r.color=e.target.value; refreshPicker(pk,r.shape,r.color); save(); };
    const op=row.querySelector('.dr-op'),opv=row.querySelector('.opv'); op.oninput=()=>opv.textContent=op.value+'%'; op.onchange=()=>{ r.opacity=(+op.value)/100; save(); };
    row.querySelector('.dr-size').onchange=e=>{ const v=parseFloat(e.target.value); if(!isNaN(v)){ r.size=v; save(); } };
    row.querySelector('.dr-label').onchange=e=>{ r.label=e.target.value; save(); };
    row.querySelector('.dr-nav').onchange=e=>{ r.navigable=e.target.checked; save(); };
  });
}
$('#drAdd')?.addEventListener('click',()=>{ drules.push({enabled:true,name:'New rule',categories:[],match:[],shape:'Circle',color:'#ffd926',opacity:1,size:4,_open:true}); renderDrules(); saveDrules(); });

""";
}
