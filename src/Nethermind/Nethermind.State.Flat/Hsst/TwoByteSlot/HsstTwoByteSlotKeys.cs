// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Hsst.TwoByteSlot;

/// <summary>
/// Shared key-encoding convention for the TwoByteSlot HSST value layouts built by
/// <see cref="HsstTwoByteSlotValueBuilder{TWriter}"/>: keys are stored in little-
/// endian byte order so a native <c>u16</c> load on a stored key recovers the
/// big-endian (logical) numeric value, which lets SIMD scans compare numerically
/// (see <see cref="UniformKeySearch.LowerBound2LE"/>).
/// </summary>
internal static class HsstTwoByteSlotKeys
{
    /// <summary>Copy <paramref name="logicalKeys"/> (BE-stored, used during build) into
    /// <paramref name="storedKeys"/> as the on-disk LE-stored convention, byte-swapping
    /// each pair. Lengths must match and be a multiple of 2.</summary>
    internal static void CopyLogicalToStored(scoped ReadOnlySpan<byte> logicalKeys, Span<byte> storedKeys)
    {
        int n = logicalKeys.Length / 2;
        for (int i = 0; i < n; i++)
        {
            storedKeys[i * 2 + 0] = logicalKeys[i * 2 + 1];
            storedKeys[i * 2 + 1] = logicalKeys[i * 2 + 0];
        }
    }
}
