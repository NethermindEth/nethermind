// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Pbt;

/// <summary>A bitmap over the positions or boundary slots of one node group.</summary>
/// <remarks>
/// Sized for the widest supported group, but every operation touches only the <see cref="WordCount"/> words the
/// configured geometry needs, which the JIT folds to a constant. It is 64 bytes, so pass it by reference on hot paths.
/// </remarks>
[InlineArray(WordCapacity)]
internal struct PbtBitmap
{
    private const int WordCapacity = (PbtGroupGeometry.MaxPositionCount + 63) >> 6;

    /// <summary>The number of words holding a position of the configured geometry; slots never need more.</summary>
    internal static readonly int WordCount = (PbtGroupGeometry.PositionCount + 63) >> 6;

    private ulong _word;

    /// <summary>A bitmap whose low 64 bits are <paramref name="bits"/>.</summary>
    internal static PbtBitmap FromLow(ulong bits)
    {
        PbtBitmap bitmap = default;
        bitmap._word = bits;
        return bitmap;
    }

    /// <summary>Reads a bitmap stored as little-endian bytes, the low bit first.</summary>
    internal static void Read(ReadOnlySpan<byte> source, out PbtBitmap bitmap)
    {
        bitmap = default;
        source.CopyTo(MemoryMarshal.AsBytes((Span<ulong>)bitmap));
    }

    /// <summary>Writes the low <c>8 * destination.Length</c> bits as little-endian bytes.</summary>
    internal readonly void Write(Span<byte> destination) => MemoryMarshal.AsBytes((ReadOnlySpan<ulong>)this)[..destination.Length].CopyTo(destination);

    private readonly ulong Word(int index) => Unsafe.Add(ref Unsafe.AsRef(in _word), index);

    [UnscopedRef]
    private ref ulong WordRef(int index) => ref Unsafe.Add(ref _word, index);

    internal readonly bool IsSet(int bit) => (Word(bit >> 6) & (1UL << bit)) != 0;

    internal void Set(int bit) => WordRef(bit >> 6) |= 1UL << bit;

    internal void Clear(int bit) => WordRef(bit >> 6) &= ~(1UL << bit);

    internal readonly bool IsEmpty
    {
        get
        {
            ulong any = 0;
            for (int index = 0; index < WordCount; index++) any |= Word(index);
            return any == 0;
        }
    }

    internal readonly int PopCount()
    {
        int count = 0;
        for (int index = 0; index < WordCount; index++) count += BitOperations.PopCount(Word(index));
        return count;
    }

    /// <summary>The number of set bits below <paramref name="bit"/>.</summary>
    internal readonly int PopCountBelow(int bit)
    {
        int count = 0;
        for (int index = 0; index < bit >> 6; index++) count += BitOperations.PopCount(Word(index));
        return (bit & 63) == 0 ? count : count + BitOperations.PopCount(Word(bit >> 6) & ((1UL << bit) - 1));
    }

    /// <summary>The number of set bits among the <paramref name="count"/> bits from <paramref name="start"/>.</summary>
    internal readonly int PopCountRange(int start, int count)
    {
        int end = start + count;
        int total = 0;
        while (start < end)
        {
            int take = Math.Min(end - start, 64 - (start & 63));
            total += BitOperations.PopCount(Word(start >> 6) & RangeMask(start & 63, take));
            start += take;
        }
        return total;
    }

    /// <summary>Whether any of the <paramref name="count"/> bits from <paramref name="start"/> is set.</summary>
    internal readonly bool AnyInRange(int start, int count)
    {
        int end = start + count;
        while (start < end)
        {
            int take = Math.Min(end - start, 64 - (start & 63));
            if ((Word(start >> 6) & RangeMask(start & 63, take)) != 0) return true;
            start += take;
        }
        return false;
    }

    private static ulong RangeMask(int offset, int count) => (count == 64 ? ulong.MaxValue : (1UL << count) - 1) << offset;

    /// <summary>The lowest set bit at or above <paramref name="bit"/>, or -1 when there is none.</summary>
    internal readonly int NextSetBit(int bit)
    {
        int index = bit >> 6;
        if (index >= WordCount) return -1;
        ulong word = Word(index) & (ulong.MaxValue << bit);
        while (word == 0)
        {
            if (++index >= WordCount) return -1;
            word = Word(index);
        }
        return (index << 6) + BitOperations.TrailingZeroCount(word);
    }

    internal void Or(in PbtBitmap other)
    {
        for (int index = 0; index < WordCount; index++) WordRef(index) |= other.Word(index);
    }

    internal void AndNot(in PbtBitmap other)
    {
        for (int index = 0; index < WordCount; index++) WordRef(index) &= ~other.Word(index);
    }
}
