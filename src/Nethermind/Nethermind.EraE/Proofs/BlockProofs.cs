// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;

namespace Nethermind.EraE.Proofs;

[SszSerializable]
public struct HistoricalBatch
{
    [SszVector(8192)]
    public SszBytes32[] BlockRoots { get; set; }

    [SszVector(8192)]
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
    [SszVector(8192)]
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

[SszSerializable]
public struct BlockProofHistoricalHashesAccumulator
{
    [SszVector(15)]
    public SszBytes32[] Data { get; set; }

    public readonly ValueHash256[] HashesAccumulator
    {
        get
        {
            ValueHash256[] result = new ValueHash256[Data.Length];
            for (int i = 0; i < Data.Length; i++) result[i] = Data[i].Hash;
            return result;
        }
    }

    public static BlockProofHistoricalHashesAccumulator From(ReadOnlySpan<ValueHash256> hashesAccumulator)
    {
        SszBytes32[] data = new SszBytes32[hashesAccumulator.Length];
        for (int i = 0; i < hashesAccumulator.Length; i++) data[i] = SszBytes32.From(hashesAccumulator[i]);
        return new() { Data = data };
    }
}

[SszSerializable]
public struct BlockProofHistoricalRoots
{
    [SszVector(14)]
    public SszBytes32[] BeaconBlockProofData { get; set; }

    public readonly ValueHash256[] BeaconBlockProof
    {
        get
        {
            ValueHash256[] result = new ValueHash256[BeaconBlockProofData.Length];
            for (int i = 0; i < BeaconBlockProofData.Length; i++) result[i] = BeaconBlockProofData[i].Hash;
            return result;
        }
    }

    public SszBytes32 BeaconBlockRootData { get; set; }
    public readonly ValueHash256 BeaconBlockRoot => BeaconBlockRootData.Hash;

    [SszVector(11)]
    public SszBytes32[] ExecutionBlockProofData { get; set; }

    public readonly ValueHash256[] ExecutionBlockProof
    {
        get
        {
            ValueHash256[] result = new ValueHash256[ExecutionBlockProofData.Length];
            for (int i = 0; i < ExecutionBlockProofData.Length; i++) result[i] = ExecutionBlockProofData[i].Hash;
            return result;
        }
    }

    public long Slot { get; set; }

    public static BlockProofHistoricalRoots From(ReadOnlySpan<ValueHash256> beaconBlockProof, ValueHash256 beaconBlockRoot, ReadOnlySpan<ValueHash256> executionBlockProof, long slot)
    {
        SszBytes32[] beaconData = new SszBytes32[beaconBlockProof.Length];
        for (int i = 0; i < beaconBlockProof.Length; i++) beaconData[i] = SszBytes32.From(beaconBlockProof[i]);
        SszBytes32[] execData = new SszBytes32[executionBlockProof.Length];
        for (int i = 0; i < executionBlockProof.Length; i++) execData[i] = SszBytes32.From(executionBlockProof[i]);
        return new()
        {
            BeaconBlockProofData = beaconData,
            BeaconBlockRootData = SszBytes32.From(beaconBlockRoot),
            ExecutionBlockProofData = execData,
            Slot = slot
        };
    }
}

[SszSerializable]
public struct BlockProofHistoricalSummaries
{
    [SszVector(13)]
    public SszBytes32[] BeaconBlockProofData { get; set; }

    public readonly ValueHash256[] BeaconBlockProof
    {
        get
        {
            ValueHash256[] result = new ValueHash256[BeaconBlockProofData.Length];
            for (int i = 0; i < BeaconBlockProofData.Length; i++) result[i] = BeaconBlockProofData[i].Hash;
            return result;
        }
    }

    public SszBytes32 BeaconBlockRootData { get; set; }
    public readonly ValueHash256 BeaconBlockRoot => BeaconBlockRootData.Hash;

    [SszList(12)]
    public SszBytes32[] ExecutionBlockProofData { get; set; }

    public readonly ValueHash256[] ExecutionBlockProof
    {
        get
        {
            ValueHash256[] result = new ValueHash256[ExecutionBlockProofData.Length];
            for (int i = 0; i < ExecutionBlockProofData.Length; i++) result[i] = ExecutionBlockProofData[i].Hash;
            return result;
        }
    }

    public long Slot { get; set; }

    public static BlockProofHistoricalSummaries From(ReadOnlySpan<ValueHash256> beaconBlockProof, ValueHash256 beaconBlockRoot, ReadOnlySpan<ValueHash256> executionBlockProof, long slot)
    {
        SszBytes32[] beaconData = new SszBytes32[beaconBlockProof.Length];
        for (int i = 0; i < beaconBlockProof.Length; i++) beaconData[i] = SszBytes32.From(beaconBlockProof[i]);
        SszBytes32[] execData = new SszBytes32[executionBlockProof.Length];
        for (int i = 0; i < executionBlockProof.Length; i++) execData[i] = SszBytes32.From(executionBlockProof[i]);
        return new()
        {
            BeaconBlockProofData = beaconData,
            BeaconBlockRootData = SszBytes32.From(beaconBlockRoot),
            ExecutionBlockProofData = execData,
            Slot = slot
        };
    }
}
