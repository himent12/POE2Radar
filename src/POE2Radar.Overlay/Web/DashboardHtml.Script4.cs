namespace POE2Radar.Overlay.Web;

/// <summary>Dashboard page — client script, last part: the Macros page (buff-keeper rule editor + live buffs,
/// chat commands, bookmarks, inspect hotkeys), the key/hotkey capture inputs, and the boot sequence.</summary>
internal static partial class DashboardHtml
{
    private const string Script4 = """
/* ── key names: mirror of Input/Hotkey.cs (canonical "Ctrl+Shift+Alt+Key") ── */
const HK_NAMED=[['Mouse3',0x04],['Mouse4',0x05],['Mouse5',0x06],['Backspace',0x08],['Tab',0x09],['Enter',0x0D],['Pause',0x13],['Esc',0x1B],
  ['Space',0x20],['PageUp',0x21],['PageDown',0x22],['End',0x23],['Home',0x24],['Left',0x25],['Up',0x26],['Right',0x27],['Down',0x28],
  ['Insert',0x2D],['Delete',0x2E],['Num*',0x6A],['Num+',0x6B],['Num-',0x6D],['Num.',0x6E],['Num/',0x6F],
  ['`',0xC0],['-',0xBD],['=',0xBB],['[',0xDB],[']',0xDD],[';',0xBA],["'",0xDE],[',',0xBC],['.',0xBE],['/',0xBF],['\\',0xDC]];
const CODE_NAMES={NumpadMultiply:'Num*',NumpadAdd:'Num+',NumpadSubtract:'Num-',NumpadDecimal:'Num.',NumpadDivide:'Num/',NumpadEnter:'Enter',
  Space:'Space',Enter:'Enter',Tab:'Tab',Backspace:'Backspace',Escape:'Esc',PageUp:'PageUp',PageDown:'PageDown',End:'End',Home:'Home',
  ArrowLeft:'Left',ArrowUp:'Up',ArrowRight:'Right',ArrowDown:'Down',Insert:'Insert',Delete:'Delete',Pause:'Pause',
  Backquote:'`',Minus:'-',Equal:'=',BracketLeft:'[',BracketRight:']',Semicolon:';',Quote:"'",Comma:',',Period:'.',Slash:'/',Backslash:'\\'};
function codeName(e){
  const c=e.code||'';
  if(/^Key[A-Z]$/.test(c)) return c.slice(3);
  if(/^Digit[0-9]$/.test(c)) return c.slice(5);
  if(/^F([1-9]|1[0-9]|2[0-4])$/.test(c)) return c;
  if(/^Numpad[0-9]$/.test(c)) return 'Num'+c.slice(6);
  return CODE_NAMES[c]||null;
}
function nameToVk(n){
  if(!n) return 0;
  if(/^[A-Za-z0-9]$/.test(n)) return n.toUpperCase().charCodeAt(0);
  let m=/^F([0-9]{1,2})$/i.exec(n); if(m && +m[1]>=1 && +m[1]<=24) return 0x70+(+m[1])-1;
  m=/^Num([0-9])$/i.exec(n); if(m) return 0x60+(+m[1]);
  const f=HK_NAMED.find(([k])=>k.toLowerCase()===n.toLowerCase()); return f?f[1]:0;
}
function vkName(vk){
  vk=+vk||0; if(!vk) return '';
  if((vk>=0x41&&vk<=0x5A)||(vk>=0x30&&vk<=0x39)) return String.fromCharCode(vk);
  if(vk>=0x70&&vk<=0x87) return 'F'+(vk-0x70+1);
  if(vk>=0x60&&vk<=0x69) return 'Num'+(vk-0x60);
  const f=HK_NAMED.find(x=>x[1]===vk); return f?f[0]:'VK'+vk.toString(16).toUpperCase().padStart(2,'0');
}
const modPrefix=e=>(e.ctrlKey?'Ctrl+':'')+(e.shiftKey?'Shift+':'')+(e.altKey?'Alt+':'');
/* Capture input: focus it, press a key (or a mouse middle/side button) → onSet(value).
   mode 'combo' → canonical hotkey string; mode 'vk' → Win32 virtual-key int (no modifiers).
   Backspace/Delete clear, Esc leaves the field, Tab moves focus as usual. */
function bindCapture(el, mode, onSet){
  el.readOnly=true; el.classList.add('kcap'); el.setAttribute('autocomplete','off');
  const shown=()=>el.dataset.shown||'';
  el.title='Click, then press a key'+(mode==='combo'?' combo':'')+'. Mouse middle / side buttons work too. Backspace clears.';
  el.addEventListener('focus',()=>el.classList.add('listening'));
  el.addEventListener('blur',()=>{ el.classList.remove('listening'); el.value=shown(); });
  el.addEventListener('keydown',e=>{
    if(e.key==='Tab') return;
    e.preventDefault(); e.stopPropagation();
    const mods=e.ctrlKey||e.shiftKey||e.altKey;
    if(['Control','Shift','Alt','Meta','OS','AltGraph'].includes(e.key)){ if(mode==='combo') el.value=modPrefix(e)+'…'; return; }
    if(e.code==='Escape' && !mods){ el.blur(); return; }
    if((e.code==='Backspace'||e.code==='Delete') && !mods){ onSet(mode==='vk'?0:''); return; }
    const name=codeName(e);
    if(mode==='vk'){ const vk=name?nameToVk(name):((e.keyCode>0&&e.keyCode<256)?e.keyCode:0); if(vk) onSet(vk); return; }
    if(name) onSet(modPrefix(e)+name);
  });
  el.addEventListener('keyup',()=>{ if(el.value.endsWith('…')) el.value=shown(); });
  el.addEventListener('mousedown',e=>{
    if(document.activeElement!==el || e.button===0 || e.button===2) return;
    const nm={1:'Mouse3',3:'Mouse4',4:'Mouse5'}[e.button]; if(!nm) return;
    e.preventDefault();
    onSet(mode==='vk'?nameToVk(nm):modPrefix(e)+nm);
  });
  el.addEventListener('mouseup',e=>{ if(e.button===3||e.button===4) e.preventDefault(); });   // no browser back/forward
  el.addEventListener('auxclick',e=>e.preventDefault());
}
function showCap(el,text){ el.dataset.shown=text||''; el.value=text||''; el.classList.toggle('unset',!text); }

/* Re-render a list while keeping keyboard focus + caret on the same field of the same row. */
function keepFocus(host, fn){
  const a=document.activeElement; let k=null, ss=null, se=null;
  if(a && host.contains(a)){ const row=a.closest('[data-i]'); k={i:row?row.dataset.i:null, f:a.dataset.f||null, act:a.dataset.act||null};
    try{ ss=a.selectionStart; se=a.selectionEnd; }catch(_){} }
  fn();
  if(!k) return;
  const row=k.i!=null?host.querySelector(`[data-i="${k.i}"]`):host; if(!row) return;
  const el=k.f?row.querySelector(`[data-f="${k.f}"]`):k.act?row.querySelector(`[data-act="${k.act}"]`):null;
  if(el){ el.focus({preventScroll:true}); try{ if(ss!=null && !el.readOnly) el.setSelectionRange(ss,se); }catch(_){} }
}

/* ── Buff keeper: settings {buffKeeper} (arm bit is hotkey/INSERT-only; never sent) ── */
let bk=null, cmds=null, bkTimer=0, bkDirty=0, cmdTimer=0, cmdDirty=0;
const BK_MAX=16;
async function loadMacros(){
  if(bkTimer||cmdTimer) return;                     // a save is pending: don't clobber local edits
  let s; try{ s=await getJSON('/api/settings'); }catch(e){ return; }
  bk=s.buffKeeper||{toggleHotkey:'F4',globalGapMs:250,rules:[]}; bk.rules=bk.rules||[];
  cmds=s.commands||{enabled:true,commands:[],bookmarks:[],inspectWikiHotkey:'',inspectDbHotkey:''};
  cmds.commands=cmds.commands||[]; cmds.bookmarks=cmds.bookmarks||[];
  try{ const p=await getJSON('/api/prices'); priceLeague=(p&&p.league)||priceLeague; }catch(e){}
  renderBk(); renderCmds();
}
const TRIGGERS=[['Missing','When missing'],['Expiring','When running low'],['Interval','On a timer']];
function bkRuleHtml(r,i,n){
  const t=r.trigger||'Missing', key=vkName(r.key);
  return `<div class="rulecard${r.enabled?'':' off'}" data-i="${i}">
    <div class="rhead">
      <label class="sw" title="Rule enabled"><input type="checkbox" data-f="enabled"${r.enabled?' checked':''} aria-label="Rule enabled"><span class="track"></span><span class="knob"></span></label>
      <input class="rname" data-f="name" value="${esc(r.name)}" placeholder="Rule name" maxlength="60" aria-label="Rule name">
      <span class="pill nodot rstatus bk-note" data-n="${i}" title="Live status">—</span>
      <span class="drord"><button class="ordbtn" data-act="up" title="Higher priority"${i===0?' disabled':''}>▲</button><button class="ordbtn" data-act="dn" title="Lower priority"${i===n-1?' disabled':''}>▼</button></span>
      <button class="delbtn" data-act="del" title="Delete rule" aria-label="Delete rule">✕</button>
    </div>
    <div class="rbody">
      <label class="field c6" title="Case-insensitive part of the internal buff id; separate alternatives with |"><span>Buff name</span>
        <input data-f="buffName" list="buffNames" value="${esc(r.buffName)}" placeholder="e.g. arcane_surge  (a|b = either)" spellcheck="false" maxlength="200"></label>
      <label class="field c3"><span>Trigger</span><select data-f="trigger">${TRIGGERS.map(([v,l])=>`<option value="${v}"${t===v?' selected':''}>${l}</option>`).join('')}</select></label>
      <label class="field c3"${t==='Expiring'?'':' hidden'}><span>Refresh below (s)</span><input type="number" data-f="refreshBelowSec" data-num="float" min="0" max="600" step="0.5" value="${r.refreshBelowSec??2}"></label>
      <label class="field c3"${t==='Interval'?'':' hidden'}><span>Every (ms)</span><input type="number" data-f="intervalMs" data-num="int" min="500" max="3600000" step="500" value="${r.intervalMs??10000}"></label>
      <label class="field c3"><span>Key to press</span><input data-f="key" placeholder="click, press a key" aria-label="Key to press" value="${esc(key)}"></label>
      <label class="field c3"><span>Rule cooldown (ms)</span><input type="number" data-f="minGapMs" data-num="int" min="250" max="60000" step="50" value="${r.minGapMs??1500}"></label>
      <label class="field c3"${t==='Interval'?' hidden':''}><span>Recast below charges</span><input type="number" data-f="minCharges" data-num="int" min="0" max="100" step="1" value="${r.minCharges??0}" title="0 = ignore charges"></label>
      <div class="ropts">
        <label class="inl"><input type="checkbox" data-f="onlyNearHostiles"${r.onlyNearHostiles?' checked':''}> Only with hostiles within</label>
        <input type="number" class="numin" data-f="hostileRange" data-num="float" min="1" max="300" step="5" value="${r.hostileRange??60}" aria-label="Hostile range"${r.onlyNearHostiles?'':' disabled'}>
        <span class="hint" style="margin-left:-10px">grid units</span>
        <label class="inl"><input type="checkbox" data-f="skipInTown"${r.skipInTown?' checked':''}> Skip in town / hideout</label>
      </div>
    </div>
  </div>`;
}
function renderBk(){
  if(!bk) return;
  const tg=$('#bkToggle'); if(document.activeElement!==tg) showCap(tg, bk.toggleHotkey||'');
  setT('bkHkHint', bk.toggleHotkey||'F4');
  const gap=$('#bkGap'); if(document.activeElement!==gap) gap.value=bk.globalGapMs??250;
  const host=$('#bkRules');
  keepFocus(host,()=>{
    host.innerHTML = bk.rules.length ? bk.rules.map((r,i)=>bkRuleHtml(r,i,bk.rules.length)).join('')
      : '<div class="empty">No rules yet. Add one, or press <b>+ Keep up</b> next to a live buff.</div>';
    host.querySelectorAll('.rulecard').forEach(card=>{
      const r=bk.rules[+card.dataset.i], ki=card.querySelector('[data-f="key"]');
      showCap(ki, vkName(r.key));
      bindCapture(ki,'vk',vk=>{ r.key=vk; showCap(ki, vkName(vk)); scheduleBk(); });
    });
  });
  $('#bkAdd').disabled=bk.rules.length>=BK_MAX;
  $('#bkAdd').textContent=bk.rules.length>=BK_MAX?'Rule limit reached ('+BK_MAX+')':'+ Add rule';
  renderBuffsLive();
}
function scheduleBk(){ bkDirty++; clearTimeout(bkTimer); bkTimer=setTimeout(saveBk,400); }
async function saveBk(){
  bkTimer=0; if(!bk) return;
  const seq=bkDirty, sent=JSON.parse(JSON.stringify(bk));
  const j=await saveSetting('buffKeeper',sent);
  const srv=j&&j.settings&&j.settings.buffKeeper; if(!srv) return;
  const w=$('#bkWarn');
  if(sent.toggleHotkey && srv.toggleHotkey!==sent.toggleHotkey && sent.toggleHotkey.toLowerCase()!==(srv.toggleHotkey||'').toLowerCase())
    w.innerHTML=`<div class="note-box warn" style="margin-bottom:12px">“${esc(sent.toggleHotkey)}” can’t be the arm hotkey (reserved: F6–F10, F12, Insert, Ctrl+D) — kept <b>${esc(srv.toggleHotkey)}</b>.</div>`;
  else w.innerHTML='';
  if(seq!==bkDirty) return;                          // edited again meanwhile: the next save reconciles
  srv.rules=srv.rules||[]; bk=srv; renderBk();
  if(ovCfg) ovCfg.buffKeeper=srv;
}
function newRule(buff){
  return {enabled:false,name:buff||'New rule',key:0x54,trigger:'Missing',buffName:buff||'',refreshBelowSec:2,intervalMs:10000,
    minGapMs:1500,onlyNearHostiles:false,hostileRange:60,skipInTown:true,minCharges:0};
}
function wireMacros(){
  const host=$('#bkRules');
  const ruleOf=el=>{ const c=el.closest('.rulecard'); return c?[bk.rules[+c.dataset.i],+c.dataset.i,c]:[null]; };
  host.addEventListener('input',e=>{
    const f=e.target.dataset.f; if(!f||!bk) return; const [r]=ruleOf(e.target); if(!r) return;
    if(f==='name'||f==='buffName'){ r[f]=e.target.value; scheduleBk(); }
  });
  host.addEventListener('change',e=>{
    const el=e.target, f=el.dataset.f; if(!f||!bk) return; const [r,,card]=ruleOf(el); if(!r) return;
    if(el.type==='checkbox'){ r[f]=el.checked;
      if(f==='enabled') card.classList.toggle('off',!el.checked);
      if(f==='onlyNearHostiles'){ const hr=card.querySelector('[data-f="hostileRange"]'); if(hr) hr.disabled=!el.checked; } }
    else if(el.dataset.num){ const v=el.dataset.num==='int'?parseInt(el.value,10):parseFloat(el.value); if(isNaN(v)) return; r[f]=v; }
    else if(f==='trigger'){ r[f]=el.value; bkDirty++; renderBk(); }
    else if(f==='name'||f==='buffName') r[f]=el.value;
    scheduleBk();
  });
  host.addEventListener('click',e=>{
    const b=e.target.closest('[data-act]'); if(!b||!bk) return; const [,i]=ruleOf(b); if(i==null) return;
    const R=bk.rules;
    if(b.dataset.act==='up' && i>0) [R[i-1],R[i]]=[R[i],R[i-1]];
    else if(b.dataset.act==='dn' && i<R.length-1) [R[i+1],R[i]]=[R[i],R[i+1]];
    else if(b.dataset.act==='del'){ if(!confirm('Delete the rule “'+(R[i].name||'unnamed')+'”?')) return; R.splice(i,1); }
    else return;
    bkDirty++; renderBk(); scheduleBk();
  });
  $('#bkAdd').onclick=()=>{ if(!bk||bk.rules.length>=BK_MAX) return; bk.rules.push(newRule('')); renderBk(); scheduleBk();
    const last=$$('#bkRules .rulecard').pop(); if(last){ last.scrollIntoView({block:'nearest'}); last.querySelector('[data-f="name"]').focus(); } };
  bindCapture($('#bkToggle'),'combo',hk=>{ if(!bk) return; if(!hk){ return; } bk.toggleHotkey=hk; showCap($('#bkToggle'),hk); scheduleBk(); });
  $('#bkGap').onchange=e=>{ const v=parseInt(e.target.value,10); if(!isNaN(v)&&bk){ bk.globalGapMs=v; scheduleBk(); } };

  // chat commands / bookmarks / inspect
  $('#cmdEnabled').onchange=e=>{ if(!cmds) return; cmds.enabled=e.target.checked; scheduleCmds(); };
  wireCmdList('#cmdList','commands');
  wireCmdList('#bmList','bookmarks');
  $('#cmdAdd').onclick=()=>{ if(!cmds) return; cmds.commands.push({enabled:true,name:'',hotkey:'',text:''}); cmdDirty++; renderCmds(); scheduleCmds();
    const last=$$('#cmdList .cmdrow').pop(); if(last) last.querySelector('[data-f="name"]').focus(); };
  $('#bmAdd').onclick=()=>{ if(!cmds) return; cmds.bookmarks.push({enabled:true,name:'',folder:'',url:'https://',hotkey:''}); cmdDirty++; renderCmds();
    const last=$$('#bmList .bmrow').pop(); if(last) last.querySelector('[data-f="name"]').focus(); };
  $$('[data-cmdhk]').forEach(el=>bindCapture(el,'combo',hk=>{ if(!cmds) return; cmds[el.dataset.cmdhk]=hk; showCap(el,hk); scheduleCmds(); }));
}

/* ── live buffs (/api/buffs, 1 s on Overview + Macros) ── */
let buffsData=null, _lbKeys='';
async function pollBuffs(){ try{ buffsData=await getJSON('/api/buffs'); }catch(e){ return; } renderBuffsLive(); }
const ruleAlts=r=>(r.buffName||'').toLowerCase().split('|').map(x=>x.trim()).filter(Boolean);
const keptBy=name=>(bk&&bk.rules||[]).some(r=>ruleAlts(r).some(a=>name.toLowerCase().includes(a)));
function noteClass(n){ n=(n||'').toLowerCase();
  if(!n||/disabled|no key|unset|off/.test(n)) return '';
  if(/recast|press|missing|low|expir/.test(n)) return 'warn';
  if(/unread|error|fail|blocked/.test(n)) return 'bad';
  return 'on'; }
function renderBuffsLive(){
  const d=buffsData; if(!d) return;
  const buffs=d.buffs||[];
  // overview module note
  if(page==='overview'){
    const base=(state&&state.buffNote)||(d.armed?'Armed':'Disarmed');
    setT('mBuffNote', d.readable===false ? base+' · buffs unreadable' : base+' · '+buffs.length+' active buff'+(buffs.length===1?'':'s'));
  }
  if(page!=='macros') return;
  setPill('bkArmed', d.armed?'on':'', d.armed?'Armed':'Disarmed');
  setT('bkNote', d.note&&d.note.toLowerCase()!==(d.armed?'armed':'')?d.note:'');
  // Rules only run while the keeper is ON, and (for safety) this page can't switch it on — say so loudly.
  const off=$('#bkOffBanner');
  if(off){
    const hk=(ovCfg&&ovCfg.buffKeeper&&ovCfg.buffKeeper.toggleHotkey)||'F4';
    off.innerHTML=d.armed?'':`<div class="note-box warn" style="margin-bottom:12px"><b>The buff keeper is OFF</b>, so no rule will fire. In game, press <kbd>${esc(hk)}</kbd> (you'll see “Buff keeper ON”) or tick it in the Insert menu → Macros.</div>`;
  }
  const ur=$('#bkUnread');
  ur.innerHTML = d.readable===false ? '<div class="note-box warn" style="margin-bottom:12px">Buffs can’t be read right now — you’re not in game, or the buff offsets need validating (run <code>Research --buffs</code>). Interval rules keep working; Missing / Expiring rules pause.</div>' : '';
  // datalist of live names for the buff-name inputs
  const names=[...new Set(buffs.map(b=>b.name).filter(Boolean))].sort();
  const dl=$('#buffNames'), dk=names.join('|'); if(dl._k!==dk){ dl._k=dk; dl.innerHTML=names.map(n=>`<option value="${esc(n)}">`).join(''); }
  // per-rule live notes
  $$('#bkRules .bk-note').forEach(el=>{ const n=(d.notes||[])[+el.dataset.n]||''; el.textContent=n||'—'; el.title=n; el.className='pill nodot rstatus bk-note '+noteClass(n); });
  // live list: rebuild only when the set changes; tick the timers in place
  const host=$('#lbList');
  setT('lbCount', d.readable===false?'':buffs.length+' active');
  if(d.readable===false){ if(_lbKeys!=='!'){ _lbKeys='!'; host.innerHTML='<div class="empty">Not readable.</div>'; } return; }
  const keys=buffs.map(b=>b.name+'#'+keptBy(b.name)).join('|');
  if(keys!==_lbKeys){
    _lbKeys=keys;
    host.innerHTML = buffs.length ? buffs.map((b,i)=>{ const kept=keptBy(b.name);
      return `<div class="buffrow${kept?' kept':''}" data-b="${i}"><div style="flex:1;min-width:0"><div class="bn" title="${esc(b.name)}">${esc(b.name)}</div><div class="bbar"><i></i></div></div>
        <span class="bt"></span>${kept?'<span class="pill nodot on" title="A rule keeps this up">kept</span>':`<button class="btn sm" data-keep="${esc(b.name)}" title="Add a rule for this buff (starts disabled)">+ Keep up</button>`}</div>`; }).join('')
      : '<div class="empty">No buffs on your character.</div>';
    host.querySelectorAll('[data-keep]').forEach(btn=>btn.onclick=()=>{
      if(!bk) return; if(bk.rules.length>=BK_MAX){ flashSaved(false,'Rule limit reached'); return; }
      bk.rules.push(newRule(btn.dataset.keep)); bkDirty++; renderBk(); scheduleBk();
      const last=$$('#bkRules .rulecard').pop(); if(last) last.scrollIntoView({block:'nearest',behavior:'smooth'});
    });
  }
  host.querySelectorAll('.buffrow').forEach(row=>{
    const b=buffs[+row.dataset.b]; if(!b) return;
    const t=b.timeLeft==null?'∞':b.timeLeft.toFixed(1)+'s';
    row.querySelector('.bt').textContent=(b.charges>1?b.charges+' × · ':'')+t;
    const bar=row.querySelector('.bbar i'); bar.style.width=(b.timeLeft==null||!b.total)?'100%':Math.max(0,Math.min(100,b.timeLeft/b.total*100))+'%';
    bar.style.opacity=b.timeLeft==null?.35:1;
  });
}

/* ── chat commands + bookmarks + inspect: settings {commands} ── */
const bmValid=u=>httpOk((u||'').trim().replace(/\{(league|item|char)\}/gi,'x'));
function cmdRowHtml(c,i){
  const lines=Math.min(5,Math.max(1,(c.text||'').split('\n').length));
  return `<div class="cmdrow" data-i="${i}">
    <label class="sw" title="Enabled"><input type="checkbox" data-f="enabled"${c.enabled?' checked':''} aria-label="Command enabled"><span class="track"></span><span class="knob"></span></label>
    <input data-f="name" value="${esc(c.name)}" placeholder="Name" maxlength="60" aria-label="Name">
    <input data-f="hotkey" placeholder="unbound" aria-label="Hotkey">
    <textarea data-f="text" rows="${lines}" placeholder="/hideout" spellcheck="false" aria-label="Chat text">${esc(c.text)}</textarea>
    <button class="delbtn" data-act="del" title="Delete command" aria-label="Delete">✕</button>
  </div>`;
}
function bmRowHtml(b,i){
  const ok=bmValid(b.url);
  return `<div class="bmrow" data-i="${i}">
    <label class="sw" title="Enabled"><input type="checkbox" data-f="enabled"${b.enabled!==false?' checked':''} aria-label="Bookmark enabled"><span class="track"></span><span class="knob"></span></label>
    <input data-f="name" value="${esc(b.name)}" placeholder="Name" maxlength="60" aria-label="Name">
    <input data-f="folder" value="${esc(b.folder)}" placeholder="Folder" maxlength="60" aria-label="Folder">
    <input class="bmurl${ok?'':' bad'}" data-f="url" value="${esc(b.url)}" placeholder="https://…" spellcheck="false" aria-label="URL" title="${ok?'':'Only absolute http(s) links are saved'}">
    <input data-f="hotkey" placeholder="unbound" aria-label="Hotkey">
    <a class="btn ghost sm" data-act="open" target="_blank" rel="noopener noreferrer" title="Open link" aria-label="Open link"><svg><use href="#i-link"/></svg></a>
    <button class="delbtn" data-act="del" title="Delete bookmark" aria-label="Delete">✕</button>
  </div>`;
}
function syncBmOpen(row,b){
  const a=row.querySelector('[data-act="open"]'), h=bmHref(b.url);
  if(h.ok){ a.href=h.url; a.removeAttribute('aria-disabled'); a.style.opacity=''; a.title='Open '+h.url; }
  else { a.removeAttribute('href'); a.setAttribute('aria-disabled','true'); a.style.opacity='.35'; a.title=h.why; }
  const u=row.querySelector('[data-f="url"]'), ok=bmValid(b.url); u.classList.toggle('bad',!ok); u.title=ok?'':'Only absolute http(s) links are saved — this bookmark isn’t saved yet';
}
function renderCmds(){
  if(!cmds) return;
  $('#cmdEnabled').checked=cmds.enabled!==false;
  const ch=$('#cmdList'), bh=$('#bmList');
  keepFocus(ch,()=>{
    ch.innerHTML = cmds.commands.length ? cmds.commands.map(cmdRowHtml).join('') : '<div class="empty">No chat commands.</div>';
    ch.querySelectorAll('.cmdrow').forEach(row=>{ const c=cmds.commands[+row.dataset.i], hk=row.querySelector('[data-f="hotkey"]');
      showCap(hk,c.hotkey); bindCapture(hk,'combo',v=>{ c.hotkey=v; showCap(hk,v); scheduleCmds(); }); });
  });
  keepFocus(bh,()=>{
    bh.innerHTML = cmds.bookmarks.length ? cmds.bookmarks.map(bmRowHtml).join('') : '<div class="empty">No bookmarks.</div>';
    bh.querySelectorAll('.bmrow').forEach(row=>{ const b=cmds.bookmarks[+row.dataset.i], hk=row.querySelector('[data-f="hotkey"]');
      showCap(hk,b.hotkey); bindCapture(hk,'combo',v=>{ b.hotkey=v; showCap(hk,v); scheduleCmds(); }); syncBmOpen(row,b); });
  });
  $$('[data-cmdhk]').forEach(el=>{ if(document.activeElement!==el) showCap(el, cmds[el.dataset.cmdhk]||''); });
  $('#cmdAdd').disabled=cmds.commands.length>=40; $('#bmAdd').disabled=cmds.bookmarks.length>=60;
}
function wireCmdList(sel, key){
  const host=$(sel);
  const itemOf=el=>{ const r=el.closest('[data-i]'); return r?[cmds[key][+r.dataset.i],+r.dataset.i,r]:[null]; };
  host.addEventListener('input',e=>{
    const f=e.target.dataset.f; if(!f||!cmds||f==='hotkey'||e.target.type==='checkbox') return;
    const [it,,row]=itemOf(e.target); if(!it) return;
    it[f]=e.target.value;
    if(f==='text') e.target.rows=Math.min(5,Math.max(1,e.target.value.split('\n').length));
    if(key==='bookmarks') syncBmOpen(row,it);
    scheduleCmds();
  });
  host.addEventListener('change',e=>{
    if(e.target.type!=='checkbox'||!cmds) return; const [it]=itemOf(e.target); if(!it) return;
    it[e.target.dataset.f]=e.target.checked; scheduleCmds();
  });
  host.addEventListener('click',e=>{
    const b=e.target.closest('[data-act]'); if(!b||!cmds) return; const [it,i]=itemOf(b); if(!it) return;
    if(b.dataset.act==='open'){ if(!b.getAttribute('href')) e.preventDefault(); return; }
    if(b.dataset.act==='del'){ cmds[key].splice(i,1); cmdDirty++; renderCmds(); scheduleCmds(); }
  });
}
function scheduleCmds(){ cmdDirty++; clearTimeout(cmdTimer); cmdTimer=setTimeout(saveCmds,400); }
async function saveCmds(){
  cmdTimer=0; if(!cmds) return;
  const seq=cmdDirty, strip=o=>{ const c={...o}; return c; };
  const validIdx=[]; cmds.bookmarks.forEach((b,i)=>{ if(bmValid(b.url)) validIdx.push(i); });
  const payload={enabled:cmds.enabled!==false, commands:cmds.commands.map(strip), bookmarks:validIdx.map(i=>strip(cmds.bookmarks[i])),
    inspectWikiHotkey:cmds.inspectWikiHotkey||'', inspectDbHotkey:cmds.inspectDbHotkey||''};
  const j=await saveSetting('commands',payload);
  const srv=j&&j.settings&&j.settings.commands; if(!srv) return;
  const toggle=(bk&&bk.toggleHotkey)||'F4', warns=[];
  const chk=(label,sent,got)=>{ if((sent||'').trim() && !(got||'')) warns.push(`${label}: <kbd>${esc(sent)}</kbd> was cleared — it collides with a reserved key (F6–F10, F12, Insert, Ctrl+D) or the buff-keeper toggle (<kbd>${esc(toggle)}</kbd>).`); };
  payload.commands.forEach((c,i)=>chk('Command “'+esc(c.name||'#'+(i+1))+'”', c.hotkey, (srv.commands[i]||{}).hotkey));
  const aligned=(srv.bookmarks||[]).length===payload.bookmarks.length;
  if(aligned) payload.bookmarks.forEach((b,i)=>chk('Bookmark “'+esc(b.name||'#'+(i+1))+'”', b.hotkey, srv.bookmarks[i].hotkey));
  else warns.push('Some bookmark links were rejected by the overlay (only absolute http(s) URLs are kept).');
  chk('Wiki inspect', payload.inspectWikiHotkey, srv.inspectWikiHotkey);
  chk('poe2db inspect', payload.inspectDbHotkey, srv.inspectDbHotkey);
  const drafts=cmds.bookmarks.length-validIdx.length;
  if(drafts) warns.push(`${drafts} bookmark${drafts===1?' has':'s have'} an invalid link and ${drafts===1?'isn’t':'aren’t'} saved yet (outlined in red).`);
  $('#cmdWarn').innerHTML=warns.length?`<div class="note-box warn" style="margin-bottom:12px">${warns.join('<br>')}</div>`:'';
  if(seq!==cmdDirty) return;
  cmds.enabled=srv.enabled; cmds.commands=srv.commands||[];
  cmds.inspectWikiHotkey=srv.inspectWikiHotkey||''; cmds.inspectDbHotkey=srv.inspectDbHotkey||'';
  if(aligned){ const nb=cmds.bookmarks.slice(); validIdx.forEach((li,k)=>{ nb[li]=srv.bookmarks[k]; }); cmds.bookmarks=nb; }
  renderCmds();
  if(ovCfg) ovCfg.commands=srv;
}

/* ── boot ── */
wireSettings(); wireHpBars(); wireTerrain(); wireGround(); wireHover(); wireMono(); wireExchange(); wireTradeCfg(); wireMapCheck(); wireMacros();
loadIcons().then(()=>{ if(drules.length) renderDrules(); });
route();
tick(); setInterval(tick, 1000);
checkVersion();
/* Price-check hotkey (Item Value → On hover): captured like the other hotkey fields, saved inside hoverPrice. */
(function(){
  const pc=$('#pcHotkey'); if(!pc) return;
  bindCapture(pc,'combo',v=>{ hover=hover||{}; hover.priceCheckHotkey=v||'Ctrl+D'; showCap(pc,hover.priceCheckHotkey); saveHover(); });
  showCap(pc,(hover&&hover.priceCheckHotkey)||'Ctrl+D');
})();
</script>
</body>
</html>
""";
}
