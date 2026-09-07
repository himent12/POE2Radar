using POE2Radar.Core.Game;
using POE2Radar.Core.Native;
using POE2Radar.Core.Pathfinding;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Input;
using NumVec2 = System.Numerics.Vector2;

namespace POE2Radar.Overlay;

public sealed partial class RadarApp
{
    private readonly BossCombat _bossCombat = new();
    private readonly DodgeInput _bossDodge = new();
    private BossCombat.Decision _bossDecision;
    private bool _combatBindingSafe;
    private NavGrid? _bossNav;
    private IReadOnlyList<Poe2Live.EntityDot> _bossEntities = Array.Empty<Poe2Live.EntityDot>();
    private string _bossLastNote = "";

    private void UpdateBoss(bool live, bool fresh, NumVec2 player, nint localPlayer,
        IReadOnlyList<Poe2Live.EntityDot> entities, Poe2Live.TerrainData? terrain)
    {
        _bossEntities = entities;
        _bossNav = terrain is null ? null : NavGrid.For(terrain);
        _bossDecision = _bossCombat.Update(new(_areaHash, DateTime.UtcNow,
            live && CombatArmed && _settings.BossCombatEnabled, fresh, player, entities,
            live ? _liveRender.PlayerVitals(localPlayer) : null, _bossNav,
            new(_settings.CombatRange, _settings.CombatKeepDistance, _settings.BossDamagePct,
                _settings.BossDodgeGapMs, _settings.BossSafeOpeningMs, _settings.BossLostGraceMs, _settings.BossHazardRadius, _settings.CombatFleeHpPct, _settings.CombatFleeRecoverPct, _settings.BossDodgeDistance), _imprisonedIds));
        if (_bossDecision.Note != _bossLastNote)
        {
            if (_bossDecision.Active) Console.WriteLine($"Boss #{_bossDecision.TargetId}: {_bossDecision.Note}");
            _bossLastNote = _bossDecision.Note ?? "";
        }
        if (!live || !fresh || !CombatArmed || !_settings.BossCombatEnabled)
            _bossDodge.Cancel(SendKeyUp);
    }

    // Boss movement always checks actual controls, including hand-written rotations. Never assumes WASD
    // means those four letters: in mouse mode those letters are often ATTACK keys.
    private bool BossBindingsSafe(nint player, DateTime now)
    {
        var read = _buildBindingRead;
        return read is not null && read.Player == player && now - read.ReadAt < TimeSpan.FromSeconds(3)
            && ControlsMatch(_settings, read.Input, read.Bar);
    }

    internal static bool ControlsMatch(RadarSettings settings, GameInputSnapshot input, SkillBarSnapshot bar)
    {
        bool Key(string action, int expected) => input.Find(action) is { Usable: true, Modifiers: 0 } b && b.Key == expected;
        if (!input.Complete || !Key("use_dodge_roll", settings.CombatDodgeKey)) return false;
        if (PathMove.IsClick(settings.MoveMethod))
            return input.Mode == "Mouse" && bar.Complete && bar.Slots.Any(s => s.ActionId == "Move" && Key("use_bound_skill" + s.Slot, settings.MoveClickKey));
        return input.Mode == "WASD" && Key("move_up", settings.MoveKeyW) && Key("move_left", settings.MoveKeyA)
            && Key("move_down", settings.MoveKeyS) && Key("move_right", settings.MoveKeyD);
    }

    private CombatAssist.Skill BossSkill(CombatAssist.Skill skill, CombatSkill source, NumVec2 player)
    {
        if (_bossDecision.Intent == BossCombat.Intent.Dodge) return skill with { Enabled = false };
        var emergency = skill.Priority && (skill.HpBelowPct > 0 || skill.EsBelowPct > 0 || skill.ManaBelowPct > 0);
        var attack = _bossDecision.Intent == BossCombat.Intent.Attack;
        if (!attack && !emergency) return skill with { Enabled = false };
        // A queued ordinary cast finishes; new long commitments require a measured quiet opening.
        if (!_bossDecision.LongWindow && !emergency && (skill.HoldMs > 450 || skill.RepeatGapMs > 650))
            return skill with { Enabled = false };
        if (source.SourceMetadata.EndsWith("SkillGemEscapeShot", StringComparison.Ordinal)
            || source.Name.Equals("Escape Shot", StringComparison.OrdinalIgnoreCase))
        {
            var boss = _bossEntities.FirstOrDefault(e => e.Id == _bossDecision.TargetId);
            var away = player - boss.Grid;
            if (_bossNav is null || boss.Id == 0 || away.LengthSquared() < .01f || skill.AimMode != "Target")
                return skill with { Enabled = false };
            var landing = player + NumVec2.Normalize(away) * _settings.BossEscapeDistance;
            if (!BossCombat.ClearLine(_bossNav, player, landing)
                || _bossNav.ClearanceCells((int)landing.X, (int)landing.Y) < 2
                || !BossCombat.SafeHazardPath(player, landing, _bossEntities, _settings.BossHazardRadius))
                return skill with { Enabled = false };
        }
        // User timing remains stored unchanged. Reassess after each short attack instead of doing a burst
        // plus an unconditional roll. Escape Shot/guard still get the full configured recovery window.
        return skill with { Repeat = _bossDecision.LongWindow ? skill.Repeat : 1, DodgeAfter = false };
    }

    private bool TickBossDodge(bool live, nint localPlayer, NumVec2 player, POE2Radar.Core.Game.Vector3? playerWorld)
    {
        if (!live || !_combatBindingSafe || !BossBindingsSafe(localPlayer, DateTime.UtcNow))
        { _bossDodge.Cancel(SendKeyUp); return false; }
        if (_bossDodge.Busy) return true;
        if (_bossDecision.Intent != BossCombat.Intent.Dodge) return false;
        AbortComboMacro(); ReleaseHeldKeys(); _runHoldUntil = DateTime.MinValue;
        var landing = _bossDecision.Destination;
        var keys = Array.Empty<ushort>();
        if (!PathMove.IsClick(_settings.MoveMethod))
        {
            var d = PathMove.Decide(new(true, true, true, player,
                new[] { ((int)_bossDecision.Destination.X, (int)_bossDecision.Destination.Y) }, 1,
                DateTime.UtcNow, DateTime.MinValue, 0, _settings.MoveMethod,
                _settings.MoveKeyW, _settings.MoveKeyA, _settings.MoveKeyS, _settings.MoveKeyD, _settings.MoveClickKey,
                Diagonals: _settings.MoveDiagonals, AxisRotationDeg: _settings.MoveAxisRotationDeg));
            keys = d.HoldKeys?.ToArray() ?? keys;
            var quantized = DodgeLanding(player, keys, _settings);
            if (quantized is not { } q || _bossNav is null || !BossCombat.ClearLine(_bossNav, player, q)
                || _bossNav.ClearanceCells((int)MathF.Round(q.X), (int)MathF.Round(q.Y)) < 2
                || !BossCombat.SafeHazardPath(player, q, _bossEntities, _settings.BossHazardRadius)
                || !BossCombat.SafeEnemyPath(player, q, _bossEntities)) return false;
            landing = q;
        }
        if (!TryAimBossPoint(landing, playerWorld)) return false;
        var vk = (ushort)_settings.CombatDodgeKey;
        if (GameHost.WouldDrop(vk)) return false;
        _bossDodge.Start(DateTime.UtcNow, vk, keys, _settings.CombatDodgeRecoverMs, SendKeyDown, SendKeyUp);
        _bossCombat.DidDodge(DateTime.UtcNow);
        Console.WriteLine($"Boss dodge dispatched: key {vk}, target {_bossDecision.TargetId}");
        _combatNote = _bossDecision.Note;
        return true;
    }

    // Validate the actual quantized key direction as well as the policy's continuous candidate.
    internal static NumVec2? DodgeLanding(NumVec2 player, IReadOnlyList<ushort> keys, RadarSettings settings)
    {
        var dir = PathMove.DirectionOf(keys, settings.MoveKeyW, settings.MoveKeyA, settings.MoveKeyS, settings.MoveKeyD);
        if (dir == (0, 0)) return null;
        var a = -settings.MoveAxisRotationDeg * MathF.PI / 180;
        var vector = NumVec2.Normalize(new(dir.x * MathF.Cos(a) - dir.y * MathF.Sin(a),
            dir.x * MathF.Sin(a) + dir.y * MathF.Cos(a)));
        return player + vector * settings.BossDodgeDistance;
    }

    private bool TryAimBossPoint(NumVec2 point, POE2Radar.Core.Game.Vector3? playerWorld)
    {
        if (_cameraMatrix is not { } matrix || playerWorld is not { } p) return false;
        if (!MapProjection.TryWorldToScreen(matrix, point.X * GridConstants.GridToWorld,
            point.Y * GridConstants.GridToWorld, p.Z, _window.Width, _window.Height, out var x, out var y)
            || x < 8 || y < 8 || x > _window.Width - 8 || y > _window.Height - 8) return false;
        GameHost.SetCursorPos(_window.OriginX + (int)x, _window.OriginY + (int)y);
        return true;
    }
}
