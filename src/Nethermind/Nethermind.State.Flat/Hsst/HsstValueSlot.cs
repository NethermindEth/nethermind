// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.State.Flat.Hsst;

/// <summary>
/// Shared helpers for BSearchIndex value-slot encoding.
///
/// The BSearchIndex header packs the value-slot width into 2 bits of the Flags byte
/// (bits 3-4), so the format only encodes the four widths <c>{2, 3, 4, 6}</c>. The
/// <see cref="MinBytesFor"/> helper rounds an arbitrary natural width up to the next
/// supported value. Lives in its own non-generic class so callers outside
/// <see cref="HsstBTreeBuilder{TWriter,TReader,TPin}"/>'s generic instantiation
/// (e.g. the leaf-boundary enumerator) can call it without specifying type arguments.
/// </summary>
internal static class HsstValueSlot
{
    /// <summary>
    /// Smallest supported value-slot width that can encode <paramref name="value"/>:
    /// returns 2 for 0/1/2-byte naturals, 3 for 3, 4 for 4, and 6 for 5/6. Naturals
    /// larger than 6 bytes never occur in practice because <c>BaseOffset</c> already
    /// caps the encodable delta range at 2⁴⁸ − 1.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int MinBytesFor(long value)
    {
        int natural = value == 0 ? 1 : (BitOperations.Log2((ulong)value) >> 3) + 1;
        return natural <= 2 ? 2
            : natural == 3 ? 3
            : natural == 4 ? 4
            : 6; // 5 and 6 both pad up to 6
    }
}
