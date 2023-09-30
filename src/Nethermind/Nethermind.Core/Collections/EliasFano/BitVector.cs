// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Nethermind.Core.Collections.EliasFano;

public struct BitVector
{
    public const int WordLen = sizeof(ulong) * 8;

    public readonly List<ulong> Words;
    public int Length { get; private set; }

    public BitVector()
    {
        Words = new List<ulong>();
        Length = 0;
    }

    public BitVector(int capacity)
    {
        int neededWords = WordsFor(capacity);
        Words = new List<ulong>(new ulong[neededWords]);
        Length = capacity;
    }

    public BitVector(BitVector bv)
    {
        Words = new List<ulong>(bv.Words);
        Length = bv.Length;
    }

    public BitVector(List<ulong> words, int length)
    {
        Words = words;
        Length = length;
    }

    public static BitVector WithCapacity(int length)
    {
        return new BitVector(new List<ulong>(WordsFor(length)), 0);
    }

    public static BitVector FromBit(bool bit, int length)
    {
        ulong word = bit ? ulong.MaxValue : 0;
        List<ulong> words = new(new ulong[WordsFor(length)]);
        for (int i = 0; i < words.Count; i++) words[i] = word;

        int shift = length % WordLen;
        if (shift != 0)
        {
            ulong mask = ((ulong)1 << shift) - 1;
            words[^1] &= mask;
        }

        return new BitVector(words, length);
    }

    public static BitVector FromBits(IEnumerable<bool> bits)
    {
        BitVector dArray = new();
        foreach (bool bit in bits) dArray.PushBit(bit);
        return dArray;
    }

    public void PushBit(bool bit)
    {
        int posInWord = Length % WordLen;
        if (bit)
        {
            if (posInWord == 0) Words.Add(1);
            else Words[^1] |= (ulong)1 << posInWord;
        }
        else
        {
            if (posInWord == 0) Words.Add(0);
        }

        Length += 1;
    }

    public bool? GetBit(int k)
    {
        if (k < Length)
        {
            int block = Math.DivRem(k, WordLen, out int shift);
            return ((Words[block] >> shift) & 1) == 1;
        }

        return null;
    }

    public void SetBit(int k, bool bit)
    {
        if (k < 0 || k >= Length)
            throw new ArgumentOutOfRangeException(nameof(k), "Invalid position");

        int word = Math.DivRem(k, WordLen, out int posInWord);
        Words[word] &= ~((ulong)1 << posInWord);
        if (bit) Words[word] |= (ulong)1 << posInWord;
    }

    public readonly ulong? GetBits(int k, int len)
    {
        if (WordLen < len || Length < k + len) return null;

        if (len == 0) return 0;

        int block = Math.DivRem(k, WordLen, out int shift);

        ulong mask = len < WordLen ? ((ulong)1 << len) - 1 : ulong.MaxValue;

        ulong bits = shift + len <= WordLen
            ? (Words[block] >> shift) & mask
            : (Words[block] >> shift) | ((Words[block + 1] << (WordLen - shift)) & mask);
        return bits;
    }

    public void SetBits(int k, ulong bits, int len)
    {
        if (WordLen < len || Length < k + len) throw new ArgumentException();

        if (len == 0) return;

        ulong mask = len < WordLen ? ((ulong)1 << len) - 1 : ulong.MaxValue;

        bits &= mask;

        int word = Math.DivRem(k, WordLen, out int posInWord);

        Words[word] &= ~(mask << posInWord);
        Words[word] |= bits << posInWord;

        int stored = WordLen - posInWord;

        if (stored < len)
        {
            Words[word + 1] &= ~(mask >> stored);
            Words[word + 1] |= bits >> stored;
        }
    }

    public void PushBits(ulong bits, int len)
    {
        if (WordLen < len) throw new ArgumentException();
        if (len == 0) return;

        ulong mask = len < WordLen ? ((ulong)1 << len) - 1 : ulong.MaxValue;

        bits &= mask;

        int posInWord = Length % WordLen;
        if (posInWord == 0)
            Words.Add(bits);
        else
        {
            Words[^1] |= bits << posInWord;
            if (len > WordLen - posInWord) Words.Add(bits >> (WordLen - posInWord));
        }

        Length += len;
    }

    /// <summary>
    ///     Returns the largest bit position such that it is less than or equal to pos` and the bit is set
    /// </summary>
    /// <param name="k">bit position</param>
    /// <returns></returns>
    public int? PredecessorSet(int k)
    {
        if (Length <= k) return null;

        int block = Math.DivRem(k, WordLen, out int temp);
        int shift = WordLen - temp - 1;
        ulong word = (Words[block] << shift) >> shift;
        while (true)
        {
            if (word == 0)
            {
                if (block == 0) return null;
            }
            else
            {
                int msb = 63 - BitOperations.LeadingZeroCount(word);
                return (block * WordLen) + msb;
            }

            block -= 1;
            word = Words[block];
        }
    }

    /// <summary>
    ///     Returns the largest bit position such that it is less than or equal to pos` and the bit is unset
    /// </summary>
    /// <param name="k">bit position</param>
    /// <returns></returns>
    public int? PredecessorUnSet(int k)
    {
        if (Length <= k) return null;

        int block = Math.DivRem(k, WordLen, out int temp);
        int shift = WordLen - temp - 1;
        ulong word = (~Words[block] << shift) >> shift;
        while (true)
        {
            if (word == 0)
            {
                if (block == 0) return null;
            }
            else
            {
                int msb = 63 - BitOperations.LeadingZeroCount(word);
                return (block * WordLen) + msb;
            }

            block -= 1;
            word = ~Words[block];
        }
    }

    public ulong? GetWord64(int k)
    {
        if (Length <= k) return null;


        int block = Math.DivRem(k, WordLen, out int shift);

        ulong word = Words[block] >> shift;

        if (shift != 0 && block + 1 < Words.Count) word |= Words[block + 1] << (64 - shift);

        return word;
    }

    private static int WordsFor(int n)
    {
        return (n + (WordLen - 1)) / WordLen;
    }
}
