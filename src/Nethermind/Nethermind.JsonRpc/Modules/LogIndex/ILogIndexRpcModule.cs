// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.JsonRpc.Modules.LogIndex;

[RpcModule(ModuleType.LogIndex)]
public interface ILogIndexRpcModule : IRpcModule
{
    [JsonRpcMethod(Description = "Retrieves log index keys (and optionally values) for the given address/topic.", IsImplemented = true, IsSharable = true)]
    ResultWrapper<Dictionary<byte[], int[]>> logIndex_keys(
        [JsonRpcParameter] LogIndexKeysRequest request
    );

    [JsonRpcMethod(Description = "Retrieves log index block number for the given filter.", IsImplemented = true, IsSharable = true)]
    ResultWrapper<int[]> logIndex_blockNumbers(
        [JsonRpcParameter] Filter filter
    );

    [JsonRpcMethod(Description = "Retrieves log index status.", IsImplemented = true, IsSharable = true)]
    ResultWrapper<LogIndexStatus> logIndex_status();

    [JsonRpcMethod(Description = "Forces log index compaction.", IsImplemented = true, IsSharable = true)]
    ResultWrapper<bool> logIndex_compact(
        [JsonRpcParameter] LogIndexCompactRequest request
    );
}
