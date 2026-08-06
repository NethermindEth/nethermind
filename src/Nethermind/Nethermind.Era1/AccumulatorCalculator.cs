// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Era1;

// https://github.com/ethereum/portal-network-specs/blob/master/history/history-network.md#algorithms
public class AccumulatorCalculator : IDisposable
{
    private readonly ArrayPoolList<ValueHash256> _roots = new(EraWriter.MaxEra1Size);

    public void Add(Hash256 headerHash, UInt256 td)
    {
        HeaderRecord.Merkleize(new()
        {
            BlockHash = headerHash,
            TotalDifficulty = td
        }, out UInt256 root);
        _roots.Add(ToValueHash256(root));
    }

    public ValueHash256 ComputeRoot()
    {
        HeaderRecordRoots.Merkleize(new() { Roots = _roots }, out UInt256 root);
        return ToValueHash256(root);
    }

    private static ValueHash256 ToValueHash256(UInt256 root)
    {
        Span<byte> bytes = stackalloc byte[ValueHash256.MemorySize];
        root.ToLittleEndian(bytes);
        return new ValueHash256(bytes);
    }

    internal void Clear() => _roots.Clear();

    public void Dispose() => _roots.Dispose();
}

[SszContainer]
internal partial struct HeaderRecord
{
    public Hash256 BlockHash { get; set; }
    public UInt256 TotalDifficulty { get; set; }
}

[SszContainer(isCollectionItself: true)]
internal partial struct HeaderRecordRoots
{
    [SszList(EraWriter.MaxEra1Size)]
    public ArrayPoolList<ValueHash256> Roots { get; set; }
}
