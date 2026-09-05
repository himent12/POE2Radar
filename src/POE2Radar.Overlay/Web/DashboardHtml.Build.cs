namespace POE2Radar.Overlay.Web;

internal static partial class DashboardHtml
{
    private const string BuildScript = """
let autoBuildPreview=null,autoBuildBusy=false;
const buildKeys=[[0,'Choose actual bound key'],[1,'Left mouse'],[2,'Right mouse'],[4,'Middle mouse'],[5,'Mouse 4'],[6,'Mouse 5'],[32,'Space'],...Array.from('1234567890ABCDEFGHIJKLMNOPQRSTUVWXYZ',k=>[k.charCodeAt(0),k])];
function autoBuildStatus(text){$('#autoBuildStatus').textContent=text;}
function updateAutoBuildApply(){
  const p=autoBuildPreview?.proposal,rows=p?.suggestions.filter(s=>s.selected)||[];
  const keys=rows.map(s=>s.skill.key);
  $('#autoBuildApply').disabled=autoBuildBusy||!p?.character.complete||!rows.length||rows.length>8||!rows.some(s=>s.role==='Main attack')||keys.some(k=>!k)||new Set(keys).size!==keys.length;
}
function renderAutoBuild(){
  const p=autoBuildPreview.proposal,c=p.character,v=c.vitals;
  $('#autoBuildSummary').textContent=c.character+' · level '+c.level+' · '+c.league+' · '+p.archetype+(v?' · Life '+v.hpUnreserved+' / Mana '+v.manaUnreserved+' / ES '+v.esUnreserved:'');
  $('#autoBuildWarnings').innerHTML=p.warnings.map(w=>'<li>'+esc(w)+'</li>').join('');
  $('#autoBuildEquipment').innerHTML=c.equipment.map(e=>'<li>'+esc(e.slot)+': <strong>'+esc(e.name)+'</strong></li>').join('')||'<li>No equipment read.</li>';
  $('#autoBuildRows').innerHTML=p.suggestions.map((s,i)=>'<div class="auto-build-row"><label><input type="checkbox" data-build-use="'+i+'" '+(s.selected?'checked':'')+' '+(!s.supported?'disabled':'')+'> <strong>'+esc(s.skill.name)+'</strong> · '+esc(s.role)+'</label><p>'+esc(s.reason)+'</p>'+(s.supported?'<label>Actual game key<select data-build-key="'+i+'">'+buildKeys.map(([key,label])=>'<option value="'+key+'" '+(key===s.skill.key?'selected':'')+'>'+label+'</option>').join('')+'</select></label><p>'+esc(skillSummary(s.skill))+' · CD '+s.skill.cooldownMs+' ms</p>':'')+'</div>').join('')||'<p>No equipped skills found.</p>';
  $('#autoBuildResult').hidden=false;
  document.querySelectorAll('[data-build-use]').forEach(el=>el.onchange=()=>{p.suggestions[+el.dataset.buildUse].selected=el.checked;updateAutoBuildApply();});
  document.querySelectorAll('[data-build-key]').forEach(el=>el.onchange=()=>{p.suggestions[+el.dataset.buildKey].skill.key=+el.value;updateAutoBuildApply();});
  updateAutoBuildApply();
}
$('#autoBuildScan')?.addEventListener('click',async()=>{
  if(autoBuildBusy)return;autoBuildBusy=true;$('#autoBuildScan').disabled=true;updateAutoBuildApply();autoBuildStatus('Reading your character, equipment and equipped gems…');
  try{
    const r=await fetch('/api/build',{cache:'no-store'}),data=await r.json();if(!r.ok)throw Error(data.error||'Scan failed');
    autoBuildPreview=data;data.proposal.suggestions.forEach(s=>s.selected=s.supported);renderAutoBuild();
    autoBuildStatus(data.proposal.character.complete?'Review the generated rules. Confirm every selected skill’s actual game key; unknown skills are skipped.':'Scan incomplete. Your existing build has not changed.');
  }catch(e){autoBuildPreview=null;$('#autoBuildResult').hidden=true;autoBuildStatus(e.message||'Scan failed; is the updated overlay running?');}
  finally{autoBuildBusy=false;$('#autoBuildScan').disabled=false;updateAutoBuildApply();}
});
$('#autoBuildApply')?.addEventListener('click',async()=>{
  if(autoBuildBusy||$('#autoBuildApply').disabled)return;
  if(!confirm('Replace your combat skill list, combat range and keep-distance with this proposal? Other settings stay unchanged. Input will NOT be armed.'))return;
  autoBuildBusy=true;$('#autoBuildScan').disabled=true;updateAutoBuildApply();
  try{
    const bindings=autoBuildPreview.proposal.suggestions.filter(s=>s.selected).map(s=>({metadata:s.metadata,key:s.skill.key}));
    const r=await fetch('/api/build',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({token:autoBuildPreview.token,bindings})}),data=await r.json();
    if(!r.ok)throw Error(data.error||'Apply failed');
    autoBuildStatus(data.note);$('#autoBuildUndo').disabled=false;autoBuildPreview=null;$('#autoBuildResult').hidden=true;await loadSettings();
  }catch(e){autoBuildStatus(e.message||'Apply failed');}
  finally{autoBuildBusy=false;$('#autoBuildScan').disabled=false;updateAutoBuildApply();}
});
$('#autoBuildUndo')?.addEventListener('click',async()=>{
  if(autoBuildBusy)return;autoBuildBusy=true;$('#autoBuildScan').disabled=true;updateAutoBuildApply();
  try{
    const r=await fetch('/api/build',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'undo'})}),data=await r.json();
    if(!r.ok)throw Error(data.error||'Undo failed');
    $('#autoBuildUndo').disabled=true;autoBuildStatus('Previous combat configuration restored.');await loadSettings();
  }catch(e){autoBuildStatus(e.message||'Undo failed');}
  finally{autoBuildBusy=false;$('#autoBuildScan').disabled=false;updateAutoBuildApply();}
});
""";
}
