namespace POE2Radar.Overlay.Web;

/// <summary>Dashboard page — client script, third part: Overview extras (hotkey sheet, bookmark links,
/// module cards), the Trade page (/api/trade summary, chart, live requests, history, settings) and the
/// waystone checker card.</summary>
internal static partial class DashboardHtml
{
    private const string Script3 = """
/* ── small formatting helpers ── */
const fmtNum=v=>{ v=+v||0; const a=Math.abs(v); return a>=100?Math.round(v).toLocaleString():a>=10?(Math.round(v*10)/10).toString():(Math.round(v*100)/100).toString(); };
const fmtEx=v=>fmtNum(v)+' ex';
const fmtSignedEx=v=>(v>0?'+':v<0?'−':'')+fmtNum(Math.abs(v||0))+' ex';
const fmtAmt=v=>{ v=+v||0; return Number.isInteger(v)?String(v):(Math.round(v*100)/100).toString(); };
function ago(t){
  const d=(Date.now()-new Date(t).getTime())/1000; if(!isFinite(d)) return '';
  if(d<60) return Math.max(0,Math.round(d))+'s ago'; if(d<3600) return Math.round(d/60)+'m ago';
  if(d<86400) return Math.round(d/3600)+'h ago'; return Math.round(d/86400)+'d ago';
}
function fmtDate(t){
  const d=new Date(t); if(isNaN(d)) return '';
  return d.toLocaleDateString(undefined,{month:'short',day:'numeric'})+' '+d.toLocaleTimeString(undefined,{hour:'2-digit',minute:'2-digit'});
}
const httpOk=u=>{ try{ const x=new URL(u); return (x.protocol==='http:'||x.protocol==='https:') && !!x.hostname; }catch(_){ return false; } };

/* ── Overview: hotkey cheat-sheet + bookmark quick links (from /api/settings) ── */
let ovCfg=null, priceLeague='';
async function loadOverviewCfg(){
  try{ ovCfg=await getJSON('/api/settings'); }catch(e){ ovCfg=null; }
  try{ const p=await getJSON('/api/prices'); priceLeague=(p&&p.league)||''; }catch(e){}
  renderHotkeys(); renderBmQuick();
  setT('mBuffHk', (ovCfg&&ovCfg.buffKeeper&&ovCfg.buffKeeper.toggleHotkey)||'F4');
}
const FIXED_HK=[['F6','Route to nearest target'],['F7','Clear routes'],['F8','Auto-flask on / off'],['F9','Quit the overlay'],
  ['F10','Inspect atlas tile'],['F12','Open this dashboard'],['Insert','In-game menu']];
function renderHotkeys(){
  const host=$('#hkList'); if(!host) return;
  const rows=FIXED_HK.map(([k,l])=>[k,l,false]);
  const c=ovCfg||{}, bk=c.buffKeeper||{}, cm=c.commands||{};
  rows.push([(c.hoverPrice&&c.hoverPrice.priceCheckHotkey)||'Ctrl+D','Price check hovered item',true]);
  if(bk.toggleHotkey) rows.push([bk.toggleHotkey,'Buff keeper on / off',true]);
  if(cm.enabled!==false) (cm.commands||[]).forEach(x=>{ if(x.enabled&&x.hotkey) rows.push([x.hotkey,'Chat: '+(x.name||x.text||''),true]); });
  (cm.bookmarks||[]).forEach(b=>{ if(b.enabled&&b.hotkey) rows.push([b.hotkey,'Open '+(b.name||'bookmark'),true]); });
  if(cm.inspectWikiHotkey) rows.push([cm.inspectWikiHotkey,'Hovered item on the wiki',true]);
  if(cm.inspectDbHotkey) rows.push([cm.inspectDbHotkey,'Hovered item on poe2db',true]);
  host.innerHTML=rows.map(([k,l,u])=>`<div class="hkrow${u?' user':''}"><kbd>${esc(k)}</kbd><span title="${esc(l)}">${esc(l)}</span></div>`).join('');
}
/* Expand {league} with the price league; {item}/{char} only exist in game, so those links can't open here. */
function bmHref(url){
  if(/\{(item|char)\}/i.test(url||'')) return {ok:false, why:'needs a hovered item / character — use its hotkey in game'};
  if(/\{league\}/i.test(url||'') && !priceLeague) return {ok:false, why:'league not detected yet'};
  const u=(url||'').replace(/\{league\}/gi, encodeURIComponent(priceLeague));
  return httpOk(u)?{ok:true,url:u}:{ok:false,why:'not an http(s) link'};
}
function renderBmQuick(){
  const host=$('#bmQuick'); if(!host) return;
  const bms=((ovCfg&&ovCfg.commands&&ovCfg.commands.bookmarks)||[]).filter(b=>b.enabled!==false);
  if(!bms.length){ host.innerHTML='<div class="empty">No bookmarks. Add some under Macros.</div>'; return; }
  const groups=new Map(); bms.forEach(b=>{ const f=(b.folder||'').trim(); if(!groups.has(f)) groups.set(f,[]); groups.get(f).push(b); });
  const keys=[...groups.keys()].sort((a,b)=>a===''?-1:b===''?1:a.localeCompare(b));
  host.innerHTML=keys.map(f=>(f?`<div class="bmfolder">${esc(f)}</div>`:'')+groups.get(f).map(b=>{
    const h=bmHref(b.url), hk=b.hotkey?`<span class="bmh"><kbd>${esc(b.hotkey)}</kbd></span>`:'';
    return h.ok
      ? `<a class="bmlink" href="${esc(h.url)}" target="_blank" rel="noopener noreferrer" title="${esc(h.url)}"><svg><use href="#i-link"/></svg><span class="bmn">${esc(b.name||h.url)}</span>${hk}</a>`
      : `<div class="bmlink off" title="${esc(h.why)}"><svg><use href="#i-link"/></svg><span class="bmn">${esc(b.name||b.url)}</span><span class="bmh">${esc(h.why.split(' — ')[0])}</span></div>`;
  }).join('')).join('');
}

/* ── Trade (/api/trade: polled every 2 s on Overview + Trade only) ── */
let tradeData=null;
async function pollTrade(){
  try{ tradeData=await getJSON('/api/trade'); }catch(e){ return; }
  renderTradeModule();
  if(page==='trade') renderTrade();
}
const openSessions=d=>((d&&d.sessions)||[]).filter(x=>['New','Invited','InArea','Trading'].includes(x.state));
function tradeLogState(d){
  if(!d || d.enabled===undefined) return {cls:'',txt:'Unavailable'};
  if(!d.enabled) return {cls:'',txt:'Disabled'};
  const lg=d.log||{};
  return lg.path ? {cls:'on',txt:'Log found'} : {cls:'bad',txt:'No Client.txt'};
}
function renderTradeModule(){
  const d=tradeData||{}, ls=tradeLogState(tradeData);
  setPill('mTradePill', ls.cls, ls.txt);
  const lg=d.log||{};
  setT('mTradeNote', d.enabled===false ? 'Enable it on the Trade page' : (lg.status||'Waiting for the game log')+(d.afk?' · AFK':''));
  const t=(d.summary||{}).today, pt=$('#ovProfit');
  if(pt){
    if(t){ pt.textContent=t.profitText||fmtSignedEx(t.profitEx); pt.className='tval'+(t.profitEx>0?' pos':t.profitEx<0?' neg':''); }
    else { pt.textContent='—'; pt.className='tval'; }
  }
  setT('ovProfitSub', t ? `${fmtSignedEx(t.profitEx)} · ${t.sold||0} sold, ${t.bought||0} bought` : 'Trade tracker not available');
  const sm=d.summary||{}, line=x=>x?`${x.profitText||fmtSignedEx(x.profitEx)} · ${x.sold||0} sold`:'—';
  setT('ovProfitWeek', line(sm.week)); setT('ovProfitAll', line(sm.all));
}
function renderTrade(){
  const d=tradeData||{}, lg=d.log||{}, ls=tradeLogState(tradeData);
  setPill('trLogPill', ls.cls, ls.txt);
  const afk=$('#trAfk'); if(afk){ afk.hidden=!d.afk; afk.className='pill warn'; }
  const info=$('#trLogInfo');
  if(info){
    if(d.enabled===undefined){ info.className='note-box'; info.textContent='The trade tracker isn’t available in this session.'; }
    else if(!d.enabled){ info.className='note-box warn'; info.textContent='The trade assistant is disabled — turn it on under Trade assistant below.'; }
    else if(!lg.path){ info.className='note-box bad'; info.innerHTML='Client.txt not found'+(lg.status?' — '+esc(lg.status):'')+'. Start the game, or set the path below.'; }
    else { info.className='note-box'; info.innerHTML=`<span class="mono">${esc(lg.path)}</span> · ${esc(lg.status||'')} · ${(lg.lines||0).toLocaleString()} lines read`; }
  }
  // summary tiles
  const sm=d.summary||{};
  $$('#trSummary [data-sum]').forEach(tile=>{
    const k=tile.dataset.sum, x=sm[k], lbl={today:'Today',week:'Last 7 days',all:'All time'}[k];
    if(!x){ tile.innerHTML=`<div class="tlbl">${lbl}</div><div class="tval">—</div>`; return; }
    const cls=x.profitEx>0?' pos':x.profitEx<0?' neg':'';
    tile.innerHTML=`<div class="tlbl">${lbl}</div>
      <div class="tval${cls}">${esc(x.profitText||fmtSignedEx(x.profitEx))}<small>${fmtSignedEx(x.profitEx)}</small></div>
      <div class="sumline"><span>Sold <b>${x.sold||0}</b></span><span>Bought <b>${x.bought||0}</b></span><span>Earned <b>${fmtEx(x.earnedEx)}</b></span><span>Spent <b>${fmtEx(x.spentEx)}</b></span></div>`
      +(x.unconverted?`<div class="tsub">${x.unconverted} trade${x.unconverted===1?'':'s'} in currencies without a price &mdash; not counted</div>`:'');
  });
  renderTradeChart(d.history||[]);
  renderRequests(d.sessions||[]);
  renderHistory(d);
}
/* Daily profit bars for the last 14 local days, from history valueEx (Sold +, Bought −). */
function renderTradeChart(hist){
  const host=$('#trChart'); if(!host) return;
  const days=[], idx=new Map(), today=new Date(); today.setHours(0,0,0,0);
  for(let i=13;i>=0;i--){ const d=new Date(today); d.setDate(d.getDate()-i); idx.set(d.toDateString(),days.length); days.push({d,v:0,n:0}); }
  let priced=0;
  hist.forEach(h=>{ if(h.valueEx==null) return; const k=new Date(h.utc); k.setHours(0,0,0,0); const i=idx.get(k.toDateString()); if(i==null) return;
    days[i].v+=(h.direction==='Bought'?-1:1)*h.valueEx; days[i].n++; priced++; });
  const total=days.reduce((a,x)=>a+x.v,0);
  setT('trChartTotal', priced?('net '+fmtSignedEx(total)):'');
  if(!priced){ host.innerHTML='<div class="empty">No priced trades in the last 14 days.</div>'; return; }
  const W=Math.max(320,Math.round(host.clientWidth||480)),H=200,L=48,R=6,T=10,B=24, iw=W-L-R, ih=H-T-B;   // 1 unit = 1 px: text stays 12px
  let mx=Math.max(0,...days.map(x=>x.v)), mn=Math.min(0,...days.map(x=>x.v));
  if(mx===mn) mx=1;
  const y=v=>T+(mx-v)/(mx-mn)*ih, bw=iw/days.length, y0=y(0);
  let g='';
  const ticks=[mx,0,mn].filter((v,i,a)=>a.indexOf(v)===i && (v===0 || Math.abs(y(v)-y0)>14));
  ticks.forEach(v=>{ g+=`<line class="gl" x1="${L}" x2="${W-R}" y1="${y(v).toFixed(1)}" y2="${y(v).toFixed(1)}"/><text x="${L-6}" y="${(y(v)+4).toFixed(1)}" text-anchor="end">${v>0?'+':''}${fmtNum(v)}</text>`; });
  g+=`<line class="ax" x1="${L}" x2="${W-R}" y1="${y0.toFixed(1)}" y2="${y0.toFixed(1)}"/>`;
  days.forEach((x,i)=>{
    const bx=L+i*bw+bw*0.16, w=bw*0.68, top=Math.min(y(x.v),y0), h=Math.max(x.v?1.5:0,Math.abs(y(x.v)-y0));
    const lbl=x.d.toLocaleDateString(undefined,{weekday:'short',month:'short',day:'numeric'});
    if(x.n) g+=`<rect class="${x.v>=0?'p':'n'}" x="${bx.toFixed(1)}" y="${top.toFixed(1)}" width="${w.toFixed(1)}" height="${h.toFixed(1)}" rx="2"><title>${esc(lbl)}: ${fmtSignedEx(x.v)} (${x.n} trade${x.n===1?'':'s'})</title></rect>`;
    if(bw>=44 || i%2===(days.length-1)%2) g+=`<text x="${(L+i*bw+bw/2).toFixed(1)}" y="${H-6}" text-anchor="middle">${i===days.length-1?'Today':x.d.getDate()}</text>`;
  });
  host.innerHTML=`<svg viewBox="0 0 ${W} ${H}" role="img" aria-label="Daily trade profit, last 14 days">${g}</svg>`;
}
const STATE_PILL={New:'warn',Invited:'blue',InArea:'on',Trading:'on'};
const splitCamel=s=>(s||'').replace(/([a-z])([A-Z])/g,'$1 $2');
function renderRequests(sessions){
  const host=$('#trReqs'); if(!host) return;
  const list=sessions.filter(x=>x.state!=='Dismissed');
  setT('trReqCount', list.length?list.length+' active':'');
  if(!list.length){ host.innerHTML='<div class="empty">No open requests.</div>'; return; }
  host.innerHTML=list.map(x=>{
    const inc=x.direction==='Incoming';
    const pos=(x.left!=null&&x.top!=null)?`left ${x.left}, top ${x.top}`:'';
    const stash=x.stashTab?`Tab “${esc(x.stashTab)}”${pos?' · '+pos:''}`:(pos?esc(pos):'');
    const wh=(x.whispers&&x.whispers.length)?x.whispers[x.whispers.length-1]:'';
    return `<div class="req">
      <span class="lead"><span class="badge ${inc?'in':'out'}" title="${inc?'Someone wants to buy from you':'You asked to buy'}">${inc?'Incoming':'Outgoing'}</span></span>
      <div class="who"><span class="adot${x.inArea?' in':''}" title="${x.inArea?'In your area':'Not in your area'}"></span>${esc(x.player)}${x.guild?` <span class="g">&lt;${esc(x.guild)}&gt;</span>`:''}
        <span class="pill nodot ${STATE_PILL[x.state]||''}">${esc(splitCamel(x.state))}</span></div>
      <div class="side"><span class="price">${esc(fmtAmt(x.amount))} ${esc(x.currency||'')}</span>
        <button class="btn ghost sm" data-dismiss="${esc(String(x.id))}">Dismiss</button></div>
      <div class="what"><span class="it">${esc(x.item||'?')}</span>${stash?`<span>${stash}</span>`:''}<span>${esc(ago(x.created))}</span>${x.repeats?`<span>asked ${x.repeats+1}×</span>`:''}${x.note?`<span>${esc(x.note)}</span>`:''}</div>
      ${wh?`<div class="wh" title="${esc(wh)}">“${esc(wh)}”</div>`:''}
    </div>`;
  }).join('');
  $$('#trReqs [data-dismiss]').forEach(b=>b.onclick=()=>tradeOp({op:'dismiss',id:+b.dataset.dismiss}));
}
function renderHistory(d){
  const host=$('#trHist'), hist=d.history||[]; if(!host) return;
  const n=d.historyCount||hist.length;
  setT('trHistCount', n?(n+' trade'+(n===1?'':'s')+(n>hist.length?' · showing the latest '+hist.length:'')):'');
  const err=$('#trHistErr');
  if(err) err.innerHTML=d.historyError?`<div class="note-box bad" style="margin-bottom:10px">${esc(d.historyError)}</div>`:'';
  $('#trClear').disabled=!hist.length;
  if(!hist.length){ host.innerHTML='<div class="empty">No completed trades yet.</div>'; return; }
  host.innerHTML=`<table><thead><tr><th>Date</th><th>Side</th><th>Player</th><th>Item</th><th class="num">Price</th><th class="num">≈ Ex</th><th></th></tr></thead><tbody>`
    +hist.map(h=>{ const sold=h.direction!=='Bought';
      return `<tr><td class="nowrap dim">${esc(fmtDate(h.utc))}</td>
        <td><span class="badge ${sold?'sold':'bought'}">${sold?'Sold':'Bought'}</span></td>
        <td class="nowrap">${esc(h.player)}</td><td class="item" title="${esc(h.item)}">${esc(h.item)}</td>
        <td class="num nowrap">${esc(fmtAmt(h.amount))} ${esc(h.currency||'')}</td>
        <td class="num nowrap ${h.valueEx==null?'dim':''}">${h.valueEx==null?'—':(sold?'+':'−')+fmtNum(h.valueEx)}</td>
        <td class="num"><button class="delbtn" data-hdel="${esc(String(h.id))}" title="Delete this record" aria-label="Delete">✕</button></td></tr>`; }).join('')
    +'</tbody></table>';
  $$('#trHist [data-hdel]').forEach(b=>b.onclick=()=>tradeOp({op:'delete',id:b.dataset.hdel}));
}
async function tradeOp(body){
  try{
    const r=await fetch('/api/trade',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
    const j=await r.json().catch(()=>null);
    flashSaved(!!(j&&j.ok), j&&j.ok?(body.op==='dismiss'?'Dismissed':body.op==='clear'?'History cleared':'Deleted'):'Not changed');
  }catch(e){ flashSaved(false,'Overlay unreachable'); }
  pollTrade();
}
$('#trClear')?.addEventListener('click',()=>{ if(confirm('Delete the whole trade history? This can’t be undone.')) tradeOp({op:'clear'}); });

/* ── trade settings card (POSTs the whole {trade}) ── */
let tradeCfg=null;
function renderTradeCfg(){
  if(!tradeCfg) return;
  $$('[data-tr]').forEach(el=>{ const k=el.dataset.tr;
    if(el.type==='checkbox') el.checked=!!tradeCfg[k];
    else if(document.activeElement!==el) el.value=tradeCfg[k]??''; });
  $$('[data-trpct]').forEach(el=>{ el.value=Math.round((tradeCfg[el.dataset.trpct]||0)*100); });
  $$('[data-trpv]').forEach(el=>{ el.textContent=Math.round((tradeCfg[el.dataset.trpv]||0)*100)+'%'; });
}
async function saveTradeCfg(){
  if(!tradeCfg) return;
  const j=await saveSetting('trade',tradeCfg);
  if(j&&j.settings&&j.settings.trade){ tradeCfg=j.settings.trade; renderTradeCfg(); }
}
function wireTradeCfg(){
  $$('[data-tr]').forEach(el=>{ const k=el.dataset.tr;
    if(el.type==='checkbox') el.onchange=()=>{ if(!tradeCfg) return; tradeCfg[k]=el.checked; saveTradeCfg(); };
    else if(el.type==='number') el.onchange=()=>{ const v=parseInt(el.value,10); if(!isNaN(v)&&tradeCfg){ tradeCfg[k]=v; saveTradeCfg(); } };
    else el.onchange=()=>{ if(!tradeCfg) return; tradeCfg[k]=el.value.trim(); saveTradeCfg(); };
  });
  $$('[data-trpct]').forEach(el=>{ const k=el.dataset.trpct, pv=$(`[data-trpv="${k}"]`);
    el.oninput=()=>{ if(pv) pv.textContent=el.value+'%'; };
    el.onchange=()=>{ if(!tradeCfg) return; tradeCfg[k]=(+el.value)/100; saveTradeCfg(); }; });
}

/* ── waystone checker (POSTs the whole {mapCheck}) ── */
let mapCheck=null, _mcTimer=0;
function renderMapCheck(){
  if(!mapCheck) return;
  $('#mcEnabled').checked=!!mapCheck.enabled;
  const ta=$('#mcList'); if(document.activeElement!==ta) ta.value=(mapCheck.dangerous||[]).join('\n');
}
function mcFromText(){ mapCheck=mapCheck||{enabled:true,dangerous:[]}; mapCheck.dangerous=$('#mcList').value.split('\n').map(x=>x.trim()).filter(Boolean); }
async function saveMapCheck(rerender){
  clearTimeout(_mcTimer); _mcTimer=0;
  const j=await saveSetting('mapCheck',mapCheck);
  if(rerender && j&&j.settings&&j.settings.mapCheck){ mapCheck=j.settings.mapCheck; renderMapCheck(); }
}
function wireMapCheck(){
  $('#mcEnabled').onchange=e=>{ if(!mapCheck) return; mapCheck.enabled=e.target.checked; saveMapCheck(false); };
  $('#mcList').oninput=()=>{ if(!mapCheck) return; mcFromText(); clearTimeout(_mcTimer); _mcTimer=setTimeout(()=>saveMapCheck(false),800); };
  $('#mcList').onchange=()=>{ if(!mapCheck) return; mcFromText(); saveMapCheck(true); };
}

""";
}
