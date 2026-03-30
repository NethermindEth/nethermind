// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.StateComposition;

/// <summary>
/// JSON-RPC interface for state composition metrics.
/// Method prefix "statecomp_" registers under the "statecomp" namespace.
/// </summary>
public interface IStateCompositionRpcModule : IRpcModule
{
    [JsonRpcMethod(IsImplemented = true,
        Description = "Run full state composition scan at head block. " +
                      "Fails fast if scan already in progress.")]
    Task<ResultWrapper<StateCompositionStats>> statecomp_getStats();

    [JsonRpcMethod(IsImplemented = true,
        Description = "Get cached stats from last completed scan. " +
                      "Stats field is null if never scanned.")]
    Task<ResultWrapper<CachedStatsResponse>> statecomp_getCachedStats();

    [JsonRpcMethod(IsImplemented = true,
        Description = "Get scan metadata (freshness, completion status).")]
    Task<ResultWrapper<ScanMetadata?>> statecomp_getCacheMetadata();

    [JsonRpcMethod(IsImplemented = true,
        Description = "Get trie depth distribution with byte sizes. " +
                      "Returns cached data or triggers new scan.")]
    Task<ResultWrapper<TrieDepthDistribution>> statecomp_getTrieDistribution();
}
