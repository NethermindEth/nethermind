// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using Nethermind.Core.Crypto;

namespace Nethermind.Core;

public enum BlockHeaderProofType : byte
{
    BlockProofHistoricalHashesAccumulator = 0,
    BlockProofHistoricalRoots = 1,
    BlockProofHistoricalSummaries = 2
}

public record BlockHeaderProof
{
    public BlockHeaderProofType ProofType { get; init; }
    public ValueHash256[]? HashesAccumulator { get; init; }
    public ValueHash256[]? BeaconBlockProof { get; init; }
    public ValueHash256[]? ExecutionBlockProof { get; init; }
    public ValueHash256? BeaconBlockRoot { get; init; }
    public long? Slot { get; init; }
}
