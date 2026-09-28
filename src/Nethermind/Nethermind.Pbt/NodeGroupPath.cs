// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;

namespace Nethermind.Pbt;

/// <summary>A path to a node within a group.</summary>
/// <remarks>The low eight bits hold the path left-aligned to the group's boundary slots; bits 8–11 hold its length.</remarks>
internal readonly struct NodeGroupPath
{
    private readonly ushort _value;

    internal NodeGroupPath(int slot, int length)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)length, (uint)PbtGroupGeometry.LevelsPerGroup);
        if ((uint)slot >= (uint)PbtGroupGeometry.BoundarySlots || (slot & ((PbtGroupGeometry.BoundarySlots >> length) - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(slot));
        _value = (ushort)(slot | (length << 8));
    }

    private NodeGroupPath(ushort value) => _value = value;

    internal static NodeGroupPath FromValidated(int slot, int length) => new((ushort)(slot | (length << 8)));

    internal int Slot => _value & 0xFF;
    internal int Length => _value >> 8;
    internal int Width => PbtGroupGeometry.BoundarySlots >> Length;

    internal int Position => PositionOf(Slot, Width);

    // Each preceding leaf contributes two post-order positions, except its still-open ancestors.
    internal static int PositionOf(int slot, int width) => 2 * (slot + width) - 2 - BitOperations.PopCount((uint)slot);

    internal int GetBit(int bit) => (Slot >> (PbtGroupGeometry.LevelsPerGroup - 1 - bit)) & 1;
    internal NodeGroupPath Left => new(Slot, Length + 1);
    internal NodeGroupPath Right => new(Slot + Width / 2, Length + 1);
}
