// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>Thrown when a block being produced holds transactions known only from EIP-8518 proof wrappers whose combined
/// proof is not ready, so the block has no valid dependency proof.</summary>
/// <remarks>The block producer rebuilds the block once without such transactions while the proof is prepared.</remarks>
internal sealed class AggregatedProofNotReadyException()
    : Exception("The EIP-8288 proof of the block's aggregated transactions is not ready.");
