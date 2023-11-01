// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Facade.Proxy.Models.MultiCall;
using Nethermind.Blockchain.Find;

namespace Nethermind.Facade.Proxy
{
    public interface IEthJsonRpcClientProxy
    {
        Task<RpcResult<UInt256>> eth_chainId();
        Task<RpcResult<long?>> eth_blockNumber();
        Task<RpcResult<UInt256?>> eth_getBalance(Address address, BlockParameterModel blockParameter = null);
        Task<RpcResult<UInt256>> eth_getTransactionCount(Address address, BlockParameterModel blockParameter = null);
        Task<RpcResult<ReceiptModel>> eth_getTransactionReceipt(Hash256 transactionHash);
        Task<RpcResult<byte[]>> eth_call(CallTransaction transaction, BlockParameterModel blockParameter = null);
        Task<RpcResult<IReadOnlyList<MultiCallBlockResult>>> eth_multicallV1(MultiCallPayload<CallTransaction> blockCalls, BlockParameterModel blockParameter = null);

        Task<RpcResult<byte[]>> eth_getCode(Address address, BlockParameterModel blockParameter = null);
        Task<RpcResult<TransactionModel>> eth_getTransactionByHash(Hash256 transactionHash);
        Task<RpcResult<TransactionModel[]>> eth_pendingTransactions();
        Task<RpcResult<Hash256>> eth_sendRawTransaction(byte[] transaction);
        Task<RpcResult<Hash256>> eth_sendTransaction(TransactionModel transaction);
        Task<RpcResult<byte[]>> eth_estimateGas(TransactionModel transaction, BlockParameterModel blockParameter = null);
        Task<RpcResult<BlockModel<Hash256>>> eth_getBlockByHash(Hash256 blockHash, bool returnFullTransactionObjects = false);
        Task<RpcResult<BlockModel<Hash256>>> eth_getBlockByNumber(BlockParameterModel blockParameter,
            bool returnFullTransactionObjects = false);
        Task<RpcResult<BlockModel<TransactionModel>>> eth_getBlockByNumberWithTransactionDetails(BlockParameterModel blockParameter,
            bool returnFullTransactionObjects = false);
        Task<RpcResult<string>> net_version();
    }
}
