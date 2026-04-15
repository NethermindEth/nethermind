// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

using Nethermind.StateComposition.Data;

namespace Nethermind.StateComposition.Rpc;

/// <summary>
/// JSON-RPC interface for state composition metrics.
/// Method prefix "statecomp_" registers under the "statecomp" namespace.
/// </summary>
[RpcModule(ModuleType.Statecomp)]
public interface IStateCompositionRpcModule : IRpcModule
{
    [JsonRpcMethod(IsImplemented = true,
        Description = "Get cached stats from last completed scan. " +
                      "Stats field is null if never scanned. " +
                      "LastScanMetadata carries scan freshness/completion info.",
        ExampleResponse = "{\"currentStats\":{\"accountsTotal\":12345678},\"blockNumber\":19000000,\"diffsSinceLastScan\":42,\"lastScanMetadata\":null}")]
    Task<ResultWrapper<CachedStatsResponse>> statecomp_getCachedStats();

    [JsonRpcMethod(IsImplemented = true,
        Description = "Get trie depth distribution with byte sizes. " +
                      "Returns cached data or fails if no scan has been run.",
        ExampleResponse = "{\"accountTrieLevels\":[{\"depth\":0,\"fullNodeCount\":1,\"shortNodeCount\":0,\"valueNodeCount\":0,\"totalSize\":532}],\"avgAccountPathDepth\":6.4,\"maxAccountDepth\":8}")]
    Task<ResultWrapper<TrieDepthDistribution>> statecomp_getTrieDistribution();

    [JsonRpcMethod(IsImplemented = true,
        Description = "Cancel the currently running scan, if any.",
        ExampleResponse = "true")]
    Task<ResultWrapper<bool>> statecomp_cancelScan();

    [JsonRpcMethod(IsImplemented = true,
        Description = "Inspect a single contract's storage trie structure. " +
                      "Returns null if the address has no storage.",
        ExampleResponse = "{\"owner\":\"0xabc...\",\"storageRoot\":\"0xdef...\",\"maxDepth\":5,\"totalNodes\":1234,\"valueNodes\":890,\"totalSize\":56789}")]
    Task<ResultWrapper<TopContractEntry?>> statecomp_inspectContract(Address? address);

    [JsonRpcMethod(IsImplemented = true,
        Description = "Get persisted stats snapshot at a specific block number. " +
                      "Returns null if no snapshot exists for that block.",
        ExampleResponse = "{\"stats\":{\"accountsTotal\":12345678},\"blockNumber\":19000000,\"stateRoot\":\"0xabc...\",\"diffsSinceBaseline\":0,\"scanBlockNumber\":19000000}")]
    Task<ResultWrapper<StateCompositionSnapshot?>> statecomp_getStatsAtBlock(long blockNumber);
}
