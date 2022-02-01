//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
