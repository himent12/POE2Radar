namespace POE2Radar.Overlay.Web;

/// <summary>Dashboard page — document head + stylesheet (calm dark UI matching the in-game INSERT menu).</summary>
internal static partial class DashboardHtml
{
    private const string Head = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>POE2 Radar</title>
<!-- Self-contained: no external fonts/CDNs/images. System UI font stack; inline SVG only. -->
<style>
  :root{
    --bg:#131416; --rail:#16171a; --panel:#1a1b1e; --panel2:#202226; --panel3:#26282d; --field:#111214;
    --line:rgba(255,255,255,.08); --line-strong:rgba(255,255,255,.14); --line-soft:rgba(255,255,255,.05);
    --ink:#e7e8eb; --ink-dim:#a4a7ae; --ink-faint:#71757d;
    --accent:#f2c25a; --accent-ink:#1a1712; --accent-soft:rgba(242,194,90,.12); --accent-line:rgba(242,194,90,.45);
    --good:#66cc80; --good-soft:rgba(102,204,128,.13);
    --bad:#e65c57; --bad-soft:rgba(230,92,87,.13);
    --blue:#66a0f2; --blue-soft:rgba(102,160,242,.13);
    --life:#e0605a; --es:#5fc9d8; --mana:#6f8ff2;
    --normal:#c9c9c9; --magic:#8c9cff; --rare:#e8d466; --unique:#e0893a; --poi:#4bb3c4;
    /* legacy names still referenced by generated markup */
    --gold:var(--accent); --gold-bright:var(--accent); --gold-deep:var(--accent-line);
    --blood:var(--bad); --blood-bright:var(--bad);
    --sans:Inter,"Segoe UI",system-ui,-apple-system,Roboto,"Helvetica Neue",Arial,sans-serif;
    --mono:ui-monospace,"JetBrains Mono","Cascadia Mono",Consolas,"Liberation Mono",monospace;
    --r:8px; --r-sm:6px;
    --shadow:0 1px 2px rgba(0,0,0,.28),0 10px 28px -14px rgba(0,0,0,.6);
    --focus:0 0 0 3px rgba(242,194,90,.22);
    color-scheme:dark;
  }
  *{box-sizing:border-box}
  html,body{margin:0; min-height:100%}
  body{background:var(--bg); color:var(--ink); font:13.5px/1.5 var(--sans); -webkit-font-smoothing:antialiased}
  a{color:var(--accent)}
  code,.mono{font-family:var(--mono); font-size:.92em}
  code{background:rgba(255,255,255,.06); padding:1px 5px; border-radius:4px}
  b,strong{font-weight:600}
  [hidden]{display:none!important}
  :focus-visible{outline:2px solid var(--accent); outline-offset:2px}
  ::selection{background:rgba(242,194,90,.3)}
  ::-webkit-scrollbar{width:10px;height:10px}
  ::-webkit-scrollbar-thumb{background:rgba(255,255,255,.1); border-radius:6px; border:2px solid transparent; background-clip:padding-box}
  ::-webkit-scrollbar-thumb:hover{background-color:rgba(255,255,255,.18)}
  ::-webkit-scrollbar-track{background:transparent}

  /* ── app frame: fixed left rail + scrolling content ── */
  .app{display:grid; grid-template-columns:228px minmax(0,1fr); min-height:100vh}
  .rail{position:sticky; top:0; height:100vh; display:flex; flex-direction:column; gap:2px; padding:18px 12px 14px;
        background:var(--rail); border-right:1px solid var(--line)}
  .brand{display:flex; align-items:center; gap:10px; padding:4px 8px 18px}
  .brand svg{width:28px; height:28px; flex:none; color:var(--accent)}
  .brand b{display:block; font-size:15px; font-weight:650; letter-spacing:.01em}
  .brand small{display:block; color:var(--ink-faint); font:11px var(--mono)}
  .navlbl{font-size:11px; font-weight:600; letter-spacing:.06em; text-transform:uppercase; color:var(--ink-faint); padding:12px 10px 6px}
  .nav{display:flex; align-items:center; gap:11px; padding:8px 10px; border-radius:var(--r-sm); color:var(--ink-dim);
       text-decoration:none; font-weight:500; font-size:13.5px; transition:background .12s,color .12s}
  .nav svg{width:17px; height:17px; flex:none; fill:none; stroke:currentColor; stroke-width:1.8; stroke-linecap:round; stroke-linejoin:round}
  .nav:hover{background:rgba(255,255,255,.045); color:var(--ink)}
  .nav.on{background:var(--accent-soft); color:var(--accent)}
  .nav .nbadge{margin-left:auto; min-width:20px; padding:0 6px; border-radius:10px; background:var(--accent); color:var(--accent-ink);
       font:600 11px/18px var(--sans); text-align:center}
  .rail-foot{margin-top:auto; padding:12px 10px 2px; border-top:1px solid var(--line); display:flex; flex-direction:column; gap:4px}
  .conn{display:flex; align-items:center; gap:8px; font-size:12.5px; color:var(--ink-dim); font-weight:500}
  .dot{width:8px; height:8px; border-radius:50%; background:var(--bad); flex:none}
  .conn.live .dot{background:var(--good); box-shadow:0 0 0 3px var(--good-soft)}
  .rail-area{font-size:12.5px; color:var(--ink); white-space:nowrap; overflow:hidden; text-overflow:ellipsis}
  .rail-sub{font-size:11.5px; color:var(--ink-faint); font-family:var(--mono)}

  .content{min-width:0; padding:26px 34px 70px; max-width:1520px; width:100%}
  .page{animation:fadein .16s ease}
  @keyframes fadein{from{opacity:.4}to{opacity:1}}
  @media (prefers-reduced-motion:reduce){.page{animation:none}}
  .phead{display:flex; align-items:flex-end; gap:16px; margin:2px 0 22px}
  .phead h1{margin:0; font-size:22px; font-weight:650; letter-spacing:-.01em}
  .phead p{margin:3px 0 0; color:var(--ink-dim); font-size:13.5px}
  .phead .aside{margin-left:auto; display:flex; align-items:center; gap:8px; flex-wrap:wrap; justify-content:flex-end}

  #updateBanner{display:flex; align-items:center; gap:10px; padding:10px 14px; margin:0 0 18px; border-radius:var(--r);
       background:var(--accent-soft); border:1px solid var(--accent-line); color:var(--accent); text-decoration:none; font-weight:600}
  #updateBanner #updateMsg{font-weight:400; color:var(--ink-dim)}

  /* ── grid + cards ── */
  .grid{display:grid; grid-template-columns:repeat(12,minmax(0,1fr)); gap:16px; margin-bottom:16px; align-items:start}
  .grid.stretch{align-items:stretch}
  .span-2{grid-column:span 2} .span-3{grid-column:span 3} .span-4{grid-column:span 4} .span-5{grid-column:span 5}
  .span-6{grid-column:span 6} .span-7{grid-column:span 7} .span-8{grid-column:span 8} .span-12{grid-column:1/-1}
  .panel-grid{display:grid; grid-template-columns:repeat(auto-fill,minmax(340px,1fr)); gap:16px; align-items:start}
  .card{background:var(--panel); border:1px solid var(--line); border-radius:var(--r); padding:18px 20px; box-shadow:var(--shadow); min-width:0}
  .card h3{display:flex; align-items:center; gap:8px; flex-wrap:wrap; margin:0 0 10px; font-size:14px; font-weight:600; color:var(--ink); letter-spacing:0}
  .card h3 .tag{color:var(--ink-faint); font-weight:400; font-size:12.5px}
  .card h3 .grow,.grow{flex:1}
  .card > .sub{color:var(--ink-dim); font-size:12.5px; margin:-4px 0 12px}
  .hint,.hint-row{color:var(--ink-faint)!important; font-size:12.5px!important; line-height:1.55}
  .hint b,.hint-row b{color:var(--ink-dim)}
  .note-box{padding:10px 12px; border-radius:var(--r-sm); background:var(--panel2); border:1px solid var(--line); color:var(--ink-dim); font-size:12.5px}
  .note-box.warn{background:var(--accent-soft); border-color:var(--accent-line); color:var(--accent)}
  .note-box.bad{background:var(--bad-soft); border-color:rgba(230,92,87,.4); color:#f0a29f}

  /* ── stat tiles ── */
  .tile{background:var(--panel); border:1px solid var(--line); border-radius:var(--r); padding:15px 18px 16px; box-shadow:var(--shadow); min-width:0}
  .tlbl{display:flex; align-items:center; gap:8px; color:var(--ink-dim); font-size:12.5px; font-weight:500}
  .tval{font-size:26px; font-weight:650; letter-spacing:-.01em; margin-top:4px; font-variant-numeric:tabular-nums; line-height:1.2;
        white-space:nowrap; overflow:hidden; text-overflow:ellipsis}
  .tval.sm{font-size:18px; line-height:1.45}
  .tval small{font-size:13px; font-weight:500; color:var(--ink-faint); margin-left:4px}
  .tsub{color:var(--ink-faint); font-size:12.5px; margin-top:4px; white-space:nowrap; overflow:hidden; text-overflow:ellipsis}
  .tval.pos{color:var(--good)} .tval.neg{color:var(--bad)}
  .sdot{width:8px; height:8px; border-radius:50%; flex:none}
  .bar{height:6px; border-radius:4px; background:rgba(255,255,255,.07); overflow:hidden; margin-top:12px}
  .bar > i{display:block; height:100%; width:0; border-radius:4px; transition:width .35s ease}
  .bar.hp > i,.sdot.hp{background:var(--life)} .bar.es > i,.sdot.es{background:var(--es)} .bar.mana > i,.sdot.mana{background:var(--mana)}
  .minis{display:grid; grid-template-columns:repeat(4,minmax(0,1fr)); gap:10px}
  .mini{background:var(--panel2); border:1px solid var(--line); border-radius:var(--r-sm); padding:10px 12px; min-width:0}
  .mini .n{font-size:20px; font-weight:650; font-variant-numeric:tabular-nums; line-height:1.2}
  .mini .l{font-size:12px; color:var(--ink-faint); white-space:nowrap; overflow:hidden; text-overflow:ellipsis}

  .kv{display:flex; justify-content:space-between; align-items:baseline; gap:12px; padding:7px 0; border-bottom:1px solid var(--line-soft); font-size:13px}
  .kv:last-child{border-bottom:none}
  .kv > span:first-child{color:var(--ink-faint)}
  .kv > span:last-child{color:var(--ink); font-weight:500; text-align:right; min-width:0; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; font-variant-numeric:tabular-nums}

  /* ── module cards (overview) ── */
  .module{display:flex; flex-direction:column; gap:8px; min-height:132px}
  .mhead{display:flex; align-items:center; gap:10px}
  .micon{width:30px; height:30px; border-radius:7px; display:grid; place-items:center; background:var(--panel3); color:var(--ink-dim); flex:none}
  .micon svg{width:16px; height:16px; fill:none; stroke:currentColor; stroke-width:1.8; stroke-linecap:round; stroke-linejoin:round}
  .mtitle{font-weight:600; font-size:14px; white-space:nowrap}
  .mhead .pill{margin-left:auto}
  .mnote{color:var(--ink-dim); font-size:12.5px; min-height:19px; overflow:hidden; text-overflow:ellipsis; display:-webkit-box; -webkit-line-clamp:2; -webkit-box-orient:vertical}
  .mfoot{margin-top:auto; display:flex; align-items:center; gap:8px; color:var(--ink-faint); font-size:12px; flex-wrap:wrap}
  .mfoot a{color:var(--ink-dim); text-decoration:none} .mfoot a:hover{color:var(--accent)}

  /* ── pills / badges / kbd ── */
  .pill{display:inline-flex; align-items:center; gap:6px; padding:2px 9px; border-radius:999px; font-size:12px; font-weight:500; line-height:18px;
        background:rgba(255,255,255,.06); color:var(--ink-dim); white-space:nowrap}
  .pill::before{content:""; width:6px; height:6px; border-radius:50%; background:currentColor; opacity:.9}
  .pill.nodot::before{display:none}
  .pill.on{background:var(--good-soft); color:var(--good)}
  .pill.bad{background:var(--bad-soft); color:var(--bad)}
  .pill.warn{background:var(--accent-soft); color:var(--accent)}
  .pill.blue{background:var(--blue-soft); color:var(--blue)}
  .badge{display:inline-block; padding:1px 8px; border-radius:5px; font-size:11.5px; font-weight:600; letter-spacing:.02em; background:rgba(255,255,255,.07); color:var(--ink-dim); white-space:nowrap}
  .badge.in{background:var(--good-soft); color:var(--good)} .badge.out{background:var(--blue-soft); color:var(--blue)}
  .badge.sold{background:var(--good-soft); color:var(--good)} .badge.bought{background:var(--blue-soft); color:var(--blue)}
  kbd{display:inline-block; min-width:22px; padding:1px 7px; border-radius:5px; background:var(--field); border:1px solid var(--line-strong);
      border-bottom-width:2px; font:600 11.5px/18px var(--mono); color:var(--ink); text-align:center; white-space:nowrap}

  /* ── buttons ── */
  .btn,.addbtn,.chip,.delbtn,.ordbtn,.pickclose{font-family:inherit; cursor:pointer; transition:background .12s,border-color .12s,color .12s}
  .btn{display:inline-flex; align-items:center; justify-content:center; gap:6px; padding:7px 13px; border-radius:var(--r-sm); font-size:13px; font-weight:500;
       background:var(--panel3); color:var(--ink); border:1px solid var(--line-strong); text-decoration:none; white-space:nowrap}
  .btn:hover{background:#2e3036}
  .btn.primary{background:var(--accent); color:var(--accent-ink); border-color:var(--accent); font-weight:600}
  .btn.primary:hover{background:#f6cd72}
  .btn.danger{background:transparent; color:var(--bad); border-color:rgba(230,92,87,.35)}
  .btn.danger:hover{background:var(--bad-soft)}
  .btn.ghost{background:transparent; border-color:transparent; color:var(--ink-dim)}
  .btn.ghost:hover{background:rgba(255,255,255,.05); color:var(--ink)}
  .btn.sm{padding:4px 10px; font-size:12.5px}
  .btn:disabled{opacity:.45; cursor:default}
  .btn svg{width:14px; height:14px; fill:none; stroke:currentColor; stroke-width:2; stroke-linecap:round; stroke-linejoin:round}
  .addbtn{display:inline-flex; align-items:center; justify-content:center; gap:6px; width:100%; margin-top:10px; padding:9px 14px; border-radius:var(--r-sm);
       font-size:13px; font-weight:500; color:var(--ink-dim); background:transparent; border:1px dashed var(--line-strong)}
  .addbtn:hover{color:var(--accent); border-color:var(--accent-line); background:var(--accent-soft)}
  .delbtn{padding:4px 9px; border-radius:var(--r-sm); font-size:12.5px; color:var(--ink-faint); background:transparent; border:1px solid var(--line); flex:none}
  .delbtn:hover{color:var(--bad); border-color:rgba(230,92,87,.45); background:var(--bad-soft)}
  .ordbtn{padding:3px 7px; font-size:10px; line-height:1.2; border-radius:5px; color:var(--ink-dim); background:var(--panel3); border:1px solid var(--line)}
  .ordbtn:hover{color:var(--accent); border-color:var(--accent-line)}
  .ordbtn:disabled{opacity:.35; cursor:default}
  .chip{display:inline-flex; align-items:center; gap:5px; padding:5px 12px; border-radius:999px; font-size:12.5px; font-weight:500;
        color:var(--ink-dim); background:var(--panel2); border:1px solid var(--line)}
  .chip:hover{color:var(--ink); border-color:var(--line-strong)}
  .chip.on{color:var(--accent); background:var(--accent-soft); border-color:var(--accent-line)}
  .chips{display:flex; flex-wrap:wrap; gap:6px; margin:6px 0 12px}
  .controls{display:flex; flex-wrap:wrap; gap:8px; align-items:center; margin-bottom:14px}
  .seg{display:inline-flex; gap:2px; padding:3px; border-radius:8px; background:var(--panel); border:1px solid var(--line); margin-bottom:16px}
  .seg button{font:500 13px var(--sans); color:var(--ink-dim); background:transparent; border:0; padding:6px 14px; border-radius:6px; cursor:pointer}
  .seg button:hover{color:var(--ink)}
  .seg button.on{background:var(--panel3); color:var(--ink); box-shadow:0 1px 2px rgba(0,0,0,.35)}

  /* ── form controls ── */
  input,select,textarea,button{font-family:inherit}
  input[type=text],input[type=search],input[type=number],input:not([type]),select,textarea,.numin,.mname,.matchin{
    font-size:13px; color:var(--ink); background:var(--field); border:1px solid var(--line-strong); border-radius:var(--r-sm);
    padding:7px 10px; min-width:0; transition:border-color .12s,box-shadow .12s}
  input:hover,select:hover,textarea:hover{border-color:rgba(255,255,255,.2)}
  input:focus,select:focus,textarea:focus{outline:none; border-color:var(--accent-line); box-shadow:var(--focus)}
  input::placeholder,textarea::placeholder{color:var(--ink-faint)}
  input[type=search]{min-width:200px; flex:1}
  textarea{resize:vertical; line-height:1.5; width:100%}
  select{cursor:pointer}
  .numin{width:104px; text-align:right; font-variant-numeric:tabular-nums}
  select.numin,.numin.selin{width:auto; text-align:left}
  .numin.keyin{width:56px; text-align:center; font-family:var(--mono)}
  .numin.sz{width:64px}
  input[type=checkbox]{accent-color:var(--accent); width:15px; height:15px; margin:0; cursor:pointer}
  input[type=color]{width:34px; height:28px; padding:2px; border:1px solid var(--line-strong); background:var(--field); border-radius:var(--r-sm); cursor:pointer; flex:none}
  input[type=color]::-webkit-color-swatch-wrapper{padding:0}
  input[type=color]::-webkit-color-swatch{border:none; border-radius:4px}
  input[type=range]{accent-color:var(--accent); cursor:pointer}
  input[type=range].op{width:84px; flex:none}
  .opv{font:12px var(--mono); color:var(--ink-faint); width:38px; text-align:right; flex:none}
  .ro{color:var(--accent); font-weight:600}
  label{cursor:pointer}

  .row{display:flex; align-items:center; justify-content:space-between; gap:16px; padding:11px 0; border-bottom:1px solid var(--line-soft)}
  .row:last-child{border-bottom:none}
  .row .rl{font-size:13.5px; color:var(--ink); min-width:0}
  .row > select,.row > input,.row > .sw,.row > .trow-ctl{flex:none}
  .row .rl small{display:block; color:var(--ink-faint); font-size:12px; margin-top:2px; line-height:1.45}
  .field{display:flex; flex-direction:column; gap:5px; min-width:0}
  .field > span,.flbl{font-size:12px; color:var(--ink-faint); font-weight:500}
  .field input,.field select,.field textarea{width:100%}
  .fgrid{display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:12px 16px}
  .fgrid .full{grid-column:1/-1}
  .inl{display:inline-flex; align-items:center; gap:8px; font-size:13px; color:var(--ink-dim); white-space:nowrap}

  /* toggle switch — green when on */
  .sw{position:relative; width:36px; height:20px; flex:none; display:inline-block; cursor:pointer}
  .sw input{position:absolute; opacity:0; width:100%; height:100%; margin:0; cursor:pointer; z-index:1}
  .sw .track{position:absolute; inset:0; border-radius:999px; background:#34363c; border:1px solid var(--line-strong); transition:background .15s,border-color .15s}
  .sw .knob{position:absolute; top:3px; left:3px; width:14px; height:14px; border-radius:50%; background:#9a9da4; transition:transform .15s,background .15s; pointer-events:none}
  .sw input:checked ~ .track{background:var(--good); border-color:var(--good)}
  .sw input:checked ~ .knob{transform:translateX(16px); background:#fff}
  .sw input:focus-visible ~ .track{box-shadow:0 0 0 2px var(--bg),0 0 0 4px var(--accent)}

  .saved{position:fixed; right:22px; bottom:20px; z-index:2000; display:flex; align-items:center; gap:8px; padding:8px 14px; border-radius:var(--r);
         background:#232529; border:1px solid var(--line-strong); color:var(--good); font-size:13px; font-weight:500; box-shadow:var(--shadow);
         opacity:0; transform:translateY(6px); transition:opacity .2s,transform .2s; pointer-events:none}
  .saved.show{opacity:1; transform:none}
  .saved.err{color:var(--bad)}

  /* ── tables ── */
  .tablewrap{overflow:auto; border:1px solid var(--line); border-radius:var(--r-sm)}
  table{width:100%; border-collapse:collapse; font-size:13px}
  thead th{position:sticky; top:0; z-index:1; text-align:left; font-weight:600; font-size:11.5px; letter-spacing:.04em; text-transform:uppercase;
           color:var(--ink-faint); padding:9px 12px; background:var(--panel2); border-bottom:1px solid var(--line)}
  tbody td{padding:8px 12px; border-bottom:1px solid var(--line-soft); vertical-align:middle}
  tbody tr:last-child td{border-bottom:none}
  tbody tr:hover{background:rgba(255,255,255,.025)}
  td.num,th.num,.num-r{text-align:right; font-variant-numeric:tabular-nums}
  td.dim{color:var(--ink-dim)} td.nowrap{white-space:nowrap}
  td.item{max-width:340px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap}
  .empty{color:var(--ink-faint); text-align:center; padding:26px 12px; font-size:13px}
  .rar-Normal{color:var(--normal)} .rar-Magic{color:var(--magic)} .rar-Rare{color:var(--rare)} .rar-Unique{color:var(--unique)}
  .friendly{color:var(--good)} .hostile{color:var(--bad)}

  /* ── overview: hotkeys / bookmarks / monoliths ── */
  .hkgrid{display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:0 22px}
  .hkrow{display:flex; align-items:center; gap:10px; padding:7px 0; border-bottom:1px solid var(--line-soft); font-size:13px; min-width:0}
  .hkrow kbd{flex:none}
  .hkrow span{color:var(--ink-dim); white-space:nowrap; overflow:hidden; text-overflow:ellipsis}
  .hkrow.user span{color:var(--ink)}
  .bmfolder{font-size:11.5px; font-weight:600; letter-spacing:.05em; text-transform:uppercase; color:var(--ink-faint); margin:12px 0 4px}
  .bmfolder:first-child{margin-top:0}
  .bmlink{display:flex; align-items:center; gap:10px; padding:7px 10px; margin:0 -10px; border-radius:var(--r-sm); color:var(--ink); text-decoration:none; min-width:0}
  .bmlink:hover{background:rgba(255,255,255,.045)}
  .bmlink svg{width:14px; height:14px; flex:none; fill:none; stroke:var(--ink-faint); stroke-width:2; stroke-linecap:round; stroke-linejoin:round}
  .bmlink .bmn{white-space:nowrap; overflow:hidden; text-overflow:ellipsis}
  .bmlink .bmh{margin-left:auto; color:var(--ink-faint); font-size:12px; white-space:nowrap}
  .bmlink.off{color:var(--ink-faint); cursor:default}
  .bmlink.off:hover{background:none}
  .mono-item{padding:10px 0; border-bottom:1px solid var(--line-soft)}
  .mono-item:last-child{border-bottom:none}
  .mono-hd{display:flex; align-items:center; gap:8px; font-size:13px}
  .mono-rw{display:flex; justify-content:space-between; gap:10px; font-size:12.5px; color:var(--ink-dim); padding:2px 0 0 14px}
  .znotes{white-space:pre-wrap; font-size:12.5px; line-height:1.55; color:var(--ink-dim); max-height:300px; overflow:auto}
  .znotes .zt{font-weight:600; color:var(--ink); margin-bottom:6px; white-space:normal}

  /* ── trade ── */
  .sumtile .tval{font-size:24px}
  .sumline{display:flex; gap:14px; flex-wrap:wrap; margin-top:10px; font-size:12.5px; color:var(--ink-dim)}
  .sumline b{color:var(--ink); font-variant-numeric:tabular-nums}
  .chart svg{display:block; width:100%; height:auto}
  .chart .ax{stroke:rgba(255,255,255,.14)}
  .chart .gl{stroke:rgba(255,255,255,.05)}
  .chart text{fill:var(--ink-faint); font:12px var(--sans)}
  .chart rect.p{fill:var(--good)} .chart rect.n{fill:var(--bad)}
  .chart rect:hover{opacity:.8}
  .req{display:grid; grid-template-columns:auto minmax(0,1fr) auto; gap:4px 14px; align-items:center; padding:12px 0; border-bottom:1px solid var(--line-soft)}
  .req:last-child{border-bottom:none}
  .req .who{display:flex; align-items:center; gap:8px; min-width:0; font-weight:600}
  .req .who .g{color:var(--ink-faint); font-weight:400}
  .req .what{grid-column:2; display:flex; gap:14px; flex-wrap:wrap; color:var(--ink-dim); font-size:12.5px}
  .req .what .it{color:var(--ink)}
  .req .price{font-weight:600; color:var(--accent); font-variant-numeric:tabular-nums; white-space:nowrap}
  .req .side{grid-row:1/span 2; grid-column:3; display:flex; flex-direction:column; align-items:flex-end; gap:6px}
  .req .wh{grid-column:2; font-size:12px; color:var(--ink-faint); font-style:italic; overflow:hidden; text-overflow:ellipsis; white-space:nowrap}
  .req .lead{grid-row:1/span 2}
  .adot{width:8px; height:8px; border-radius:50%; background:#4a4d54; flex:none}
  .adot.in{background:var(--good); box-shadow:0 0 0 3px var(--good-soft)}

  /* ── macros ── */
  .rulecard{border:1px solid var(--line); border-radius:var(--r); background:var(--panel2); padding:12px 14px; margin-bottom:10px}
  .rulecard.off{opacity:.72}
  .rulecard.off:focus-within{opacity:1}
  .rhead{display:flex; align-items:center; gap:10px}
  .rhead .rname{flex:1; min-width:120px; font-weight:600}
  .rhead .rstatus{max-width:260px; overflow:hidden; text-overflow:ellipsis}
  .rbody{display:grid; grid-template-columns:repeat(12,minmax(0,1fr)); gap:10px 12px; margin-top:12px}
  .rbody .c3{grid-column:span 3} .rbody .c4{grid-column:span 4} .rbody .c5{grid-column:span 5} .rbody .c6{grid-column:span 6} .rbody .c12{grid-column:1/-1}
  .ropts{grid-column:1/-1; display:flex; align-items:center; gap:18px; flex-wrap:wrap; padding-top:4px}
  .ropts .numin{width:74px; padding:5px 8px}
  .kcap{cursor:pointer; text-align:center; font-family:var(--mono); caret-color:transparent}
  .kcap.listening{border-color:var(--accent-line); box-shadow:var(--focus); color:var(--accent)}
  .kcap.unset{color:var(--ink-faint)}
  .buffrow{display:flex; align-items:center; gap:10px; padding:8px 0; border-bottom:1px solid var(--line-soft); min-width:0}
  .buffrow:last-child{border-bottom:none}
  .buffrow .bn{flex:1; min-width:0; font:12.5px var(--mono); white-space:nowrap; overflow:hidden; text-overflow:ellipsis}
  .buffrow .bt{font-size:12px; color:var(--ink-faint); white-space:nowrap; font-variant-numeric:tabular-nums}
  .buffrow .bbar{height:3px; border-radius:2px; background:rgba(255,255,255,.07); margin-top:4px; overflow:hidden}
  .buffrow .bbar i{display:block; height:100%; background:var(--accent)}
  .buffrow.kept .bn{color:var(--good)}
  .sticky{position:sticky; top:20px}
  .cmdrow{display:grid; grid-template-columns:auto 150px 120px minmax(0,1fr) auto; gap:10px; align-items:start; padding:10px 0; border-bottom:1px solid var(--line-soft)}
  .cmdrow:last-of-type{border-bottom:none}
  .cmdrow .sw{margin-top:7px}
  .cmdrow textarea{min-height:36px; font-family:var(--mono); font-size:12.5px}
  .bmrow{display:grid; grid-template-columns:auto 170px 130px minmax(0,1fr) 130px auto auto; gap:10px; align-items:center; padding:9px 0; border-bottom:1px solid var(--line-soft)}
  .bmrow:last-of-type{border-bottom:none}
  .bmrow .bad{border-color:rgba(230,92,87,.55)}
  .rowhead{display:grid; gap:10px; padding:0 0 6px; font-size:11.5px; font-weight:600; letter-spacing:.04em; text-transform:uppercase; color:var(--ink-faint); border-bottom:1px solid var(--line)}
  .cmdrow.rowhead{grid-template-columns:36px 150px 120px minmax(0,1fr) 34px; align-items:end}
  .bmrow.rowhead{grid-template-columns:36px 170px 130px minmax(0,1fr) 130px 34px 34px}
  .warnline{color:var(--accent); font-size:12px; margin-top:4px}
  .ph{display:flex; flex-wrap:wrap; gap:6px 16px; font-size:12.5px; color:var(--ink-dim); margin:0 0 12px}
  .ph code{color:var(--accent)}

  /* ── existing editors (Rules / Landmarks / HP bars / terrain / atlas) ── */
  .stylerow{display:flex; align-items:center; gap:9px; padding:9px 0; border-bottom:1px solid var(--line-soft); flex-wrap:wrap}
  .stylerow .nm{flex:1 1 110px; min-width:90px; font-size:13px}
  .mechrow{border:1px solid var(--line); border-radius:var(--r); background:var(--panel2); padding:10px 12px; margin-bottom:8px}
  .mechrow .top{display:flex; align-items:center; gap:9px; margin-bottom:8px}
  .mechrow .top input.mname{flex:1}
  .mechrow .matchin{width:100%; margin-bottom:8px; font-size:12.5px}
  .mechrow .ctl{display:flex; align-items:center; gap:9px; flex-wrap:wrap}
  .mcats{display:flex; align-items:center; gap:6px; flex-wrap:wrap; margin-bottom:10px}
  .mcats-lbl{font-size:12px; color:var(--ink-faint); margin-right:4px; font-weight:500}
  .mcats-hint{font-size:12px; color:var(--ink-faint)}
  .catchip{display:inline-flex; align-items:center; font-size:12px; color:var(--ink-dim); background:var(--field); border:1px solid var(--line-strong); border-radius:999px; padding:2px 10px; cursor:pointer; user-select:none}
  .catchip:hover{border-color:rgba(255,255,255,.25)}
  .catchip.on{color:var(--accent-ink); background:var(--accent); border-color:var(--accent); font-weight:600}
  .catchip input{display:none}
  .drrow{padding:9px 12px; transition:border-color .12s}
  .drrow:hover{border-color:var(--line-strong)}
  .drrow.open{border-color:var(--accent-line)}
  .drhead{display:flex; align-items:center; gap:10px; cursor:pointer; min-width:0}
  .drcaret{color:var(--ink-faint); width:10px; font-size:10px; flex:none}
  .drswatch{width:16px; height:16px; flex:none; display:inline-flex}
  .drswatch svg{width:16px; height:16px; display:block}
  .drnm{font-weight:600; white-space:nowrap; flex:none; max-width:220px; overflow:hidden; text-overflow:ellipsis}
  .drsum{flex:1 1 auto; min-width:0; color:var(--ink-faint); font-size:12.5px; white-space:nowrap; overflow:hidden; text-overflow:ellipsis}
  .drbadges{display:inline-flex; gap:4px; flex:none}
  .drbadge{font-size:11px; font-weight:600; color:var(--ink-dim); background:rgba(255,255,255,.07); border-radius:5px; padding:1px 7px; white-space:nowrap}
  .drbadge.hide{color:var(--bad); background:var(--bad-soft)}
  .drrow.off .drnm,.drrow.off .drsum,.drrow.off .drswatch{opacity:.45}
  .drbody{margin-top:12px; padding-top:12px; border-top:1px solid var(--line)}
  .drord{display:inline-flex; gap:3px; flex:none}
  .drconds{display:flex; align-items:center; gap:12px; flex-wrap:wrap; margin-bottom:10px}
  .drsel{display:inline-flex; align-items:center; gap:6px; font-size:12px; color:var(--ink-faint); font-weight:500}
  .drsel select{font-size:12.5px; padding:4px 8px}
  .drflag{display:inline-flex; align-items:center; gap:6px; font-size:12.5px; color:var(--ink-dim); cursor:pointer; user-select:none; white-space:nowrap}
  .dr-hideflag{color:var(--bad)}
  .drrow.hideon{opacity:.8}
  .drrow.hideon .iconpick,.drrow.hideon .dr-color,.drrow.hideon .dr-op,.drrow.hideon .dr-size,.drrow.hideon .dr-label,.drrow.hideon .opv{opacity:.4; pointer-events:none}
  .hpgrid{display:grid; grid-template-columns:30px 66px 1fr 48px 1fr; gap:9px 12px; align-items:center; padding:6px 0 2px}
  .hpgrid input[type=checkbox]{justify-self:center}
  .hpgrid .hph{font-size:11.5px; font-weight:600; letter-spacing:.04em; text-transform:uppercase; color:var(--ink-faint); white-space:nowrap}
  .hpgrid .hpr{font-size:13px}
  .hpgrid .numin{width:100%; padding:5px 8px}
  .hpgrid input[type=color]{width:100%}
  .hpshared{display:grid; grid-template-columns:repeat(3,minmax(0,1fr)); gap:12px; margin-top:12px; padding-top:12px; border-top:1px solid var(--line-soft)}
  .hpshared label{display:flex; flex-direction:column; gap:5px; font-size:12px; color:var(--ink-faint); font-weight:500}
  .hpshared .numin{width:100%}
  .trow-ctl{display:flex; align-items:center; gap:9px; flex:none}

  .iconpick{display:inline-flex; align-items:center; gap:7px; min-width:112px; background:var(--field); border:1px solid var(--line-strong); border-radius:var(--r-sm); padding:4px 8px; cursor:pointer; flex:none}
  .iconpick:hover{border-color:var(--accent-line)}
  .iconpick .ipreview{width:16px; height:16px; flex:none; display:inline-flex}
  .iconpick .ipreview svg{width:16px; height:16px; display:block}
  .iconpick .ipname{font-size:12.5px; white-space:nowrap; overflow:hidden; text-overflow:ellipsis}
  .iconpick .ipcar{margin-left:auto; color:var(--ink-faint); font-size:8px}
  #iconPop{position:fixed; z-index:1000; display:none; background:var(--panel2); border:1px solid var(--line-strong); border-radius:var(--r); box-shadow:0 18px 40px -12px rgba(0,0,0,.8); padding:8px; max-height:320px; overflow:auto}
  #iconPop.open{display:block}
  .ipop-grid{display:grid; grid-template-columns:repeat(6,42px); gap:4px}
  .ipop-cell{display:flex; flex-direction:column; align-items:center; justify-content:center; gap:3px; width:42px; height:44px; border:1px solid transparent; border-radius:6px; cursor:pointer; color:var(--ink)}
  .ipop-cell:hover{background:var(--panel3); border-color:var(--line-strong)}
  .ipop-cell.sel{border-color:var(--accent); background:var(--accent-soft)}
  .ipop-cell svg{width:20px; height:20px; display:block}
  .ipop-cell .cn{font-size:8px; line-height:1; color:var(--ink-faint); max-width:40px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap}

  #pickPop{position:fixed; inset:0; z-index:1100; display:none; background:rgba(8,9,10,.66); padding:6vh 4vw; backdrop-filter:blur(2px)}
  #pickPop.open{display:flex; justify-content:center; align-items:flex-start}
  .pickbox{display:flex; flex-direction:column; width:min(780px,100%); max-height:86vh; background:var(--panel); border:1px solid var(--line-strong); border-radius:12px; box-shadow:0 30px 70px -20px rgba(0,0,0,.9); overflow:hidden}
  .pickhead{display:flex; align-items:center; gap:10px; padding:14px; border-bottom:1px solid var(--line)}
  .pickhead #pickSearch{flex:1}
  .pickkinds{display:inline-flex; gap:4px}
  .pickclose{font-size:13px; color:var(--ink-dim); background:transparent; border:1px solid var(--line); border-radius:var(--r-sm); padding:6px 10px}
  .pickclose:hover{color:var(--bad); border-color:rgba(230,92,87,.45)}
  .picklist{overflow:auto; padding:4px 0}
  .pickrow{display:flex; align-items:center; gap:10px; padding:8px 14px; cursor:pointer; border-bottom:1px solid var(--line-soft)}
  .pickrow:hover{background:var(--panel2)}
  .pickbadge{flex:none; font-size:11px; font-weight:600; color:var(--ink-dim); background:rgba(255,255,255,.07); border-radius:5px; padding:1px 7px; min-width:62px; text-align:center}
  .pickbadge.tile{color:var(--poi); background:rgba(75,179,196,.13)}
  .pickbadge.entity{color:var(--accent); background:var(--accent-soft)}
  .pickbadge.mod{color:#4fd9c4; background:rgba(79,217,196,.12)}
  .pickcount{flex:none; font:12px var(--mono); color:var(--ink-dim)}
  .picknm{flex:none; font-weight:600; max-width:240px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap}
  .picksub{flex:1; min-width:0; color:var(--ink-faint); font-size:12px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; font-family:var(--mono)}
  .pickrar{flex:none; font-size:12px; color:var(--rare)}
  .pickempty{padding:26px 14px; color:var(--ink-faint); text-align:center}
  .pickfoot{padding:10px 14px; border-top:1px solid var(--line); color:var(--ink-faint); font-size:12px}

  .lmrow{display:flex; align-items:center; gap:10px; padding:7px 0; border-bottom:1px solid var(--line-soft)}
  .lmbadge{flex:none; min-width:54px; text-align:center; font-size:11px; font-weight:600; color:var(--ink-dim); background:rgba(255,255,255,.07); border-radius:5px; padding:1px 6px}
  .lmbadge.user{color:var(--accent); background:var(--accent-soft)}
  .lmbadge.hidden{color:var(--bad); background:var(--bad-soft)}
  .lmarea{flex:none; min-width:70px; font:12px var(--mono); color:var(--ink-dim)}
  .lmlabel{flex:none; width:220px}
  .lmpath{flex:1; min-width:0; color:var(--ink-faint); font:12px var(--mono); overflow:hidden; text-overflow:ellipsis; white-space:nowrap}
  .lmrow.sup .lmlabel,.lmrow.sup .lmpath{opacity:.5}
  .lmadd{display:flex; gap:8px; flex-wrap:wrap; align-items:center; margin-top:12px; padding:12px; border:1px dashed var(--line-strong); border-radius:var(--r)}
  .lmadd input{flex:1; min-width:140px}
  .hidechips .chip b{margin-left:4px; color:var(--ink-faint); cursor:pointer}
  .hidechips .chip b:hover{color:var(--bad)}

  .amono{font-family:var(--mono); color:var(--ink-dim); font-size:12px}
  .arow{display:grid; grid-template-columns:minmax(200px,2fr) minmax(120px,1.4fr) 120px; gap:10px; align-items:center; padding:6px 10px; border-bottom:1px solid var(--line-soft); font-size:13px}
  .arow.nrow{grid-template-columns:60px minmax(90px,1fr) minmax(200px,2fr) 130px; cursor:pointer}
  .arow.nrow:hover{background:rgba(255,255,255,.03)}
  .arow.nrow.sel{background:var(--accent-soft)}
  .ntag{font-size:11px; font-weight:600; padding:0 6px; border-radius:5px; background:rgba(255,255,255,.07); margin-right:3px}
  .ntag.tc{color:var(--accent)} .ntag.tv{color:var(--ink-dim)}
  #atlasHlTable{background:var(--panel)}
  #atlasHlTable .hlrow:hover{background:rgba(255,255,255,.03)}
  #atlasGroups input:not([type=color]),#atlasGroups textarea{font-size:12.5px}
  #atlasOpts label{display:inline-flex; align-items:center; gap:7px; color:var(--ink-dim); font-size:13px}
  #atlasOpts input[type=number]{padding:4px 8px}
  .achip{background:var(--accent-soft)!important; border-color:var(--accent-line)!important}

  /* ── responsive ── */
  @media (max-width:1320px){
    .span-3{grid-column:span 6} .span-5,.span-7{grid-column:span 6}
    .hkgrid{grid-template-columns:1fr}
  }
  @media (max-width:1180px){
    .app{grid-template-columns:200px minmax(0,1fr)}
    .content{padding:22px 22px 60px}
    .span-4,.span-5,.span-6,.span-7,.span-8{grid-column:1/-1}
    #trSummary .span-4{grid-column:span 4}
    .sumline{gap:4px 12px}
    .sticky{position:static}
    .bmrow{grid-template-columns:auto minmax(0,1fr) minmax(0,1fr) auto auto; }
    .bmrow .bmurl{grid-column:2/-1; grid-row:2}
    .bmrow.rowhead{display:none}
    .cmdrow{grid-template-columns:auto minmax(0,1fr) 140px auto}
    .cmdrow textarea{grid-column:2/-1; grid-row:2}
    .cmdrow.rowhead{display:none}
    .rbody .c3,.rbody .c4,.rbody .c5{grid-column:span 6}
  }
</style>
</head>
""";
}
