// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Threading;

namespace Nethermind.Core.Precompiles;

/// <summary>
/// Benchmark instrumentation: how often the precompile lookups run, to size the address-indexing change.
/// Reported per block by the PCACHE rows in <c>PrecompileCaches</c>. Testing branch only - never merge to master.
/// </summary>
public static class PrecompileLookupCounters
{
    /// <summary> Every <c>IReleaseSpec.IsPrecompile</c> call, from block processing, RPC and the tx pool alike. </summary>
    public static readonly StripedLong IsPrecompileCalls = new();

    /// <summary> The subset of those that answered yes, so the rest is what the address prefilter would skip. </summary>
    public static readonly StripedLong IsPrecompileHits = new();

    /// <summary> Precompile resolutions through <c>CodeInfoRepository</c>. </summary>
    public static readonly StripedLong CodeInfoLookups = new();

    /// <summary> Precompile resolutions through <c>PrecompileCachedCodeInfoRepository</c>. </summary>
    public static readonly StripedLong CachedCodeInfoLookups = new();
}
