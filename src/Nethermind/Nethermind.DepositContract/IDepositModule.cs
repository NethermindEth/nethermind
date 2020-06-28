//  Copyright (c) 2018 Demerzel Solutions Limited
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
// 

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.DepositContract
{
    [RpcModule(ModuleType.Deposit)]
    public interface IDepositModule : IModule
    {
        [JsonRpcMethod(Description = "Deploys the deposit contract")]
        ValueTask<ResultWrapper<Keccak>> deposit_deploy(Address senderAddress);
        
        [JsonRpcMethod(Description = "Sets the contract address")]
        ValueTask<ResultWrapper<bool>> deposit_setContractAddress(Address contractAddress);
        
        [JsonRpcMethod(Description = "Deposits 32ETH at the validator address")]
        ValueTask<ResultWrapper<Keccak>> deposit_make(
            Address senderAddress,
            byte[] blsPublicKey,
            byte[] withdrawalCredentials,
            byte[] blsSignature);

        [JsonRpcMethod(Description = "Retrieves all Eth2 deposits from this chain.")]
        ValueTask<ResultWrapper<DepositModule.DepositData[]>> deposit_getAll();
    }
}
