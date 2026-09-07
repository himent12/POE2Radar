#!/usr/bin/env python3
"""Build the compact, offline gem catalogue from a pinned RePoE PoE2 revision.
Usage: python3 resources/auto-build/generate_catalog.py [revision]
Data source: https://github.com/repoe-fork/poe2 (game data extracted by RePoE).
No downloaded code is executed. Review catalogue changes after game patches.
"""
import hashlib
import json
from pathlib import Path
import sys
from urllib.request import urlopen

revision = sys.argv[1] if len(sys.argv) > 1 else json.load(urlopen('https://api.github.com/repos/repoe-fork/poe2/commits/master'))['sha']
root = Path(__file__).resolve().parents[2]
def fetch(name):
    return json.load(urlopen(f'https://raw.githubusercontent.com/repoe-fork/poe2/{revision}/data/{name}.json'))
gems, skills = fetch('skill_gems'), fetch('skills')
result = {}
for metadata, gem in sorted(gems.items()):
    if gem.get('base_item', {}).get('release_state') != 'released':
        continue
    effects = []
    for skill_id in gem.get('grants_skills') or []:
        skill = skills.get(skill_id, {})
        active = skill.get('active_skill') or {}
        static = skill.get('static') or {}
        costs = [static.get('costs', {})] + [v.get('costs', {}) for v in skill.get('per_level', {}).values()]
        effects.append(dict(Id=skill_id, Name=active.get('display_name', ''),
            Types=active.get('types', []),
            Manual=active.get('is_manually_casted', False), CastMs=skill.get('cast_time', 0),
            CooldownMs=max([static.get('cooldown', 0)] + [v.get('cooldown', 0) for v in skill.get('per_level', {}).values()]),
            CostKinds=sorted({k for c in costs for k,v in c.items() if v > 0}),
            Reserves=bool(static.get('reservations')) or any(v.get('reservations') for v in skill.get('per_level', {}).values())))
    result[metadata] = dict(Name=gem.get('base_item', {}).get('display_name', ''),
        GemType=gem.get('gem_type', ''), Tags=gem.get('tags', []), Effects=effects)
target = root/'src/POE2Radar.Core/Game/poe2_skill_catalog.json'
output = json.dumps(result, ensure_ascii=False, separators=(',', ':'))+'\n'
target.write_text(output)
(root/'resources/auto-build/catalog-source.json').write_text(json.dumps(dict(
    repository='https://github.com/repoe-fork/poe2', revision=revision, gems=len(result),
    sha256=hashlib.sha256(output.encode()).hexdigest()), indent=2)+'\n')
print(f'Wrote {len(result)} gems from {revision}')
