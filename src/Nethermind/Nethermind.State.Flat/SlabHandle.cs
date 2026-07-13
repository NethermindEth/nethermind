// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat;

[Flags]
public enum SlabFlags : byte
{
    None = 0,
    HasKeccak = 1,
    EmptyUnknown = 2,
}

/// <summary>
/// Packed 64-bit handle into a <see cref="SlabArena"/>; layout (LSB first):
/// rlpLen:12 | offset:20 | slabIndex:18 | flags:2. <see cref="None"/> (all-zero) never denotes
/// a record: the arena pads slab 0 so no record starts at (slab 0, offset 0).
/// </summary>
public readonly struct SlabHandle(ulong packed) : IEquatable<SlabHandle>
{
    public const int MaxRlpLen = (1 << 12) - 1;
    public const int MaxOffset = (1 << 20) - 1;
    public const int MaxSlabIndex = (1 << 18) - 1;
    public static readonly SlabHandle None = default;

    public ulong Packed { get; } = packed;

    public int RlpLength => (int)(Packed & 0xFFF);
    public int Offset => (int)((Packed >> 12) & 0xFFFFF);
    public int SlabIndex => (int)((Packed >> 32) & 0x3FFFF);
    public SlabFlags Flags => (SlabFlags)((Packed >> 50) & 0x3);
    public bool IsNone => Packed == 0;

    public static SlabHandle Create(int slabIndex, int offset, int rlpLength, SlabFlags flags)
    {
        if ((uint)rlpLength > MaxRlpLen) throw new InvalidOperationException($"Node RLP length {rlpLength} exceeds the slab handle limit {MaxRlpLen}");
        if ((uint)offset > MaxOffset) throw new InvalidOperationException($"Slab offset {offset} exceeds the handle limit");
        if ((uint)slabIndex > MaxSlabIndex) throw new InvalidOperationException($"Slab index {slabIndex} exceeds the handle limit");
        return new SlabHandle((ulong)(uint)rlpLength | ((ulong)(uint)offset << 12) | ((ulong)(uint)slabIndex << 32) | ((ulong)(byte)flags << 50));
    }

    public bool Equals(SlabHandle other) => Packed == other.Packed;
    public override bool Equals(object? obj) => obj is SlabHandle other && Equals(other);
    public override int GetHashCode() => Packed.GetHashCode();
}
