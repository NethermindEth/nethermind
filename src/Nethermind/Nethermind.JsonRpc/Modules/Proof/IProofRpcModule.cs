﻿//  Copyright (c) 2021 Demerzel Solutions Limited
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

using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc.Data;

namespace Nethermind.JsonRpc.Modules.Proof
{
    /// <summary>
    /// Allows to retrieve transaction, call and state data alongside the merkle proofs / witnesses.
    /// </summary>
    [RpcModule(ModuleType.Proof)]
    public interface IProofRpcModule : IRpcModule
    {
        [JsonRpcMethod(IsImplemented = false, Description = "This function returns the same result as `eth_getTransactionByHash` and also a tx proof and a serialized block header.", IsSharable = false)]
        ResultWrapper<CallResultWithProof> proof_call(TransactionForRpc tx, BlockParameter blockParameter);
        
        [JsonRpcMethod(IsImplemented = true, Description = "This function returns the same result as `eth_getTransactionReceipt` and also a tx proof, receipt proof and serialized block headers.", IsSharable = false)]
        ResultWrapper<TransactionWithProof> proof_getTransactionByHash(Keccak txHash, bool includeHeader);
        
        [JsonRpcMethod(IsImplemented = true, Description = "This function should return the same result as `eth_call` and also proofs of all USED accunts and their storages and serialized block headers", IsSharable = false)]
        ResultWrapper<ReceiptWithProof> proof_getTransactionReceipt(Keccak txHash, bool includeHeader);
    }
}
