// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Taiko.Rpc;

/// <summary>
/// Per-network thresholds and resolver for the <c>taikoAuth_last{Certain,}{L1Origin,BlockID}ByBatchID</c>
/// RPC family: results below the per-network threshold are reported as JSON null; equal or above
/// pass through. Devnet, Masaya, and unknown chain ids have no threshold (the resolver returns <c>null</c>).
/// </summary>
public static class BatchLookupThresholds
{
    /// <summary>Chain id of the Taiko Alethia mainnet.</summary>
    public const ulong TaikoMainnetChainId = 167_000;

    /// <summary>Chain id of the Taiko Hoodi testnet.</summary>
    public const ulong TaikoHoodiChainId = 167_013;

    /// <summary>
    /// Mainnet threshold: the first allowed block id (first Shasta block). Named after the
    /// strict-&lt; comparison semantics rather than the upstream "last Pacaya" label, which is
    /// inverted relative to the strict-&lt; operator.
    /// </summary>
    public const ulong TaikoMainnetBatchLookupThreshold = 4_990_434;

    /// <summary>Hoodi threshold: same semantics as <see cref="TaikoMainnetBatchLookupThreshold"/>.</summary>
    public const ulong TaikoHoodiBatchLookupThreshold = 3_951_005;

    /// <summary>
    /// Returns the threshold for <paramref name="chainId"/>, or <c>null</c> on networks where the
    /// gate does not apply (Devnet 167_001, Masaya 167_011, unknown).
    /// </summary>
    /// <param name="chainId">Chain id from <see cref="Nethermind.Core.Specs.ISpecProvider.ChainId"/>.</param>
    public static ulong? ResolveBatchLookupThreshold(ulong chainId) => chainId switch
    {
        TaikoMainnetChainId => TaikoMainnetBatchLookupThreshold,
        TaikoHoodiChainId => TaikoHoodiBatchLookupThreshold,
        _ => null,
    };
}
