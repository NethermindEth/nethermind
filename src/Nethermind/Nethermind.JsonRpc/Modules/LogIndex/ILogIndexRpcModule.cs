// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.JsonRpc.Modules.LogIndex;

[RpcModule(ModuleType.LogIndex)]
public interface ILogIndexRpcModule : IRpcModule
{
    [JsonRpcMethod(Description = "Retrieves log index keys (and optionally values) for the given address/topic.", IsImplemented = true, IsSharable = true)]
    ResultWrapper<Dictionary<byte[], int[]>> logIndex_keys(
        [JsonRpcParameter(ExampleValue = "{\"key\":\"0xC02aaA39b223FE8D0A0e5C4F27eAD9083C756Cc2\"}")]
        LogIndexKeysRequest request
    );

    [JsonRpcMethod(Description = "Retrieves log index status.", IsImplemented = true, IsSharable = true)]
    ResultWrapper<LogIndexStatus> logIndex_status();
}
