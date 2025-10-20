// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Core;

public enum BlockHeaderProofType: byte {
    BlockProofHistoricalHashesAccumulator = 0,
    BlockProofHistoricalRoots = 1,
    BlockProofHistoricalSummaries = 2
}

public class BlockHeaderProof
{
    internal BlockHeaderProof() { }

    public BlockHeaderProofType ProofType { get; set; }
    public ArrayPool<Hash256> HashesAccumulator { get; set; }
    public ArrayPool<Hash256> BeaconBlockProofHistoricalRoots { get; set; }
    public ArrayPool<Hash256> BeaconBlockProofHistoricalSummaries { get; set; }
    public ArrayPool<Hash256> ExecutionBlockProof { get; set; }
    public Hash256 BeaconBlockRoot { get; set; }
    public long Slot { get; set; }

    public BlockHeaderProof(
        ArrayPool<Hash256> hashesAccumulator,
        BlockHeaderProofType proofType = BlockHeaderProofType.BlockProofHistoricalHashesAccumulator)
    {
        ProofType = proofType;
        HashesAccumulator = hashesAccumulator;
    }

    public BlockHeaderProof(
        ArrayPool<Hash256> beaconBlockProofHistoricalRoots, 
        ArrayPool<Hash256> executionBlockProof,
        Hash256 beaconBlockRoot,
        long slot,
        BlockHeaderProofType proofType = BlockHeaderProofType.BlockProofHistoricalRoots)
    {
        ProofType = proofType;
        BeaconBlockProofHistoricalRoots = beaconBlockProofHistoricalRoots;
        ExecutionBlockProof = executionBlockProof;
        BeaconBlockRoot = beaconBlockRoot;
        Slot = slot;
    }

    public BlockHeaderProof(
        ArrayPool<Hash256> beaconBlockProofHistoricalSummaries, 
        ArrayPool<Hash256> executionBlockProof,
        Hash256 beaconBlockRoot,
        long slot, 
        BlockHeaderProofType proofType = BlockHeaderProofType.BlockProofHistoricalSummaries)
    {
        ProofType = proofType;
        BeaconBlockProofHistoricalSummaries = beaconBlockProofHistoricalSummaries;
        ExecutionBlockProof = executionBlockProof;
        BeaconBlockRoot = beaconBlockRoot;
        Slot = slot;
    }

    public string ToString(string indent)
    {
        return "";
    }

    public override string ToString() => ToString(string.Empty);
}
