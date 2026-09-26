// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

public partial struct EvmPooledMemory
{
    /// <summary>
    /// Returns the 32 bytes at <paramref name="offset"/> when they lie inside both the active and the initialized memory,
    /// or a null reference when they do not.
    /// </summary>
    /// <param name="offset">The start of the word, below 2^32.</param>
    /// <remarks>
    /// A word there needs no expansion gas and no initialization, so reading or overwriting it in place is the whole
    /// access. The same caveat as <see cref="Load32BytesAfterGas"/> applies to the returned ref.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref byte GetActiveInitializedWord(ulong offset) => ref GetActiveInitializedRange(offset, WordSize);

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
            ulong newSize = (end + (WordSize - 1UL)) & ~(WordSize - 1UL);
            ulong cost = ExpansionCost(size >> 5, newSize >> 5);
            if (gas < cost) return ref Unsafe.NullRef<byte>();

            gas -= cost;
            Size = newSize;
        }

        // The initialized size read again and the offset rederived rather than held through the expansion charge,
        // where each would take a callee-saved register.
        if (end > _initializedSize) _initializedSize = end;
        return ref Unsafe.Add(ref GetBackingReference(), (nint)(end - WordSize));
    }
}
