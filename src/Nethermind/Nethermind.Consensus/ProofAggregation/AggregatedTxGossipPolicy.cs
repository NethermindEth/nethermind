// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.TxPool;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>Keeps transactions known only from an EIP-8518 proof wrapper out of pooled-transaction announcements.</summary>
/// <remarks>Their aggregated witnesses cannot be reconstructed, so they have no complete pooled form to serve; they travel
/// in proof wrappers only.</remarks>
public sealed class AggregatedTxGossipPolicy : ITxGossipPolicy
{
    public bool ShouldGossipTransaction(Transaction tx) => !FrameTxClaimVerifier.HasAggregatedWitness(tx);
}
