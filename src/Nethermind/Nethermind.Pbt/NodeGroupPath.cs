// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;

namespace Nethermind.Pbt;

/// <summary>A path to a node within a four-level group.</summary>
/// <remarks>The low four bits hold the left-aligned path; bits 4–6 hold its length (0–4).</remarks>
internal readonly struct NodeGroupPath
{
    private readonly byte _value;

    internal NodeGroupPath(int slot, int length)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)length, 4u);
        if ((uint)slot > 15 || (slot & ((16 >> length) - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(slot));
        _value = (byte)(slot | (length << 4));
    }

    internal int Slot => _value & 0xF;
    internal int Length => (_value >> 4) & 7;
    internal int Width => PbtFourLevelGroupGeometry.BoundarySlots >> Length;

    // Each preceding leaf contributes two post-order positions, except its still-open ancestors.
    internal int Position => 2 * (Slot + Width) - 2 - BitOperations.PopCount((uint)Slot);

    internal int GetBit(int bit) => (Slot >> (3 - bit)) & 1;
    internal NodeGroupPath Left => new(Slot, Length + 1);
    internal NodeGroupPath Right => new(Slot + Width / 2, Length + 1);
}
