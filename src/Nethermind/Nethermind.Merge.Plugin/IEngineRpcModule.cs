// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.Merge.Plugin;

[RpcModule(ModuleType.Engine)]
public partial interface IEngineRpcModule : IRpcModule
{
    [JsonRpcMethod(
        Description = "Returns the currently supported list of Engine API methods.",
        IsSharable = true,
        IsImplemented = true)]
    ResultWrapper<IEnumerable<string>> engine_exchangeCapabilities(IEnumerable<string> methods);
}
