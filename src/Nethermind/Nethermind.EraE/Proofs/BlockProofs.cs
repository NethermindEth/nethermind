// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;

namespace Nethermind.EraE.Proofs;

// Per the Ethereum beacon chain spec, HistoricalBatch.block_roots and .state_roots are
// fixed-length vectors of SLOTS_PER_HISTORICAL_ROOT = 8192 entries (one per slot in a period).
[SszContainer]
partial struct HistoricalBatch
{
    [SszVector(HistoricalRootConstants.SlotsPerHistoricalRoot)]
    public SszBytes32[] BlockRoots { get; set; }

    [SszVector(HistoricalRootConstants.SlotsPerHistoricalRoot)]
    public SszBytes32[] StateRoots { get; set; }

    public static HistoricalBatch From(ReadOnlySpan<ValueHash256> blockRoots, ReadOnlySpan<ValueHash256> stateRoots)
        => new()
        {
            BlockRoots = HistoricalRootConstants.ToSszVector(blockRoots, nameof(blockRoots)),
            StateRoots = HistoricalRootConstants.ToSszVector(stateRoots, nameof(stateRoots))
        };
}

[SszContainer]
partial struct ValueHash256Vector
{
    [SszVector(HistoricalRootConstants.SlotsPerHistoricalRoot)]
    public SszBytes32[] Data { get; set; }

    public static ValueHash256Vector From(ReadOnlySpan<ValueHash256> hashesAccumulator)
        => new() { Data = HistoricalRootConstants.ToSszVector(hashesAccumulator, nameof(hashesAccumulator)) };

    public readonly ValueHash256[] Hashes()
    {
        ValueHash256[] result = new ValueHash256[Data.Length];
        for (int i = 0; i < Data.Length; i++) result[i] = Data[i].Hash;
        return result;
    }
}

internal static class HistoricalRootConstants
{
    public const int SlotsPerHistoricalRoot = 8192;

    public static SszBytes32[] ToSszVector(ReadOnlySpan<ValueHash256> hashes, string argumentName)
    {
        if (hashes.Length > SlotsPerHistoricalRoot)
            throw new ArgumentException($"Historical root vectors cannot contain more than {SlotsPerHistoricalRoot} hashes.", argumentName);

        SszBytes32[] data = new SszBytes32[SlotsPerHistoricalRoot];
        for (int i = 0; i < hashes.Length; i++)
            data[i] = SszBytes32.From(hashes[i]);

        return data;
    }
}
