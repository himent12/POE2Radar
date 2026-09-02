let meta = #{
    name: "quest-combat-bot",
    description: "Build out quest-follow plus a build-customizable combat bot on the existing POE2Radar overlay",
    when_to_use: "Turn POE2Radar nav + combat assist into a full in-zone bot: quest objectives, movement along routes, customizable skill rotation",
    phases: [
        #{ title: "Inventory", detail: "read existing nav, combat, dashboard" },
        #{ title: "Implement", detail: "quest follow, movement, rotation, dashboard, tests" },
        #{ title: "Verify", detail: "build, tests, adversarial check" },
        #{ title: "Fix", detail: "close remaining gaps once" },
    ],
};

let summary_schema = #{
    "type": "object",
    "required": ["summary", "files"],
    "properties": #{
        "summary": #{ "type": "string" },
        "files": #{ "type": "array", "items": #{ "type": "string" }, "maxItems": 20 },
    },
};

let done_schema = #{
    "type": "object",
    "required": ["done", "summary", "files"],
    "properties": #{
        "done": #{ "type": "boolean" },
        "summary": #{ "type": "string" },
        "files": #{ "type": "array", "items": #{ "type": "string" }, "maxItems": 24 },
    },
};

let verify_schema = #{
    "type": "object",
    "required": ["ok", "evidence", "gaps"],
    "properties": #{
        "ok": #{ "type": "boolean" },
        "evidence": #{ "type": "string" },
        "gaps": #{ "type": "array", "items": #{ "type": "string" }, "maxItems": 12 },
    },
};

fn text_or(v, fallback) {
    if v != () && type_of(v) == "string" { v } else { fallback }
}

fn files_or(v) {
    let out = [];
    if v != () && type_of(v) == "array" {
        for f in v { if type_of(f) == "string" { out.push(f); } }
    }
    out
}

let build_keys = "Q";
if args != () && args.build != () && type_of(args.build) == "string" {
    let b = args.build;
    b.trim();
    if b != "" { build_keys = b; }
}

log("quest-combat-bot: default skill keys=" + build_keys);

phase("Inventory");
let inv_jobs = [];
inv_jobs.push(#{
    label: "inv-nav",
    capability_mode: "read-only",
    output_schema: summary_schema,
    prompt: "You are inventorying POE2Radar in /home/him/Documents/poe2map. Use grep and read_file. Do not answer from memory. Map how navigation already works: NavTarget, AddNearestPathTarget, ToggleNavTarget, MaintainRoutes, SelectedPath, AutoNavPatterns, ZoneGuide, zone_notes.json, landmarks. Summarize how a quest bot could pick the next objective (transition, landmark matching zone notes, entity POI) from that existing target list. Return {summary, files} with concrete file paths you read. Empty files is invalid.",
});
inv_jobs.push(#{
    label: "inv-combat",
    capability_mode: "read-only",
    output_schema: summary_schema,
    prompt: "You are inventorying POE2Radar in /home/him/Documents/poe2map. Use grep and read_file. Do not answer from memory. Map combat assist: CombatAssist.cs, TickCombatAssist, F4, GameHost.TapKey, flask gates, RadarSettings combat fields, entity hostility (Reaction & 0x7F). Summarize how to extend one-key tap into a customizable skill rotation (list of keys, per-skill cooldown, range, only hostiles). Stay external: no injection, no WriteProcessMemory. Return {summary, files} of paths you actually read.",
});
inv_jobs.push(#{
    label: "inv-ui",
    capability_mode: "read-only",
    output_schema: summary_schema,
    prompt: "You are inventorying POE2Radar in /home/him/Documents/poe2map. Use grep and read_file. Do not answer from memory. Map RadarSettings JSON persist, ApiServer ApplySettings whitelist (combatAssistEnabled must stay F4-only), DashboardHtml settings cards, RadarState /state fields. Summarize where a bot profile (skill keys, quest follow on/off, movement on/off) should live without allowing HTTP to arm the bot. Return {summary, files} of paths you actually read.",
});
let inv = parallel(inv_jobs);

let inv_nav = "";
let inv_combat = "";
let inv_ui = "";
if inv.len() > 0 && inv[0] != () && inv[0].success && inv[0].output.summary != () { inv_nav = inv[0].output.summary; }
if inv.len() > 1 && inv[1] != () && inv[1].success && inv[1].output.summary != () { inv_combat = inv[1].output.summary; }
if inv.len() > 2 && inv[2] != () && inv[2].success && inv[2].output.summary != () { inv_ui = inv[2].output.summary; }
log("inventory nav=" + (if inv_nav == "" { "missing" } else { "ok" }));

phase("Implement");

let rules = "";
rules += "Workspace: /home/him/Documents/poe2map. Stay external: OpenProcess/process_vm_readv reads only. Never inject, hook, or write PoE2 memory. Keystrokes only via GameHost.TapKey. Foreground-gated, in-game-gated, per-action cooldown, local kill-switch. Bot master switch defaults OFF. F4 already toggles simple combat; keep that or fold into a bot master (F3) without breaking F8 flask. ";
rules += "Do not arm the bot over HTTP. Dashboard may tune keys/range/cooldowns and show status. ";
rules += "Default skill keys for this run: " + build_keys + ". ";
rules += "Use grep/read_file before editing. Match existing C# style. TreatWarningsAsErrors is on. After edits, dotnet test src/POE2Radar.Tests and dotnet build POE2Radar.slnx -c Release. ";

let p_quest = rules;
p_quest += "TASK: Implement quest follow on top of existing nav. Read Overlay/RenderContext.cs NavTarget, RadarApp AddNearestPathTarget/ToggleNavTarget/MaintainRoutes, ZoneGuide, zone_notes.json. ";
p_quest += "When bot quest-follow is armed, each zone auto-select a nav target that matches the current area's zone notes or a Transition/waypoint/boss landmark, using the existing selection + A* route. ";
p_quest += "Pure decision function (area code, notes, landmarks, entities -> target id or none) in Overlay so tests can call it without a live client. Wire into the world tick next to auto-nav patterns. Persist QuestFollowEnabled default false, local hotkey, dashboard status only for the arm bit. Inventory notes: " + json_encode(inv_nav) + " Return {done, summary, files}.";

let r_quest = agent(p_quest, #{
    label: "impl-quest",
    capability_mode: "execute",
    output_schema: done_schema,
});
log("quest implement " + (if r_quest != () && r_quest.success { "ok" } else { "fail" }));

let p_move = rules;
p_move += "TASK: Implement movement along the already-computed SelectedPath waypoints. Read RadarApp MaintainRoutes, SelectedPath, GameHost.TapKey, Overlay tick. ";
p_move += "When bot movement is armed AND PoE2 is focused AND in-game, tap WASD (or a configurable move method) toward the next waypoint of the first selected path. Pure decision: player grid, waypoint list, arrive radius -> which VK to tap or none. ";
p_move += "Do not send movement when no path, unfocused, or disarmed. Default MoveEnabled false. Local hotkey or the bot master. Tests for: no path, arrived, need north, unfocused, disarmed. Inventory: " + json_encode(inv_nav) + " Return {done, summary, files}.";

let r_move = agent(p_move, #{
    label: "impl-move",
    capability_mode: "execute",
    output_schema: done_schema,
});
log("move implement " + (if r_move != () && r_move.success { "ok" } else { "fail" }));

let p_rot = rules;
p_rot += "TASK: Expand CombatAssist from one attack key into a customizable rotation. Read src/POE2Radar.Overlay/Input/CombatAssist.cs, TickCombatAssist, RadarSettings combat fields, CombatAssistTests. ";
p_rot += "Add an ordered list of skills {vk, cooldownMs} (default one skill from " + build_keys + "). While armed + focused + in-game + hostile in range, fire the next ready skill (skip on cooldown, wrap the list). Keep the existing Decide gates (friendly Reaction mask, alive monsters, range). ";
p_rot += "Persist CombatSkills in radar_settings.json. Dashboard can edit the skill list and range but cannot set CombatAssistEnabled. Extend CombatAssistTests for rotation, per-skill cooldown, empty list. Inventory: " + json_encode(inv_combat) + " Return {done, summary, files}.";

let r_rot = agent(p_rot, #{
    label: "impl-rotation",
    capability_mode: "execute",
    output_schema: done_schema,
});
log("rotation implement " + (if r_rot != () && r_rot.success { "ok" } else { "fail" }));

let p_dash = rules;
p_dash += "TASK: Dashboard + settings for the bot profile. Read RadarSettings, ApiServer ReadSettings/ApplySettings, DashboardHtml, RadarState. ";
p_dash += "Add a Combat/Bot card: skill list editor, range, move on/off status, quest-follow status. Arm bits (combat, move, quest) are F-keys only — never ApplySettings keys. /state must show bot notes. Wire loadSettings data-set keys for tunables only. Inventory: " + json_encode(inv_ui) + " Return {done, summary, files}.";

let r_dash = agent(p_dash, #{
    label: "impl-dashboard",
    capability_mode: "execute",
    output_schema: done_schema,
});
log("dashboard implement " + (if r_dash != () && r_dash.success { "ok" } else { "fail" }));

phase("Verify");
let p_test = rules;
p_test += "Run from /home/him/Documents/poe2map: dotnet test src/POE2Radar.Tests/POE2Radar.Tests.csproj -c Release and dotnet build POE2Radar.slnx -c Release. ";
p_test += "Read the new decision functions and tests. Set ok=true only if tests and build are green AND quest-follow, movement, and rotation decision functions exist as shipped Overlay types (not copies). evidence must quote test counts and file paths. gaps lists missing pieces. Return {ok, evidence, gaps}.";

let r_test = agent(p_test, #{
    label: "verify-tests",
    capability_mode: "execute",
    output_schema: verify_schema,
});

let p_skep = "Adversarially verify the POE2Radar bot in /home/him/Documents/poe2map. Use grep and read_file. Do not trust implementer summaries. Confirm: (1) quest follow picks a nav target from zone notes/landmarks via a pure function, (2) movement taps keys from SelectedPath only when armed+focused, (3) combat rotation is a list of skills with cooldowns, (4) HTTP cannot arm combat/move/quest, (5) no injection/WPM. Set ok=true only with file evidence. Otherwise ok=false and list gaps. Return {ok, evidence, gaps}.";
let r_skep = agent(p_skep, #{
    label: "verify-skeptic",
    capability_mode: "read-only",
    output_schema: verify_schema,
});

let test_ok = r_test != () && r_test.success && r_test.output.ok == true
    && r_test.output.evidence != () && r_test.output.evidence != "";
let skep_ok = r_skep != () && r_skep.success && r_skep.output.ok == true
    && r_skep.output.evidence != () && r_skep.output.evidence != "";

let gaps = [];
if r_test != () && r_test.success && r_test.output.gaps != () {
    for g in r_test.output.gaps { gaps.push(g); }
}
if r_skep != () && r_skep.success && r_skep.output.gaps != () {
    for g in r_skep.output.gaps { gaps.push(g); }
}
if !test_ok { log("verify-tests did not confirm"); }
if !skep_ok { log("verify-skeptic did not confirm"); }

phase("Fix");
if gaps.len() > 0 || !test_ok || !skep_ok {
    let p_fix = rules;
    p_fix += "Close these remaining gaps in the shipped Overlay/tests. Read the current code first. Then edit, then re-run dotnet test and dotnet build -c Release. Gaps: " + json_encode(gaps);
    let test_ev = "";
    if r_test != () && r_test.success { test_ev = text_or(r_test.output.evidence, ""); }
    p_fix += " Test evidence was: " + json_encode(test_ev);
    p_fix += " Return {done, summary, files}.";
    let r_fix = agent(p_fix, #{
        label: "fix-gaps",
        capability_mode: "execute",
        output_schema: done_schema,
    });
    log("fix " + (if r_fix != () && r_fix.success { "ok" } else { "fail" }));
} else {
    log("no fix pass needed");
}

let report = "";
report += "# quest-combat-bot\n\n";
report += "Skill keys: " + build_keys + "\n\n";
report += "## Inventory\n" + inv_nav + "\n\n" + inv_combat + "\n\n" + inv_ui + "\n\n";
report += "## Verify\n";
if r_test != () && r_test.success { report += text_or(r_test.output.evidence, "") + "\n"; }
if r_skep != () && r_skep.success { report += text_or(r_skep.output.evidence, "") + "\n"; }
report += "\nGaps: " + json_encode(gaps) + "\n";
let path = write_scratch_file("report.md", report);
complete(#{
    path: path,
    summary: "Quest-follow + customizable combat bot build pass finished",
    gaps: gaps,
    test_ok: test_ok,
    skeptic_ok: skep_ok,
});
