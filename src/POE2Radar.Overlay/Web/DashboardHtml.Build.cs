namespace POE2Radar.Overlay.Web;

internal static partial class DashboardHtml
{
    private const string BuildScript = """
let autoBuildPreview=null,autoBuildBusy=false;
const buildKeys=[[0,'Choose actual bound key'],[1,'Left mouse'],[2,'Right mouse'],[4,'Middle mouse'],[5,'Mouse 4'],[6,'Mouse 5'],[32,'Space'],...Array.from('1234567890ABCDEFGHIJKLMNOPQRSTUVWXYZ',k=>[k.charCodeAt(0),k]),...Array.from({length:10},(_,i)=>[96+i,'Numpad '+i]),[186,';'],[187,'='],[188,','],[189,'-'],[190,'.'],[191,'/'],[192,'`'],[219,'['],[220,'\\'],[221,']'],[222,"'"]];
const buildModifiers=[[0,'None'],[2,'Ctrl'],[1,'Shift'],[4,'Alt'],[3,'Ctrl + Shift'],[6,'Ctrl + Alt'],[5,'Shift + Alt'],[7,'Ctrl + Shift + Alt']];
function autoBuildStatus(text){$('#autoBuildStatus').textContent=text;}
function buildSlot(slot){return autoBuildPreview?.proposal.input?.bindings.find(b=>b.action==='use_bound_skill'+slot);}
function buildValidation(){
  const p=autoBuildPreview?.proposal,rows=p?.suggestions.filter(s=>s.selected)||[];
  if(!p)return 'Scan your character to begin.';
  if(!p.character.complete)return 'Character scan is incomplete. Load into a zone and scan again.';
  if(Date.now()>=Date.parse(autoBuildPreview.expiresAt))return 'Preview expired. Scan again.';
  if(!rows.length||rows.length>13)return 'Select between one and thirteen skills.';
  if(!rows.some(s=>['Main attack','Summon','Low-mana fallback'].includes(s.role)))return 'Select a main attack, basic attack or summon.';
  if(rows.some(s=>!s.skill.key))return 'Assign a game slot or actual key to every selected skill.';
  const keys=rows.map(s=>s.skill.key+':'+(s.skill.modifiers||0));
  if(new Set(keys).size!==keys.length)return 'Two selected skills share the same key combination.';
  return '';
}
function updateAutoBuildApply(){
  const problem=buildValidation();
  $('#autoBuildApply').disabled=autoBuildBusy||!!problem;
  $('#autoBuildValidation').textContent=problem||'Ready to save. Review the selected skills and detected controls.';
  $('#autoBuildUndo').disabled=autoBuildBusy||!$('#autoBuildUndo').dataset.available;
}
function renderAutoBuild(){
  const p=autoBuildPreview.proposal,c=p.character,v=c.vitals,input=p.input;
  $('#autoBuildSummary').textContent=c.character+' · level '+c.level+' · '+c.league+' · '+p.archetype+(v?' · Life '+v.hpUnreserved+' / Mana '+v.manaUnreserved+' / ES '+v.esUnreserved:'');
  $('#autoBuildWarnings').innerHTML=p.warnings.map(w=>'<li>'+esc(w)+'</li>').join('');
  $('#autoBuildEquipment').innerHTML=c.equipment.map(e=>'<li>'+esc(e.slot)+': <strong>'+esc(e.name)+'</strong></li>').join('')||'<li>No equipment read.</li>';
  $('#autoBuildInput').textContent=input?.complete?'Detected '+input.mode+' controls'+(c.skillBar?.complete?' and live skill assignments.':'; confirm skill assignments below.'):'No complete game-key configuration. You can choose keys manually.';
  $('#autoBuildSource').textContent=input?.source||'No configuration selected.';
  const imported=['use_dodge_roll','use_flask_in_slot1','use_flask_in_slot2',...(input?.mode==='WASD'?['move_up','move_left','move_down','move_right']:[])];
  const controlNames={use_dodge_roll:'Dodge / run',use_flask_in_slot1:'Life flask',use_flask_in_slot2:'Mana flask',move_up:'Up',move_left:'Left',move_down:'Down',move_right:'Right'};
  $('#autoBuildControls').textContent=(input?.bindings||[]).filter(b=>imported.includes(b.action)).map(b=>controlNames[b.action]+': '+b.label).join(' · ');
  $('#autoBuildImport').disabled=!input?.complete||imported.some(a=>!input.bindings.some(b=>b.action===a&&b.usable&&b.modifiers===0));
  if(input?.mode==='Mouse'&&!c.skillBar?.complete)$('#autoBuildImport').disabled=true;
  if(input?.mode==='Mouse'&&c.skillBar?.complete){
    const move=c.skillBar.slots.filter(s=>s.actionId==='Move').map(s=>buildSlot(s.slot)).find(b=>b?.usable&&b.modifiers===0);
    $('#autoBuildControls').textContent+=' · Move Only: '+(move?.label||'Assign a single key in game before importing');
    if(!move)$('#autoBuildImport').disabled=true;
  }
  $('#autoBuildImport').checked=!$('#autoBuildImport').disabled;
  $('#autoBuildRows').innerHTML=p.suggestions.map((s,i)=>{
    const sk=s.skill,keys=buildKeys.some(k=>k[0]===sk.key)?buildKeys:[...buildKeys,[sk.key,'VK '+sk.key]];
    const slots=Array.from({length:13},(_,j)=>j+1).map(slot=>{
      const b=buildSlot(slot);return b?'<option value="'+slot+'" '+(sk.sourceSlot===slot?'selected':'')+' '+(!b.usable?'disabled':'')+'>Slot '+slot+' · '+esc(b.label)+'</option>':'';
    }).join('');
    return '<div class="auto-build-row"><label><input type="checkbox" data-build-use="'+i+'" '+(s.selected?'checked':'')+' '+(!s.supported?'disabled':'')+'> <strong>'+esc(sk.name)+'</strong> · '+esc(s.role)+'</label><p>'+esc(s.reason)+'</p>'+(s.supported?'<label>Assigned game slot<select data-build-slot="'+i+'"><option value="0">Manual key</option>'+slots+'</select></label><label>Actual game key<select data-build-key="'+i+'" '+(sk.sourceSlot?'disabled':'')+'>'+keys.map(([key,label])=>'<option value="'+key+'" '+(key===sk.key?'selected':'')+'>'+esc(label)+'</option>').join('')+'</select></label><label>Modifier<select data-build-mod="'+i+'" '+(sk.sourceSlot?'disabled':'')+'>'+buildModifiers.map(([m,label])=>'<option value="'+m+'" '+(m===(sk.modifiers||0)?'selected':'')+'>'+label+'</option>').join('')+'</select></label><p>'+esc(s.bindingSource)+' · '+esc(skillSummary(sk))+' · CD '+sk.cooldownMs+' ms · Cast interval '+sk.repeatGapMs+' ms'+(sk.holdMs?' · Hold '+sk.holdMs+' ms':'')+'</p>':'')+'</div>';
  }).join('')||'<p>No equipped skills found.</p>';
  $('#autoBuildResult').hidden=false;
  document.querySelectorAll('[data-build-use]').forEach(el=>el.onchange=()=>{p.suggestions[+el.dataset.buildUse].selected=el.checked;updateAutoBuildApply();});
  document.querySelectorAll('[data-build-slot]').forEach(el=>el.onchange=()=>{
    const s=p.suggestions[+el.dataset.buildSlot];s.skill.sourceSlot=+el.value;
    if(+el.value){const b=buildSlot(+el.value);s.skill.key=b.key;s.skill.modifiers=b.modifiers;s.bindingSource='Confirmed game slot '+el.value;}
    else{s.bindingSource='Manual binding';}
    const keepImport=$('#autoBuildImport').checked;renderAutoBuild();$('#autoBuildImport').checked=keepImport;
  });
  document.querySelectorAll('[data-build-key]').forEach(el=>el.onchange=()=>{p.suggestions[+el.dataset.buildKey].skill.key=+el.value;updateAutoBuildApply();});
  document.querySelectorAll('[data-build-mod]').forEach(el=>el.onchange=()=>{p.suggestions[+el.dataset.buildMod].skill.modifiers=+el.value;updateAutoBuildApply();});
  updateAutoBuildApply();
}
setInterval(()=>{if(autoBuildPreview)updateAutoBuildApply();},1000);
$('#autoBuildScan')?.addEventListener('click',async()=>{
  if(autoBuildBusy)return;autoBuildBusy=true;$('#autoBuildScan').disabled=true;updateAutoBuildApply();autoBuildStatus('Reading character, equipped gems and game key settings…');
  try{
    const r=await fetch('/api/build',{cache:'no-store'}),data=await r.json();if(!r.ok)throw Error(data.error||'Scan failed');
    autoBuildPreview=data;data.proposal.suggestions.forEach(s=>s.selected=s.recommended);renderAutoBuild();
    autoBuildStatus(data.proposal.character.complete?(data.proposal.character.skillBar?.complete?'Your live skill assignments and keys are filled in. Review the rotation and save.':'Review your rotation and confirm each skill’s bar slot.'):'Scan incomplete. Your existing build has not changed.');
  }catch(e){autoBuildPreview=null;$('#autoBuildResult').hidden=true;autoBuildStatus(e.message||'Scan failed; is the updated overlay running?');}
  finally{autoBuildBusy=false;$('#autoBuildScan').disabled=false;updateAutoBuildApply();}
});
$('#autoBuildApply')?.addEventListener('click',async()=>{
  if(autoBuildBusy||$('#autoBuildApply').disabled)return;
  autoBuildBusy=true;$('#autoBuildScan').disabled=true;updateAutoBuildApply();
  try{
    const bindings=autoBuildPreview.proposal.suggestions.filter(s=>s.selected).map(s=>({metadata:s.metadata,key:s.skill.key,modifiers:s.skill.modifiers||0,slot:s.skill.sourceSlot||0}));
    const r=await fetch('/api/build',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({token:autoBuildPreview.token,bindings,importControls:$('#autoBuildImport').checked})}),data=await r.json();
    if(!r.ok)throw Error(data.error||'Apply failed');
    autoBuildStatus(data.note);$('#autoBuildUndo').dataset.available='true';autoBuildPreview=null;$('#autoBuildResult').hidden=true;await loadSettings();
  }catch(e){autoBuildStatus(e.message||'Apply failed');}
  finally{autoBuildBusy=false;$('#autoBuildScan').disabled=false;updateAutoBuildApply();}
});
$('#autoBuildUndo')?.addEventListener('click',async()=>{
  if(autoBuildBusy)return;autoBuildBusy=true;$('#autoBuildScan').disabled=true;updateAutoBuildApply();
  try{
    const r=await fetch('/api/build',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({action:'undo'})}),data=await r.json();
    if(!r.ok)throw Error(data.error||'Undo failed');
    delete $('#autoBuildUndo').dataset.available;autoBuildPreview=null;$('#autoBuildResult').hidden=true;autoBuildStatus('Previous combat configuration and controls restored.');await loadSettings();
  }catch(e){autoBuildStatus(e.message||'Undo failed');}
  finally{autoBuildBusy=false;$('#autoBuildScan').disabled=false;updateAutoBuildApply();}
});
""";
}
