// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections.EliasFano;

namespace Nethermind.Core.Collections.EliasFano;

public class DArray
{
    public readonly BitVector _data;
    public readonly DArrayIndex _indexS1;
    public readonly DArrayIndex _indexS0;

    public DArray(BitVector bv)
    {
        _data = new BitVector(bv);
        _indexS1 = new DArrayIndex(bv, true);
        _indexS0 = new DArrayIndex(bv, false);
    }

    public DArray(BitVector bv, DArrayIndex indexS0, DArrayIndex indexS1)
    {
        _data = bv;
        _indexS1 = indexS1;
        _indexS0 = indexS0;
    }

    public static DArray FromBits(IEnumerable<bool> bits)
    {
        BitVector data = new ();
        foreach (bool bit in bits) data.PushBit(bit);
        return new DArray(data);
    }

    public int? Select0(int k)
    {
        return _indexS0.Select(_data, k);
    }

    public int? Select1(int k)
    {
        return _indexS1.Select(_data, k);
    }
}

public class DArrayIndex
{
    const int BlockLen = 1024;
    const int MaxInBlockDistance = 1 << 16;
    const int SubBlockLen = 32;

    public readonly int[] _blockInventory;
    public readonly ushort[] _subBlockInventory;
    public readonly int[] _overflowPositions;
    public int NumPositions { get; }
    public bool OverOne { get; }

    public DArrayIndex(int[] blockInventory, ushort[] subBlockInventory, int[] overflowPositions, int numPositions, bool overOne)
    {
        _blockInventory = blockInventory;
        _subBlockInventory = subBlockInventory;
        _overflowPositions = overflowPositions;
        NumPositions = numPositions;
        OverOne = overOne;
    }

    public DArrayIndex(BitVector bv, bool overOne)
    {
        OverOne = overOne;
        List<int> curBlockPositions = new();
        List<int> blockInventory = new();
        List<ushort> subBlockInventory = new();
        List<int> overflowPositions = new();
        int numPositions = 0;

        for (int wordIndex = 0; wordIndex < bv.Words.Count; wordIndex++)
        {
            int currPos = wordIndex * 64;
            ulong currWord = overOne ? GetWordOverOne(bv, wordIndex) : GetWordOverZero(bv, wordIndex);

            while (true)
            {
                int l = NumberOfTrailingZeros(currWord);
                currPos += l;
                currWord >>= l;
                if (currPos >= bv.Length) break;

                curBlockPositions.Add(currPos);
                if (curBlockPositions.Count == BlockLen)
                {
                    FlushCurBlock(curBlockPositions, blockInventory, subBlockInventory, overflowPositions);
                }

                currWord >>= 1;
                currPos += 1;
                numPositions += 1;
            }
        }

        if (curBlockPositions.Count > 0)
            FlushCurBlock(curBlockPositions, blockInventory, subBlockInventory, overflowPositions);

        _blockInventory = blockInventory.ToArray();
        _subBlockInventory = subBlockInventory.ToArray();
        _overflowPositions = overflowPositions.ToArray();
        NumPositions = numPositions;
    }

    public int? Select(BitVector bv, int k)
    {
        if (NumPositions <= k) return null;

        int block = k / BlockLen;
        int blockPos = _blockInventory[block];

        if (blockPos < 0)
        {
            int overflowPos = -blockPos - 1;
            return _overflowPositions[overflowPos + (k % BlockLen)];
        }

        int subBlock = k / SubBlockLen;
        int remainder = k % SubBlockLen;

        int startPos = blockPos + _subBlockInventory[subBlock];

        int sel;
        if (remainder == 0)
        {
            sel = startPos;
        }
        else
        {
            int wordIdx = startPos / 64;
            int wordShift = startPos % 64;
            ulong wordE = OverOne ? GetWordOverOne(bv, wordIdx) : GetWordOverZero(bv, wordIdx);
            ulong word = wordE & (ulong.MaxValue << wordShift);

            while (true)
            {
                int popCount = BitOperations.PopCount(word);
                if (remainder < popCount) break;
                remainder -= popCount;
                wordIdx += 1;
                word = OverOne ? GetWordOverOne(bv, wordIdx) : GetWordOverZero(bv, wordIdx);
            }

            sel = 64 * wordIdx + SelectInWord(word, remainder)!.Value;
        }

        return sel;
    }

    private static void FlushCurBlock(
        List<int> curBlockPositions,
        ICollection<int> blockInventory,
        ICollection<ushort> subBlockInventory,
        List<int> overflowPositions
    )
    {
        int first = curBlockPositions[0];
        int last = curBlockPositions[^1];

        if (last - first < MaxInBlockDistance)
        {
            blockInventory.Add(first);
            for (int i = 0; i < curBlockPositions.Count; i += SubBlockLen)
            {
                subBlockInventory.Add((ushort)(curBlockPositions[i] - first));
            }
        }
        else
        {
            blockInventory.Add(-(overflowPositions.Count + 1));
            overflowPositions.AddRange(curBlockPositions);

            for (int i = 0; i < curBlockPositions.Count; i += SubBlockLen)
            {
                subBlockInventory.Add(ushort.MaxValue);
            }
        }
        curBlockPositions.Clear();
    }

    private static ulong GetWordOverZero(BitVector bv, int wordIdx) => ~bv.Words[wordIdx];

    private static ulong GetWordOverOne(BitVector bv, int wordIdx) => bv.Words[wordIdx];

    private static int NumberOfTrailingZeros(ulong i) => BitOperations.TrailingZeroCount(i);

    private static int? SelectInWord(ulong x, int k)
    {
        int index = 0;
        while (x != 0)
        {
            if ((x & 1) != 0) k--;
            if (k == -1) return index;
            index++;
            x >>= 1;
        }
        return -1;
    }
}
