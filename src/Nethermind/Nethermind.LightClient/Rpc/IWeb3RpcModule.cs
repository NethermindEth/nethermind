// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.LightClient.Rpc;

[RpcModule(ModuleType.Web3)]
internal interface IWeb3RpcModule : IRpcModule
{
    ResultWrapper<string> web3_clientVersion();
    ResultWrapper<Hash256> web3_sha3([JsonRpcParameter(ConverterType = typeof(RpcParameterConverters.StrictDataConverter))] byte[] data);
}
