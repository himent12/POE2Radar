namespace POE2Radar.Overlay.Web;

/// <summary>Dashboard page — client script, second half (editors + boot).</summary>
internal static partial class DashboardHtml
{
    private const string Script2 = """
/* ── Add-rule picker: browse the area's live ENTITIES + terrain TILE names + monster MODS, filter,
   click to seed a rule (entity → entity rule by category; tile → Tile rule; mod → Monster rule whose
   Mods matcher targets that affix id). Removes the guesswork of typing metadata/mod ids. ── */
let _pickEl=null, _pickEnts=[], _pickTiles=[], _pickKind='all', _pickQ='';
const lastSeg=s=>((s||'').split('/').pop()||'').replace(/@\d+$/,'').replace(/\.tdt$/i,'');
function ensurePick(){
  if(_pickEl) return _pickEl;
  _pickEl=document.createElement('div'); _pickEl.id='pickPop';
  _pickEl.innerHTML=`<div class="pickbox">
    <div class="pickhead">
      <input id="pickSearch" type="search" placeholder="filter by name / metadata / tile path / mod id…">
      <span class="pickkinds"><button class="chip on" data-k="all">All</button><button class="chip" data-k="entity">Entities</button><button class="chip" data-k="tile">Tiles</button><button class="chip" data-k="mod">Mods</button></span>
      <button class="pickclose" title="close">✕</button>
    </div>
    <div class="picklist" id="pickList"></div>
    <div class="pickfoot">Click a target to add a rule for it (opens expanded to refine). Entities seed an entity rule; tiles seed a Tile rule; mods seed a Monster rule matching that affix.</div>
  </div>`;
  document.body.appendChild(_pickEl);
  _pickEl.querySelector('.pickclose').onclick=()=>_pickEl.classList.remove('open');
  _pickEl.onclick=e=>{ if(e.target===_pickEl) _pickEl.classList.remove('open'); };
  _pickEl.querySelector('#pickSearch').oninput=e=>{ _pickQ=e.target.value.toLowerCase(); renderPick(); };
  _pickEl.querySelectorAll('.pickkinds .chip').forEach(c=>c.onclick=()=>{ _pickKind=c.dataset.k; _pickEl.querySelectorAll('.pickkinds .chip').forEach(x=>x.classList.toggle('on',x===c)); renderPick(); });
  return _pickEl;
}
async function openPicker(){
  const pop=ensurePick(); pop.classList.add('open');
  _pickQ=''; _pickKind='all';
  pop.querySelector('#pickSearch').value=''; pop.querySelectorAll('.pickkinds .chip').forEach((x,j)=>x.classList.toggle('on',j===0));
  $('#pickList').innerHTML='<div class="pickempty">Loading…</div>';
  try{ _pickEnts=await getJSON('/entities?limit=1000')||[]; }catch(_){ _pickEnts=[]; }
  try{ const t=await getJSON('/api/tiles'); _pickTiles=(t&&t.tiles)||[]; }catch(_){ _pickTiles=[]; }
  renderPick(); pop.querySelector('#pickSearch').focus();
}
/* Aggregate the live entities' affix-mod ids into distinct rows: one per mod id, with a carrier
   count and a few example monster names — so you can see which auras/buffs are actually in the zone
   right now and pick one to track. (Each entity lists a mod id at most once, so count = #monsters.) */
function pickMods(){
  const map=new Map(); // modId -> {count, names:Set}
  _pickEnts.forEach(e=>{ (e.mods||[]).forEach(m=>{ if(!m)return; let v=map.get(m); if(!v){v={count:0,names:new Set()}; map.set(m,v);} v.count++; const nm=e.name||lastSeg(e.metadata); if(nm) v.names.add(nm); }); });
  return [...map.entries()].sort((a,b)=>b[1].count-a[1].count).map(([m,v])=>({
    kind:'mod', cat:'Mod', name:m, modId:m, count:v.count,
    sub:[...v.names].slice(0,4).join(', ')||'monster affix',
  }));
}
function pickItems(){
  const q=_pickQ, out=[];
  if(_pickKind==='all'||_pickKind==='entity'){
    const seen=new Set();
    _pickEnts.forEach(e=>{ const k=e.category+'|'+e.metadata; if(seen.has(k))return; seen.add(k);
      if(q && !((e.metadata||'').toLowerCase().includes(q)||(e.name||'').toLowerCase().includes(q)||(e.category||'').toLowerCase().includes(q)))return;
      out.push({kind:'entity',cat:e.category,name:e.name||lastSeg(e.metadata),sub:e.metadata,rarity:e.rarity}); });
  }
  if(_pickKind==='all'||_pickKind==='tile'){
    _pickTiles.forEach(p=>{ if(q && !p.toLowerCase().includes(q))return; out.push({kind:'tile',cat:'Tile',name:lastSeg(p),sub:p}); });
  }
  if(_pickKind==='all'||_pickKind==='mod'){
    pickMods().forEach(it=>{ if(q && !(it.name.toLowerCase().includes(q)||it.sub.toLowerCase().includes(q)))return; out.push(it); });
  }
  return out;
}
function renderPick(){
  const items=pickItems(), list=$('#pickList');
  list.innerHTML = items.length ? items.slice(0,600).map((it,i)=>
    `<div class="pickrow" data-i="${i}"><span class="pickbadge ${it.kind}">${it.kind==='tile'?'TILE':it.kind==='mod'?'MOD':esc(it.cat)}</span>`
    +`<span class="picknm">${esc(it.name)}</span><span class="picksub">${esc(it.sub)}</span>`
    +(it.kind==='mod'?`<span class="pickcount">×${it.count}</span>`:'')
    +(it.rarity&&it.rarity!=='NonMonster'?`<span class="pickrar">${esc(it.rarity)}</span>`:'')+`</div>`).join('')
    : `<div class="pickempty">No matches${(_pickEnts.length+_pickTiles.length===0)?' — are you in game?':''}.</div>`;
  $$('#pickList .pickrow').forEach(row=>row.onclick=()=>pickItem(items[+row.dataset.i]));
}
function pickItem(it){
  if(!it) return;
  let r;
  if(it.kind==='tile')
    r={enabled:true,name:it.name,categories:['Tile'],match:[lastSeg(it.sub)],shape:'Diamond',color:'#f259f2',opacity:1,size:5,navigable:true,_open:true};
  else if(it.kind==='mod')
    r={enabled:true,name:it.name,categories:['Monster'],match:[],mods:[it.modId],shape:'Star',color:'#26d9c0',opacity:1,size:6,_open:true};
  else
    r={enabled:true,name:it.name,categories:[it.cat],match:[lastSeg(it.sub)],shape:'Star',color:'#ffd926',opacity:1,size:6,_open:true};
  drules.unshift(r); renderDrules(); saveDrules();
  _pickEl.classList.remove('open');
  const first=$('#drList .drrow'); if(first) first.scrollIntoView({block:'center'});
}
$('#drPick')?.addEventListener('click',openPicker);
function renderHidden(){
  $('#hideList').innerHTML = hidden.length ? hidden.map(p=>
    `<span class="chip on" data-p="${esc(p)}">${esc(p)} <b style="margin-left:5px;cursor:pointer">&#10005;</b></span>`).join('')
    : '<span style="color:var(--ink-faint);font-size:11px;font-style:italic">Nothing hidden.</span>';
  $$('#hideList .chip').forEach(c=>c.querySelector('b').onclick=()=>{ postHidden({remove:c.dataset.p}).then(loadFilters); });
}
$('#hideAdd').onclick=()=>{
  const p=$('#hidePattern').value.trim(); if(!p) return;
  $('#hidePattern').value='';
  postHidden({add:p}).then(loadFilters);
};
$('#hidePattern').onkeydown=e=>{ if(e.key==='Enter') $('#hideAdd').click(); };

/* ── Landmarks tab: view/edit the curated map-label table (baked + user overlay) + import/export ── */
let lmEntries=[], lmAreaOnly=true, lmQ='';
function flashL(){ const m=$('#savedMsgL'); if(!m) return; m.classList.add('show'); clearTimeout(m._t); m._t=setTimeout(()=>m.classList.remove('show'),1100); }
async function loadLandmarks(){
  try{ const r=await getJSON('/api/landmarks'); lmEntries=r.entries||[]; }catch(e){ lmEntries=[]; }
  const a=$('#lmArea'); if(a && !a.value) a.value=(state&&state.areaCode)||'';
  renderLandmarks();
}
async function postLandmarks(body){
  try{ const r=await fetch('/api/landmarks',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)}); const j=await r.json(); if(j&&j.entries) lmEntries=j.entries; flashL(); }catch(e){}
  renderLandmarks();
}
function lmRow(e){
  const badge=e.suppressed?'hidden':e.source;
  const del=e.suppressed?'Restore':(e.source==='user'?'Remove':'Hide');
  return `<div class="lmrow${e.suppressed?' sup':''}" data-area="${esc(e.area)}" data-pat="${esc(e.pattern)}">
    <span class="lmbadge ${badge}">${badge}</span>
    <span class="lmarea">${esc(e.area)}</span>
    <input class="mname lmlabel" value="${esc(e.label||'')}" placeholder="${e.suppressed?'(hidden)':'label'}">
    <span class="lmpath" title="${esc(e.pattern)}">${esc(e.pattern)}</span>
    <button class="delbtn lm-del">${del}</button>
  </div>`;
}
function renderLandmarks(){
  const host=$('#lmList'); if(!host) return;
  const area=(state&&state.areaCode)||'';
  const rows=lmEntries.filter(e=>{
    if(lmAreaOnly && e.area!=='*' && e.area!==area) return false;
    if(lmQ){ if(!((e.area+' '+e.pattern+' '+(e.label||'')).toLowerCase().includes(lmQ))) return false; }
    return true;
  });
  host.innerHTML = rows.length ? rows.map(lmRow).join('')
    : `<div class="row"><div class="rl hint-row">No curated landmarks${lmAreaOnly?' for this area ('+esc(area||'—')+')':''}. Add one below${lmAreaOnly?', or turn off &ldquo;This area only&rdquo;':''}.</div></div>`;
  $$('#lmList .lmrow').forEach(row=>{
    const area=row.dataset.area, pat=row.dataset.pat, e=lmEntries.find(x=>x.area===area&&x.pattern===pat); if(!e) return;
    row.querySelector('.lmlabel').onchange=ev=>postLandmarks({set:{area,pattern:pat,label:ev.target.value}});
    row.querySelector('.lm-del').onclick=()=>{
      if(e.suppressed || e.source==='user') postLandmarks({remove:{area,pattern:pat}}); // restore baked / delete user
      else postLandmarks({set:{area,pattern:pat,label:null}});                          // suppress a baked entry
    };
  });
}
$('#lmSearch')?.addEventListener('input',e=>{ lmQ=e.target.value.toLowerCase(); renderLandmarks(); });
$('#lmAreaOnly')?.addEventListener('click',()=>{ lmAreaOnly=!lmAreaOnly; $('#lmAreaOnly').classList.toggle('on',lmAreaOnly); renderLandmarks(); });
$('#lmAdd')?.addEventListener('click',()=>{
  const area=($('#lmArea').value||'').trim(), pat=($('#lmPat').value||'').trim(), label=($('#lmLabel').value||'').trim();
  if(!area||!pat||!label) return;
  $('#lmPat').value=''; $('#lmLabel').value='';
  postLandmarks({set:{area,pattern:pat,label}});
});
$('#lmExport')?.addEventListener('click',async()=>{
  try{ const txt=await (await fetch('/api/landmarks?export=1',{cache:'no-store'})).text();
    const a=document.createElement('a'); a.href=URL.createObjectURL(new Blob([txt],{type:'application/json'}));
    a.download='CustomLandmarks.json'; a.click(); URL.revokeObjectURL(a.href);
  }catch(e){}
});
$('#lmImport')?.addEventListener('click',()=>{
  const inp=document.createElement('input'); inp.type='file'; inp.accept='.json,application/json';
  inp.onchange=()=>{ const f=inp.files&&inp.files[0]; if(!f) return; const rd=new FileReader();
    rd.onload=()=>{ try{ postLandmarks({import:JSON.parse(rd.result)}); }catch(_){ alert('Invalid JSON file'); } };
    rd.readAsText(f); };
  inp.click();
});

/* ── atlas tab (read-only inspection of the map-data we can read) ── */
let atlasOptsWired=false, atlasGroupsData=[];
async function wireAtlasOpts(){
  let s; try{ s=await getJSON('/api/settings'); }catch(e){ return; }
  document.querySelectorAll('#atlasOpts [data-atset]').forEach(el=>{
    const k=el.dataset.atset;
    if(el.type==='checkbox'){ el.checked=!!s[k]; el.onchange=()=>saveSetting(k,el.checked); }
    else { el.value=s[k]; el.onchange=()=>saveSetting(k, parseFloat(el.value)); }
  });
  atlasGroupsData = Array.isArray(s.atlasGroups) ? s.atlasGroups.map(g=>({name:g.name||'',color:g.color||'#E0B341',maps:(g.maps||[]).slice()})) : [];
  renderAtlasGroups();
}
function saveAtlasGroups(){ saveSetting('atlasGroups', atlasGroupsData); }
function renderAtlasGroups(){
  const box=document.querySelector('#atlasGroups'); if(!box) return;
  if(atlasGroupsData.length===0){ box.innerHTML='<span class="hint-row" style="opacity:.6">No groups. Maps in a group draw in its colour when tracked.</span>'; return; }
  box.innerHTML = atlasGroupsData.map((g,i)=>
    '<div style="display:grid;grid-template-columns:130px 44px 1fr 60px;gap:8px;align-items:start;padding:5px 0;border-bottom:1px solid var(--line)">'
    +'<input data-gi="'+i+'" data-gf="name" value="'+esc(g.name)+'" placeholder="group name" style="width:100%">'
    +'<input data-gi="'+i+'" data-gf="color" type="color" value="'+esc(g.color)+'" style="width:40px;height:28px;padding:0;border:none;background:none">'
    +'<textarea data-gi="'+i+'" data-gf="maps" rows="2" placeholder="one map name per line" style="width:100%;resize:vertical">'+esc((g.maps||[]).join('\n'))+'</textarea>'
    +'<button class="chip" data-gdel="'+i+'">Delete</button></div>'
  ).join('');
  box.querySelectorAll('[data-gf]').forEach(el=>{
    const i=+el.dataset.gi, f=el.dataset.gf;
    el.onchange=()=>{ if(f==='maps') atlasGroupsData[i].maps=el.value.split('\n').map(x=>x.trim()).filter(Boolean); else atlasGroupsData[i][f]=el.value; saveAtlasGroups(); };
  });
  box.querySelectorAll('[data-gdel]').forEach(b=>b.onclick=()=>{ atlasGroupsData.splice(+b.dataset.gdel,1); renderAtlasGroups(); saveAtlasGroups(); });
}
document.querySelector('#atlasGroupAdd')?.addEventListener('click',()=>{ atlasGroupsData.push({name:'New group',color:'#E0B341',maps:[]}); renderAtlasGroups(); saveAtlasGroups(); });
async function loadAtlas(){
  $('#atlasStatus').textContent='reading…';
  if(!atlasOptsWired){ atlasOptsWired=true; wireAtlasOpts(); }
  try{ atlasData=await getJSON('/api/atlas'); }catch(e){ atlasData={located:false,note:'request failed'}; }
  renderAtlas();
}
function renderAtlas(){
  const d=atlasData; if(!d){ return; }
  const st=$('#atlasStatus'); const nd=d.nodes;
  if(!(nd&&nd.total)) st.textContent = d.note ? 'scanning…' : 'atlas closed — open it in-game + Refresh';
  else st.textContent = nd.total+' nodes · '+nd.hasContent+' with content · '
        +(d.allKinds?.length||0)+' kind / '+(d.allTags?.length||0)+' content / '+(d.allMaps?.length||0)+' map filters';
  // Seed active rules from the overlay (once): tracked + arrow sets. Then render the filter table.
  if(atlasHl===null){ atlasHl=new Set((d.highlightTags||[]).map(t=>t.toLowerCase())); atlasNav=new Set((d.navTags||[]).map(t=>t.toLowerCase())); atlasArrow=new Set((d.arrowTags||[]).map(t=>t.toLowerCase())); }
  renderAtlasHighlight(d);
}
// Biome index → friendly-ish label (best-effort; index is the ground truth).
const BIOMES=['Grass','Sand','Swamp','Forest','Snow','Stone','Volcanic','Coast','Cave','Vaal','Water','Desert','Special'];
const biomeName=i=>(i>=0&&i<BIOMES.length)?BIOMES[i]:('biome '+i);

// Highlight-rule chips: one per distinct content tag on the atlas. Click to toggle → ONLY matching maps
// are drawn in-game. Active set is pushed to the overlay (persisted there).
// Classify a filter row into a category for the table (and grouping/colour).
function catContent(t){ const s=t.toLowerCase(); if(/not shown|\[dnt\]/.test(s))return'Hidden'; if(/boss/.test(s))return'Boss'; if(/influence/.test(s))return'Influence'; return'Mechanic'; }
function catMap(t){ const s=t.toLowerCase(); if(/citadel/.test(s))return'Citadel'; if(/tower/.test(s))return'Tower'; if(/temple/.test(s))return'Temple'; if(/vaal/.test(s))return'Vaal'; return'Map'; }
// Per-category colour (badge tint).
const CATCOL={Boss:'#e0533a',Mechanic:'#3ca0ff',Influence:'#a06cff',Hidden:'#ff5db1',Citadel:'#e0b341',Tower:'#2fb6a8',Temple:'#d98a2b',Vaal:'#c0395a',Unique:'#c678dd',Merchant:'#5aa9e6',Map:'#8a93a0',Type:'#d98a2b'};
function catBadge(cat){ const c=CATCOL[cat]||'#8a93a0'; return '<span style="display:inline-block;padding:1px 8px;border-radius:10px;font-size:11px;font-weight:600;background:'+c+'26;color:'+c+';border:1px solid '+c+'66">'+esc(cat)+'</span>'; }
// Build the unified filter list (content + map) with {title,count,cat,group}.
function atlasFilterRows(d){
  const rows=[];
  // Kind rows first: tracking one (e.g. "Tower") rings + routes to EVERY map of that archetype.
  (d.allKinds||[]).forEach(t=>rows.push({title:t.tag,count:t.count,group:'Kind',cat:t.tag}));
  // Type rows (#7): maps.json type/tags — unique / lineage / arbiter. One-click route-to-all-of-a-kind.
  (d.allDataTags||[]).forEach(t=>rows.push({title:t.tag,count:t.count,group:'Type',cat:'Type'}));
  (d.allTags||[]).forEach(t=>rows.push({title:t.tag,count:t.count,group:'Content',cat:catContent(t.tag),desc:t.desc}));
  (d.allMaps||[]).forEach(t=>rows.push({title:t.tag,count:t.count,group:'Map',cat:catMap(t.tag)}));
  return rows;
}
let atlasHlSort={key:'count',dir:-1};
function renderAtlasHighlight(d){
  const box=$('#atlasHlTable'); if(!box) return;
  let rows=atlasFilterRows(d);
  if(rows.length===0){ box.innerHTML='<span class="hint-row" style="padding:8px;display:block">No filters yet (open the Atlas + Refresh).</span>'; updateHlCount(); return; }
  if(atlasGroup!=='all') rows=rows.filter(r=>r.group===atlasGroup);
  const flt=($('#atlasHlFilter')?.value||'').trim().toLowerCase();
  if(flt) rows=rows.filter(r=>r.title.toLowerCase().includes(flt)||r.cat.toLowerCase().includes(flt)||r.group.toLowerCase().includes(flt));
  if(atlasHlSelOnly) rows=rows.filter(r=>{const k=r.title.toLowerCase(); return atlasHl.has(k)||atlasNav.has(k)||atlasArrow.has(k);});
  const k=atlasHlSort.key, dir=atlasHlSort.dir;
  rows.sort((a,b)=>{
    const ak=a.title.toLowerCase(), bk=b.title.toLowerCase();
    let v;
    if(k==='count') v=a.count-b.count;
    else if(k==='trk') v=(atlasHl.has(ak)?1:0)-(atlasHl.has(bk)?1:0);
    else if(k==='nav') v=(atlasNav.has(ak)?1:0)-(atlasNav.has(bk)?1:0);
    else if(k==='arw') v=(atlasArrow.has(ak)?1:0)-(atlasArrow.has(bk)?1:0);
    else v=(''+a[k]).localeCompare(''+b[k]);
    return v*dir || a.title.localeCompare(b.title);
  });
  const sa=key=> atlasHlSort.key===key ? (atlasHlSort.dir<0?' ▼':' ▲') : '';
  const cell='display:grid;grid-template-columns:30px 30px 34px 1fr 50px 90px;gap:8px;align-items:center;padding:5px 9px';
  let html='<div style="'+cell+';position:sticky;top:0;background:var(--panel,#1a1a1a);border-bottom:1px solid var(--line);font-weight:600;font-size:11px;text-transform:uppercase;opacity:.75">'
    +'<span data-sort="trk" title="Highlight: ring the map in-game (click to sort)" style="cursor:pointer">&#9745;'+sa('trk')+'</span>'
    +'<span data-sort="nav" title="Nav-to: draw a route to it (click to sort)" style="cursor:pointer">&#8674;'+sa('nav')+'</span>'
    +'<span data-sort="arw" title="Arrow: edge arrow toward it when off-screen (click to sort)" style="cursor:pointer">&#10148;'+sa('arw')+'</span>'
    +'<span data-sort="title" style="cursor:pointer">Title'+sa('title')+'</span>'
    +'<span data-sort="count" style="cursor:pointer;text-align:right">Count'+sa('count')+'</span>'
    +'<span data-sort="cat" style="cursor:pointer">Category'+sa('cat')+'</span></div>';
  html+=rows.map(r=>{
    const key=r.title.toLowerCase(); const trk=atlasHl.has(key), nav=atlasNav.has(key), arw=atlasArrow.has(key);
    return '<div class="hlrow" data-tag="'+esc(r.title)+'" title="click row = toggle Highlight" style="'+cell+';cursor:pointer;border-bottom:1px solid var(--line)'+((trk||nav||arw)?';background:rgba(60,160,255,.14)':'')+'">'
      +'<span style="font-size:15px">'+(trk?'☑':'☐')+'</span>'
      +'<span class="hlnav" data-tag="'+esc(r.title)+'" title="toggle nav-to (route)" style="font-size:15px;cursor:pointer;color:'+(nav?'#3ddc97':'#4a525c')+'">&#8674;</span>'
      +'<span class="hlarw" data-tag="'+esc(r.title)+'" title="toggle off-screen arrow" style="font-size:15px;cursor:pointer;color:'+(arw?'#e0b341':'#4a525c')+'">➤</span>'
      +'<span title="'+esc(r.desc||r.title)+'">'+esc(r.title)+'</span>'
      +'<span class="amono" style="text-align:right">'+r.count+'</span>'
      +'<span>'+catBadge(r.cat)+'</span></div>';
  }).join('');
  box.innerHTML=html;
  $$('#atlasHlTable [data-sort]').forEach(h=>h.onclick=()=>{ const key=h.dataset.sort; if(atlasHlSort.key===key) atlasHlSort.dir*=-1; else atlasHlSort={key,dir:(key==='count'||key==='trk'||key==='nav'||key==='arw')?-1:1}; renderAtlasHighlight(d); });
  $$('#atlasHlTable .hlnav[data-tag]').forEach(a=>a.onclick=e=>{
    e.stopPropagation(); const key=a.dataset.tag.toLowerCase();
    if(atlasNav.has(key)) atlasNav.delete(key); else atlasNav.add(key);
    renderAtlasHighlight(d); postAtlasHighlight();
  });
  $$('#atlasHlTable .hlarw[data-tag]').forEach(a=>a.onclick=e=>{
    e.stopPropagation(); const key=a.dataset.tag.toLowerCase();
    if(atlasArrow.has(key)) atlasArrow.delete(key); else atlasArrow.add(key);
    renderAtlasHighlight(d); postAtlasHighlight();
  });
  $$('#atlasHlTable .hlrow[data-tag]').forEach(row=>row.onclick=()=>{
    const key=row.dataset.tag.toLowerCase();
    if(atlasHl.has(key)) atlasHl.delete(key); else atlasHl.add(key);
    renderAtlasHighlight(d); postAtlasHighlight();
  });
  updateHlCount();
}
// Active-rule chips: one removable chip per tag that has any toggle on, showing which (✓⇢➤). Click ✕ to drop it.
function updateHlCount(){
  const box=$('#atlasActive'); if(!box) return;
  const keys=new Set([...(atlasHl||[]),...(atlasNav||[]),...(atlasArrow||[])]);
  if(keys.size===0){ box.innerHTML='<span class="hint-row" style="opacity:.6">No active rules &mdash; click a row or a Quick set.</span>'; return; }
  // Recover original-case titles from the data.
  const titleOf={}; (atlasData?atlasFilterRows(atlasData):[]).forEach(r=>titleOf[r.title.toLowerCase()]=r.title);
  const chip=k=>{ const t=titleOf[k]||k; const marks=(atlasHl.has(k)?'<span title="Highlight">&#9745;</span>':'')+(atlasNav.has(k)?'<span style="color:#3ddc97" title="Nav">&#8674;</span>':'')+(atlasArrow.has(k)?'<span style="color:#e0b341" title="Arrow">&#10148;</span>':'');
    return '<span class="achip" data-k="'+esc(k)+'" style="display:inline-flex;align-items:center;gap:5px;padding:3px 7px;margin:0 5px 5px 0;border:1px solid var(--line);border-radius:12px;font-size:12px;background:rgba(60,160,255,.10)">'+marks+'<b>'+esc(t)+'</b><span class="achipx" data-k="'+esc(k)+'" style="cursor:pointer;opacity:.6;font-weight:700">&times;</span></span>'; };
  box.innerHTML=[...keys].sort().map(chip).join('');
  $$('#atlasActive .achipx').forEach(x=>x.onclick=()=>{ const k=x.dataset.k; atlasHl.delete(k); atlasNav.delete(k); atlasArrow.delete(k); renderAtlasHighlight(atlasData); postAtlasHighlight(); });
}
// Push the active rules (original-case) to the overlay.
async function postAtlasHighlight(){
  // Build {tag,color,track,nav,arrow} rules: colour = the row's category colour, so in-game rings match the table.
  const rows=atlasData?atlasFilterRows(atlasData):[];
  const rules=rows.filter(r=>{const k=r.title.toLowerCase(); return atlasHl.has(k)||atlasNav.has(k)||atlasArrow.has(k);})
    .map(r=>{const k=r.title.toLowerCase(); return {tag:r.title, color:(CATCOL[r.cat]||'#3ca0ff'), track:atlasHl.has(k), nav:atlasNav.has(k), arrow:atlasArrow.has(k)};});
  try{ await fetch('/api/atlas-highlight',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({rules})}); }catch(e){}
}
$('#atlasHlClear')?.addEventListener('click',()=>{ atlasHl.clear(); atlasNav.clear(); atlasArrow.clear(); if(atlasData) renderAtlasHighlight(atlasData); postAtlasHighlight(); });
$('#atlasHlFilter')?.addEventListener('input',()=>{ if(atlasData) renderAtlasHighlight(atlasData); });
$('#atlasHlSelOnly')?.addEventListener('click',e=>{ atlasHlSelOnly=!atlasHlSelOnly; e.target.classList.toggle('on',atlasHlSelOnly); if(atlasData) renderAtlasHighlight(atlasData); });
$('#atlasHelp')?.addEventListener('click',()=>{ const b=$('#atlasHelpBox'); if(b) b.hidden=!b.hidden; });
// Group filter chips (All / Kind / Content / Map).
$$('[data-group]').forEach(b=>b.addEventListener('click',()=>{ atlasGroup=b.dataset.group; $$('[data-group]').forEach(x=>x.classList.toggle('on',x===b)); if(atlasData) renderAtlasHighlight(atlasData); }));
// Quick presets: select matching rows and flip the relevant toggles in one click.
const ATLAS_PRESETS={
  citadels:{m:r=>r.cat==='Citadel'||/citadel/i.test(r.title), trk:1,nav:1,arw:1},
  deadly:  {m:r=>/deadly/i.test(r.title),                     trk:1,nav:1,arw:0},
  bosses:  {m:r=>/boss/i.test(r.title),                       trk:1,nav:0,arw:0},
  towers:  {m:r=>r.cat==='Tower'||/tower/i.test(r.title),     trk:1,nav:1,arw:0},
  uniques: {m:r=>r.cat==='Unique'||/unique/i.test(r.title),   trk:1,nav:1,arw:0},
};
$$('#atlasPresets [data-preset]').forEach(b=>b.addEventListener('click',()=>{
  const p=ATLAS_PRESETS[b.dataset.preset]; if(!p||!atlasData) return;
  atlasFilterRows(atlasData).filter(p.m).forEach(r=>{ const k=r.title.toLowerCase(); if(p.trk)atlasHl.add(k); if(p.nav)atlasNav.add(k); if(p.arw)atlasArrow.add(k); });
  renderAtlasHighlight(atlasData); postAtlasHighlight();
}));

// Live-nodes grid: each row is a real atlas node. Click a row to SELECT it → the overlay highlights
// it in-game (projection calibration loop). Selection is the set of element addresses.
function renderAtlasNodes(d, f){
  let list=d.nodeList||[];
  if(f) list=list.filter(n=> (''+n.id).includes(f) || biomeName(n.biome).toLowerCase().includes(f)
      || (n.map||'').toLowerCase().includes(f) || (n.hasContent&&'content'.includes(f))
      || (!n.visited&&'unvisited'.includes(f)) || ('biome '+n.biome).includes(f)
      || (n.tags||[]).some(t=>t.toLowerCase().includes(f)));   // match on map name + content names
  if(list.length===0){ $('#atlasList').innerHTML='<div class="hint-row">No live nodes (open the Atlas in-game, then Refresh).</div>'; return; }
  // Content nodes first (the interesting ones), then by tag count.
  list=list.slice().sort((a,b)=>((b.tags||[]).length)-((a.tags||[]).length));
  const head='<div class="arow ahead nrow"><span>Map</span><span>Content</span><span>Biome</span><span>Pos</span></div>';
  const body=list.slice(0,1200).map(n=>{
    const sel=atlasSel.has(n.el)?' sel':'';
    const hot=((n.map&&atlasHl.has(n.map.toLowerCase()))||(n.tags||[]).some(t=>atlasHl.has(t.toLowerCase())));
    const val=(n.tags&&n.tags.length)?' val':'';
    const content=(n.tags||[]).map(t=>'<span class="ntag tc">'+esc(t)+'</span>').join(' ')||'<span class="hint-row">—</span>';
    return '<div class="arow nrow'+val+sel+(hot?' sel':'')+'" data-el="'+esc(n.el)+'">'
      +'<span title="'+esc(n.map||'')+'">'+esc(n.map||'—')+(n.visited?' <span class="ntag tv">✓</span>':'')+'</span>'
      +'<span>'+content+'</span><span>'+esc(biomeName(n.biome))+'</span>'
      +'<span class="amono">('+n.x+','+n.y+')</span></div>';
  }).join('');
  $('#atlasList').innerHTML=head+body
    +'<div class="hint-row" style="margin-top:10px"><b>Click a node row to highlight it in-game</b> (drives the overlay’s atlas highlight — use it to confirm positions / calibrate). Click again to deselect. Showing '+Math.min(list.length,1200)+' of '+list.length+' nodes.</div>';
  $$('#atlasList .nrow[data-el]').forEach(row=>row.onclick=()=>{
    const el=row.dataset.el;
    if(atlasSel.has(el)) atlasSel.delete(el); else atlasSel.add(el);
    row.classList.toggle('sel',atlasSel.has(el));
    postAtlasSel();
  });
}
async function postAtlasSel(){ try{ await fetch('/api/atlas-select',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({els:[...atlasSel]})}); }catch(e){} }

$('#atlasRefresh')?.addEventListener('click',loadAtlas);
$('#atlasSearch')?.addEventListener('input',()=>{ if(atlasData) renderAtlas(); });
$$('#atlasViewCatalog,#atlasViewRegion,#atlasViewNodes').forEach(b=>b?.addEventListener('click',()=>{
  atlasView=b.dataset.view;
  $$('#atlasViewCatalog,#atlasViewRegion,#atlasViewNodes').forEach(x=>x.classList.toggle('on',x===b));
  renderAtlas();
}));

/* ── left rail ── */
function renderState(){
  const s=state; if(!s) return;
  const hp=Math.max(0,Math.min(100,s.hpPct||0)), mp=Math.max(0,Math.min(100,s.manaPct||0)), es=Math.max(0,Math.min(100,s.esPct||0));
  $('#hpBar').style.width=hp+'%'; $('#mpBar').style.width=mp+'%'; $('#esBar').style.width=es+'%';
  $('#hpNum').textContent=hp.toFixed(0)+'%'; $('#mpNum').textContent=mp.toFixed(0)+'%'; $('#esNum').textContent=es.toFixed(0)+'%';
  const areaName=(s.areaName&&s.areaName!==s.areaCode)?s.areaName:'';
  $('#kAreaName').textContent=areaName||s.areaCode||'—';
  $('#kArea').textContent=s.areaCode||'—';
  const act=s.areaAct||0;
  $('#kAlvl').textContent=(act?'Act '+act+' · ':'')+(s.areaLevel?('lvl '+s.areaLevel):'—');
  $('#kMap').textContent=s.mapVisible?'yes':'no';
  $('#kFlask').textContent=(s.autoFlask?'on':'off')+(s.flask?' · '+s.flask:'');
  const fs=$('#flaskState'); if(fs) fs.textContent=(s.autoFlask?'ON':'OFF')+(s.flask?' · '+s.flask:'');
  $('#kBot').textContent=(s.bot?'on':'off')+(s.botNote?' · '+s.botNote:'');
  const bs=$('#botState'); if(bs) bs.textContent=(s.bot?'ON':'OFF')+(s.botNote?' · '+s.botNote:'');
  $('#kClear').textContent=(s.mapClear?'on':'off')+(s.clear?' · '+s.clear:'');
  const cls=$('#mapClearState'); if(cls) cls.textContent=(s.mapClear?'ON':'OFF')+(s.clear?' · '+s.clear:'');
  $('#kCombat').textContent=(s.combatAssist?'on':'off')+(s.combat?' · '+s.combat:'');
  const cs=$('#combatState'); if(cs) cs.textContent=(s.combatAssist?'ON':'OFF')+(s.combat?' · '+s.combat:'');
  $('#kQuest').textContent=(s.questFollow?'on':'off')+(s.quest?' · '+s.quest:'');
  const qs=$('#questFollowState'); if(qs) qs.textContent=(s.questFollow?'ON':'OFF')+(s.quest?' · '+s.quest:'');
  $('#kMove').textContent=(s.pathMove?'on':'off')+(s.move?' · '+s.move:'');
  const ms=$('#pathMoveState'); if(ms) ms.textContent=(s.pathMove?'ON':'OFF')+(s.move?' · '+s.move:'');
  $('#cEnt').textContent=s.entityCount||0;
  $('#cPoi').textContent=s.poiCount||0;
  $('#cMon').textContent=(s.counts&&s.counts.Monster)||0;
  $('#cLm').textContent=s.landmarkCount||0;
  $('#areaChip').innerHTML = (areaName||s.areaCode||'—') + ' <b>·</b> ' + (s.inGame?'in game':'town/menu');

  // Runeshape monoliths (from /state): each monolith's value-tier header (best ex · anchor · N holes)
  // with its priced reward rows. Sorted server-side by value; hidden when the area has none.
  const mc=$('#monoCard'), ml=$('#monoList');
  const monos=(s.monoliths||[]).slice().sort((a,b)=>(b.bestEx||0)-(a.bestEx||0));
  if(monos.length){
    mc.hidden=false;
    ml.innerHTML = monos.map(m=>{
      const tier = (m.bestEx||0)>=30 ? '#66e066' : (m.bestEx||0)>=18 ? '#e6c84d' : '#cfcfcf';
      const hdr = (m.bestEx>0?('<b style="color:'+tier+'">'+Math.round(m.bestEx)+' ex</b> · '):'')
                + esc(m.anchor||'?') + ' · ' + (m.holes||0) + 'h' + (m.collected?' · <span style="opacity:.6">collected</span>':'');
      const rows=(m.rewards||[]).filter(r=>r.ex>0).slice(0,6)
        .map(r=>'<div style="display:flex;justify-content:space-between;gap:8px"><span>'+esc(r.name)+(r.count>1?(' ×'+r.count):'')+'</span><span style="opacity:.85">'+Math.round(r.ex)+' ex</span></div>').join('');
      return '<div style="margin:0 0 9px"><div style="margin-bottom:2px">'+hdr+'</div>'
           + '<div style="font-size:12px;opacity:.9;padding-left:8px">'+(rows||'<span style="opacity:.6">no priced rewards</span>')+'</div></div>';
    }).join('');
  } else { mc.hidden=true; ml.innerHTML=''; }

  // Zone leveling notes (from /api/zone): title + note text, hidden when there's nothing to show.
  const zn=$('#zoneNotes');
  if(zone && (zone.notes||'').trim()){
    zn.hidden=false;
    zn.innerHTML='<div class="zt">'+esc(zone.title||zone.name||'')+'</div>'+esc(zone.notes);
  } else { zn.hidden=true; }
}

// Update banner: show a download link if a newer version exists on GitHub (best-effort).
async function checkVersion(){
  try{
    const v=await getJSON('/api/version');
    if(v && v.updateAvailable){
      const b=$('#updateBanner'); if(!b) return;
      const m=$('#updateMsg'); if(m) m.textContent=' — '+(v.latest||'')+' (you have v'+(v.current||'?')+')';
      b.href=v.url||'#'; b.hidden=false; b.style.display='flex';
    }
  }catch(e){}
}

wireSettings(); wireHpBars(); wireTerrain(); wireGround(); wireHover(); wireMono(); wireExchange();
document.querySelector('#combatSkillAdd')?.addEventListener('click',()=>{
  if(combatSkillsData.length>=8) return;
  combatSkillsData.push({key:nextQwer(), cooldownMs:400, range:0});
  renderCombatSkills(); saveCombatSkills();
});
loadIcons().then(()=>{ loadSettings(); loadFilters(); }); // Rules is the default tab
tick(); setInterval(tick, 1000);
checkVersion();
</script>
</body>
</html>
""";
}
