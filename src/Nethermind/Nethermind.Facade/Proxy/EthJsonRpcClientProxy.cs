//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; witrhout even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Facade.Proxy.Models;

namespace Nethermind.Facade.Proxy
{
    public class EthJsonRpcClientProxy : IEthJsonRpcClientProxy
    {
        private readonly IJsonRpcClientProxy _proxy;

        public EthJsonRpcClientProxy(IJsonRpcClientProxy proxy)
        {
            _proxy = proxy ?? throw new ArgumentNullException(nameof(proxy));
        }

        public Task<RpcResult<UInt256>> eth_chainId()
            => _proxy.SendAsync<UInt256>(nameof(eth_chainId));

        public Task<RpcResult<long?>> eth_blockNumber()
            => _proxy.SendAsync<long?>(nameof(eth_blockNumber));

        public Task<RpcResult<UInt256?>> eth_getBalance(Address address, BlockParameterModel blockParameter = null)
            => _proxy.SendAsync<UInt256?>(nameof(eth_getBalance), address, MapBlockParameter(blockParameter));

        public Task<RpcResult<UInt256?>> eth_getTransactionCount(Address address, BlockParameterModel blockParameter = null)
            => _proxy.SendAsync<UInt256?>(nameof(eth_getTransactionCount), address, MapBlockParameter(blockParameter));

        public Task<RpcResult<ReceiptModel>> eth_getTransactionReceipt(Keccak transactionHash)
            => _proxy.SendAsync<ReceiptModel>(nameof(eth_getTransactionReceipt), transactionHash);
        
        public Task<RpcResult<byte[]>> eth_call(CallTransactionModel transaction,
            BlockParameterModel blockParameter = null)
            => _proxy.SendAsync<byte[]>(nameof(eth_call), transaction, MapBlockParameter(blockParameter));

        public Task<RpcResult<byte[]>> eth_getCode(Address address, BlockParameterModel blockParameter = null)
            => _proxy.SendAsync<byte[]>(nameof(eth_getCode), address, MapBlockParameter(blockParameter));

        public Task<RpcResult<TransactionModel>> eth_getTransactionByHash(Keccak transactionHash)
            => _proxy.SendAsync<TransactionModel>(nameof(eth_getTransactionByHash), transactionHash);

        public Task<RpcResult<Keccak>> eth_sendRawTransaction(byte[] transaction)
            => _proxy.SendAsync<Keccak>(nameof(eth_sendRawTransaction), transaction);

        public Task<RpcResult<BlockModel>> eth_getBlockByHash(Keccak blockHash,
            bool returnFullTransactionObjects = false)
            => _proxy.SendAsync<BlockModel>(nameof(eth_getBlockByHash), blockHash, returnFullTransactionObjects);

        public Task<RpcResult<BlockModel>> eth_getBlockByNumber(BlockParameterModel blockParameter,
            bool returnFullTransactionObjects = false)
            => _proxy.SendAsync<BlockModel>(nameof(eth_getBlockByNumber), MapBlockParameter(blockParameter),
                returnFullTransactionObjects);

        private static object MapBlockParameter(BlockParameterModel blockParameter)
        {
            if (blockParameter is null)
            {
                return null;
            }

            if (blockParameter.Number.HasValue)
            {
                return blockParameter.Number.Value;
            }

            return string.IsNullOrWhiteSpace(blockParameter.Type) ? null : blockParameter.Type.ToLowerInvariant();
        }
    }
}