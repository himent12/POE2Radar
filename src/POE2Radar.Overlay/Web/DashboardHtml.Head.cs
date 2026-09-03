namespace POE2Radar.Overlay.Web;

/// <summary>Dashboard page — document head + stylesheet.</summary>
internal static partial class DashboardHtml
{
    private const string Head = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>POE2Radar — Console</title>
<!-- Self-contained: no external fonts/CDNs. Falls back to local system serif/mono fonts. -->
<style>
  :root{
    --bg:#0a0907; --bg2:#100d09; --panel:#15110b; --panel2:#1b1610;
    --line:#3a2f1d; --line-soft:#271f14;
    --ink:#e8dcc2; --ink-dim:#9c8e72; --ink-faint:#6b5f49;
    --gold:#c8a049; --gold-bright:#ecca7e; --gold-deep:#8a6d34;
    --blood:#9c342a; --blood-bright:#d6584a;
    --rare:#f1e36b; --magic:#7f93ff; --unique:#d2641e; --normal:#cdc6b4;
    --good:#79b06a; --poi:#4bb3c4;
    --shadow:0 18px 40px -20px rgba(0,0,0,.9);
  }
  *{box-sizing:border-box}
  html,body{height:100%}
  body{
    margin:0; background:
      radial-gradient(120% 90% at 50% -10%, #1a150d 0%, var(--bg) 55%) fixed,
      var(--bg);
    color:var(--ink);
    font-family:"IBM Plex Mono","Consolas",ui-monospace,monospace;
    font-size:13px; line-height:1.5;
    -webkit-font-smoothing:antialiased;
    overflow:hidden;
  }
  /* grain + vignette atmosphere */
  body::before{
    content:""; position:fixed; inset:0; pointer-events:none; z-index:999;
    background:radial-gradient(120% 120% at 50% 40%, transparent 58%, rgba(0,0,0,.55) 100%);
    mix-blend-mode:multiply;
  }
  body::after{
    content:""; position:fixed; inset:0; pointer-events:none; z-index:998; opacity:.045;
    background-image:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='160' height='160'%3E%3Cfilter id='n'%3E%3CfeTurbulence type='fractalNoise' baseFrequency='.9' numOctaves='2'/%3E%3C/filter%3E%3Crect width='100%25' height='100%25' filter='url(%23n)'/%3E%3C/svg%3E");
  }

  .shell{display:grid; grid-template-rows:auto 1fr; height:100vh}

  /* ── masthead ── */
  header{
    display:flex; align-items:center; gap:20px; padding:14px 26px;
    border-bottom:1px solid var(--line);
    background:linear-gradient(180deg, rgba(30,24,14,.6), transparent);
  }
  .mark{display:flex; align-items:baseline; gap:12px}
  .mark h1{
    font-family:"Cinzel","Georgia",serif; font-weight:700; font-size:22px; margin:0;
    letter-spacing:.14em; color:var(--gold-bright);
    text-shadow:0 1px 0 #000, 0 0 22px rgba(200,160,73,.25);
  }
  .mark .sub{font-size:10px; letter-spacing:.42em; color:var(--ink-faint); text-transform:uppercase}
  .hgap{flex:1}
  .conn{display:flex; align-items:center; gap:9px; font-size:11px; letter-spacing:.1em; color:var(--ink-dim); text-transform:uppercase}
  .dot{width:9px; height:9px; border-radius:50%; background:var(--blood); box-shadow:0 0 0 0 rgba(214,88,74,.5); }
  .conn.live .dot{background:var(--good); animation:pulse 2.2s infinite}
  @keyframes pulse{0%{box-shadow:0 0 0 0 rgba(121,176,106,.5)}70%{box-shadow:0 0 0 7px rgba(121,176,106,0)}100%{box-shadow:0 0 0 0 rgba(121,176,106,0)}}
  .area-chip{
    font-family:"Cinzel","Georgia",serif; letter-spacing:.08em; color:var(--ink);
    border:1px solid var(--line); padding:5px 14px; border-radius:2px;
    background:var(--panel); font-size:13px;
  }
  .area-chip b{color:var(--gold-bright); font-weight:600}

  /* ── body grid ── */
  .body{display:grid; grid-template-columns:300px 1fr; gap:0; min-height:0}
  aside{
    border-right:1px solid var(--line); padding:22px 22px 0;
    overflow-y:auto; background:linear-gradient(180deg, rgba(20,16,10,.5), transparent 220px);
  }
  main{display:grid; grid-template-rows:auto 1fr; min-height:0; min-width:0}

  /* ── vitals ── */
  .vital{margin-bottom:18px}
  .vital .vlabel{display:flex; justify-content:space-between; font-size:10px; letter-spacing:.18em; text-transform:uppercase; color:var(--ink-dim); margin-bottom:6px}
  .vital .vlabel .num{color:var(--ink); font-weight:600}
  .bar{height:9px; border:1px solid var(--line); background:#0c0a07; border-radius:1px; overflow:hidden; position:relative}
  .bar > i{display:block; height:100%; transition:width .35s ease}
  .bar.hp > i{background:linear-gradient(90deg,#6e1f18,var(--blood-bright))}
  .bar.es > i{background:linear-gradient(90deg,#1f6e63,#33e0c4)}
  .bar.mana > i{background:linear-gradient(90deg,#23306e,var(--magic))}

  .sect{font-family:"Cinzel","Georgia",serif; font-size:12px; letter-spacing:.22em; text-transform:uppercase; color:var(--gold); margin:24px 0 12px; display:flex; align-items:center; gap:10px}
  .sect::after{content:""; flex:1; height:1px; background:linear-gradient(90deg,var(--line),transparent)}

  .kv{display:flex; justify-content:space-between; padding:5px 0; border-bottom:1px dotted var(--line-soft); font-size:12px}
  .kv span:first-child{color:var(--ink-faint); letter-spacing:.04em}
  .kv span:last-child{color:var(--ink); font-weight:500}

  .tally{display:grid; grid-template-columns:1fr 1fr; gap:7px; margin-top:4px}
  .tally .t{border:1px solid var(--line-soft); background:var(--panel); padding:9px 10px; border-radius:2px}
  .tally .t .n{font-size:20px; font-weight:600; color:var(--gold-bright); font-family:"Cinzel","Georgia",serif; line-height:1}
  .tally .t .l{font-size:9px; letter-spacing:.16em; text-transform:uppercase; color:var(--ink-faint); margin-top:4px}

  /* ── zone leveling notes ── */
  .znotes{margin-top:12px; padding:11px 13px; border:1px solid var(--line-soft); border-left:2px solid var(--gold-deep); border-radius:2px; background:var(--panel); white-space:pre-wrap; font-size:11px; line-height:1.5; color:var(--ink-dim); max-height:240px; overflow:auto}
  .znotes .zt{font-family:"Cinzel","Georgia",serif; font-size:11px; letter-spacing:.1em; color:var(--gold-bright); margin-bottom:6px; white-space:normal}

  /* ── tabs ── */
  .tabs{display:flex; gap:2px; padding:14px 26px 0; border-bottom:1px solid var(--line)}
  .tab{
    font-family:"Cinzel","Georgia",serif; font-size:12px; letter-spacing:.16em; text-transform:uppercase;
    color:var(--ink-faint); background:transparent; border:1px solid transparent; border-bottom:none;
    padding:9px 20px; cursor:pointer; border-radius:3px 3px 0 0; position:relative; top:1px;
  }
  .tab:hover{color:var(--ink-dim)}
  .tab.on{color:var(--gold-bright); background:var(--panel); border-color:var(--line); }
  .tab.on::after{content:""; position:absolute; left:0; right:0; bottom:-1px; height:2px; background:var(--panel)}

  .view{overflow:auto; padding:22px 26px; min-height:0}
  .view[hidden]{display:none}
  /* ── atlas tab ── */
  .arow{display:grid; grid-template-columns:minmax(200px,2fr) minmax(120px,1.4fr) 120px; gap:10px; align-items:center;
        padding:5px 10px; border-bottom:1px solid var(--line); font-size:13px}
  .arow.ahead{font-weight:600; color:var(--ink-dim); border-bottom:1px solid var(--line); position:sticky; top:0; background:var(--panel)}
  .arow.val{background:rgba(255,168,38,.07)}
  .arow .acode{font-family:ui-monospace,Consolas,monospace; color:var(--ink)}
  .arow.val .acode{color:var(--gold-bright)}
  .arow .aname{color:var(--ink-dim)}
  .arow .aid{display:inline-block; min-width:22px; color:var(--ink-dim); font-family:ui-monospace,Consolas,monospace}
  .rin{color:#6ee787; font-weight:600} .rno{color:var(--ink-dim); opacity:.5}
  .arow.nrow{grid-template-columns:60px minmax(90px,1fr) minmax(200px,2fr) 130px; cursor:pointer}
  .arow.nrow:hover{background:rgba(255,255,255,.04)}
  .arow.nrow.sel{background:rgba(60,220,255,.16); outline:1px solid var(--edge,#3cdcff)}
  .amono{font-family:ui-monospace,Consolas,monospace; color:var(--ink-dim); font-size:12px}
  .ntag{font-size:10px; font-weight:600; padding:0 6px; border-radius:8px; border:1px solid var(--line); margin-right:3px}
  .ntag.tc{color:#ff9f43;border-color:#a35a00} .ntag.tv{color:var(--ink-dim)} .ntag.tu{color:#6ee787;border-color:#2f6b3f}
  .ntag.tk{color:#73a6ff;border-color:#2a4a80} .ntag.ts{color:#c98bff;border-color:#5a3a80}
  .akind{font-size:11px; font-weight:600; padding:1px 8px; border-radius:10px; border:1px solid var(--line); color:var(--ink-dim)}
  .akind.k-boss{color:#ff7300; border-color:#ff7300} .akind.k-unique{color:#ff9f43; border-color:#a35a00}
  .akind.k-tower{color:#73a6ff; border-color:#2a4a80} .akind.k-merchant{color:#c98bff; border-color:#5a3a80}

  /* ── controls ── */
  .controls{display:flex; flex-wrap:wrap; gap:8px; align-items:center; margin-bottom:16px}
  .chip{
    font-size:11px; letter-spacing:.06em; color:var(--ink-dim);
    border:1px solid var(--line-soft); background:var(--panel); padding:6px 12px; border-radius:14px; cursor:pointer;
    transition:all .15s;
  }
  .chip:hover{border-color:var(--gold-deep); color:var(--ink)}
  .chip.on{background:var(--gold-deep); border-color:var(--gold); color:#1a140a; font-weight:600}
  .chips{display:flex; flex-wrap:wrap; gap:6px; margin:4px 0 12px}
  input[type=search]{
    font-family:inherit; font-size:12px; color:var(--ink); background:#0c0a07;
    border:1px solid var(--line); border-radius:2px; padding:7px 12px; min-width:200px; flex:1;
  }
  input[type=search]:focus{outline:none; border-color:var(--gold-deep)}
  input[type=search]::placeholder{color:var(--ink-faint)}

  /* ── tables ── */
  table{width:100%; border-collapse:collapse; font-size:12px}
  thead th{
    text-align:left; font-weight:500; font-size:10px; letter-spacing:.14em; text-transform:uppercase;
    color:var(--ink-faint); padding:8px 10px; border-bottom:1px solid var(--line); position:sticky; top:-22px;
    background:var(--bg);
  }
  tbody td{padding:7px 10px; border-bottom:1px solid var(--line-soft); white-space:nowrap}
  tbody tr:hover{background:rgba(200,160,73,.05)}
  .meta{color:var(--ink-faint); font-size:11px; max-width:380px; overflow:hidden; text-overflow:ellipsis}
  .rar-Normal{color:var(--normal)} .rar-Magic{color:var(--magic)} .rar-Rare{color:var(--rare)} .rar-Unique{color:var(--unique)}
  .pill{font-size:9px; letter-spacing:.1em; text-transform:uppercase; padding:2px 7px; border-radius:10px; border:1px solid currentColor}
  .friendly{color:var(--good)} .hostile{color:var(--blood-bright)}
  .num-r{text-align:right; color:var(--ink-dim)}
  .hpbar{width:60px; height:6px; border:1px solid var(--line); border-radius:1px; overflow:hidden; display:inline-block; vertical-align:middle}
  .hpbar > i{display:block; height:100%; background:linear-gradient(90deg,#6e1f18,var(--blood-bright))}

  .lm{display:flex; align-items:center; gap:14px; padding:11px 14px; border:1px solid var(--line-soft); border-radius:3px; margin-bottom:8px; background:var(--panel)}
  .lm:hover{border-color:var(--gold-deep)}
  .lm .name{font-family:"Spectral","Georgia",serif; font-size:15px; color:var(--gold-bright); font-style:italic}
  .lm .path{font-size:10px; color:var(--ink-faint); overflow:hidden; text-overflow:ellipsis; white-space:nowrap}
  .lm .dist{margin-left:auto; font-family:"Cinzel","Georgia",serif; color:var(--ink); font-size:14px; flex:none}
  .lm .dist small{color:var(--ink-faint); font-size:9px; letter-spacing:.1em; display:block; text-align:right}

  .empty{color:var(--ink-faint); text-align:center; padding:60px 0; font-style:italic; font-family:"Spectral","Georgia",serif; font-size:15px}
  ::-webkit-scrollbar{width:10px;height:10px}
  ::-webkit-scrollbar-thumb{background:var(--line); border-radius:5px; border:2px solid var(--bg)}
  ::-webkit-scrollbar-track{background:transparent}

  /* ── console / control panel ── */
  .panel-grid{display:grid; grid-template-columns:repeat(auto-fill,minmax(330px,1fr)); gap:22px; align-items:start}
  .card{border:1px solid var(--line); border-radius:4px; background:var(--panel); padding:18px 22px; box-shadow:var(--shadow)}
  .card h3{font-family:"Cinzel","Georgia",serif; font-size:12px; letter-spacing:.2em; text-transform:uppercase; color:var(--gold); margin:0 0 8px}
  .card h3 .tag{color:var(--ink-faint); font-size:10px; letter-spacing:.1em}
  .row{display:flex; align-items:center; justify-content:space-between; gap:16px; padding:11px 0; border-bottom:1px dotted var(--line-soft)}
  .row:last-child{border-bottom:none}
  .row .rl{font-size:12px; color:var(--ink); min-width:0}
  .row .rl small{display:block; color:var(--ink-faint); font-size:10px; letter-spacing:.03em; margin-top:3px; line-height:1.4}
  .sw{position:relative; width:44px; height:23px; flex:none; cursor:pointer; display:inline-block}
  .sw input{opacity:0; width:0; height:0; position:absolute}
  .sw .track{position:absolute; inset:0; background:#0c0a07; border:1px solid var(--line); border-radius:12px; transition:.2s}
  .sw .knob{position:absolute; top:3px; left:3px; width:15px; height:15px; border-radius:50%; background:var(--ink-faint); transition:.2s}
  .sw input:checked ~ .track{background:var(--gold-deep); border-color:var(--gold)}
  .sw input:checked ~ .knob{transform:translateX(21px); background:var(--gold-bright); box-shadow:0 0 9px -1px var(--gold-bright)}
  .numin{font-family:inherit; font-size:12px; color:var(--ink); background:#0c0a07; border:1px solid var(--line); border-radius:2px; padding:6px 9px; width:96px; text-align:right}
  .numin:focus{outline:none; border-color:var(--gold-deep)}
  .ro{color:var(--gold-bright); font-family:"Cinzel","Georgia",serif; font-size:14px}
  .hint-row{color:var(--ink-faint)!important; font-size:11px!important; font-style:italic}
  .saved{font-size:10px; letter-spacing:.18em; text-transform:uppercase; color:var(--good); opacity:0; transition:opacity .3s}
  .saved.show{opacity:1}

  /* ── icon / mechanic style editors ── */
  .stylerow{display:flex; align-items:center; gap:9px; padding:9px 0; border-bottom:1px dotted var(--line-soft); flex-wrap:wrap}
  .stylerow:last-child{border-bottom:none}
  .stylerow .nm{flex:1 1 110px; min-width:90px; font-size:12px; color:var(--ink)}
  .stylerow .sw{width:38px; height:20px}
  .stylerow .sw .knob{width:13px; height:13px}
  .stylerow .sw input:checked ~ .knob{transform:translateX(18px)}
  input[type=color]{width:30px; height:24px; padding:0; border:1px solid var(--line); background:#0c0a07; border-radius:2px; cursor:pointer; flex:none}
  input[type=range].op{width:78px; accent-color:var(--gold); flex:none}
  .opv{font-size:10px; color:var(--ink-faint); width:30px; text-align:right}
  .numin.sz{width:56px}
  .mechrow{border:1px solid var(--line-soft); border-radius:3px; background:var(--panel2); padding:10px 12px; margin-bottom:8px}
  .mechrow .top{display:flex; align-items:center; gap:9px; margin-bottom:8px}
  .mechrow .top input.mname{flex:1; font-family:inherit; font-size:12px; color:var(--ink); background:#0c0a07; border:1px solid var(--line); border-radius:2px; padding:5px 9px}
  .mechrow .matchin{width:100%; font-family:inherit; font-size:11px; color:var(--ink-dim); background:#0c0a07; border:1px solid var(--line); border-radius:2px; padding:5px 9px; margin-bottom:8px}
  .mechrow .ctl{display:flex; align-items:center; gap:9px; flex-wrap:wrap}
  .mcats{display:flex; align-items:center; gap:6px; flex-wrap:wrap; margin-bottom:8px}
  .mcats-lbl{font-size:10px; letter-spacing:.06em; text-transform:uppercase; color:var(--ink-faint); margin-right:2px}
  .mcats-hint{font-size:10px; font-style:italic; color:var(--ink-faint)}
  .catchip{display:inline-flex; align-items:center; font-size:11px; color:var(--ink-dim); background:#0c0a07; border:1px solid var(--line); border-radius:10px; padding:2px 9px; cursor:pointer; user-select:none}
  .catchip:hover{border-color:var(--gold-deep)}
  .catchip.on{color:var(--bg); background:var(--gold); border-color:var(--gold-bright); font-weight:600}
  .catchip input{display:none}
  /* Display-rule rows: collapsed one-line header, expand to the full editor. */
  .drrow{padding:8px 12px}
  .drhead{display:flex; align-items:center; gap:9px; cursor:pointer}
  .drhead .sw{flex:none}
  .drcaret{color:var(--ink-faint); width:10px; font-size:10px; flex:none}
  .drswatch{width:15px; height:15px; flex:none; display:inline-flex}
  .drswatch svg{width:15px; height:15px; display:block}
  .drnm{font-weight:600; color:var(--ink); white-space:nowrap; flex:none; max-width:200px; overflow:hidden; text-overflow:ellipsis}
  .drsum{flex:1 1 auto; min-width:0; color:var(--ink-faint); font-size:11px; white-space:nowrap; overflow:hidden; text-overflow:ellipsis}
  .drbadges{display:inline-flex; gap:4px; flex:none}
  .drbadge{font-size:9px; text-transform:uppercase; letter-spacing:.05em; color:var(--ink-dim); border:1px solid var(--line); border-radius:8px; padding:1px 6px; white-space:nowrap}
  .drbadge.hide{color:var(--blood-bright); border-color:var(--blood)}
  .drrow.off .drnm,.drrow.off .drsum,.drrow.off .drswatch{opacity:.45}
  .drbody{margin-top:10px; padding-top:10px; border-top:1px dotted var(--line-soft)}
  .drbody .top{align-items:center; margin-bottom:8px}
  .drord{display:inline-flex; gap:2px; flex:none}
  .drhead .delbtn{flex:none}
  .ordbtn{font-size:10px; line-height:1; color:var(--ink-dim); background:#0c0a07; border:1px solid var(--line); border-radius:2px; padding:3px 6px; cursor:pointer}
  .ordbtn:hover{color:var(--gold-bright); border-color:var(--gold-deep)}
  .drconds{display:flex; align-items:center; gap:10px; flex-wrap:wrap; margin-bottom:8px}
  .drsel{display:inline-flex; align-items:center; gap:5px; font-size:10px; letter-spacing:.05em; text-transform:uppercase; color:var(--ink-faint)}
  .drsel select{font-family:inherit; font-size:11px; text-transform:none; letter-spacing:0; color:var(--ink); background:#0c0a07; border:1px solid var(--line); border-radius:2px; padding:3px 6px}
  .drsel select:hover{border-color:var(--gold-deep)}
  .drflag{display:inline-flex; align-items:center; gap:5px; font-size:11px; color:var(--ink-dim); cursor:pointer; user-select:none; white-space:nowrap}
  .dr-hideflag{color:var(--blood-bright)}
  .drrow.hideon{opacity:.72}
  .drrow.hideon .iconpick,.drrow.hideon .dr-color,.drrow.hideon .dr-op,.drrow.hideon .dr-size,.drrow.hideon .dr-label,.drrow.hideon .opv{opacity:.4; pointer-events:none}
  /* consolidated HP-bar card: per-rarity grid + shared geometry footer */
  .hpgrid{display:grid; grid-template-columns:30px 64px 1fr 30px 1fr; gap:9px 11px; align-items:center; padding:4px 0 2px}
  .hpgrid input[type=checkbox]{margin:0; justify-self:center}
  .hpgrid .hph{font-size:10px; letter-spacing:.06em; text-transform:uppercase; color:var(--ink-faint); text-align:right}
  .hpgrid .hph:first-child{text-align:left}
  .hpgrid .hpr{font-size:12px; color:var(--ink)}
  .hpgrid .numin{width:100%; min-width:0; padding:5px 8px}
  .hpgrid input[type=color]{width:100%}
  .hpshared{display:flex; gap:16px; flex-wrap:wrap; margin-top:10px; padding-top:11px; border-top:1px dotted var(--line-soft)}
  .hpshared label{display:flex; align-items:center; gap:7px; font-size:11px; color:var(--ink-dim)}
  .hpshared .numin{width:62px}
  .delbtn{font-family:inherit; font-size:11px; color:var(--blood-bright); background:transparent; border:1px solid var(--line); border-radius:2px; padding:4px 9px; cursor:pointer; flex:none}
  .trow-ctl{display:flex; align-items:center; gap:9px; flex:none}

  /* ── SVG icon picker (replaces the plain shape <select>): a button showing the chosen icon's
       silhouette + name, opening a shared popup grid of icon previews. ── */
  .iconpick{display:inline-flex; align-items:center; gap:6px; min-width:104px; background:#0c0a07; border:1px solid var(--line); border-radius:2px; padding:3px 7px; cursor:pointer; flex:none}
  .iconpick:hover{border-color:var(--gold-deep)}
  .iconpick .ipreview{width:15px; height:15px; flex:none; display:inline-flex; color:var(--ink)}
  .iconpick .ipreview svg{width:15px; height:15px; display:block}
  .iconpick .ipname{font-size:11px; color:var(--ink); white-space:nowrap; overflow:hidden; text-overflow:ellipsis}
  .iconpick .ipcar{margin-left:auto; color:var(--ink-faint); font-size:8px}
  #iconPop{position:fixed; z-index:1000; display:none; background:var(--panel2); border:1px solid var(--gold-deep); border-radius:4px; box-shadow:var(--shadow); padding:8px; max-height:300px; overflow:auto}
  #iconPop.open{display:block}
  /* Add-rule picker modal: browse live entities + terrain tiles. */
  #pickPop{position:fixed; inset:0; z-index:1100; display:none; background:rgba(0,0,0,.62); padding:6vh 4vw}
  #pickPop.open{display:flex; justify-content:center; align-items:flex-start}
  .pickbox{display:flex; flex-direction:column; width:min(760px,100%); max-height:88vh; background:var(--panel); border:1px solid var(--gold-deep); border-radius:6px; box-shadow:var(--shadow); overflow:hidden}
  .pickhead{display:flex; align-items:center; gap:10px; padding:12px 14px; border-bottom:1px solid var(--line)}
  .pickhead #pickSearch{flex:1; font-family:inherit; font-size:13px; color:var(--ink); background:#0c0a07; border:1px solid var(--line); border-radius:3px; padding:8px 11px}
  .pickkinds{display:inline-flex; gap:3px}
  .pickclose{font-size:13px; color:var(--ink-dim); background:transparent; border:1px solid var(--line); border-radius:3px; padding:6px 10px; cursor:pointer}
  .pickclose:hover{color:var(--blood-bright); border-color:var(--blood)}
  .picklist{overflow:auto; padding:4px 0}
  .pickrow{display:flex; align-items:center; gap:10px; padding:7px 14px; cursor:pointer; border-bottom:1px dotted var(--line-soft)}
  .pickrow:hover{background:var(--panel2)}
  .pickbadge{flex:none; font-size:9px; text-transform:uppercase; letter-spacing:.05em; color:var(--ink-dim); background:#0c0a07; border:1px solid var(--line); border-radius:8px; padding:2px 7px; min-width:58px; text-align:center}
  .pickbadge.tile{color:var(--poi); border-color:var(--poi)}
  .pickbadge.entity{color:var(--gold)}
  .pickbadge.mod{color:#26d9c0; border-color:#1c9e8c}
  .pickcount{flex:none; font-size:10px; color:var(--ink-dim); font-family:"Cinzel","Georgia",serif}
  .picknm{flex:none; font-weight:600; color:var(--ink); max-width:230px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap}
  .picksub{flex:1; min-width:0; color:var(--ink-faint); font-size:11px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap}
  .pickrar{flex:none; font-size:10px; color:var(--rare)}
  .pickempty{padding:24px 14px; color:var(--ink-faint); font-style:italic; text-align:center}
  .pickfoot{padding:9px 14px; border-top:1px solid var(--line); color:var(--ink-faint); font-size:11px}
  /* Landmarks tab rows */
  .lmrow{display:flex; align-items:center; gap:10px; padding:6px 0; border-bottom:1px dotted var(--line-soft)}
  .lmbadge{flex:none; min-width:48px; text-align:center; font-size:9px; text-transform:uppercase; letter-spacing:.05em; color:var(--ink-dim); border:1px solid var(--line); border-radius:8px; padding:2px 6px}
  .lmbadge.user{color:var(--gold); border-color:var(--gold-deep)}
  .lmbadge.hidden{color:var(--blood-bright); border-color:var(--blood)}
  .lmarea{flex:none; min-width:64px; font-size:11px; color:var(--ink-dim); font-family:"Consolas",monospace}
  .lmlabel{flex:none; width:200px}
  .lmpath{flex:1; min-width:0; color:var(--ink-faint); font-size:11px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; font-family:"Consolas",monospace}
  .lmrow.sup .lmlabel,.lmrow.sup .lmpath{opacity:.5}
  .ipop-grid{display:grid; grid-template-columns:repeat(6,38px); gap:4px}
  .ipop-cell{display:flex; flex-direction:column; align-items:center; justify-content:center; gap:3px; width:38px; height:40px; border:1px solid transparent; border-radius:3px; cursor:pointer; color:var(--ink)}
  .ipop-cell:hover{border-color:var(--gold); background:#0c0a07}
  .ipop-cell.sel{border-color:var(--gold-bright); background:#0c0a07}
  .ipop-cell svg{width:20px; height:20px; display:block}
  .ipop-cell .cn{font-size:7px; line-height:1; color:var(--ink-faint); max-width:36px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap}
  .delbtn:hover{border-color:var(--blood-bright)}
  .addbtn{font-family:"Cinzel","Georgia",serif; font-size:11px; letter-spacing:.1em; color:var(--gold-bright); background:transparent; border:1px dashed var(--gold-deep); border-radius:3px; padding:8px 14px; cursor:pointer; width:100%; margin-top:4px}
  .addbtn:hover{background:rgba(200,160,73,.07)}
  .skrow{display:flex;align-items:center;gap:8px;padding:7px 0;border-bottom:1px dotted var(--line-soft)}
  .skrow:last-child{border-bottom:none}
  .skrow .skn{color:var(--ink-faint);font-size:10px;width:16px;flex:none}
  .skrow .sk-key{width:44px;text-align:center}
  .skrow .sk-cd{width:80px}
  .skrow .sk-rg{width:72px}
.skrow .sk-min{width:64px}
.skrow .sk-hp{width:64px}
.skrow input[type=checkbox]{width:16px;height:16px;margin:0 10px 0 4px}
  .skhead{display:flex;align-items:center;gap:8px;padding:4px 0 2px;color:var(--ink-faint);font-size:10px;letter-spacing:.06em;text-transform:uppercase}
  .skhead span:nth-child(1){width:16px;flex:none}
  .skhead span:nth-child(2){width:44px;flex:none}
  .skhead span:nth-child(3){width:80px;flex:none}
.skhead span:nth-child(4){width:72px;flex:none}
.skhead span:nth-child(5){width:64px;flex:none}
.skhead span:nth-child(6){width:64px;flex:none}
.skhead span:nth-child(7){width:30px;flex:none}
.skhead span:nth-child(8){width:30px;flex:none}
  .skhead span:nth-child(4){width:72px;flex:none}

  /* ── dashboard nav list ── */
  .navrow{display:flex; align-items:center; gap:12px; padding:9px 12px; border:1px solid var(--line-soft); border-radius:3px; margin-bottom:6px; background:var(--panel); cursor:pointer}
  .navrow:hover{border-color:var(--gold-deep)}
  .navrow.sel{border-color:var(--gold); background:rgba(200,160,73,.07)}
  .navbtn{width:18px; height:18px; flex:none; border:1px solid var(--ink-faint); border-radius:50%; display:flex; align-items:center; justify-content:center; font-size:11px; color:#120d06; line-height:1}
  .navrow:not(.sel) .navbtn{color:var(--ink-faint)}
  .navname{flex:1; min-width:0; color:var(--ink); overflow:hidden; text-overflow:ellipsis; white-space:nowrap; font-family:"Spectral","Georgia",serif; font-size:14px}
  .navrow.sel .navname{color:var(--gold-bright)}
  .navtag{font-size:9px; letter-spacing:.12em; text-transform:uppercase; color:var(--ink-faint); border:1px solid var(--line-soft); border-radius:10px; padding:2px 8px; flex:none}
  .navdist{font-family:"Cinzel","Georgia",serif; color:var(--ink-dim); font-size:13px; min-width:48px; text-align:right; flex:none}
</style>
</head>
""";
}
