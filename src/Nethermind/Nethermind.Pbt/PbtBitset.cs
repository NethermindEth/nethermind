// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Nethermind.Pbt;

/// <summary>Operations on caller-owned little-endian arrays of 64-bit bitmap words.</summary>
internal static class PbtBitset
{
    internal const int BitsPerWord = sizeof(ulong) * 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Contains(ReadOnlySpan<ulong> words, int bit) =>
        (words[bit / BitsPerWord] & (1UL << (bit % BitsPerWord))) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Set(Span<ulong> words, int bit) =>
        words[bit / BitsPerWord] |= 1UL << (bit % BitsPerWord);

    public static void Or(Span<ulong> destination, ReadOnlySpan<ulong> source)
    {
        for (int i = 0; i < destination.Length; i++) destination[i] |= source[i];
    }

    public static void And(Span<ulong> destination, ReadOnlySpan<ulong> source)
    {
        for (int i = 0; i < destination.Length; i++) destination[i] &= source[i];
    }

    public static void AndNot(Span<ulong> destination, ReadOnlySpan<ulong> excluded)
    {
        for (int i = 0; i < destination.Length; i++) destination[i] &= ~excluded[i];
    }

    public static bool Any(ReadOnlySpan<ulong> words)
    {
        foreach (ulong word in words)
        {
            if (word != 0) return true;
        }

        return false;
    }

    public static int PopCount(ReadOnlySpan<ulong> words)
    {
        int count = 0;
        foreach (ulong word in words) count += BitOperations.PopCount(word);
        return count;
    }

    public static bool IsSingleBit(ReadOnlySpan<ulong> words)
    {
        bool found = false;
        foreach (ulong word in words)
        {
            if (word == 0) continue;
            if (found || !BitOperations.IsPow2(word)) return false;
            found = true;
        }

        return found;
    }

    public static int FirstSet(ReadOnlySpan<ulong> words)
    {
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i] != 0) return i * BitsPerWord + BitOperations.TrailingZeroCount(words[i]);
        }

        return -1;
    }

    /// <summary>
    /// The one word holding <c>[firstBit, firstBit + width)</c>, masked to that range; <c>false</c>
    /// where the range spans more than one.
    /// </summary>
    /// <remarks>
    /// The fold asks about power-of-two ranges aligned to their own width, so every range up to a word
    /// wide takes this path and the loops below run only for a tiling wider than one — where that same
    /// alignment leaves them whole words to walk, which is what they assume.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryRangeWord(ReadOnlySpan<ulong> words, int firstBit, int width, out ulong masked)
    {
        int offset = firstBit % BitsPerWord;
        if (offset + width > BitsPerWord)
        {
            Debug.Assert(offset == 0 && width % BitsPerWord == 0, "a range past one word must be word-aligned");
            masked = 0;
            return false;
        }

        // a range as wide as the word shifts its one bit clean out, which C# wraps back to bit zero
        ulong mask = width == BitsPerWord ? ulong.MaxValue : ((1UL << width) - 1) << offset;
        masked = words[firstBit / BitsPerWord] & mask;
        return true;
    }

    public static int PopCountRange(ReadOnlySpan<ulong> words, int firstBit, int width)
    {
        if (TryRangeWord(words, firstBit, width, out ulong single)) return BitOperations.PopCount(single);

        int count = 0;
        int end = firstBit + width;
        for (int word = firstBit / BitsPerWord; word < end / BitsPerWord; word++) count += BitOperations.PopCount(words[word]);
        return count;
    }

    public static bool AnyInRange(ReadOnlySpan<ulong> words, int firstBit, int width)
    {
        if (TryRangeWord(words, firstBit, width, out ulong single)) return single != 0;

        int end = firstBit + width;
        for (int word = firstBit / BitsPerWord; word < end / BitsPerWord; word++)
        {
            if (words[word] != 0) return true;
        }

        return false;
    }

    public static int FirstSetInRange(ReadOnlySpan<ulong> words, int firstBit, int width)
    {
        if (TryRangeWord(words, firstBit, width, out ulong single))
        {
            return single == 0 ? -1 : firstBit / BitsPerWord * BitsPerWord + BitOperations.TrailingZeroCount(single);
        }

        int end = firstBit + width;
        for (int word = firstBit / BitsPerWord; word < end / BitsPerWord; word++)
        {
            if (words[word] != 0) return word * BitsPerWord + BitOperations.TrailingZeroCount(words[word]);
        }

        return -1;
    }
}
