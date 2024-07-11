// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin;

[RpcModule(ModuleType.Engine)]
public partial interface IEngineRpcModule : IRpcModule
{
    [JsonRpcMethod(
        Description = "Returns the currently supported list of Engine API methods.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<IEnumerable<string>> engine_exchangeCapabilities(IEnumerable<string> methods);

    [JsonRpcMethod(
        Description = "Returns the client version specification.",
        IsSharable = true,
        IsImplemented = true)]

    ResultWrapper<ClientVersionV1[]> engine_getClientVersionV1(ClientVersionV1 clientVersionV1);
}
