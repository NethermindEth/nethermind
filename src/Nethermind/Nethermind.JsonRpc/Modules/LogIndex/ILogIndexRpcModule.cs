// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.JsonRpc.Modules.LogIndex;

[RpcModule(ModuleType.LogIndex)]
public interface ILogIndexRpcModule : IRpcModule
{
    [JsonRpcMethod(Description = "Retrieves log index block number for the given filter.", IsImplemented = true, IsSharable = true)]
    ResultWrapper<int[]> logIndex_blockNumbers(
        [JsonRpcParameter] Filter filter
    );

    [JsonRpcMethod(Description = "Retrieves log index status.", IsImplemented = true, IsSharable = true)]
    ResultWrapper<LogIndexStatus> logIndex_status();

    // TODO: add compaction RPC
}
