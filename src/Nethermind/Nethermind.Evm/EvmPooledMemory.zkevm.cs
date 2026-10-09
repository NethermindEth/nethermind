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
    /// Returns the 32 bytes at <paramref name="offset"/> ready to be overwritten when they need no new backing and leave
    /// a gap of at most two words below them, charging the expansion they need to <paramref name="gas"/>; a null
    /// reference, with nothing charged or changed, when they do not or the gas does not cover the expansion.
    /// </summary>
    /// <param name="offset">The start of the word, below 2^32.</param>
    /// <param name="gas">The remaining execution gas.</param>
    /// <remarks>
    /// The word may extend the initialized memory up to the backing's capacity, as <see cref="StoreNativeWordAfterGas"/>
    /// lets it, clearing any gap it leaves below it; the caller must write all 32 bytes. The same caveat as
    /// <see cref="Load32BytesAfterGas"/> applies to the returned ref.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref byte TryPrepareWordOverwrite(ulong offset, ref ulong gas)
    {
        Debug.Assert(offset <= uint.MaxValue);
        ulong end = offset + WordSize;
        ulong initializedSize = _initializedSize;
        if (end > initializedSize && (offset > initializedSize + MaxClearedGap || end > GetBackingCapacity()))
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
        initializedSize = _initializedSize;
        ref byte word = ref Unsafe.Add(ref GetBackingReference(), (nint)(end - WordSize));
        if (end > initializedSize)
        {
            _initializedSize = end;
            // Every frame's first MSTORE, of the free memory pointer, leaves a two-word gap. Cleared without a loop,
            // which would cost the frameless handler a frame: a word from where the initialized memory ends, which may
            // run into the word the caller overwrites, and the word below that one when the gap needs it.
            nint gap = (nint)(end - WordSize) - (nint)initializedSize;
            if (gap > 0)
            {
                ClearWord(ref Unsafe.Subtract(ref word, gap));
                if (gap > WordSize) ClearWord(ref Unsafe.Subtract(ref word, WordSize));
            }
        }

        return ref word;
    }

    /// <summary>
    /// Returns the first of the <paramref name="length"/> bytes at <paramref name="offset"/> ready to be overwritten when
    /// they need no new backing and leave a gap of at most two words below them, charging the expansion they need to
    /// <paramref name="gas"/>; a null reference, with nothing charged or changed, when they do not or the gas does not
    /// cover the expansion.
    /// </summary>
    /// <param name="offset">The start of the range, below 2^32.</param>
    /// <param name="length">The length of the range, from 1 to 2^32 - 1.</param>
    /// <param name="gas">The remaining execution gas.</param>
    /// <remarks>
    /// <see cref="TryPrepareWordOverwrite"/> for any length; the caller must write every byte of the range. The same
    /// caveat as <see cref="Load32BytesAfterGas"/> applies to the returned ref.
    /// </remarks>
    internal ref byte TryPrepareRangeOverwrite(ulong offset, ulong length, ref ulong gas)
    {
        Debug.Assert(offset <= uint.MaxValue && length is > 0 and <= uint.MaxValue);
        ulong end = offset + length;
        ulong initializedSize = _initializedSize;
        if (end > initializedSize && (offset > initializedSize + MaxClearedGap || end > GetBackingCapacity()))
            return ref Unsafe.NullRef<byte>();

        Debug.Assert(end <= MaxMemorySize, "The backing never reaches past the largest addressable size.");
        ulong size = Size;
        if (end > size)
        {
            ulong newWords = (end + (WordSize - 1UL)) >> 5;
            ulong words = size >> 5;
            ulong cost = newWords * GasCostOf.Memory + ((newWords * newWords) >> 9) - words * GasCostOf.Memory - ((words * words) >> 9);
            if (gas < cost) return ref Unsafe.NullRef<byte>();

            gas -= cost;
            Size = newWords << 5;
        }

        ref byte start = ref Unsafe.Add(ref GetBackingReference(), (nint)offset);
        if (end > initializedSize)
        {
            _initializedSize = end;
            if (offset > initializedSize)
                Unsafe.InitBlockUnaligned(ref Unsafe.Subtract(ref start, (nint)(offset - initializedSize)), 0, (uint)(offset - initializedSize));
        }

        return ref start;
    }

    /// <summary>The widest gap below a word <see cref="TryPrepareWordOverwrite"/> clears.</summary>
    private const ulong MaxClearedGap = 2 * WordSize;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ClearWord(ref byte word)
    {
        Unsafe.WriteUnaligned(ref word, 0UL);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref word, sizeof(ulong)), 0UL);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref word, 2 * sizeof(ulong)), 0UL);
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref word, 3 * sizeof(ulong)), 0UL);
    }
}
