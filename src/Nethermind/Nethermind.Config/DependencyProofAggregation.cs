// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Config;

/// <summary>Where this node aggregates the leanSPHINCS witnesses of EIP-8141 claims into EIP-8288 recursive proofs.</summary>
/// <remarks>Affects only what the node produces: every node accepts and verifies both raw and aggregated dependency proofs.</remarks>
public enum DependencyProofAggregation
{
    /// <summary>No aggregation: produced blocks carry raw witnesses (dependency proof version <c>0x00</c>).</summary>
    None,

    /// <summary>The block producer aggregates its block's leanSPHINCS witnesses into one recursive proof (version <c>0x01</c>)
    /// when the proof is ready and smaller than the raw witnesses it replaces, and keeps them raw otherwise.</summary>
    BuilderOnly,
}
