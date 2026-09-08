// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;

namespace Nethermind.Pbt;

/// <summary>A path to an internal node within a four-level group.</summary>
/// <remarks>The low four bits hold the left-aligned path; bits 4–5 hold its length (0–3). Boundary leaves are addressed by slot.</remarks>
internal readonly struct NodeGroupPath(int slot, int length)
{
    private readonly byte _value = (byte)(slot | (length << 4));

    internal int Slot => _value & 0xF;
    internal int Length => (_value >> 4) & 3;
    internal int Width => PbtFourLevelGroupGeometry.BoundarySlots >> Length;

    // Each preceding leaf contributes two post-order positions, except its still-open ancestors.
    internal int Position => 2 * (Slot + Width) - 2 - BitOperations.PopCount((uint)Slot);

    internal NodeGroupPath Left => new(Slot, Length + 1);
    internal NodeGroupPath Right => new(Slot + Width / 2, Length + 1);
}
