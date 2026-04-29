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
        Description = "Get current state composition: cumulative stats, trie depth distribution, and scan metadata. " +
                      "Check LastScanMetadata.IsComplete to confirm a scan has run; " +
                      "stats are zero-valued before the first scan completes.",
        ExampleResponse = "{\"currentStats\":{\"accountsTotal\":12345678},\"trieDistribution\":{\"accountTrieLevels\":[...]},\"blockNumber\":19000000,\"diffsSinceLastScan\":42,\"lastScanMetadata\":{\"isComplete\":true}}")]
    Task<ResultWrapper<StateCompositionReport>> statecomp_get();

    [JsonRpcMethod(IsImplemented = true,
        Description = "Cancel the currently running scan, if any. " +
                      "Returns true if a scan was active and cancellation was signalled; false if no scan was running.",
        ExampleResponse = "true")]
    Task<ResultWrapper<bool>> statecomp_cancelScan();

    [JsonRpcMethod(IsImplemented = true,
        Description = "Inspect a single contract's storage trie structure. " +
                      "Returns null if the address has no storage.",
        ExampleResponse = "{\"owner\":\"0xabc...\",\"storageRoot\":\"0xdef...\",\"maxDepth\":5,\"totalNodes\":1234,\"valueNodes\":890,\"totalSize\":56789}")]
    Task<ResultWrapper<TopContractEntry?>> statecomp_inspectContract(Address? address);
}
