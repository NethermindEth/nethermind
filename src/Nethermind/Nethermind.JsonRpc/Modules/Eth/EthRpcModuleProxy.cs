// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Filters;
using Nethermind.Facade.Proxy;
using Nethermind.Facade.Proxy.Models;
using Nethermind.Int256;
using Nethermind.JsonRpc.Data;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using Nethermind.Wallet;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class EthRpcModuleProxy : IEthRpcModule
    {
        private readonly IEthJsonRpcClientProxy _proxy;
        private readonly IWallet _wallet;

        public EthRpcModuleProxy(IEthJsonRpcClientProxy proxy, IWallet wallet)
        {
            _proxy = proxy;
            _wallet = wallet;
        }

        public ResultWrapper<ulong> eth_chainId()
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<string> eth_protocolVersion()
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<SyncingResult> eth_syncing()
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<Address> eth_coinbase()
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<bool?> eth_mining()
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<FeeHistoryResults> eth_feeHistory(long blockCount, BlockParameter newestBlock, double[]? rewardPercentiles = null)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<byte[]> eth_snapshot()
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<UInt256?> eth_hashrate()
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<UInt256?> eth_maxPriorityFeePerGas()
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<UInt256?> eth_gasPrice()
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<IEnumerable<Address>> eth_accounts()
        {
            throw new NotSupportedException();
        }

        public async Task<ResultWrapper<long?>> eth_blockNumber()
            => ResultWrapper<long?>.From(await _proxy.eth_blockNumber());

        public async Task<ResultWrapper<UInt256?>> eth_getBalance(Address address, BlockParameter blockParameter)
            => ResultWrapper<UInt256?>.From(await _proxy.eth_getBalance(address, MapBlockParameter(blockParameter)));

        public ResultWrapper<UInt256> eth_getStorageAt(Address address, UInt256 positionIndex,
            BlockParameter blockParameter)
        {
            throw new NotSupportedException();
        }

        public async Task<ResultWrapper<UInt256>> eth_getTransactionCount(Address address, BlockParameter blockParameter)
            => ResultWrapper<UInt256>.From(await _proxy.eth_getTransactionCount(address, MapBlockParameter(blockParameter)));

        public ResultWrapper<UInt256?> eth_getBlockTransactionCountByHash(Keccak blockHash)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<UInt256?> eth_getBlockTransactionCountByNumber(BlockParameter blockParameter)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<UInt256?> eth_getUncleCountByBlockHash(Keccak blockHash)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<UInt256?> eth_getUncleCountByBlockNumber(BlockParameter blockParameter)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<byte[]> eth_getCode(Address address, BlockParameter blockParameter)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<byte[]> eth_sign(Address addressData, byte[] message)
        {
            throw new NotSupportedException();
        }

        public async Task<ResultWrapper<Keccak>> eth_sendTransaction(TransactionForRpc rpcTx)
        {
            Transaction transaction = rpcTx.ToTransactionWithDefaults();
            if (transaction.Signature is null)
            {
                RpcResult<UInt256> chainIdResult = await _proxy.eth_chainId();
                ulong chainId = chainIdResult?.IsValid == true ? (ulong)chainIdResult.Result : 0;
                RpcResult<UInt256> nonceResult =
                    await _proxy.eth_getTransactionCount(transaction.SenderAddress, BlockParameterModel.Pending);
                transaction.Nonce = nonceResult?.IsValid == true ? nonceResult.Result : UInt256.Zero;
                _wallet.Sign(transaction, chainId);
            }

            return ResultWrapper<Keccak>.From(await _proxy.eth_sendRawTransaction(Rlp.Encode(transaction).Bytes));
        }

        public async Task<ResultWrapper<Keccak>> eth_sendRawTransaction(byte[] transaction)
            => ResultWrapper<Keccak>.From(await _proxy.eth_sendRawTransaction(Rlp.Encode(transaction).Bytes));


        public ResultWrapper<string> eth_call(TransactionForRpc transactionCall, BlockParameter? blockParameter = null)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<UInt256?> eth_estimateGas(
            TransactionForRpc transactionCall,
            BlockParameter? blockParameter = null)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<AccessListForRpc> eth_createAccessList(TransactionForRpc transactionCall, BlockParameter? blockParameter = null, bool optimize = true)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<BlockForRpc> eth_getBlockByHash(Keccak blockHash, bool returnFullTransactionObjects)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<BlockForRpc> eth_getBlockByNumber(BlockParameter blockParameter,
            bool returnFullTransactionObjects)
        {
            throw new NotSupportedException();
        }

        public async Task<ResultWrapper<TransactionForRpc>> eth_getTransactionByHash(Keccak transactionHash)
        {
            RpcResult<TransactionModel> result = await _proxy.eth_getTransactionByHash(transactionHash);
            TransactionForRpc? transaction = MapTransaction(result.Result);
            return transaction is null
                ? ResultWrapper<TransactionForRpc>.Fail("Transaction was not found.")
                : ResultWrapper<TransactionForRpc>.Success(transaction);
        }

        public ResultWrapper<TransactionForRpc[]> eth_pendingTransactions()
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<TransactionForRpc> eth_getTransactionByBlockHashAndIndex(Keccak blockHash,
            UInt256 positionIndex)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<TransactionForRpc> eth_getTransactionByBlockNumberAndIndex(BlockParameter blockParameter,
            UInt256 positionIndex)
        {
            throw new NotSupportedException();
        }

        public async Task<ResultWrapper<ReceiptForRpc>> eth_getTransactionReceipt(Keccak txHashData)
        {
            RpcResult<ReceiptModel> result = await _proxy.eth_getTransactionReceipt(txHashData);
            ReceiptForRpc? receipt = MapReceipt(result.Result);

            return receipt is null
                ? ResultWrapper<ReceiptForRpc>.Fail("Receipt was not found.")
                : ResultWrapper<ReceiptForRpc>.Success(receipt);
        }

        public ResultWrapper<BlockForRpc> eth_getUncleByBlockHashAndIndex(Keccak blockHashData, UInt256 positionIndex)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<BlockForRpc> eth_getUncleByBlockNumberAndIndex(BlockParameter blockParameter,
            UInt256 positionIndex)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<UInt256?> eth_newFilter(Filter filter)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<UInt256?> eth_newBlockFilter()
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<UInt256?> eth_newPendingTransactionFilter()
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<bool?> eth_uninstallFilter(UInt256 filterId)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<IEnumerable<object>> eth_getFilterChanges(UInt256 filterId)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<IEnumerable<FilterLog>> eth_getFilterLogs(UInt256 filterId)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<IEnumerable<FilterLog>> eth_getLogs(Filter filter)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<IEnumerable<byte[]>> eth_getWork()
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<bool?> eth_submitWork(byte[] nonce, Keccak headerPowHash, byte[] mixDigest)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<bool?> eth_submitHashrate(string hashRate, string id)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<AccountProof> eth_getProof(Address accountAddress, UInt256[] hashRate,
            BlockParameter blockParameter)
        {
            throw new NotSupportedException();
        }

        public ResultWrapper<AccountForRpc> eth_getAccount(Address accountAddress, BlockParameter blockParameter)
        {
            throw new NotSupportedException();
        }

        private static TransactionForRpc? MapTransaction(TransactionModel? transaction)
        {
            if (transaction is null)
            {
                return null;
            }

            return new TransactionForRpc
            {
                BlockHash = transaction.BlockHash,
                BlockNumber = (long)transaction.BlockNumber,
                From = transaction.From,
                To = transaction.To,
                Gas = (long)transaction.Gas,
                GasPrice = transaction.GasPrice,
                Hash = transaction.Hash,
                Input = transaction.Input,
                Nonce = transaction.Nonce,
                Value = transaction.Value
            };
        }

        private static ReceiptForRpc? MapReceipt(ReceiptModel? receipt)
        {
            if (receipt is null)
            {
                return null;
            }

            return new ReceiptForRpc
            {
                BlockHash = receipt.BlockHash,
                BlockNumber = (long)receipt.BlockNumber,
                ContractAddress = receipt.ContractAddress,
                CumulativeGasUsed = (long)receipt.CumulativeGasUsed,
                From = receipt.From,
                GasUsed = (long)receipt.GasUsed,
                Logs = receipt.Logs?.Select(MapLogEntry).ToArray() ?? Array.Empty<LogEntryForRpc>(),
                Status = (long)receipt.Status,
                To = receipt.To,
                TransactionHash = receipt.TransactionHash,
                TransactionIndex = (long)receipt.TransactionIndex,
                LogsBloom = receipt.LogsBloom is null ? null : new Bloom(receipt.LogsBloom),
                EffectiveGasPrice = receipt.EffectiveGasPrice
            };
        }

        private static LogEntryForRpc MapLogEntry(LogModel log)
            => new()
            {
                Address = log.Address,
                Data = log.Data,
                Removed = log.Removed,
                Topics = log.Topics,
                BlockHash = log.BlockHash,
                BlockNumber = (long)log.BlockNumber,
                LogIndex = (long)log.LogIndex,
                TransactionHash = log.TransactionHash,
                TransactionIndex = (long)log.TransactionIndex
            };

        private static BlockParameterModel? MapBlockParameter(BlockParameter? blockParameter)
        {
            if (blockParameter is null)
            {
                return null;
            }

            if (blockParameter.Type == BlockParameterType.BlockNumber && blockParameter.BlockNumber.HasValue)
            {
                return BlockParameterModel.FromNumber(blockParameter.BlockNumber.Value);
            }

            return new BlockParameterModel
            {
                Type = blockParameter.Type.ToString()
            };
        }
    }
}
