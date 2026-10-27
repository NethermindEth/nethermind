// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;

namespace Nethermind.Pbt;

/// <summary>A path to a node within a three-level group.</summary>
/// <remarks>The low three bits hold the left-aligned path; bits 4–6 hold its length (0–3).</remarks>
public readonly struct NodeGroupPath
{
    private readonly byte _value;

    public NodeGroupPath(int slot, int length)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)length, (uint)PbtThreeLevelGroupGeometry.LevelsPerGroup);
        if ((uint)slot >= PbtThreeLevelGroupGeometry.BoundarySlots
            || (slot & ((PbtThreeLevelGroupGeometry.BoundarySlots >> length) - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(slot));
        _value = (byte)(slot | (length << 4));
    }

    public int Slot => _value & 0xF;
    public int Length => (_value >> 4) & 7;
    public int Width => PbtThreeLevelGroupGeometry.BoundarySlots >> Length;

    // Each preceding leaf contributes two post-order positions, except its still-open ancestors.
    public int Position => 2 * (Slot + Width) - 2 - BitOperations.PopCount((uint)Slot);

    /// <summary>Whether a key in boundary slot <paramref name="slot"/> lies under this path; no key lies under one past the last slot.</summary>
    public bool Covers(int slot) => ((slot ^ Slot) >> (PbtThreeLevelGroupGeometry.LevelsPerGroup - Length)) == 0;

    public NodeGroupPath Left => new(Slot, Length + 1);
    public NodeGroupPath Right => new(Slot + Width / 2, Length + 1);
}
