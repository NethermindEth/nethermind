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

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;
using Nethermind.Facade.Proxy;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Core.Services
{
    public class NdmBlockchainBridgeProxy : INdmBlockchainBridge
    {
        private readonly IEthJsonRpcClientProxy _proxy;

        public NdmBlockchainBridgeProxy(IEthJsonRpcClientProxy proxy)
        {
            _proxy = proxy ?? throw new ArgumentNullException(nameof(proxy));
        }

        public async Task<long> GetLatestBlockNumberAsync()
        {
            var result = await _proxy.eth_blockNumber();

            return result?.IsValid == true && result.Result.HasValue ? result.Result.Value : 0;
        }

        public async Task<byte[]> GetCodeAsync(Address address)
        {
            var result = await _proxy.eth_getCode(address, BlockParameterModel.Latest);

            return result?.IsValid == true ? result.Result : Array.Empty<byte>();
        }

        public async Task<Block?> FindBlockAsync(Keccak blockHash)
        {
            var result = await _proxy.eth_getBlockByHash(blockHash);

            return result?.IsValid == true ? result.Result?.ToBlock() : null;
        }

        public async Task<Block?> FindBlockAsync(long blockNumber)
        {
            var result = await _proxy.eth_getBlockByNumber(BlockParameterModel.FromNumber(blockNumber));

            return result?.IsValid == true ? result.Result?.ToBlock() : null;
        }

        public async Task<Block?> GetLatestBlockAsync()
        {
            var result = await _proxy.eth_getBlockByNumber(BlockParameterModel.Latest);

            return result?.IsValid == true ? result.Result?.ToBlock() : null;
        }

        public async Task<UInt256> GetNonceAsync(Address address)
        {
            var result = await _proxy.eth_getTransactionCount(address, BlockParameterModel.Pending);

            return result?.IsValid == true ? result.Result ?? UInt256.Zero : UInt256.Zero;
        }

        public Task<UInt256> ReserveOwnTransactionNonceAsync(Address address) => GetNonceAsync(address);

        public async Task<NdmTransaction?> GetTransactionAsync(Keccak transactionHash)
        {
            var transactionTask = _proxy.eth_getTransactionByHash(transactionHash);
            var receiptTask = _proxy.eth_getTransactionReceipt(transactionHash);
            await Task.WhenAll(transactionTask, receiptTask);
            
            return transactionTask.Result?.Result is null
                ? null
                : MapTransaction(transactionTask.Result.Result, receiptTask.Result?.Result);
        }

        public async Task<ulong> GetNetworkIdAsync()
        {
            RpcResult<UInt256>? result = await _proxy.eth_chainId();

            return result?.IsValid == true ? (ulong)result.Result : 0ul;
        }

        public async Task<byte[]> CallAsync(Transaction transaction)
        {
            var result = await _proxy.eth_call(CallTransactionModel.FromTransaction(transaction));

            return result?.IsValid == true ? result.Result ?? Array.Empty<byte>() : Array.Empty<byte>();
        }

        public async Task<byte[]> CallAsync(Transaction transaction, long blockNumber)
        {
            var result = await _proxy.eth_call(CallTransactionModel.FromTransaction(transaction),
                BlockParameterModel.FromNumber(blockNumber));

            return result?.IsValid == true ? result.Result ?? Array.Empty<byte>() : Array.Empty<byte>();
        }

        public async ValueTask<Keccak?> SendOwnTransactionAsync(Transaction transaction)
        {
            var data = Rlp.Encode(transaction).Bytes;
            var result = await _proxy.eth_sendRawTransaction(data);

            return result?.IsValid == true ? result.Result : null;
        }

        private static NdmTransaction MapTransaction(TransactionModel transaction, ReceiptModel? receipt)
        {
            var isPending = receipt is null;
            return new NdmTransaction(transaction.ToTransaction(), isPending, (long) (receipt?.BlockNumber ?? 0),
                receipt?.BlockHash, (long) (receipt?.GasUsed ?? 0));
        }
    }
}
