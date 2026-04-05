// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;

namespace Nethermind.EraE.Proofs;

// Per the Ethereum beacon chain spec, HistoricalBatch.block_roots and .state_roots are
// fixed-length vectors of SLOTS_PER_HISTORICAL_ROOT = 8192 entries (one per slot in a period).
[SszSerializable]
public struct HistoricalBatch
{
    [SszVector(8192)] // SLOTS_PER_HISTORICAL_ROOT
    public SszBytes32[] BlockRoots { get; set; }

    [SszVector(8192)] // SLOTS_PER_HISTORICAL_ROOT
    public SszBytes32[] StateRoots { get; set; }

    public static HistoricalBatch From(ReadOnlySpan<ValueHash256> blockRoots, ReadOnlySpan<ValueHash256> stateRoots)
    {
        SszBytes32[] blockRootsData = new SszBytes32[blockRoots.Length];
        for (int i = 0; i < blockRoots.Length; i++) blockRootsData[i] = SszBytes32.From(blockRoots[i]);
        SszBytes32[] stateRootsData = new SszBytes32[stateRoots.Length];
        for (int i = 0; i < stateRoots.Length; i++) stateRootsData[i] = SszBytes32.From(stateRoots[i]);
        return new() { BlockRoots = blockRootsData, StateRoots = stateRootsData };
    }
}

[SszSerializable]
public struct ValueHash256Vector
{
    [SszVector(8192)] // SLOTS_PER_HISTORICAL_ROOT
    public SszBytes32[] Data { get; set; }

    public static ValueHash256Vector From(ReadOnlySpan<ValueHash256> hashesAccumulator)
    {
        SszBytes32[] data = new SszBytes32[hashesAccumulator.Length];
        for (int i = 0; i < hashesAccumulator.Length; i++) data[i] = SszBytes32.From(hashesAccumulator[i]);
        return new() { Data = data };
    }

    public readonly ValueHash256[] Hashes()
    {
        ValueHash256[] result = new ValueHash256[Data.Length];
        for (int i = 0; i < Data.Length; i++) result[i] = Data[i].Hash;
        return result;
    }
}

