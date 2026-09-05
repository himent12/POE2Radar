# Current-client patch validation

Validated by read-only probes against the running Proton client, module base
`0x140000000`, image size `0x4C9C000`, in `G2_10_1` (area level 17).
Absolute addresses are observations, not persisted runtime addresses.

## Startup signature

The old pattern hard-coded a conditional-branch displacement of `0x116`; the
new executable uses `0x120`. The updated pattern wildcards the displacement
and anchors on the following `mov ecx, 0x148` allocation-size instruction.
It matches once, at `0x14010BFAE`, resolving RIP-relative to slot `0x1445D1EE8`.
A shorter pattern without the allocation size matched three unrelated globals.

## Offset changes

| Field | Previous | Current | Evidence |
| --- | --- | --- | --- |
| GameState.CurrentStatePtr | 0x08 | 0x10 | Active state resolves to local player |
| GameState.States | 0x48 | 0x50 | Slot 4 agrees with active vector |
| AreaInstance.AreaInfoPtr | 0xA0 | 0x98 | G2_10_1 |
| AreaInstance.CurrentAreaLevel | 0xC4 | 0xBC | 17 |
| AreaInstance.CurrentAreaHash | 0x11C | 0x114 | Header -8 shift; 0xC0F57AB7, paired seed at 0x118; cross-zone validation pending |
| AreaInstance.LocalPlayer | 0x5C0 | 0x5D0 | Metadata/Characters/DexInt/DexIntFourb |
| AreaInstance.ServerDataPtr | 0x5A0 | 0x5B0 | 117 inventory entries; shared-pointer invariants checked |
| AreaInstance.AwakeEntities | 0x6E0 | 0x6F0 | 227 tree nodes including player |
| AreaInstance.SleepingEntities | 0x6F0 | 0x700 | 5070 tree nodes with entity metadata |
| AreaInstance.TerrainMetadata | 0x8C0 | 0x8D0 | 103×103 tiles; 2807265 packed bytes = 1185×103×23 |
| ServerData.League | 0x21E0 | 0x2160 | Runes of Aldur |
| MapUiElement.Shift | 0x368 | 0x350 | (0,0) |
| MapUiElement.DefaultShift | 0x370 | 0x358 | (0,-20), two elements |
| MapUiElement.Zoom | 0x3A8 | 0x390 | 0.5 |
| UiElement.Flags | 0x180 | 0x168 | Tab toggle: 0x5026F1 → 0x502EF1 |
| UiElement.ScaleIndex | 0x18A | 0x172 | Root scale index 3; map index 2 |

Existing Life offsets still read HP 373/373, mana 237/237, ES 101/101.
Production readers decode terrain 2370×2369 (packed row padding), player grid
(1346.5, 313.5), entities, landmarks, and an open map at zoom 0.5.

## Reproduce

Build Research, then run `POE2Radar.Research --pid <PID> --patch-check` while
loaded into a zone. This new command exercises production readers without
starting the overlay or sending input. It returns nonzero on failed checks.
Use existing `--find-entities`, `--find-terrain`, and `--vitals` for deeper probes.

Atlas, tooltip geometry, league reward panels, and input automation were not
validated in this pass. A passing smoke check is not a full offset-table audit.
