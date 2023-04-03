// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.AccountAbstraction.Data;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.AccountAbstraction
{
    [RpcModule(ModuleType.AccountAbstraction)]
    public interface IAccountAbstractionRpcModule : IRpcModule
    {
        [JsonRpcMethod(Description = "Adds user operation to the user operation pool.", IsImplemented = true)]
        ResultWrapper<Keccak> eth_sendUserOperation(UserOperationRpc userOperationRpc, Address entryPointContractAddress);

        [JsonRpcMethod(Description = "Returns the addresses of the EIP-4337 entrypoint contracts supported by this node", IsImplemented = true)]
        ResultWrapper<Address[]> eth_supportedEntryPoints();
    }
}
