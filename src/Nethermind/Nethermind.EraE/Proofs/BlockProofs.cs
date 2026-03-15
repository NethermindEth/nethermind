// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;

namespace Nethermind.EraE.Proofs;

[SszSerializable]
public struct SSZBytes32
{
    [SszVector(32)]
    public byte[] Data { get; set; }

    public readonly ValueHash256 Hash => new(Data);

    public static SSZBytes32 From(ValueHash256 hash) => new() { Data = hash.ToByteArray() };
}

[SszSerializable]
public struct HistoricalBatch
{
    [SszVector(8192)]
    public SSZBytes32[] BlockRoots { get; set; }

    [SszVector(8192)]
    public SSZBytes32[] StateRoots { get; set; }

    public static HistoricalBatch From(ReadOnlySpan<ValueHash256> blockRoots, ReadOnlySpan<ValueHash256> stateRoots)
    {
        SSZBytes32[] blockRootsData = new SSZBytes32[blockRoots.Length];
        for (int i = 0; i < blockRoots.Length; i++) blockRootsData[i] = SSZBytes32.From(blockRoots[i]);
        SSZBytes32[] stateRootsData = new SSZBytes32[stateRoots.Length];
        for (int i = 0; i < stateRoots.Length; i++) stateRootsData[i] = SSZBytes32.From(stateRoots[i]);
        return new() { BlockRoots = blockRootsData, StateRoots = stateRootsData };
    }
}

[SszSerializable]
public struct ValueHash256Vector
{
    [SszVector(8192)]
    public SSZBytes32[] Data { get; set; }

    public static ValueHash256Vector From(ReadOnlySpan<ValueHash256> hashesAccumulator)
    {
        SSZBytes32[] data = new SSZBytes32[hashesAccumulator.Length];
        for (int i = 0; i < hashesAccumulator.Length; i++) data[i] = SSZBytes32.From(hashesAccumulator[i]);
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
    public SSZBytes32[] Data { get; set; }

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
        SSZBytes32[] data = new SSZBytes32[hashesAccumulator.Length];
        for (int i = 0; i < hashesAccumulator.Length; i++) data[i] = SSZBytes32.From(hashesAccumulator[i]);
        return new() { Data = data };
    }
}

[SszSerializable]
public struct BlockProofHistoricalRoots
{
    [SszVector(14)]
    public SSZBytes32[] BeaconBlockProofData { get; set; }

    public readonly ValueHash256[] BeaconBlockProof
    {
        get
        {
            ValueHash256[] result = new ValueHash256[BeaconBlockProofData.Length];
            for (int i = 0; i < BeaconBlockProofData.Length; i++) result[i] = BeaconBlockProofData[i].Hash;
            return result;
        }
    }

    public SSZBytes32 BeaconBlockRootData { get; set; }
    public readonly ValueHash256 BeaconBlockRoot => BeaconBlockRootData.Hash;

    [SszVector(11)]
    public SSZBytes32[] ExecutionBlockProofData { get; set; }

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
        SSZBytes32[] beaconData = new SSZBytes32[beaconBlockProof.Length];
        for (int i = 0; i < beaconBlockProof.Length; i++) beaconData[i] = SSZBytes32.From(beaconBlockProof[i]);
        SSZBytes32[] execData = new SSZBytes32[executionBlockProof.Length];
        for (int i = 0; i < executionBlockProof.Length; i++) execData[i] = SSZBytes32.From(executionBlockProof[i]);
        return new()
        {
            BeaconBlockProofData = beaconData,
            BeaconBlockRootData = SSZBytes32.From(beaconBlockRoot),
            ExecutionBlockProofData = execData,
            Slot = slot
        };
    }
}

[SszSerializable]
public struct BlockProofHistoricalSummaries
{
    [SszVector(13)]
    public SSZBytes32[] BeaconBlockProofData { get; set; }

    public readonly ValueHash256[] BeaconBlockProof
    {
        get
        {
            ValueHash256[] result = new ValueHash256[BeaconBlockProofData.Length];
            for (int i = 0; i < BeaconBlockProofData.Length; i++) result[i] = BeaconBlockProofData[i].Hash;
            return result;
        }
    }

    public SSZBytes32 BeaconBlockRootData { get; set; }
    public readonly ValueHash256 BeaconBlockRoot => BeaconBlockRootData.Hash;

    [SszList(12)]
    public SSZBytes32[] ExecutionBlockProofData { get; set; }

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
        SSZBytes32[] beaconData = new SSZBytes32[beaconBlockProof.Length];
        for (int i = 0; i < beaconBlockProof.Length; i++) beaconData[i] = SSZBytes32.From(beaconBlockProof[i]);
        SSZBytes32[] execData = new SSZBytes32[executionBlockProof.Length];
        for (int i = 0; i < executionBlockProof.Length; i++) execData[i] = SSZBytes32.From(executionBlockProof[i]);
        return new()
        {
            BeaconBlockProofData = beaconData,
            BeaconBlockRootData = SSZBytes32.From(beaconBlockRoot),
            ExecutionBlockProofData = execData,
            Slot = slot
        };
    }
}
