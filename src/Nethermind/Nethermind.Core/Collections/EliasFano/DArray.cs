// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Core.Collections.EliasFano;

public class DArray
{
    public readonly DArrayIndex _indexSet;
    public readonly DArrayIndex _indexUnSet;
    public BitVector _data;

    public DArray(BitVector bv)
    {
        _data = new BitVector(bv);
        _indexSet = new DArrayIndex(_data, true);
        _indexUnSet = new DArrayIndex(_data, false);
    }

    public DArray(BitVector bv, DArrayIndex indexUnSet, DArrayIndex indexSet)
    {
        _data = bv;
        _indexSet = indexSet;
        _indexUnSet = indexUnSet;
    }

    // Returns the number of bits set
    public int NumOnes => _indexSet.NumOnes;

    // Returns the number of bits stored
    public int NumBits => _data.Length;

    public static DArray FromBits(IEnumerable<bool> bits)
    {
        BitVector data = new();
        foreach (bool bit in bits) data.PushBit(bit);
        return new DArray(data);
    }

    /// <summary>
    ///     Searches the position of the `k`-th bit unset
    /// </summary>
    /// <param name="k"></param>
    /// <returns></returns>
    public int? SelectUnSet(int k)
    {
        return _indexUnSet.Select(_data, k);
    }

    /// <summary>
    ///     Searches the position of the `k`-th bit set
    /// </summary>
    /// <param name="k"></param>
    /// <returns></returns>
    public int? SelectSet(int k)
    {
        return _indexSet.Select(_data, k);
    }
}
