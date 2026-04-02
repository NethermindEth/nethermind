// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
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
        Description = "Run full state composition scan at a given block (defaults to head). " +
                      "Fails fast if scan already in progress.")]
    Task<ResultWrapper<StateCompositionStats>> statecomp_getStats(
        BlockParameter? blockParameter = null);

    [JsonRpcMethod(IsImplemented = true,
        Description = "Get cached stats from a completed scan. " +
                      "Pass a block number to get a specific scan, or omit for most recent.")]
    Task<ResultWrapper<CachedStatsResponse>> statecomp_getCachedStats(
        BlockParameter? blockParameter = null);

    [JsonRpcMethod(IsImplemented = true,
        Description = "List metadata for all cached scans, ordered by block number ascending.")]
    Task<ResultWrapper<IReadOnlyList<ScanMetadata>>> statecomp_listScans();

    [JsonRpcMethod(IsImplemented = true,
        Description = "Get trie depth distribution for a cached scan. " +
                      "Pass a block number to get a specific scan, or omit for most recent.")]
    Task<ResultWrapper<TrieDepthDistribution>> statecomp_getTrieDistribution(
        BlockParameter? blockParameter = null);

    [JsonRpcMethod(IsImplemented = true,
        Description = "Cancel the currently running scan, if any.")]
    Task<ResultWrapper<bool>> statecomp_cancelScan();

    [JsonRpcMethod(IsImplemented = true,
        Description = "Inspect a single contract's storage trie structure at a given block (defaults to head). " +
                      "Returns null if the address has no storage.")]
    Task<ResultWrapper<TopContractEntry?>> statecomp_inspectContract(
        Address address, BlockParameter? blockParameter = null);
}
