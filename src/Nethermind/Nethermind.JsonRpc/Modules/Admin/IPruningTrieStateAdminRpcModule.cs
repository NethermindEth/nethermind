// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.FullPruning;

namespace Nethermind.JsonRpc.Modules.Admin;

[RpcModule(ModuleType.Admin)]
public interface IPruningTrieStateAdminRpcModule : IRpcModule
{
    [JsonRpcMethod(Description = "Runs full pruning if enabled.",
        EdgeCaseHint = "",
        ExampleResponse = "\"Starting\"",
        IsImplemented = true)]
    ResultWrapper<PruningStatus> admin_prune();

    [JsonRpcMethod(Description = "Runs VerifyTrie.",
        EdgeCaseHint = "",
        ExampleResponse = "\"Starting\"",
        IsImplemented = true)]
    ResultWrapper<string> admin_verifyTrie(BlockParameter block);
}
