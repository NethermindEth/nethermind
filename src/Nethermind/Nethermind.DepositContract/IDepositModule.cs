// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
