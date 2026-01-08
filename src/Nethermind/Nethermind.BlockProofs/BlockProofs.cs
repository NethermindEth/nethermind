// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BlockProofs;

[SszSerializable]
public struct SSZBytes32
{
    [SszVector(32)]
    public byte[] Data { get; set; }

    public ValueHash256 Hash => new(Data);

    public static SSZBytes32 From(ValueHash256 hash)
    {
        return new SSZBytes32 { Data = hash.ToByteArray() };
    }
}


[SszSerializable]
public struct HistoricalBatch {
    [SszVector(8192)]
    public SSZBytes32[] BlockRoots { get; set; }

    [SszVector(8192)]
    public SSZBytes32[] StateRoots { get; set; }

    public static HistoricalBatch From(ValueHash256[] blockRoots, ValueHash256[] stateRoots) {
        return new HistoricalBatch {
            BlockRoots = blockRoots.Select(SSZBytes32.From).ToArray(),
            StateRoots = stateRoots.Select(SSZBytes32.From).ToArray()
        };
    }
}

[SszSerializable]
public struct ValueHash256Vector
{
    [SszVector(8192)]
    public SSZBytes32[] Data { get; set; }

    public static ValueHash256Vector From(ValueHash256[] hashesAccumulator)
    {
        return new ValueHash256Vector { Data = hashesAccumulator.Select(SSZBytes32.From).ToArray() };
    }

    public ValueHash256[] Hashes() {
        return Data.Select(x => x.Hash).ToArray();
    }
}

[SszSerializable]
public struct BlockProofHistoricalHashesAccumulator
{
    [SszVector(15)]
    public SSZBytes32[] Data { get; set; }

    // method to convert to Hash256[]
    public ValueHash256[] HashesAccumulator => Data.Select(x => x.Hash).ToArray();

    public static BlockProofHistoricalHashesAccumulator From(ValueHash256[] hashesAccumulator)
    {
        return new BlockProofHistoricalHashesAccumulator { Data = hashesAccumulator.Select(SSZBytes32.From).ToArray() };
    }
}


[SszSerializable]
public struct BlockProofHistoricalRoots
{
    [SszVector(14)]
    public SSZBytes32[] BeaconBlockProofData { get; set; }
    public ValueHash256[] BeaconBlockProof => BeaconBlockProofData.Select(x => x.Hash).ToArray();

    public SSZBytes32 BeaconBlockRootData { get; set; }
    public ValueHash256 BeaconBlockRoot => BeaconBlockRootData.Hash;

    [SszVector(11)]
    public SSZBytes32[] ExecutionBlockProofData { get; set; }
    public ValueHash256[] ExecutionBlockProof => ExecutionBlockProofData.Select(x => x.Hash).ToArray();

    public long Slot { get; set; }

    public static BlockProofHistoricalRoots From(ValueHash256[] beaconBlockProof, ValueHash256 beaconBlockRoot, ValueHash256[] executionBlockProof, long slot)
    {
        return new BlockProofHistoricalRoots {
            BeaconBlockProofData = beaconBlockProof.Select(SSZBytes32.From).ToArray(),
            BeaconBlockRootData = SSZBytes32.From(beaconBlockRoot),
            ExecutionBlockProofData = executionBlockProof.Select(SSZBytes32.From).ToArray(),
            Slot = slot
        };
    }
}

[SszSerializable]
public struct BlockProofHistoricalSummaries
{
    [SszVector(13)]
    public SSZBytes32[] BeaconBlockProofData { get; set; }
    public ValueHash256[] BeaconBlockProof => BeaconBlockProofData.Select(x => x.Hash).ToArray();

    public SSZBytes32 BeaconBlockRootData { get; set; }
    public ValueHash256 BeaconBlockRoot => BeaconBlockRootData.Hash;

    [SszList(12)]
    public SSZBytes32[] ExecutionBlockProofData { get; set; }
    public ValueHash256[] ExecutionBlockProof => ExecutionBlockProofData.Select(x => x.Hash).ToArray();

    public long Slot { get; set; }

    public static BlockProofHistoricalSummaries From(ValueHash256[] beaconBlockProof, ValueHash256 beaconBlockRoot, ValueHash256[] executionBlockProof, long slot)
    {
        return new BlockProofHistoricalSummaries {
            BeaconBlockProofData = beaconBlockProof.Select(SSZBytes32.From).ToArray(),
            BeaconBlockRootData = SSZBytes32.From(beaconBlockRoot),
            ExecutionBlockProofData = executionBlockProof.Select(SSZBytes32.From).ToArray(),
            Slot = slot
        };
    }
}
