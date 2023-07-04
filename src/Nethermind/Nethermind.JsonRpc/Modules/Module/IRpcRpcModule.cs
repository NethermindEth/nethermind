// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;

namespace Nethermind.JsonRpc.Modules.Rpc;

[RpcModule(ModuleType.Rpc)]
public interface IRpcRpcModule : IRpcModule
{
    [JsonRpcMethod(Description = "Retrieves a list of modules.", IsImplemented = true, IsSharable = true)]
    ResultWrapper<IDictionary<String, String>> rpc_modules();
}
