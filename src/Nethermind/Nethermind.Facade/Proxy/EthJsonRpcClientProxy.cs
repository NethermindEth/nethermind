// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Facade.Proxy.Models.MultiCall;
using System.Collections.Generic;

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

        public Task<RpcResult<UInt256>> eth_getTransactionCount(Address address, BlockParameterModel blockParameter = null)
            => _proxy.SendAsync<UInt256>(nameof(eth_getTransactionCount), address, MapBlockParameter(blockParameter));

        public Task<RpcResult<ReceiptModel>> eth_getTransactionReceipt(Hash256 transactionHash)
            => _proxy.SendAsync<ReceiptModel>(nameof(eth_getTransactionReceipt), transactionHash);

        public Task<RpcResult<byte[]>> eth_call(CallTransaction transaction,
            BlockParameterModel blockParameter = null)
            => _proxy.SendAsync<byte[]>(nameof(eth_call), transaction, MapBlockParameter(blockParameter));

        public Task<RpcResult<IReadOnlyList<MultiCallBlockResult>>> eth_multicallV1(MultiCallPayload<CallTransaction> blockCalls, BlockParameterModel blockParameter = null)
        => _proxy.SendAsync<IReadOnlyList<MultiCallBlockResult>>(nameof(eth_multicallV1), blockCalls, MapBlockParameter(blockParameter));

        public Task<RpcResult<byte[]>> eth_getCode(Address address, BlockParameterModel blockParameter = null)
            => _proxy.SendAsync<byte[]>(nameof(eth_getCode), address, MapBlockParameter(blockParameter));

        public Task<RpcResult<TransactionModel>> eth_getTransactionByHash(Hash256 transactionHash)
            => _proxy.SendAsync<TransactionModel>(nameof(eth_getTransactionByHash), transactionHash);

        public Task<RpcResult<TransactionModel[]>> eth_pendingTransactions()
            => _proxy.SendAsync<TransactionModel[]>(nameof(eth_pendingTransactions));

        public Task<RpcResult<Hash256>> eth_sendRawTransaction(byte[] transaction)
            => _proxy.SendAsync<Hash256>(nameof(eth_sendRawTransaction), transaction);

        public Task<RpcResult<Hash256>> eth_sendTransaction(TransactionModel transaction)
            => _proxy.SendAsync<Hash256>(nameof(eth_sendTransaction), transaction);

        public Task<RpcResult<byte[]>> eth_estimateGas(TransactionModel transaction, BlockParameterModel blockParameter = null)
            => _proxy.SendAsync<byte[]>(nameof(eth_estimateGas), transaction);

        public Task<RpcResult<BlockModel<Hash256>>> eth_getBlockByHash(Hash256 blockHash,
            bool returnFullTransactionObjects = false)
            => _proxy.SendAsync<BlockModel<Hash256>>(nameof(eth_getBlockByHash), blockHash, returnFullTransactionObjects);

        public Task<RpcResult<BlockModel<Hash256>>> eth_getBlockByNumber(BlockParameterModel blockParameter,
            bool returnFullTransactionObjects = false)
            => _proxy.SendAsync<BlockModel<Hash256>>(nameof(eth_getBlockByNumber), MapBlockParameter(blockParameter),
                returnFullTransactionObjects);

        public Task<RpcResult<BlockModel<TransactionModel>>> eth_getBlockByNumberWithTransactionDetails(BlockParameterModel blockParameter,
            bool returnFullTransactionObjects = false)
            => _proxy.SendAsync<BlockModel<TransactionModel>>(nameof(eth_getBlockByNumber), MapBlockParameter(blockParameter),
                returnFullTransactionObjects);

        public Task<RpcResult<string>> net_version()
            => _proxy.SendAsync<string>(nameof(net_version));

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
