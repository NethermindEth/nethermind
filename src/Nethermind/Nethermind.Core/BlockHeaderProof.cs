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
    public Root[]? HashesAccumulator { get; set; }
    public Root[]? BeaconBlockProof { get; set; }
    public Root[]? ExecutionBlockProof { get; set; }
    public Root? BeaconBlockRoot { get; set; }
    public long? Slot { get; set; }

    public BlockHeaderProof(
        Root[] hashesAccumulator,
        BlockHeaderProofType proofType = BlockHeaderProofType.BlockProofHistoricalHashesAccumulator)
    {
        ProofType = proofType;
        HashesAccumulator = hashesAccumulator;
    }

    public BlockHeaderProof(
        Root[] beaconBlockProof, 
        Root[] executionBlockProof,
        Root beaconBlockRoot,
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
