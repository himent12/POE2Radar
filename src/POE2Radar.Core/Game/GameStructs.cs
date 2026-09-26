using System.Runtime.InteropServices;

namespace POE2Radar.Core.Game;

/// <summary>
/// Blittable structs mirroring PoE2 memory layout for direct <c>ReadStruct&lt;T&gt;</c> reads.
/// </summary>

/// <summary>std::vector — 3 pointers (first/last/end). Count = (last-first)/elementSize.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct StdVector
{
    public nint First;
    public nint Last;
    public nint End;
}

[StructLayout(LayoutKind.Sequential)]
public struct Vector2
{
    public float X;
    public float Y;
}

[StructLayout(LayoutKind.Sequential)]
public struct Vector3
{
    public float X;
    public float Y;
    public float Z;
}

/// <summary>
/// Health / Mana / EnergyShield pool. ReservedFlat@0x10, ReservedFraction@0x14 (e.g. 2023 = 20.23%),
/// Regen@0x28, Max@0x2C, Current@0x30. Layout validated live (PoE1-lineage; unchanged in PoE2).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x34)]
public struct VitalStruct
{
    [FieldOffset(0x10)] public int ReservedFlat;
    [FieldOffset(0x14)] public int ReservedFraction;
    [FieldOffset(0x28)] public float Regen;
    [FieldOffset(0x2C)] public int Max;
    [FieldOffset(0x30)] public int Current;

    public readonly bool LooksValid(bool allowEmpty = false)
    {
        if (Max == 0) return allowEmpty && Current == 0 && ReservedFlat == 0 && ReservedFraction is >= 0 and <= 10000;
        if (Max <= 0 || Max > 10_000_000) return false;
        if (Current < -Max || Current > Max + 1) return false;
        return ReservedFlat >= 0 && ReservedFlat <= Max;
    }
}
