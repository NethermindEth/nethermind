// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Clear(Span<ulong> words, int bit) =>
        words[bit / BitsPerWord] &= ~(1UL << (bit % BitsPerWord));

    public static void Copy(ReadOnlySpan<ulong> source, Span<ulong> destination) => source.CopyTo(destination);

    public static void Or(Span<ulong> destination, ReadOnlySpan<ulong> source)
    {
        for (int i = 0; i < destination.Length; i++) destination[i] |= source[i];
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

    public static int PopCountRange(ReadOnlySpan<ulong> words, int firstBit, int width)
    {
        int count = 0;
        int end = firstBit + width;
        int word = firstBit / BitsPerWord;
        int offset = firstBit % BitsPerWord;

        while (firstBit < end)
        {
            int bits = Math.Min(BitsPerWord - offset, end - firstBit);
            ulong mask = ulong.MaxValue >> (BitsPerWord - bits) << offset;
            count += BitOperations.PopCount(words[word] & mask);
            firstBit += bits;
            word++;
            offset = 0;
        }

        return count;
    }

    public static bool AnyInRange(ReadOnlySpan<ulong> words, int firstBit, int width) =>
        PopCountRange(words, firstBit, width) != 0;

    public static int FirstSetInRange(ReadOnlySpan<ulong> words, int firstBit, int width)
    {
        int end = firstBit + width;
        for (int bit = firstBit; bit < end; bit++)
        {
            if (Contains(words, bit)) return bit;
        }

        return -1;
    }
}
