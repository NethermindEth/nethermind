// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Facade.Proxy.Models.MultiCall;

namespace Nethermind.Facade.Proxy
{
    public interface IEthJsonRpcClientProxy
    {
        Task<RpcResult<UInt256>> eth_chainId();
        Task<RpcResult<long?>> eth_blockNumber();
        Task<RpcResult<UInt256?>> eth_getBalance(Address address, BlockParameterModel blockParameter = null);
        Task<RpcResult<UInt256>> eth_getTransactionCount(Address address, BlockParameterModel blockParameter = null);
        Task<RpcResult<ReceiptModel>> eth_getTransactionReceipt(Keccak transactionHash);
        Task<RpcResult<byte[]>> eth_call(CallTransactionModel transaction, BlockParameterModel blockParameter = null);

        //TODO:add tests
        Task<RpcResult<MultiCallBlockResult[]>> eth_multicall(ulong version, MultiCallBlockStateCallsModel[] blockCalls, BlockParameterModel blockParameter = null, bool traceTransfers = true);
        Task<RpcResult<byte[]>> eth_getCode(Address address, BlockParameterModel blockParameter = null);
        Task<RpcResult<TransactionModel>> eth_getTransactionByHash(Keccak transactionHash);
        Task<RpcResult<TransactionModel[]>> eth_pendingTransactions();
        Task<RpcResult<Keccak>> eth_sendRawTransaction(byte[] transaction);
        Task<RpcResult<Keccak>> eth_sendTransaction(TransactionModel transaction);
        Task<RpcResult<byte[]>> eth_estimateGas(TransactionModel transaction, BlockParameterModel blockParameter = null);
        Task<RpcResult<BlockModel<Keccak>>> eth_getBlockByHash(Keccak blockHash, bool returnFullTransactionObjects = false);
        Task<RpcResult<BlockModel<Keccak>>> eth_getBlockByNumber(BlockParameterModel blockParameter,
            bool returnFullTransactionObjects = false);
        Task<RpcResult<BlockModel<TransactionModel>>> eth_getBlockByNumberWithTransactionDetails(BlockParameterModel blockParameter,
            bool returnFullTransactionObjects = false);
        Task<RpcResult<string>> net_version();
    }
}
