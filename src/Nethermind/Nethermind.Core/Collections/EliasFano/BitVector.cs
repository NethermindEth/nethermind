// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Core.Collections.EliasFano;

public struct BitVector
{
    private const int WordLen = sizeof(ulong) * 8;

    public List<ulong> Words;
    public int Length { get; private set; }

    public BitVector()
    {
        Words = new List<ulong>();
        Length = 0;
    }

    public BitVector(int capacity)
    {
        Words = new List<ulong>(new ulong[WordsFor(capacity)]);
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
        for (int i = 0; i < words.Count; i++)
        {
            words[i] = word;
        }

        int shift = length % WordLen;
        if (shift != 0)
        {
            ulong mask = ((ulong)1 << shift) - 1;
            words[^1] &= mask;
        }

        return new BitVector { Words = words, Length = length };
    }

    public static BitVector FromBits(IEnumerable<bool> bits)
    {
        BitVector dArray = new BitVector();
        foreach (bool bit in bits) dArray.PushBit(bit);
        return dArray;
    }

    public void PushBit(bool bit)
    {
        int posInWord = Length % WordLen;
        if(posInWord == 0) Words.Add(Convert.ToUInt32(bit));
        else Words[^1] |= (Convert.ToUInt32(bit)) << posInWord;
        Length += 1;
    }

    public bool? GetBit(int pos)
    {
        if (pos < Length)
        {
            int block = pos / WordLen;
            int shift = pos % WordLen;
            return ((Words[block] >> shift) & 1) == 1;
        }

        return null;
    }

    public void SetBit(int pos, bool bit)
    {
        if (Length <= pos) throw new ArgumentException();

        int word = pos / WordLen;
        int posInWord = pos % WordLen;
        Words[word] &= ~((ulong)1 << posInWord);
        Words[word] |= (ulong)Convert.ToUInt32(bit) << posInWord;
    }

    public readonly ulong? GetBits(int pos, int len)
    {
        if (WordLen < len || Length < pos + len) return null;

        if (len == 0) return 0;

        (int block, int shift) = (pos / WordLen, pos % WordLen);

        ulong mask = len < WordLen ? ((ulong)1 << len) - 1 : ulong.MaxValue;

        ulong bits = shift + len <= WordLen
            ? Words[block] >> shift & mask
            : (Words[block] >> shift) | (Words[block + 1] << (WordLen - shift) & mask);
        return bits;
    }

    public void SetBits(int pos, ulong bits, int len)
    {
        if (WordLen < len || Length < pos + len) throw new ArgumentException();

        if (len == 0) return;

        ulong mask = len < WordLen ? ((ulong)1 << len) - 1 : ulong.MaxValue;

        bits &= mask;

        int word = pos / WordLen;
        int posInWord = pos % WordLen;

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
        {
            Words.Add(bits);
        }
        else
        {
            Words[^1] |= bits << posInWord;
            if (len > WordLen - posInWord)
            {
                Words.Add(bits >> WordLen - posInWord);
            }
        }

        Length += len;
    }

    private static int WordsFor(int n) => (n + (WordLen - 1)) / WordLen;
}
