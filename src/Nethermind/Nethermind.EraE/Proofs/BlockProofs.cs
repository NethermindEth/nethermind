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
    [SszVector(8192)] // SLOTS_PER_HISTORICAL_ROOT
    public ValueHash256[] BlockRoots { get; set; }

    [SszVector(8192)] // SLOTS_PER_HISTORICAL_ROOT
    public ValueHash256[] StateRoots { get; set; }

    public static HistoricalBatch From(ReadOnlySpan<ValueHash256> blockRoots, ReadOnlySpan<ValueHash256> stateRoots) =>
        new() { BlockRoots = blockRoots.ToArray(), StateRoots = stateRoots.ToArray() };
}

[SszContainer]
partial struct ValueHash256Vector
{
    [SszVector(8192)] // SLOTS_PER_HISTORICAL_ROOT
    public ValueHash256[] Data { get; set; }

    public static ValueHash256Vector From(ReadOnlySpan<ValueHash256> hashesAccumulator) =>
        new() { Data = hashesAccumulator.ToArray() };

    public readonly ValueHash256[] Hashes() => Data.ToArray();
}
