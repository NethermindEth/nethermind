// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Merkleization;

namespace Nethermind.Era1;

// https://github.com/ethereum/portal-network-specs/blob/master/history/history-network.md#algorithms
internal class AccumulatorCalculator : IDisposable
{
    ArrayPoolList<ReadOnlyMemory<byte>> _roots;

    public AccumulatorCalculator()
    {
        _roots = new(EraWriter.MaxEra1Size);
    }

    public void Add(Hash256 headerHash, UInt256 td)
    {
        Merkleizer merkleizer = new Merkleizer((int)Merkle.NextPowerOfTwoExponent(2));
        merkleizer.Feed(headerHash.Bytes);
        merkleizer.Feed(td);
        _roots.Add(merkleizer.CalculateRoot().ToLittleEndian());
    }

    public ValueHash256 ComputeRoot()
    {
        Merkleizer merkleizer = new Merkleizer(0);
        merkleizer.Feed(_roots, EraWriter.MaxEra1Size);
        UInt256 root = merkleizer.CalculateRoot();
        return new ValueHash256(MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1)));
    }

    internal void Clear() => _roots.Clear();

    public void Dispose() => _roots.Dispose();
}
