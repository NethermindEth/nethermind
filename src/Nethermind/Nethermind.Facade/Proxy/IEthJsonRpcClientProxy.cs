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

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Facade.Proxy.Models;

namespace Nethermind.Facade.Proxy
{
    public interface IEthJsonRpcClientProxy
    {
        Task<RpcResult<UInt256>> eth_chainId();
        Task<RpcResult<long?>> eth_blockNumber();
        Task<RpcResult<UInt256?>> eth_getBalance(Address address, BlockParameterModel blockParameter = null);
        Task<RpcResult<UInt256?>> eth_getTransactionCount(Address address, BlockParameterModel blockParameter = null);
        Task<RpcResult<ReceiptModel>> eth_getTransactionReceipt(Keccak transactionHash);
        Task<RpcResult<byte[]>> eth_call(CallTransactionModel transaction, BlockParameterModel blockParameter = null);
        Task<RpcResult<byte[]>> eth_getCode(Address address, BlockParameterModel blockParameter = null);
        Task<RpcResult<TransactionModel>> eth_getTransactionByHash(Keccak transactionHash);
        Task<RpcResult<TransactionModel[]>> eth_pendingTransactions();
        Task<RpcResult<Keccak>> eth_sendRawTransaction(byte[] transaction);
        Task<RpcResult<Keccak>> eth_sendTransaction(TransactionModel transaction);
        Task<RpcResult<byte[]>> eth_estimateGas(TransactionModel transaction,  BlockParameterModel blockParameter = null);
        Task<RpcResult<BlockModel<Keccak>>> eth_getBlockByHash(Keccak blockHash, bool returnFullTransactionObjects = false);
        Task<RpcResult<BlockModel<Keccak>>> eth_getBlockByNumber(BlockParameterModel blockParameter,
            bool returnFullTransactionObjects = false);
        Task<RpcResult<BlockModel<TransactionModel>>> eth_getBlockByNumberWithTransactionDetails(BlockParameterModel blockParameter,
            bool returnFullTransactionObjects = false);
        Task<RpcResult<string>> net_version();
    }
}
