// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.LightClient.Rpc;

[RpcModule(ModuleType.Net)]
internal interface IVerifiedNetRpcModule : IRpcModule
{
    ResultWrapper<string> net_version();
    ResultWrapper<bool> net_listening();
}
