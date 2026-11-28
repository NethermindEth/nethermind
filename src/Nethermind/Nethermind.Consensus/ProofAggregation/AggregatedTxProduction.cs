// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.ProofAggregation;

/// <summary>What one block production scope's builds may do with transactions known only from EIP-8518 proof wrappers.</summary>
public sealed class AggregatedTxProduction
{
    /// <summary>The parent and timestamp of a build whose aggregated transactions could not be proven in time, so that its
    /// rebuild leaves them out; cleared once a build of that block has run.</summary>
    /// <remarks>Keyed to the block, so a rebuild that never runs leaves no other block without them.</remarks>
    public (Hash256 Parent, ulong Timestamp)? ExcludeAggregatedFor { get; set; }

    /// <summary>Whether <paramref name="header"/>'s build leaves the aggregated transactions out.</summary>
    public bool Excludes(BlockHeader header) => ExcludeAggregatedFor == (header.ParentHash!, header.Timestamp);
}
