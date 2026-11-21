// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.LightClient.Rpc;

[RpcModule(ModuleType.Web3)]
internal interface IVerifiedWeb3RpcModule : IRpcModule
{
    ResultWrapper<string> web3_clientVersion();
}
