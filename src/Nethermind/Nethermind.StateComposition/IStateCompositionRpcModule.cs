// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.StateComposition;

/// <summary>
/// JSON-RPC interface for state composition metrics.
/// Method prefix "statecomp_" registers under the "statecomp" namespace.
/// </summary>
[RpcModule(ModuleType.Statecomp)]
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
                      "Returns cached data or fails if no scan has been run.")]
    Task<ResultWrapper<TrieDepthDistribution>> statecomp_getTrieDistribution();

    [JsonRpcMethod(IsImplemented = true,
        Description = "Cancel the currently running scan, if any.")]
    Task<ResultWrapper<bool>> statecomp_cancelScan();

    [JsonRpcMethod(IsImplemented = true,
        Description = "Inspect a single contract's storage trie structure. " +
                      "Returns null if the address has no storage.")]
    Task<ResultWrapper<TopContractEntry?>> statecomp_inspectContract(Address address);
}
