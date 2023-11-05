// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Cortex.SimpleSerialize;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Era1;
//See https://github.com/ethereum/portal-network-specs/blob/master/history-network.md#algorithms
internal class AccumulatorCalculator
{
    List<SszComposite> _roots;

    public AccumulatorCalculator()
    {
        _roots = new(EraWriter.MaxEra1Size);
    }

    public void Add(Keccak headerHash, UInt256 td)
    {
        if (headerHash is null) throw new ArgumentNullException(nameof(headerHash));
        SszTree tree = new( new SszContainer(new[]
        {
            new SszBasicVector(headerHash.Bytes),
            new SszBasicVector(td.ToLittleEndian())
        }));
        _roots.Add(new SszBasicVector(tree.HashTreeRoot()));
    }

    public ReadOnlySpan<byte> ComputeRoot()
    {
        return new SszTree(new SszList(_roots, EraWriter.MaxEra1Size)).HashTreeRoot();
    }

    internal void Clear() => _roots.Clear();
}
