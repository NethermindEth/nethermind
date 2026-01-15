// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
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

    public BlockHeaderProofType? ProofType { get; set; }
    public ValueHash256[]? HashesAccumulator { get; set; }
    public ValueHash256[]? BeaconBlockProof { get; set; }
    public ValueHash256[]? ExecutionBlockProof { get; set; }
    public ValueHash256? BeaconBlockRoot { get; set; }
    public long? Slot { get; set; }

    public BlockHeaderProof(
        ValueHash256[] hashesAccumulator,
        BlockHeaderProofType proofType = BlockHeaderProofType.BlockProofHistoricalHashesAccumulator)
    {
        ProofType = proofType;
        HashesAccumulator = hashesAccumulator;
    }

    public BlockHeaderProof(
        ValueHash256[] beaconBlockProof, 
        ValueHash256[] executionBlockProof,
        ValueHash256 beaconBlockRoot,
        long slot, 
        BlockHeaderProofType proofType = BlockHeaderProofType.BlockProofHistoricalSummaries)
    {
        ProofType = proofType;
        BeaconBlockProof = beaconBlockProof;
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
