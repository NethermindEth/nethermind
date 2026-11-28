// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>What one block production scope's builds may do with transactions known only from EIP-8518 proof wrappers.</summary>
public sealed class AggregatedTxProduction
{
    /// <summary>Set when the last build's aggregated transactions could not be proven in time, so that its rebuild leaves
    /// them out; cleared once the rebuild has run.</summary>
    public bool ExcludeAggregated { get; set; }
}
