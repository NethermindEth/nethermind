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
using System.Text;
using Nethermind.Blockchain.Filters;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Dirichlet.Numerics;
using Nethermind.JsonRpc.DataModel;
using Block = Nethermind.JsonRpc.DataModel.Block;
using Filter = Nethermind.JsonRpc.DataModel.Filter;
using Transaction = Nethermind.JsonRpc.DataModel.Transaction;
using TransactionReceipt = Nethermind.JsonRpc.DataModel.TransactionReceipt;

namespace Nethermind.JsonRpc.Module
{
    public class EthModule : ModuleBase, IEthModule
    {
        private readonly IBlockchainBridge _blockchainBridge;
        private readonly IJsonRpcModelMapper _modelMapper;

        public EthModule(IJsonSerializer jsonSerializer, IConfigProvider configurationProvider, IJsonRpcModelMapper modelMapper, ILogManager logManager, IBlockchainBridge blockchainBridge) : base(configurationProvider, logManager, jsonSerializer)
        {
            _blockchainBridge = blockchainBridge;
            _modelMapper = modelMapper;
        }

        public ResultWrapper<string> eth_protocolVersion()
        {
            return ResultWrapper<string>.Success("62");
        }

        public ResultWrapper<SynchingResult> eth_syncing()
        {
            var result = new SynchingResult
            {
                IsSynching = true,
                CurrentBlock = new Quantity(_blockchainBridge.Head.Number),
                HighestBlock = new Quantity(_blockchainBridge.BestKnown),
                StartingBlock = new Quantity(0)
            };
            
            if (Logger.IsTrace) Logger.Trace($"eth_syncing request, result: {result.ToJson()}");
            return ResultWrapper<SynchingResult>.Success(result);
        }

        public ResultWrapper<Data> eth_snapshot()
        {
            return ResultWrapper<Data>.Fail("eth_snapshot not supported");
        }

        public ResultWrapper<Data> eth_coinbase()
        {
            return ResultWrapper<Data>.Success(new Data(Address.Zero));
        }

        public ResultWrapper<bool> eth_mining()
        {
            return ResultWrapper<bool>.Success(false);
        }

        public ResultWrapper<Quantity> eth_hashrate()
        {
            return ResultWrapper<Quantity>.Success(new Quantity(0));
        }

        [Todo("Gas pricer to be implemented")]
        public ResultWrapper<Quantity> eth_gasPrice()
        {
            return ResultWrapper<Quantity>.Success(new Quantity(20.Wei()));
        }

        public ResultWrapper<IEnumerable<Data>> eth_accounts()
        {
            try
            {
                var result = _blockchainBridge.GetWalletAccounts();
                Data[] data = result.Select(x => new Data(x.Bytes)).ToArray();
                if (Logger.IsTrace) Logger.Trace($"eth_accounts request, result: {string.Join(", ", data.Select(x => x.Value.ToString()))}");
                return ResultWrapper<IEnumerable<Data>>.Success(data.ToArray());
            }
            catch (Exception e)
            {
                if (Logger.IsError) Logger.Error($"Failed to server eth_accounts request", e);
                return ResultWrapper<IEnumerable<Data>>.Fail("Error while getting key addresses from wallet.");
            }
        }

        public ResultWrapper<Quantity> eth_blockNumber()
        {
            if (_blockchainBridge.Head == null)
            {
                return ResultWrapper<Quantity>.Fail($"Incorrect head block: {(_blockchainBridge.Head != null ? "HeadBlock is null" : "HeadBlock header is null")}");
            }

            var number = _blockchainBridge.Head.Number;
            if (Logger.IsTrace) Logger.Trace($"eth_blockNumber request, result: {number}");
            return ResultWrapper<Quantity>.Success(new Quantity(number));
        }

        public ResultWrapper<Quantity> eth_getBalance(Data address, BlockParameter blockParameter)
        {
            if (_blockchainBridge.Head == null)
            {
                return ResultWrapper<Quantity>.Fail($"Incorrect head block: {(_blockchainBridge.Head != null ? "HeadBlock is null" : "HeadBlock header is null")}");
            }

            var result = GetAccountBalance(new Address(address.Value), blockParameter);
            if (result.Result.ResultType == ResultType.Failure)
            {
                return result;
            }

            if (Logger.IsTrace) Logger.Trace($"eth_getBalance request {address.ToJson()}, {blockParameter}, result: {result.Data.AsNumber()}");
            return result;
        }

        public ResultWrapper<Data> eth_getStorageAt(Data address, Quantity positionIndex, BlockParameter blockParameter)
        {
            return ResultWrapper<Data>.Fail("eth_getStorageAt not supported");
        }

        public ResultWrapper<Quantity> eth_getTransactionCount(Data address, BlockParameter blockParameter)
        {
            if (_blockchainBridge.Head == null)
            {
                return ResultWrapper<Quantity>.Fail($"Incorrect head block: {(_blockchainBridge.Head != null ? "HeadBlock is null" : "HeadBlock header is null")}");
            }

            var result = GetAccountNonce(new Address(address.Value), blockParameter);
            if (result.Result.ResultType == ResultType.Failure)
            {
                return result;
            }

            if (Logger.IsTrace) Logger.Trace($"eth_getTransactionCount request {address.ToJson()}, {blockParameter}, result: {result.Data.AsNumber()}");
            return result;
        }

        public ResultWrapper<Quantity> eth_getBlockTransactionCountByHash(Data blockHash)
        {
            var block = _blockchainBridge.FindBlock(new Keccak(blockHash.Value), false);
            if (block == null)
            {
                return ResultWrapper<Quantity>.Fail($"Cannot find block for hash: {blockHash.Value}", ErrorType.NotFound);
            }

            if (Logger.IsTrace) Logger.Trace($"eth_getBlockTransactionCountByHash request {blockHash.ToJson()}, result: {block.Transactions.Length}");
            return ResultWrapper<Quantity>.Success(new Quantity(block.Transactions.Length));
        }

        public ResultWrapper<Quantity> eth_getBlockTransactionCountByNumber(BlockParameter blockParameter)
        {
            if (_blockchainBridge.Head == null)
            {
                return ResultWrapper<Quantity>.Fail($"Incorrect head block: {(_blockchainBridge.Head != null ? "HeadBlock is null" : "HeadBlock header is null")}");
            }

            var transactionCount = GetTransactionCount(blockParameter);
            if (transactionCount.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<Quantity>.Fail(transactionCount.Result.Error, transactionCount.ErrorType);
            }

            if (Logger.IsTrace) Logger.Trace($"eth_getBlockTransactionCountByNumber request {blockParameter}, result: {transactionCount.Data.AsNumber()}");
            return transactionCount;
        }

        public ResultWrapper<Quantity> eth_getUncleCountByBlockHash(Data blockHash)
        {
            var block = _blockchainBridge.FindBlock(new Keccak(blockHash.Value), false);
            if (block == null)
            {
                return ResultWrapper<Quantity>.Fail($"Cannot find block for hash: {blockHash.Value}", ErrorType.NotFound);
            }

            if (Logger.IsTrace) Logger.Trace($"eth_getUncleCountByBlockHash request {blockHash.ToJson()}, result: {block.Transactions.Length}");
            return ResultWrapper<Quantity>.Success(new Quantity(block.Ommers.Length));
        }

        public ResultWrapper<Quantity> eth_getUncleCountByBlockNumber(BlockParameter blockParameter)
        {
            if (_blockchainBridge.Head == null)
            {
                return ResultWrapper<Quantity>.Fail($"Incorrect head block: {(_blockchainBridge.Head != null ? "HeadBlock is null" : "HeadBlock header is null")}");
            }

            var ommersCount = GetOmmersCount(blockParameter);
            if (ommersCount.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<Quantity>.Fail(ommersCount.Result.Error, ommersCount.ErrorType);
            }

            if (Logger.IsTrace) Logger.Trace($"eth_getUncleCountByBlockNumber request {blockParameter}, result: {ommersCount.Data.AsNumber()}");
            return ommersCount;
        }

        public ResultWrapper<Data> eth_getCode(Data address, BlockParameter blockParameter)
        {
            if (_blockchainBridge.Head == null)
            {
                return ResultWrapper<Data>.Fail($"Incorrect head block: {(_blockchainBridge.Head != null ? "HeadBlock is null" : "HeadBlock header is null")}");
            }

            var result = GetAccountCode(new Address(address.Value), blockParameter);
            if (result.Result.ResultType == ResultType.Failure)
            {
                return result;
            }

            if (Logger.IsTrace) Logger.Trace($"eth_getCode request {address.ToJson()}, {blockParameter}, result: {result.Data.ToJson()}");
            return result;
        }

        public ResultWrapper<Data> eth_sign(Data addressData, Data message)
        {
            Signature sig;
            try
            {
                Address address = new Address(addressData.Value);
                var messageText = Encoding.GetEncoding(JsonRpcConfig.MessageEncoding).GetString(message.Value);
                var signatureText = string.Format(JsonRpcConfig.SignatureTemplate, messageText.Length, messageText);
                sig = _blockchainBridge.Sign(address, Keccak.Compute(signatureText));
            }
            catch (Exception)
            {
                return ResultWrapper<Data>.Fail($"Unable to sign as {addressData}");
            }

            if (Logger.IsTrace) Logger.Trace($"eth_sign request {addressData.ToJson()}, {message.ToJson()}, result: {sig}");
            return ResultWrapper<Data>.Success(new Data(sig.Bytes));
        }

        public ResultWrapper<Data> eth_sendTransaction(Transaction transaction)
        {
            Core.Transaction tx = _modelMapper.MapTransaction(transaction);
            Keccak txHash = _blockchainBridge.SendTransaction(tx);
            return ResultWrapper<Data>.Success(new Data(txHash));
        }

        public ResultWrapper<Data> eth_sendRawTransaction(Data transaction)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<Data> eth_call(Transaction transactionCall, BlockParameter blockParameter)
        {
            Core.Block block = GetBlock(blockParameter).Data;
            byte[] result = _blockchainBridge.Call(block, _modelMapper.MapTransaction(transactionCall));
            return ResultWrapper<Data>.Success(new Data(result));
        }

        public ResultWrapper<Quantity> eth_estimateGas(Transaction transactionCall, BlockParameter blockParameter)
        {
            return ResultWrapper<Quantity>.Fail("eth_estimateGas not supported");
        }

        public ResultWrapper<Block> eth_getBlockByHash(Data blockHash, bool returnFullTransactionObjects)
        {
            var block = _blockchainBridge.FindBlock(new Keccak(blockHash.Value), false);
            if (block == null)
            {
                return ResultWrapper<Block>.Fail($"Cannot find block for hash: {blockHash.Value}", ErrorType.NotFound);
            }

            var blockModel = _modelMapper.MapBlock(block, returnFullTransactionObjects);

            if (Logger.IsDebug) Logger.Debug($"eth_getBlockByHash request {blockHash.ToJson()}, result: {GetJsonLog(blockModel.ToJson())}");
            return ResultWrapper<Block>.Success(blockModel);
        }

        public ResultWrapper<Block> eth_getBlockByNumber(BlockParameter blockParameter, bool returnFullTransactionObjects)
        {
            if (_blockchainBridge.Head == null)
            {
                return ResultWrapper<Block>.Fail($"Incorrect head block: {(_blockchainBridge.Head != null ? "HeadBlock is null" : "HeadBlock header is null")}");
            }

            var result = GetBlock(blockParameter);
            if (result.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<Block>.Fail(result.Result.Error, result.ErrorType);
            }

            var blockModel = _modelMapper.MapBlock(result.Data, returnFullTransactionObjects);

            if (Logger.IsTrace) Logger.Trace($"eth_getBlockByNumber request {blockParameter}, result: {GetJsonLog(blockModel.ToJson())}");
            return ResultWrapper<Block>.Success(blockModel);
        }

        public ResultWrapper<Transaction> eth_getTransactionByHash(Data transactionHash)
        {
            (Core.TransactionReceipt receipt, Core.Transaction transaction) = _blockchainBridge.GetTransaction(new Keccak(transactionHash.Value));
            if (transaction == null)
            {
                return ResultWrapper<Transaction>.Fail($"Cannot find transaction for hash: {transactionHash.Value}", ErrorType.NotFound);
            }

            var transactionModel = _modelMapper.MapTransaction(receipt, transaction);
            if (Logger.IsTrace) Logger.Trace($"eth_getTransactionByHash request {transactionHash.ToJson()}, result: {GetJsonLog(transactionModel.ToJson())}");
            return ResultWrapper<Transaction>.Success(transactionModel);
        }

        public ResultWrapper<Transaction> eth_getTransactionByBlockHashAndIndex(Data blockHash, Quantity positionIndex)
        {
            var block = _blockchainBridge.FindBlock(new Keccak(blockHash.Value), false);
            if (block == null)
            {
                return ResultWrapper<Transaction>.Fail($"Cannot find block for hash: {blockHash.Value}", ErrorType.NotFound);
            }

            var index = positionIndex.AsNumber();
            if (!index.HasValue)
            {
                return ResultWrapper<Transaction>.Fail("Position Index is required", ErrorType.InvalidParams);
            }

            if (index.Value < 0 || index.Value > block.Transactions.Length - 1)
            {
                return ResultWrapper<Transaction>.Fail("Position Index is incorrect", ErrorType.InvalidParams);
            }

            var transaction = block.Transactions[(int) index.Value];
            var transactionModel = _modelMapper.MapTransaction(block.Hash, block.Number, (int) index.Value, transaction);

            if(Logger.IsDebug) Logger.Debug($"eth_getTransactionByBlockHashAndIndex request {blockHash.ToJson()}, index: {positionIndex.ToJson()}, result: {GetJsonLog(transactionModel.ToJson())}");
            return ResultWrapper<Transaction>.Success(transactionModel);
        }

        public ResultWrapper<Transaction> eth_getTransactionByBlockNumberAndIndex(BlockParameter blockParameter, Quantity positionIndex)
        {
            if (_blockchainBridge.Head == null)
            {
                return ResultWrapper<Transaction>.Fail($"Incorrect head block: {(_blockchainBridge.Head != null ? "HeadBlock is null" : "HeadBlock header is null")}");
            }

            var result = GetBlock(blockParameter);
            if (result.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<Transaction>.Fail(result.Result.Error, result.ErrorType);
            }

            var index = positionIndex.AsNumber();
            if (!index.HasValue)
            {
                return ResultWrapper<Transaction>.Fail("Position Index is required", ErrorType.InvalidParams);
            }

            if (index.Value < 0 || index.Value > result.Data.Transactions.Length - 1)
            {
                return ResultWrapper<Transaction>.Fail("Position Index is incorrect", ErrorType.InvalidParams);
            }

            Core.Block block = result.Data;
            var transaction = block.Transactions[(int) index.Value];
            var transactionModel = _modelMapper.MapTransaction(block.Hash, block.Number, (int) index.Value, transaction);

            if(Logger.IsDebug) Logger.Debug($"eth_getTransactionByBlockNumberAndIndex request {blockParameter}, index: {positionIndex.ToJson()}, result: {GetJsonLog(transactionModel.ToJson())}");
            return ResultWrapper<Transaction>.Success(transactionModel);
        }

        public ResultWrapper<TransactionReceipt> eth_getTransactionReceipt(Data txHashData)
        {
            Keccak txHash = new Keccak(txHashData.Value);
            var transactionReceipt = _blockchainBridge.GetTransactionReceipt(txHash);
            if (transactionReceipt == null)
            {
                return ResultWrapper<TransactionReceipt>.Fail($"Cannot find transactionReceipt for transaction hash: {txHash}", ErrorType.NotFound);
            }

            var transactionReceiptModel = _modelMapper.MapTransactionReceipt(txHash, transactionReceipt);
            if (Logger.IsTrace) Logger.Trace($"eth_getTransactionReceipt request {txHashData.ToJson()}, result: {GetJsonLog(transactionReceiptModel.ToJson())}");
            return ResultWrapper<TransactionReceipt>.Success(transactionReceiptModel);
        }

        public ResultWrapper<Block> eth_getUncleByBlockHashAndIndex(Data blockHashData, Quantity positionIndex)
        {
            Keccak blockHash = new Keccak(blockHashData.Value);
            var block = _blockchainBridge.FindBlock(blockHash, false);
            if (block == null)
            {
                return ResultWrapper<Block>.Fail($"Cannot find block for hash: {blockHash}", ErrorType.NotFound);
            }

            var index = positionIndex.AsNumber();
            if (!index.HasValue)
            {
                return ResultWrapper<Block>.Fail("Position Index is required", ErrorType.InvalidParams);
            }

            if (index.Value < 0 || index.Value > block.Ommers.Length - 1)
            {
                return ResultWrapper<Block>.Fail("Position Index is incorrect", ErrorType.InvalidParams);
            }

            var ommerHeader = block.Ommers[(int) index.Value];
            var ommer = _blockchainBridge.FindBlock(ommerHeader.Hash, false);
            if (ommer == null)
            {
                return ResultWrapper<Block>.Fail($"Cannot find ommer for hash: {ommerHeader.Hash}", ErrorType.NotFound);
            }

            var blockModel = _modelMapper.MapBlock(ommer, false);

            if (Logger.IsTrace) Logger.Trace($"eth_getUncleByBlockHashAndIndex request {blockHashData.ToJson()}, index: {positionIndex.ToJson()}, result: {GetJsonLog(blockModel.ToJson())}");
            return ResultWrapper<Block>.Success(blockModel);
        }

        public ResultWrapper<Block> eth_getUncleByBlockNumberAndIndex(BlockParameter blockParameter, Quantity positionIndex)
        {
            if (_blockchainBridge.Head == null)
            {
                return ResultWrapper<Block>.Fail($"Incorrect head block: {(_blockchainBridge.Head != null ? "HeadBlock is null" : "HeadBlock header is null")}");
            }

            var result = GetBlock(blockParameter);
            if (result.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<Block>.Fail(result.Result.Error, result.ErrorType);
            }

            var index = positionIndex.AsNumber();
            if (!index.HasValue)
            {
                return ResultWrapper<Block>.Fail("Position Index is required", ErrorType.InvalidParams);
            }

            if (index.Value < 0 || index.Value > result.Data.Ommers.Length - 1)
            {
                return ResultWrapper<Block>.Fail("Position Index is incorrect", ErrorType.InvalidParams);
            }

            var ommerHeader = result.Data.Ommers[(int) index.Value];
            var ommer = _blockchainBridge.FindBlock(ommerHeader.Hash, false);
            if (ommer == null)
            {
                return ResultWrapper<Block>.Fail($"Cannot find ommer for hash: {ommerHeader.Hash}", ErrorType.NotFound);
            }

            var blockModel = _modelMapper.MapBlock(ommer, false);

            if(Logger.IsDebug) Logger.Debug($"eth_getUncleByBlockNumberAndIndex request {blockParameter}, index: {positionIndex.ToJson()}, result: {GetJsonLog(blockModel.ToJson())}");
            return ResultWrapper<Block>.Success(blockModel);
        }

        public ResultWrapper<IEnumerable<string>> eth_getCompilers()
        {
            return ResultWrapper<IEnumerable<string>>.Fail("eth_getCompilers is DEPRECATED");
        }

        public ResultWrapper<Data> eth_compileLLL(string code)
        {
            return ResultWrapper<Data>.Fail("eth_compileLLL is DEPRECATED");
        }

        public ResultWrapper<Data> eth_compileSolidity(string code)
        {
            return ResultWrapper<Data>.Fail("eth_compileSolidity is DEPRECATED");
        }

        public ResultWrapper<Data> eth_compileSerpent(string code)
        {
            return ResultWrapper<Data>.Fail("eth_compileSerpent is DEPRECATED");
        }

        public ResultWrapper<Quantity> eth_newFilter(Filter filter)
        {
            var fromBlock = MapFilterBlock(filter.FromBlock);
            var toBlock = MapFilterBlock(filter.ToBlock);
            int filterId = _blockchainBridge.NewFilter(fromBlock, toBlock, filter.Address, filter.Topics);
            return ResultWrapper<Quantity>.Success(new Quantity(filterId));
        }

        private FilterBlock MapFilterBlock(BlockParameter parameter)
            => parameter.BlockId != null
                ? new FilterBlock(new UInt256(parameter.BlockId.AsNumber() ?? 0))
                : new FilterBlock(MapFilterBlockType(parameter.Type));

        private FilterBlockType MapFilterBlockType(BlockParameterType type)
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

        public ResultWrapper<Quantity> eth_newBlockFilter()
        {
            int filterId = _blockchainBridge.NewBlockFilter();
            return ResultWrapper<Quantity>.Success(new Quantity(filterId));
        }

        public ResultWrapper<Quantity> eth_newPendingTransactionFilter(Filter filter)
        {
            return ResultWrapper<Quantity>.Fail("eth_newPendingTransactionFilter not supported");
        }

        public ResultWrapper<bool> eth_uninstallFilter(Quantity filterId)
        {
            _blockchainBridge.UninstallFilter(filterId.Value.ToInt32());
            return ResultWrapper<bool>.Success(true);
        }

        public ResultWrapper<IEnumerable<object>> eth_getFilterChanges(Quantity filterId)
        {
            var id = filterId.Value.ToInt32();
            FilterType filterType = _blockchainBridge.GetFilterType(id);
            switch (filterType)
            {
                case FilterType.BlockFilter:
                    return _blockchainBridge.FilterExists(id)
                        ? ResultWrapper<IEnumerable<object>>.Success(_blockchainBridge.GetBlockFilterChanges(id).Select(b => new Data(b.Bytes)).ToArray())
                        : ResultWrapper<IEnumerable<object>>.Fail($"Filter with id: '{filterId}' does not exist.");
                case FilterType.LogFilter:
                    return _blockchainBridge.FilterExists(id)
                        ? ResultWrapper<IEnumerable<object>>.Success(MapLogs(_blockchainBridge.GetLogFilterChanges(id)).ToArray())
                        : ResultWrapper<IEnumerable<object>>.Fail($"Filter with id: '{filterId}' does not exist.");
                default:
                    throw new NotSupportedException($"Filter type {filterType} is not supported");
            }
        }

        public ResultWrapper<IEnumerable<Log>> eth_getFilterLogs(Quantity filterId)
        {
            var id = filterId.Value.ToInt32();

            return _blockchainBridge.FilterExists(id)
                ? ResultWrapper<IEnumerable<Log>>.Success(MapLogs(_blockchainBridge.GetFilterLogs(id)))
                : ResultWrapper<IEnumerable<Log>>.Fail($"Filter with id: '{filterId}' does not exist.");
        }

        public ResultWrapper<IEnumerable<Log>> eth_getLogs(Filter filter)
        {
            var fromBlock = MapFilterBlock(filter.FromBlock);
            var toBlock = MapFilterBlock(filter.ToBlock);

            return ResultWrapper<IEnumerable<Log>>.Success(MapLogs(_blockchainBridge.GetLogs(fromBlock, toBlock,
                filter.Address,
                filter.Topics)));
        }

        private IEnumerable<Log> MapLogs(IEnumerable<FilterLog> logs)
            =>  logs.Select(l => new Log
                {
                    Removed = l.Removed,
                    LogIndex = new Quantity(l.LogIndex),
                    TransactionHash = new Data(l.TransactionHash),
                    TransactionIndex = new Quantity(l.TransactionIndex),
                    BlockHash = new Data(l.BlockHash),
                    BlockNumber = new Quantity(l.BlockNumber),
                    Data = new Data(l.Data),
                    Address = new Data(l.Address),
                    Topics = l.Topics.Select(t => new Data(t)).ToArray()
                });

        public ResultWrapper<IEnumerable<Data>> eth_getWork()
        {
            return ResultWrapper<IEnumerable<Data>>.Fail("eth_getWork not supported");
        }

        public ResultWrapper<bool> eth_submitWork(Data nonce, Data headerPowHash, Data mixDigest)
        {
            return ResultWrapper<bool>.Fail("eth_submitWork not supported");
        }

        public ResultWrapper<bool> eth_submitHashrate(string hashRate, string id)
        {
            return ResultWrapper<bool>.Fail("eth_submitHashrate not supported");
        }

        private ResultWrapper<Quantity> GetOmmersCount(BlockParameter blockParameter)
        {
            if (blockParameter.Type == BlockParameterType.Pending)
            {
                var headBlock = _blockchainBridge.FindBlock(_blockchainBridge.BestSuggested.Hash, false);
                var count = headBlock.Ommers.Length;
                return ResultWrapper<Quantity>.Success(new Quantity(count));
            }

            var block = GetBlock(blockParameter);
            if (block.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<Quantity>.Fail(block.Result.Error);
            }

            return ResultWrapper<Quantity>.Success(new Quantity(block.Data.Ommers.Length));
        }

        private ResultWrapper<Quantity> GetTransactionCount(BlockParameter blockParameter)
        {
            if (blockParameter.Type == BlockParameterType.Pending)
            {
                var headBlock = _blockchainBridge.FindBlock(_blockchainBridge.BestSuggested.Hash, false);
                var count = headBlock.Transactions.Length;
                return ResultWrapper<Quantity>.Success(new Quantity(count));
            }

            var block = GetBlock(blockParameter);
            if (block.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<Quantity>.Fail(block.Result.Error);
            }

            return ResultWrapper<Quantity>.Success(new Quantity(block.Data.Transactions.Length));
        }

        private ResultWrapper<Data> GetAccountCode(Address address, BlockParameter blockParameter)
        {
            if (blockParameter.Type == BlockParameterType.Pending)
            {
                var code = _blockchainBridge.GetCode(address);
                return ResultWrapper<Data>.Success(new Data(code));
            }

            var block = GetBlock(blockParameter);
            if (block.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<Data>.Fail(block.Result.Error);
            }

            return GetAccountCode(address, block.Data.Header.StateRoot);
        }

        private ResultWrapper<Quantity> GetAccountNonce(Address address, BlockParameter blockParameter)
        {
            if (blockParameter.Type == BlockParameterType.Pending)
            {
                var nonce = _blockchainBridge.GetNonce(address);
                return ResultWrapper<Quantity>.Success(new Quantity(nonce));
            }

            var block = GetBlock(blockParameter);
            if (block.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<Quantity>.Fail(block.Result.Error);
            }

            return GetAccountNonce(address, block.Data.Header.StateRoot);
        }

        private ResultWrapper<Quantity> GetAccountBalance(Address address, BlockParameter blockParameter)
        {
            if (blockParameter.Type == BlockParameterType.Pending)
            {
                var balance = _blockchainBridge.GetBalance(address);
                return ResultWrapper<Quantity>.Success(new Quantity(balance));
            }

            var block = GetBlock(blockParameter);
            if (block.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<Quantity>.Fail(block.Result.Error);
            }

            return GetAccountBalance(address, block.Data.Header.StateRoot);
        }

        private ResultWrapper<Core.Block> GetBlock(BlockParameter blockParameter)
        {
            switch (blockParameter.Type)
            {
                case BlockParameterType.Pending:
                    var pending = _blockchainBridge.FindBlock(_blockchainBridge.BestSuggested.Hash, false);
                    return ResultWrapper<Core.Block>.Success(pending); // TODO: a pending block for sealEngine, work in progress
                case BlockParameterType.Latest:
                    return ResultWrapper<Core.Block>.Success(_blockchainBridge.RetrieveHeadBlock());

                case BlockParameterType.Earliest:
                    var genesis = _blockchainBridge.RetrieveGenesisBlock();
                    return ResultWrapper<Core.Block>.Success(genesis);
                case BlockParameterType.BlockId:
                    if (blockParameter.BlockId?.Value == null)
                    {
                        return ResultWrapper<Core.Block>.Fail($"Block id is required for {BlockParameterType.BlockId}", ErrorType.InvalidParams);
                    }

                    var value = blockParameter.BlockId.AsNumber();
                    if (!value.HasValue)
                    {
                        return ResultWrapper<Core.Block>.Fail("Invalid block id", ErrorType.InvalidParams);
                    }

                    var block = _blockchainBridge.FindBlock(new UInt256(value.Value));
                    if (block == null)
                    {
                        return ResultWrapper<Core.Block>.Fail($"Cannot find block for {value.Value}", ErrorType.NotFound);
                    }

                    return ResultWrapper<Core.Block>.Success(block);
                default:
                    throw new Exception($"BlockParameterType not supported: {blockParameter.Type}");
            }
        }

        private ResultWrapper<Quantity> GetAccountBalance(Address address, Keccak stateRoot)
        {
            var account = _blockchainBridge.GetAccount(address, stateRoot);
            if (account == null)
            {
                return ResultWrapper<Quantity>.Fail("Cannot find account", ErrorType.NotFound);
            }

            return ResultWrapper<Quantity>.Success(new Quantity(account.Balance));
        }

        private ResultWrapper<Quantity> GetAccountNonce(Address address, Keccak stateRoot)
        {
            var account = _blockchainBridge.GetAccount(address, stateRoot);
            if (account == null)
            {
                return ResultWrapper<Quantity>.Fail("Cannot find account", ErrorType.NotFound);
            }

            return ResultWrapper<Quantity>.Success(new Quantity(account.Nonce));
        }

        private ResultWrapper<Data> GetAccountCode(Address address, Keccak stateRoot)
        {
            var account = _blockchainBridge.GetAccount(address, stateRoot);
            if (account == null)
            {
                return ResultWrapper<Data>.Fail("Cannot find account", ErrorType.NotFound);
            }

            var code = _blockchainBridge.GetCode(account.CodeHash);
            return ResultWrapper<Data>.Success(new Data(code));
        }
    }
}