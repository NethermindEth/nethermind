/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Filters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Model;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Facade;
using Nethermind.JsonRpc.Data;
using Nethermind.Logging;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class EthModule : ModuleBase, IEthModule
    {
        private Encoding _messageEncoding = Encoding.UTF8;

        private readonly IBlockchainBridge _blockchainBridge;

        private ReaderWriterLockSlim _readerWriterLockSlim = new ReaderWriterLockSlim();

        public EthModule(ILogManager logManager, IBlockchainBridge blockchainBridge) : base(logManager)
        {
            _blockchainBridge = blockchainBridge;
        }

        public override ModuleType ModuleType => ModuleType.Eth;

        public ResultWrapper<string> eth_protocolVersion()
        {
            return ResultWrapper<string>.Success("62");
        }

        public ResultWrapper<SyncingResult> eth_syncing()
        {
            try
            {
                _readerWriterLockSlim.EnterReadLock();
                SyncingResult result;
                if (_blockchainBridge.IsSyncing)
                {
                    result = new SyncingResult
                    {
                        CurrentBlock = _blockchainBridge.Head.Number,
                        HighestBlock = _blockchainBridge.BestKnown,
                        StartingBlock = 0L,
                        IsSyncing = true
                    };
                }
                else
                {
                    result = SyncingResult.NotSyncing;
                }

                return ResultWrapper<SyncingResult>.Success(result);
            }
            finally
            {
                _readerWriterLockSlim.ExitReadLock();
            }
        }

        public ResultWrapper<byte[]> eth_snapshot()
        {
            return ResultWrapper<byte[]>.Fail("eth_snapshot not supported");
        }

        public ResultWrapper<Address> eth_coinbase()
        {
            return ResultWrapper<Address>.Success(Address.Zero);
        }

        public ResultWrapper<bool?> eth_mining()
        {
            return ResultWrapper<bool?>.Success(false);
        }

        public ResultWrapper<BigInteger?> eth_hashrate()
        {
            return ResultWrapper<BigInteger?>.Success(0);
        }

        [Todo("Gas pricer to be implemented")]
        public ResultWrapper<BigInteger?> eth_gasPrice()
        {
            return ResultWrapper<BigInteger?>.Success(20.GWei());
        }

        public ResultWrapper<IEnumerable<Address>> eth_accounts()
        {
            try
            {
                _readerWriterLockSlim.EnterReadLock();
                try
                {
                    var result = _blockchainBridge.GetWalletAccounts();
                    Address[] data = result.ToArray();
                    return ResultWrapper<IEnumerable<Address>>.Success(data.ToArray());
                }
                catch (Exception e)
                {
                    return ResultWrapper<IEnumerable<Address>>.Fail("Error while getting key addresses from wallet.");
                }
            }
            finally
            {
                _readerWriterLockSlim.ExitReadLock();
            }
        }

        public ResultWrapper<BigInteger?> eth_blockNumber()
        {
            try
            {
                _readerWriterLockSlim.EnterReadLock();
                if (_blockchainBridge.Head == null)
                {
                    return ResultWrapper<BigInteger?>.Fail($"Incorrect head block", ErrorType.InternalError, null);
                }

                var number = _blockchainBridge.Head.Number;
                return ResultWrapper<BigInteger?>.Success(number);
            }
            finally
            {
                _readerWriterLockSlim.ExitReadLock();
            }
        }

        public ResultWrapper<BigInteger?> eth_getBalance(Address address, BlockParameter blockParameter)
        {
            try
            {
                _readerWriterLockSlim.EnterWriteLock();
                if (_blockchainBridge.Head == null)
                {
                    return ResultWrapper<BigInteger?>.Fail("Incorrect head block", ErrorType.InternalError, null);
                }

                var result = GetAccountBalance(address, blockParameter);
                if (result.Result.ResultType == ResultType.Failure)
                {
                    return ResultWrapper<BigInteger?>.Fail($"Could not find balance of {address} at {blockParameter}", ErrorType.InternalError, null);
                }

                return result;
            }
            finally
            {
                _readerWriterLockSlim.ExitWriteLock();
            }
        }

        public ResultWrapper<byte[]> eth_getStorageAt(Address address, BigInteger positionIndex, BlockParameter blockParameter)
        {
            try
            {
                _readerWriterLockSlim.EnterWriteLock();
                if (_blockchainBridge.Head == null)
                {
                    return ResultWrapper<byte[]>.Fail($"Incorrect head block: {(_blockchainBridge.Head != null ? "HeadBlock is null" : "HeadBlock header is null")}");
                }

                var result = GetStorage(address, positionIndex, blockParameter);
                if (result.Result.ResultType == ResultType.Failure)
                {
                    return result;
                }

                return result;
            }
            finally
            {
                _readerWriterLockSlim.ExitWriteLock();
            }
        }

        public ResultWrapper<BigInteger?> eth_getTransactionCount(Address address, BlockParameter blockParameter)
        {
            try
            {
                _readerWriterLockSlim.EnterWriteLock();
                if (_blockchainBridge.Head == null)
                {
                    return ResultWrapper<BigInteger?>.Fail($"Incorrect head block", ErrorType.InternalError, null);
                }

                var result = GetAccountNonce(address, blockParameter);
                if (result.Result.ResultType == ResultType.Failure)
                {
                    return result;
                }

                return result;
            }
            finally
            {
                _readerWriterLockSlim.ExitWriteLock();
            }
        }

        public ResultWrapper<BigInteger?> eth_getBlockTransactionCountByHash(Keccak blockHash)
        {
            try
            {
                _readerWriterLockSlim.EnterReadLock();
                Block block = _blockchainBridge.FindBlock(blockHash);
                if (block == null)
                {
                    return ResultWrapper<BigInteger?>.Fail($"Cannot find block for hash: {blockHash}", ErrorType.NotFound, null);
                }

                return ResultWrapper<BigInteger?>.Success(block.Transactions.Length);
            }
            finally
            {
                _readerWriterLockSlim.ExitReadLock();
            }
        }

        public ResultWrapper<BigInteger?> eth_getBlockTransactionCountByNumber(BlockParameter blockParameter)
        {
            try
            {
                _readerWriterLockSlim.EnterReadLock();
                if (_blockchainBridge.Head == null)
                {
                    return ResultWrapper<BigInteger?>.Fail($"Incorrect head block", ErrorType.InternalError, null);
                }

                var transactionCount = GetTransactionCount(blockParameter);
                if (transactionCount.Result.ResultType == ResultType.Failure)
                {
                    return ResultWrapper<BigInteger?>.Fail(transactionCount.Result.Error, transactionCount.ErrorType, null);
                }

                if (Logger.IsTrace) Logger.Trace($"eth_getBlockTransactionCountByNumber request {blockParameter}, result: {transactionCount.Data}");
                return transactionCount;
            }
            finally
            {
                _readerWriterLockSlim.ExitReadLock();
            }
        }

        public ResultWrapper<BigInteger?> eth_getUncleCountByBlockHash(Keccak blockHash)
        {
            try
            {
                _readerWriterLockSlim.EnterReadLock();
                var block = _blockchainBridge.FindBlock(blockHash);
                if (block == null)
                {
                    return ResultWrapper<BigInteger?>.Fail($"Cannot find block for hash: {blockHash}", ErrorType.NotFound, null);
                }

                if (Logger.IsTrace) Logger.Trace($"eth_getUncleCountByBlockHash request {blockHash}, result: {block.Transactions.Length}");
                return ResultWrapper<BigInteger?>.Success(block.Ommers.Length);
            }
            finally
            {
                _readerWriterLockSlim.ExitReadLock();
            }
        }

        public ResultWrapper<BigInteger?> eth_getUncleCountByBlockNumber(BlockParameter blockParameter)
        {
            try
            {
                _readerWriterLockSlim.EnterReadLock();
                if (_blockchainBridge.Head == null)
                {
                    return ResultWrapper<BigInteger?>.Fail($"Incorrect head block", ErrorType.InternalError, null);
                }

                var ommersCount = GetOmmersCount(blockParameter);
                if (ommersCount.Result.ResultType == ResultType.Failure)
                {
                    return ResultWrapper<BigInteger?>.Fail(ommersCount.Result.Error, ommersCount.ErrorType);
                }

                if (Logger.IsTrace) Logger.Trace($"eth_getUncleCountByBlockNumber request {blockParameter}, result: {ommersCount.Data}");
                return ommersCount;
            }
            finally
            {
                _readerWriterLockSlim.ExitReadLock();
            }
        }

        private static List<JsonConverter> _converters = new List<JsonConverter>
        {
            new SyncingResultConverter()
        };

        public override IReadOnlyCollection<JsonConverter> GetConverters()
        {
            return _converters;
        }

        public ResultWrapper<byte[]> eth_getCode(Address address, BlockParameter blockParameter)
        {
            try
            {
                _readerWriterLockSlim.EnterWriteLock();
                if (_blockchainBridge.Head == null)
                {
                    return ResultWrapper<byte[]>.Fail($"Incorrect head block: {(_blockchainBridge.Head != null ? "HeadBlock is null" : "HeadBlock header is null")}");
                }

                var result = GetAccountCode(address, blockParameter);
                if (result.Result.ResultType == ResultType.Failure)
                {
                    return result;
                }

                if (Logger.IsTrace) Logger.Trace($"eth_getCode request {address}, {blockParameter}, result: {result.Data.ToHexString(true)}");
                return result;
            }
            finally
            {
                _readerWriterLockSlim.ExitWriteLock();
            }
        }


        public ResultWrapper<byte[]> eth_sign(Address addressData, byte[] message)
        {
            try
            {
                _readerWriterLockSlim.EnterReadLock();
                Signature sig;
                try
                {
                    Address address = addressData;
                    string messageText = _messageEncoding.GetString(message);
                    const string signatureTemplate = "\x19Ethereum Signed Message:\n{0}{1}";
                    string signatureText = string.Format(signatureTemplate, messageText.Length, messageText);
                    sig = _blockchainBridge.Sign(address, Keccak.Compute(signatureText));
                }
                catch (Exception)
                {
                    return ResultWrapper<byte[]>.Fail($"Unable to sign as {addressData}");
                }

                if (Logger.IsTrace) Logger.Trace($"eth_sign request {addressData}, {message}, result: {sig}");
                return ResultWrapper<byte[]>.Success(sig.Bytes);
            }
            finally
            {
                _readerWriterLockSlim.ExitReadLock();
            }
        }

        public ResultWrapper<Keccak> eth_sendTransaction(TransactionForRpc transactionForRpc)
        {
            try
            {
                _readerWriterLockSlim.EnterWriteLock();
                Transaction tx = transactionForRpc.ToTransaction();
                if (tx.Signature == null)
                {
                    tx.Nonce = (UInt256) _blockchainBridge.GetNonce(tx.SenderAddress);
                    _blockchainBridge.Sign(tx);
                }

                Keccak txHash = _blockchainBridge.SendTransaction(tx, true);
                return ResultWrapper<Keccak>.Success(txHash);
            }
            finally
            {
                _readerWriterLockSlim.ExitWriteLock();
            }
        }

        public ResultWrapper<Keccak> eth_sendRawTransaction(byte[] transaction)
        {
            try
            {
                _readerWriterLockSlim.EnterWriteLock();
                Transaction tx = Rlp.Decode<Transaction>(transaction);
                Keccak txHash = _blockchainBridge.SendTransaction(tx, true);
                return ResultWrapper<Keccak>.Success(txHash);
            }
            finally
            {
                _readerWriterLockSlim.ExitWriteLock();
            }
        }

        public ResultWrapper<byte[]> eth_call(TransactionForRpc transactionCall, BlockParameter blockParameter = null)
        {
            try
            {
                _readerWriterLockSlim.EnterWriteLock();
                BlockHeader block = blockParameter == null ? _blockchainBridge.Head : _blockchainBridge.GetBlock(blockParameter).Header;
                BlockchainBridge.CallOutput result = _blockchainBridge.Call(block, transactionCall.ToTransaction());

                if (result.Error != null)
                {
                    return ResultWrapper<byte[]>.Fail($"VM Exception while processing transaction: {result.Error}", ErrorType.ExecutionError, result.OutputData);
                }

                return ResultWrapper<byte[]>.Success(result.OutputData);
            }
            finally
            {
                _readerWriterLockSlim.ExitWriteLock();
            }
        }

        public ResultWrapper<BigInteger?> eth_estimateGas(TransactionForRpc transactionCall)
        {
            try
            {
                _readerWriterLockSlim.EnterWriteLock();
                Block headBlock = _blockchainBridge.FindHeadBlock();
                if (transactionCall.Gas == null)
                {
                    transactionCall.Gas = headBlock.GasLimit;
                }

                long result = _blockchainBridge.EstimateGas(headBlock, transactionCall.ToTransaction());
                return ResultWrapper<BigInteger?>.Success(result);
            }
            finally
            {
                _readerWriterLockSlim.ExitWriteLock();
            }
        }

        public ResultWrapper<BlockForRpc> eth_getBlockByHash(Keccak blockHash, bool returnFullTransactionObjects)
        {
            try
            {
                _readerWriterLockSlim.EnterReadLock();
                var block = _blockchainBridge.FindBlock(blockHash);
                if (block != null && returnFullTransactionObjects)
                {
                    _blockchainBridge.RecoverTxSenders(block);
                }

                return ResultWrapper<BlockForRpc>.Success(block == null ? null : new BlockForRpc(block, returnFullTransactionObjects));
            }
            finally
            {
                _readerWriterLockSlim.ExitReadLock();
            }
        }

        public ResultWrapper<BlockForRpc> eth_getBlockByNumber(BlockParameter blockParameter, bool returnFullTransactionObjects)
        {
            try
            {
//                _readerWriterLockSlim.EnterReadLock();
                if (_blockchainBridge.Head == null)
                {
                    return ResultWrapper<BlockForRpc>.Fail("Incorrect head block");
                }

                Block block;
                try
                {
                    block = _blockchainBridge.GetBlock(blockParameter, true, true);
                }
                catch (JsonRpcException ex)
                {
                    return ResultWrapper<BlockForRpc>.Fail(ex.Message, ex.ErrorType, null);
                }
                
                if (block != null && returnFullTransactionObjects)
                {
                    _blockchainBridge.RecoverTxSenders(block);
                }

                return ResultWrapper<BlockForRpc>.Success(block == null ? null : new BlockForRpc(block, returnFullTransactionObjects));
            }
            finally
            {
//                _readerWriterLockSlim.ExitReadLock();
            }
        }

        public ResultWrapper<TransactionForRpc> eth_getTransactionByHash(Keccak transactionHash)
        {
            try
            {
                _readerWriterLockSlim.EnterReadLock();
                (TxReceipt receipt, Transaction transaction) = _blockchainBridge.GetTransaction(transactionHash);
                if (transaction == null)
                {
                    return ResultWrapper<TransactionForRpc>.Success(null);
                }

                _blockchainBridge.RecoverTxSender(transaction, receipt.BlockNumber);
                var transactionModel = new TransactionForRpc(receipt.BlockHash, receipt.BlockNumber, receipt.Index, transaction);
                if (Logger.IsTrace) Logger.Trace($"eth_getTransactionByHash request {transactionHash}, result: {transactionModel.Hash}");
                return ResultWrapper<TransactionForRpc>.Success(transactionModel);
            }
            finally
            {
                _readerWriterLockSlim.ExitReadLock();
            }
        }

        public ResultWrapper<TransactionForRpc> eth_getTransactionByBlockHashAndIndex(Keccak blockHash, BigInteger positionIndex)
        {
            try
            {
                _readerWriterLockSlim.EnterReadLock();
                var block = _blockchainBridge.FindBlock(blockHash);
                if (block == null)
                {
                    return ResultWrapper<TransactionForRpc>.Fail($"Cannot find block for hash: {blockHash}", ErrorType.NotFound);
                }

                if (positionIndex < 0 || positionIndex > block.Transactions.Length - 1)
                {
                    return ResultWrapper<TransactionForRpc>.Fail("Position Index is incorrect", ErrorType.InvalidParams);
                }

                var transaction = block.Transactions[(int) positionIndex];
                _blockchainBridge.RecoverTxSender(transaction, block.Number);

                var transactionModel = new TransactionForRpc(block.Hash, block.Number, (int) positionIndex, transaction);

                if (Logger.IsDebug) Logger.Debug($"eth_getTransactionByBlockHashAndIndex request {blockHash}, index: {positionIndex}, result: {transactionModel.Hash}");
                return ResultWrapper<TransactionForRpc>.Success(transactionModel);
            }
            finally
            {
                _readerWriterLockSlim.ExitReadLock();
            }
        }

        public ResultWrapper<TransactionForRpc> eth_getTransactionByBlockNumberAndIndex(BlockParameter blockParameter, BigInteger positionIndex)
        {
            try
            {
                _readerWriterLockSlim.EnterReadLock();
                if (_blockchainBridge.Head == null)
                {
                    return ResultWrapper<TransactionForRpc>.Fail($"Incorrect head block");
                }

                Block block;
                try
                {
                    block = _blockchainBridge.GetBlock(blockParameter);
                }
                catch (JsonRpcException ex)
                {
                    return ResultWrapper<TransactionForRpc>.Fail(ex.Message, ex.ErrorType, null);
                }

                if (positionIndex < 0 || positionIndex > block.Transactions.Length - 1)
                {
                    return ResultWrapper<TransactionForRpc>.Fail("Position Index is incorrect", ErrorType.InvalidParams);
                }

                var transaction = block.Transactions[(int) positionIndex];
                _blockchainBridge.RecoverTxSender(transaction, block.Number);

                var transactionModel = new TransactionForRpc(block.Hash, block.Number, (int) positionIndex, transaction);

                if (Logger.IsDebug) Logger.Debug($"eth_getTransactionByBlockNumberAndIndex request {blockParameter}, index: {positionIndex}, result: {transactionModel.Hash}");
                return ResultWrapper<TransactionForRpc>.Success(transactionModel);
            }
            finally
            {
                _readerWriterLockSlim.ExitReadLock();
            }
        }

        public ResultWrapper<ReceiptForRpc> eth_getTransactionReceipt(Keccak txHash)
        {
            try
            {
                _readerWriterLockSlim.EnterReadLock();
                var receipt = _blockchainBridge.GetReceipt(txHash);
                if (receipt == null)
                {
                    return ResultWrapper<ReceiptForRpc>.Success(null);
                }

                var receiptModel = new ReceiptForRpc(txHash, receipt);
                if (Logger.IsTrace) Logger.Trace($"eth_getTransactionReceipt request {txHash}, result: {txHash}");
                return ResultWrapper<ReceiptForRpc>.Success(receiptModel);
            }
            finally
            {
                _readerWriterLockSlim.ExitReadLock();
            }
        }

        public ResultWrapper<BlockForRpc> eth_getUncleByBlockHashAndIndex(Keccak blockHashData, BigInteger positionIndex)
        {
            try
            {
                _readerWriterLockSlim.EnterReadLock();
                Keccak blockHash = blockHashData;
                var block = _blockchainBridge.FindBlock(blockHash);
                if (block == null)
                {
                    return ResultWrapper<BlockForRpc>.Fail($"Cannot find block for hash: {blockHash}", ErrorType.NotFound);
                }

                if (positionIndex < 0 || positionIndex > block.Ommers.Length - 1)
                {
                    return ResultWrapper<BlockForRpc>.Fail("Position Index is incorrect", ErrorType.InvalidParams);
                }

                var ommerHeader = block.Ommers[(int) positionIndex];
                var ommer = _blockchainBridge.FindBlock(ommerHeader.Hash);
                if (ommer == null)
                {
                    return ResultWrapper<BlockForRpc>.Fail($"Cannot find ommer for hash: {ommerHeader.Hash}", ErrorType.NotFound);
                }

                if (Logger.IsTrace) Logger.Trace($"eth_getUncleByBlockHashAndIndex request {blockHashData}, index: {positionIndex}, result: {block}");
                return ResultWrapper<BlockForRpc>.Success(new BlockForRpc(block, false));
            }
            finally
            {
                _readerWriterLockSlim.ExitReadLock();
            }
        }

        public ResultWrapper<BlockForRpc> eth_getUncleByBlockNumberAndIndex(BlockParameter blockParameter, BigInteger positionIndex)
        {
            try
            {
                _readerWriterLockSlim.EnterReadLock();
                if (_blockchainBridge.Head == null)
                {
                    return ResultWrapper<BlockForRpc>.Fail($"Incorrect head block: {(_blockchainBridge.Head != null ? "HeadBlock is null" : "HeadBlock header is null")}");
                }

                Block block;
                try
                {
                    block = _blockchainBridge.GetBlock(blockParameter);
                }
                catch (JsonRpcException ex)
                {
                    return ResultWrapper<BlockForRpc>.Fail(ex.Message, ex.ErrorType, null);
                }

                if (positionIndex < 0 || positionIndex > block.Ommers.Length - 1)
                {
                    return ResultWrapper<BlockForRpc>.Fail("Position Index is incorrect", ErrorType.InvalidParams);
                }

                var ommerHeader = block.Ommers[(int) positionIndex];
                var ommer = _blockchainBridge.FindBlock(ommerHeader.Hash);
                if (ommer == null)
                {
                    return ResultWrapper<BlockForRpc>.Fail($"Cannot find ommer for hash: {ommerHeader.Hash}", ErrorType.NotFound);
                }

                _blockchainBridge.RecoverTxSenders(ommer);
                
                return ResultWrapper<BlockForRpc>.Success(new BlockForRpc(block, false));
            }
            finally
            {
                _readerWriterLockSlim.ExitReadLock();
            }
        }

        public ResultWrapper<BigInteger?> eth_newFilter(Filter filter)
        {
            FilterBlock fromBlock = MapFilterBlock(filter.FromBlock);
            FilterBlock toBlock = MapFilterBlock(filter.ToBlock);
            int filterId = _blockchainBridge.NewFilter(fromBlock, toBlock, filter.Address, filter.Topics);
            return ResultWrapper<BigInteger?>.Success(filterId);
        }

        private static FilterBlock MapFilterBlock(BlockParameter parameter)
            => parameter.BlockId != null
                ? new FilterBlock(parameter.BlockId ?? 0)
                : new FilterBlock(MapFilterBlockType(parameter.Type));

        private static FilterBlockType MapFilterBlockType(BlockParameterType type)
        {
            switch (type)
            {
                case BlockParameterType.Latest: return FilterBlockType.Latest;
                case BlockParameterType.Earliest: return FilterBlockType.Earliest;
                case BlockParameterType.Pending: return FilterBlockType.Pending;
                case BlockParameterType.BlockId: return FilterBlockType.BlockId;
                default: return FilterBlockType.Latest;
            }
        }

        public ResultWrapper<BigInteger?> eth_newBlockFilter()
        {
            int filterId = _blockchainBridge.NewBlockFilter();
            return ResultWrapper<BigInteger?>.Success(filterId);
        }

        public ResultWrapper<BigInteger?> eth_newPendingTransactionFilter()
        {
            int filterId = _blockchainBridge.NewPendingTransactionFilter();
            return ResultWrapper<BigInteger?>.Success(filterId);
        }

        public ResultWrapper<bool?> eth_uninstallFilter(BigInteger filterId)
        {
            _blockchainBridge.UninstallFilter((int) filterId);
            return ResultWrapper<bool?>.Success(true);
        }

        public ResultWrapper<IEnumerable<object>> eth_getFilterChanges(BigInteger filterId)
        {
            int id = (int) filterId;
            FilterType filterType = _blockchainBridge.GetFilterType(id);
            switch (filterType)
            {
                case FilterType.BlockFilter:
                {
                    return _blockchainBridge.FilterExists(id)
                        ? ResultWrapper<IEnumerable<object>>.Success(_blockchainBridge.GetBlockFilterChanges(id)
                            .Select(b => new JsonRpc.Data.Data(b.Bytes)).ToArray())
                        : ResultWrapper<IEnumerable<object>>.Fail($"Filter with id: '{filterId}' does not exist.");
                }

                case FilterType.PendingTransactionFilter:
                {
                    return _blockchainBridge.FilterExists(id)
                        ? ResultWrapper<IEnumerable<object>>.Success(_blockchainBridge
                            .GetPendingTransactionFilterChanges(id).Select(b => new JsonRpc.Data.Data(b.Bytes))
                            .ToArray())
                        : ResultWrapper<IEnumerable<object>>.Fail($"Filter with id: '{filterId}' does not exist.");
                }

                case FilterType.LogFilter:
                {
                    return _blockchainBridge.FilterExists(id)
                        ? ResultWrapper<IEnumerable<object>>.Success(
                            _blockchainBridge.GetLogFilterChanges(id).ToArray())
                        : ResultWrapper<IEnumerable<object>>.Fail($"Filter with id: '{filterId}' does not exist.");
                }

                default:
                {
                    throw new NotSupportedException($"Filter type {filterType} is not supported");
                }
            }
        }

        public ResultWrapper<IEnumerable<FilterLog>> eth_getFilterLogs(BigInteger filterId)
        {
            var id = (int) filterId;

            return _blockchainBridge.FilterExists(id)
                ? ResultWrapper<IEnumerable<FilterLog>>.Success(_blockchainBridge.GetFilterLogs(id))
                : ResultWrapper<IEnumerable<FilterLog>>.Fail($"Filter with id: '{filterId}' does not exist.");
        }

        public ResultWrapper<IEnumerable<FilterLog>> eth_getLogs(Filter filter)
        {
            FilterBlock fromBlock = MapFilterBlock(filter.FromBlock);
            FilterBlock toBlock = MapFilterBlock(filter.ToBlock);

            return ResultWrapper<IEnumerable<FilterLog>>.Success(_blockchainBridge.GetLogs(fromBlock, toBlock,
                filter.Address,
                filter.Topics));
        }

        public ResultWrapper<IEnumerable<byte[]>> eth_getWork()
        {
            return ResultWrapper<IEnumerable<byte[]>>.Fail("eth_getWork not supported", ErrorType.MethodNotFound);
        }

        public ResultWrapper<bool?> eth_submitWork(byte[] nonce, Keccak headerPowHash, byte[] mixDigest)
        {
            return ResultWrapper<bool?>.Fail("eth_submitWork not supported", ErrorType.MethodNotFound, null);
        }

        public ResultWrapper<bool?> eth_submitHashrate(string hashRate, string id)
        {
            return ResultWrapper<bool?>.Fail("eth_submitHashrate not supported", ErrorType.MethodNotFound, null);
        }

        private ResultWrapper<BigInteger?> GetOmmersCount(BlockParameter blockParameter)
        {
            Block block;
            try
            {
                block = _blockchainBridge.GetBlock(blockParameter);
            }
            catch (JsonRpcException ex)
            {
                return ResultWrapper<BigInteger?>.Fail(ex.Message, ex.ErrorType, null);
            }

            return ResultWrapper<BigInteger?>.Success(block.Ommers.Length);
        }

        private ResultWrapper<BigInteger?> GetTransactionCount(BlockParameter blockParameter)
        {
            Block block;
            try
            {
                block = _blockchainBridge.GetBlock(blockParameter);
            }
            catch (JsonRpcException ex)
            {
                return ResultWrapper<BigInteger?>.Fail(ex.Message, ex.ErrorType, null);
            }

            return ResultWrapper<BigInteger?>.Success(block.Transactions.Length);
        }

        private ResultWrapper<byte[]> GetAccountCode(Address address, BlockParameter blockParameter)
        {
            if (blockParameter.Type == BlockParameterType.Pending)
            {
                blockParameter.Type = BlockParameterType.Latest;
            }

            Block block;
            try
            {
                block = _blockchainBridge.GetBlock(blockParameter);
            }
            catch (JsonRpcException ex)
            {
                return ResultWrapper<byte[]>.Fail(ex.Message, ex.ErrorType, null);
            }

            return GetAccountCode(address, block.StateRoot);
        }

        private ResultWrapper<BigInteger?> GetAccountNonce(Address address, BlockParameter blockParameter)
        {
            if (blockParameter.Type == BlockParameterType.Pending)
            {
                blockParameter.Type = BlockParameterType.Latest;
            }

            Block block;
            try
            {
                block = _blockchainBridge.GetBlock(blockParameter);
            }
            catch (JsonRpcException ex)
            {
                return ResultWrapper<BigInteger?>.Fail(ex.Message, ex.ErrorType, null);
            }

            if (block == null)
            {
                return ResultWrapper<BigInteger?>.Fail("Block not found", ErrorType.NotFound, null);
            }

            Account account = _blockchainBridge.GetAccount(address, block.StateRoot);
            return ResultWrapper<BigInteger?>.Success(account?.Nonce ?? 0);
        }

        private ResultWrapper<BigInteger?> GetAccountBalance(Address address, BlockParameter blockParameter)
        {
            if (blockParameter.Type == BlockParameterType.Pending)
            {
                blockParameter.Type = BlockParameterType.Latest;
            }

            Block block;
            try
            {
                block = _blockchainBridge.GetBlock(blockParameter);
            }
            catch (JsonRpcException ex)
            {
                return ResultWrapper<BigInteger?>.Fail(ex.Message, ex.ErrorType, null);
            }

            return GetAccountBalance(address, block.StateRoot);
        }

        private ResultWrapper<byte[]> GetStorage(Address address, BigInteger index, BlockParameter blockParameter)
        {
            if (blockParameter.Type == BlockParameterType.Pending)
            {
                blockParameter.Type = BlockParameterType.Latest;
            }

            Block block;
            try
            {
                block = _blockchainBridge.GetBlock(blockParameter);
            }
            catch (JsonRpcException ex)
            {
                return ResultWrapper<byte[]>.Fail(ex.Message, ex.ErrorType, null);
            }

            return GetAccountStorage(address, index, block.StateRoot);
        }

        private ResultWrapper<byte[]> GetAccountStorage(Address address, BigInteger index, Keccak stateRoot)
        {
            Account account = _blockchainBridge.GetAccount(address, stateRoot);
            if (account == null)
            {
                return ResultWrapper<byte[]>.Success(Bytes.Empty);
            }

            return ResultWrapper<byte[]>.Success(_blockchainBridge.GetStorage(address, index, stateRoot));
        }

        private ResultWrapper<BigInteger?> GetAccountBalance(Address address, Keccak stateRoot)
        {
            Account account = _blockchainBridge.GetAccount(address, stateRoot);
            if (account == null)
            {
                return ResultWrapper<BigInteger?>.Success(0);
            }

            return ResultWrapper<BigInteger?>.Success(account.Balance);
        }

        private ResultWrapper<byte[]> GetAccountCode(Address address, Keccak stateRoot)
        {
            Account account = _blockchainBridge.GetAccount(address, stateRoot);
            if (account == null)
            {
                return ResultWrapper<byte[]>.Success(Bytes.Empty);
            }

            var code = _blockchainBridge.GetCode(account.CodeHash);
            return ResultWrapper<byte[]>.Success(code);
        }
    }
}