// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core;

namespace Nethermind.Evm;

public partial struct EvmPooledMemory
{
    /// <summary>
    /// Returns the first of the <paramref name="length"/> bytes at <paramref name="offset"/> when they lie inside both
    /// the active and the initialized memory, or a null reference when they do not.
    /// </summary>
    /// <param name="offset">The start of the range, below 2^32.</param>
    /// <param name="length">The length of the range, below 2^32.</param>
    /// <remarks>The same caveat as <see cref="Load32BytesAfterGas"/> applies to the returned ref.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref byte GetActiveInitializedRange(ulong offset, ulong length)
    {
        Debug.Assert(offset <= uint.MaxValue && length <= uint.MaxValue);
        ulong end = offset + length;
        if (end > Size || end > _initializedSize) return ref Unsafe.NullRef<byte>();
        return ref Unsafe.Add(ref GetBackingReference(), (nint)offset);
    }

    /// <summary>
    /// Returns the 32 bytes at <paramref name="offset"/> ready to be overwritten when they need no clearing and no new
    /// backing, charging the expansion they need to <paramref name="gas"/>; a null reference, with nothing charged or
    /// changed, when they do or the gas does not cover the expansion.
    /// </summary>
    /// <param name="offset">The start of the word, below 2^32.</param>
    /// <param name="gas">The remaining execution gas.</param>
    /// <remarks>
    /// A word that starts inside the initialized memory leaves no gap to clear below it, so it may extend the
    /// initialized memory up to the backing's capacity, as <see cref="StoreNativeWordAfterGas"/> lets it; the caller
    /// must write all 32 bytes. The same caveat as <see cref="Load32BytesAfterGas"/> applies to the returned ref.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref byte TryPrepareWordOverwrite(ulong offset, ref ulong gas)
    {
        Debug.Assert(offset <= uint.MaxValue);
        ulong end = offset + WordSize;
        ulong initializedSize = _initializedSize;
        if (end > initializedSize && (offset > initializedSize || end > GetBackingCapacity()))
            return ref Unsafe.NullRef<byte>();

        Debug.Assert(end <= MaxMemorySize, "The backing never reaches past the largest addressable size.");
        ulong size = Size;
        if (end > size)
        {
            // ExpansionCost without a held multiplier or rounded size: the frameless MSTORE handler has no register left.
            Debug.Assert(GasCostOf.Memory == 3, "The linear term adds the word counts once per unit of the memory charge.");
            ulong newWords = (end + (WordSize - 1UL)) >> 5;
            ulong words = size >> 5;
            ulong cost = (newWords * newWords) >> 9;
            cost += newWords;
            cost += newWords;
            cost += newWords;
            cost -= words;
            cost -= words;
            cost -= words;
            cost -= (words * words) >> 9;
            if (gas < cost) return ref Unsafe.NullRef<byte>();

            gas -= cost;
            Size = newWords << 5;
        }

        // Reread rather than held through the expansion charge, which would take callee-saved registers.
        if (end > _initializedSize) _initializedSize = end;
        return ref Unsafe.Add(ref GetBackingReference(), (nint)(end - WordSize));
    }
}
