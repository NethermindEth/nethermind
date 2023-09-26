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
    public BitVector _data;
    public DArrayIndex _indexS1;
    public DArrayIndex? _indexS0;

    public int NumOnes => _indexS1.NumOnes;
    public int NumBits => _data.Length;

    public DArray(BitVector bv)
    {
        _data = new BitVector(bv);
        _indexS1 = new DArrayIndex(_data, true);
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

    public void EnableSelect0()
    {
        _indexS0 = new DArrayIndex(_data, false);
    }

    public int? Select0(int k)
    {
        return _indexS0?.Select(_data, k);
    }

    public int? Select1(int k)
    {
        return _indexS1.Select(_data, k);
    }
}
