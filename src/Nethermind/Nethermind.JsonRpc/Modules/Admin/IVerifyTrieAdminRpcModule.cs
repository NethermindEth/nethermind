// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Find;

namespace Nethermind.JsonRpc.Modules.Admin;

[RpcModule(ModuleType.Admin)]
public interface IVerifyTrieAdminRpcModule : IRpcModule
{
    [JsonRpcMethod(Description = "Runs VerifyTrie.",
        EdgeCaseHint = "",
        ExampleResponse = "\"Starting\"",
        IsImplemented = true)]
    ResultWrapper<string> admin_verifyTrie(BlockParameter block);
}
