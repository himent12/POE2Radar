#!/usr/bin/env python3
"""Regenerate src/POE2Radar.Core/Game/poe2_item_data.json from the RePoE PoE2 export.

The item appraisal needs two things the live read can't give it:
  * affix tiers: every rollable prefix/suffix with its family, required level, stat ranges and ordered spawn
    tags, so a rolled mod can be placed as "T2 of 9 on boots, 92% of the best roll";
  * base stats: each equipment base's tags and its base damage / attack time / armour / evasion / ES,
    so weapon DPS and total defences can be computed from the base plus the item's local mods.

Run after a content patch (the RePoE site tracks the game version):
    python3 resources/poe2-data/generate_item_data.py            # downloads mods.json + base_items.json
    python3 resources/poe2-data/generate_item_data.py /tmp/repoe  # or reads them from a directory
"""
import json
import os
import sys
import urllib.request

SOURCE = "https://repoe-fork.github.io/poe2/"
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "src", "POE2Radar.Core", "Game", "poe2_item_data.json")

# Mods that can sit on a rare/magic item as a regular affix: equipment (item), Abyss desecrated, jewels (misc).
AFFIX_DOMAINS = {"item", "desecrated", "misc"}

EQUIPMENT_CLASSES = {
    "Body Armour", "Helmet", "Gloves", "Boots", "Shield", "Buckler", "Focus", "Quiver",
    "Ring", "Amulet", "Belt", "Jewel",
    "One Hand Mace", "Two Hand Mace", "Warstaff", "Spear", "Bow", "Crossbow", "Talisman",
    "One Hand Sword", "Two Hand Sword", "One Hand Axe", "Two Hand Axe", "Flail", "Dagger", "Claw",
    "Staff", "Wand", "Sceptre",
}


def load(folder, name):
    if folder:
        with open(os.path.join(folder, name), encoding="utf-8") as f:
            return json.load(f)
    with urllib.request.urlopen(SOURCE + name, timeout=120) as r:
        return json.load(r)


def version():
    try:
        with urllib.request.urlopen(SOURCE, timeout=30) as r:
            page = r.read().decode("utf-8", "replace")
        marker = page.find("version ")
        return page[marker + 8:page.find("<", marker)].strip() if marker >= 0 else "unknown"
    except OSError:
        return "unknown"


def spawn_tags(weights):
    """Ordered spawn tags; the first tag the base carries decides. "!tag" = weight 0. The trailing
    catch-all "default: 0" is dropped (a base with no listed tag can't roll the mod anyway)."""
    tags = ["!" + w["tag"] if w["weight"] == 0 else w["tag"] for w in weights]
    while tags and tags[-1] == "!default":
        tags.pop()
    return tags


def main():
    folder = sys.argv[1] if len(sys.argv) > 1 else None
    mods = load(folder, "mods.json")
    bases = load(folder, "base_items.json")

    affixes = {}
    for mod_id, m in mods.items():
        if m["domain"] not in AFFIX_DOMAINS or m["generation_type"] not in ("prefix", "suffix"):
            continue
        stats = [[s["id"], s["min"], s["max"]] for s in m["stats"]]
        if not stats:
            continue
        affixes[mod_id] = {
            "f": m["type"],
            "g": m["generation_type"][0],
            "d": m["domain"][0],
            "l": m["required_level"],
            "s": stats,
            "w": spawn_tags(m["spawn_weights"]),
        }

    out_bases = {}
    for path, b in bases.items():
        if b["release_state"] != "released" or b["item_class"] not in EQUIPMENT_CLASSES:
            continue
        p = b["properties"]
        entry = {"n": b["name"], "c": b["item_class"], "t": [t for t in b["tags"] if t != "default"]}
        for key, prop in (("ar", "armour"), ("ev", "evasion"), ("es", "energy_shield")):
            if p.get(prop):
                entry[key] = p[prop]["max"]
        if p.get("physical_damage_max"):
            entry["dmin"] = p["physical_damage_min"]
            entry["dmax"] = p["physical_damage_max"]
        if p.get("attack_time"):
            entry["at"] = p["attack_time"]
        out_bases[path] = entry

    # source/version record where the table came from (provenance only; the overlay doesn't read them).
    data = {"source": SOURCE, "version": version(), "affixes": affixes, "bases": out_bases}
    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(data, f, separators=(",", ":"), sort_keys=True)
    print(f"{len(affixes)} affixes, {len(out_bases)} bases -> {OUT}")


if __name__ == "__main__":
    main()
