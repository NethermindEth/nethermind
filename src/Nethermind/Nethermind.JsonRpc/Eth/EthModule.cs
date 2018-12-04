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
using Nethermind.Blockchain.Filters;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Dirichlet.Numerics;
using Nethermind.JsonRpc.Module;

namespace Nethermind.JsonRpc.Eth
{
    public class EthModule : ModuleBase, IEthModule
    {
        private Encoding _messageEncoding = Encoding.UTF8;
        
        private const string SignatureTemplate = "\x19Ethereum Signed Message:\n{0}{1}";
        
        private readonly IBlockchainBridge _blockchainBridge;
        
        private readonly IJsonRpcModelMapper _modelMapper;

        public EthModule(IJsonSerializer jsonSerializer, IConfigProvider configurationProvider, IJsonRpcModelMapper modelMapper, ILogManager logManager, IBlockchainBridge blockchainBridge) : base(configurationProvider, logManager, jsonSerializer)
        {
            _blockchainBridge = blockchainBridge;
            _modelMapper = modelMapper;
        }
        
        public override ModuleType ModuleType => ModuleType.Eth;

        public ResultWrapper<string> eth_protocolVersion()
        {
            return ResultWrapper<string>.Success("62");
        }

        public ResultWrapper<SyncingResult> eth_syncing()
        {
            var result = new SyncingResult
            {
                CurrentBlock = _blockchainBridge.Head.Number,
                HighestBlock = _blockchainBridge.BestKnown,
                StartingBlock = UInt256.Zero
            };
            
            if (Logger.IsTrace) Logger.Trace($"eth_syncing request, result: {_blockchainBridge.Head.Number}/{_blockchainBridge.BestKnown}");
            return ResultWrapper<SyncingResult>.Success(result);
        }

        public ResultWrapper<byte[]> eth_snapshot()
        {
            return ResultWrapper<byte[]>.Fail("eth_snapshot not supported");
        }

        public ResultWrapper<Address> eth_coinbase()
        {
            return ResultWrapper<Address>.Success(Address.Zero);
        }

        public ResultWrapper<bool> eth_mining()
        {
            return ResultWrapper<bool>.Success(false);
        }

        public ResultWrapper<BigInteger> eth_hashrate()
        {
            return ResultWrapper<BigInteger>.Success(0);
        }

        [Todo("Gas pricer to be implemented")]
        public ResultWrapper<BigInteger> eth_gasPrice()
        {
            return ResultWrapper<BigInteger>.Success(20.GWei());
        }

        public ResultWrapper<IEnumerable<Address>> eth_accounts()
        {
            try
            {
                var result = _blockchainBridge.GetWalletAccounts();
                Address[] data = result.ToArray();
                if (Logger.IsTrace) Logger.Trace($"eth_accounts request, result: {string.Join(", ", data.Select(x => x.ToString()))}");
                return ResultWrapper<IEnumerable<Address>>.Success(data.ToArray());
            }
            catch (Exception e)
            {
                if (Logger.IsError) Logger.Error($"Failed to server eth_accounts request", e);
                return ResultWrapper<IEnumerable<Address>>.Fail("Error while getting key addresses from wallet.");
            }
        }

        public ResultWrapper<BigInteger> eth_blockNumber()
        {
            if (_blockchainBridge.Head == null)
            {
                return ResultWrapper<BigInteger>.Fail($"Incorrect head block: {(_blockchainBridge.Head != null ? "HeadBlock is null" : "HeadBlock header is null")}");
            }

            var number = _blockchainBridge.Head.Number;
            if (Logger.IsTrace) Logger.Trace($"eth_blockNumber request, result: {number}");
            return ResultWrapper<BigInteger>.Success(number);
        }

        public ResultWrapper<BigInteger> eth_getBalance(Address address, BlockParameter blockParameter)
        {
            if (_blockchainBridge.Head == null)
            {
                return ResultWrapper<BigInteger>.Fail($"Incorrect head block: {(_blockchainBridge.Head != null ? "HeadBlock is null" : "HeadBlock header is null")}");
            }

            var result = GetAccountBalance(address, blockParameter);
            if (result.Result.ResultType == ResultType.Failure)
            {
                return result;
            }

            if (Logger.IsTrace) Logger.Trace($"eth_getBalance request {address}, {blockParameter}, result: {result.Data}");
            return result;
        }

        public ResultWrapper<byte[]> eth_getStorageAt(Address address, BigInteger positionIndex, BlockParameter blockParameter)
        {
            return ResultWrapper<byte[]>.Fail("eth_getStorageAt not supported");
        }

        public ResultWrapper<BigInteger> eth_getTransactionCount(Address address, BlockParameter blockParameter)
        {
            if (_blockchainBridge.Head == null)
            {
                return ResultWrapper<BigInteger>.Fail($"Incorrect head block: {(_blockchainBridge.Head != null ? "HeadBlock is null" : "HeadBlock header is null")}");
            }

            var result = GetAccountNonce(address, blockParameter);
            if (result.Result.ResultType == ResultType.Failure)
            {
                return result;
            }

            if (Logger.IsTrace) Logger.Trace($"eth_getTransactionCount request {address}, {blockParameter}, result: {result.Data}");
            return result;
        }

        public ResultWrapper<BigInteger> eth_getBlockTransactionCountByHash(Keccak blockHash)
        {
            var block = _blockchainBridge.FindBlock(blockHash, false);
            if (block == null)
            {
                return ResultWrapper<BigInteger>.Fail($"Cannot find block for hash: {blockHash}", ErrorType.NotFound);
            }

            if (Logger.IsTrace) Logger.Trace($"eth_getBlockTransactionCountByHash request {blockHash}, result: {block.Transactions.Length}");
            return ResultWrapper<BigInteger>.Success(block.Transactions.Length);
        }

        public ResultWrapper<BigInteger> eth_getBlockTransactionCountByNumber(BlockParameter blockParameter)
        {
            if (_blockchainBridge.Head == null)
            {
                return ResultWrapper<BigInteger>.Fail($"Incorrect head block: {(_blockchainBridge.Head != null ? "HeadBlock is null" : "HeadBlock header is null")}");
            }

            var transactionCount = GetTransactionCount(blockParameter);
            if (transactionCount.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<BigInteger>.Fail(transactionCount.Result.Error, transactionCount.ErrorType);
            }

            if (Logger.IsTrace) Logger.Trace($"eth_getBlockTransactionCountByNumber request {blockParameter}, result: {transactionCount.Data}");
            return transactionCount;
        }

        public ResultWrapper<BigInteger> eth_getUncleCountByBlockHash(Keccak blockHash)
        {
            var block = _blockchainBridge.FindBlock(blockHash, false);
            if (block == null)
            {
                return ResultWrapper<BigInteger>.Fail($"Cannot find block for hash: {blockHash}", ErrorType.NotFound);
            }

            if (Logger.IsTrace) Logger.Trace($"eth_getUncleCountByBlockHash request {blockHash}, result: {block.Transactions.Length}");
            return ResultWrapper<BigInteger>.Success(block.Ommers.Length);
        }

        public ResultWrapper<BigInteger> eth_getUncleCountByBlockNumber(BlockParameter blockParameter)
        {
            if (_blockchainBridge.Head == null)
            {
                return ResultWrapper<BigInteger>.Fail($"Incorrect head block: {(_blockchainBridge.Head != null ? "HeadBlock is null" : "HeadBlock header is null")}");
            }

            var ommersCount = GetOmmersCount(blockParameter);
            if (ommersCount.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<BigInteger>.Fail(ommersCount.Result.Error, ommersCount.ErrorType);
            }

            if (Logger.IsTrace) Logger.Trace($"eth_getUncleCountByBlockNumber request {blockParameter}, result: {ommersCount.Data}");
            return ommersCount;
        }

        public ResultWrapper<byte[]> eth_getCode(Address address, BlockParameter blockParameter)
        {
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

        public ResultWrapper<byte[]> eth_sign(Address addressData, byte[] message)
        {
            Signature sig;
            try
            {
                Address address = addressData;
                var messageText = _messageEncoding.GetString(message);
                var signatureText = string.Format(SignatureTemplate, messageText.Length, messageText);
                sig = _blockchainBridge.Sign(address, Keccak.Compute(signatureText));
            }
            catch (Exception)
            {
                return ResultWrapper<byte[]>.Fail($"Unable to sign as {addressData}");
            }

            if (Logger.IsTrace) Logger.Trace($"eth_sign request {addressData}, {message}, result: {sig}");
            return ResultWrapper<byte[]>.Success(sig.Bytes);
        }

        public ResultWrapper<Keccak> eth_sendTransaction(Transaction transaction)
        {
            Core.Transaction tx = _modelMapper.MapTransaction(transaction);
            Keccak txHash = _blockchainBridge.SendTransaction(tx);
            return ResultWrapper<Keccak>.Success(txHash);
        }

        public ResultWrapper<Keccak> eth_sendRawTransaction(byte[] transaction)
        {
            throw new NotImplementedException();
        }

        public ResultWrapper<byte[]> eth_call(Transaction transactionCall, BlockParameter blockParameter)
        {
            Core.Block block = GetBlock(blockParameter).Data;
            byte[] result = _blockchainBridge.Call(block, _modelMapper.MapTransaction(transactionCall));
            return ResultWrapper<byte[]>.Success(result);
        }

        public ResultWrapper<BigInteger> eth_estimateGas(Transaction transactionCall, BlockParameter blockParameter)
        {
            return ResultWrapper<BigInteger>.Fail("eth_estimateGas not supported");
        }

        public ResultWrapper<Block> eth_getBlockByHash(Keccak blockHash, bool returnFullTransactionObjects)
        {
            var block = _blockchainBridge.FindBlock(blockHash, false);
            if (block == null)
            {
                return ResultWrapper<Block>.Fail($"Cannot find block for hash: {blockHash}", ErrorType.NotFound);
            }

            var blockModel = _modelMapper.MapBlock(block, returnFullTransactionObjects);

            if (Logger.IsDebug) Logger.Debug($"eth_getBlockByHash request {blockHash}, result: {GetJsonLog(blockModel.ToJson())}");
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
                if (Logger.IsTrace) Logger.Trace($"eth_getBlockByNumber request {blockParameter}, result: {result.ErrorType}");
                return ResultWrapper<Block>.Fail(result.Result.Error, result.ErrorType);
            }

            var blockModel = _modelMapper.MapBlock(result.Data, returnFullTransactionObjects);

            if (Logger.IsTrace) Logger.Trace($"eth_getBlockByNumber request {blockParameter}, result: {GetJsonLog(blockModel.ToJson())}");
            return ResultWrapper<Block>.Success(blockModel);
        }

        public ResultWrapper<Transaction> eth_getTransactionByHash(Keccak transactionHash)
        {
            (Core.TransactionReceipt receipt, Core.Transaction transaction) = _blockchainBridge.GetTransaction(transactionHash);
            if (transaction == null)
            {
                return ResultWrapper<Transaction>.Fail($"Cannot find transaction for hash: {transactionHash}", ErrorType.NotFound);
            }

            var transactionModel = _modelMapper.MapTransaction(receipt, transaction);
            if (Logger.IsTrace) Logger.Trace($"eth_getTransactionByHash request {transactionHash}, result: {GetJsonLog(transactionModel.ToJson())}");
            return ResultWrapper<Transaction>.Success(transactionModel);
        }

        public ResultWrapper<Transaction> eth_getTransactionByBlockHashAndIndex(Keccak blockHash, BigInteger positionIndex)
        {
            var block = _blockchainBridge.FindBlock(blockHash, false);
            if (block == null)
            {
                return ResultWrapper<Transaction>.Fail($"Cannot find block for hash: {blockHash}", ErrorType.NotFound);
            }

            if (positionIndex < 0 || positionIndex > block.Transactions.Length - 1)
            {
                return ResultWrapper<Transaction>.Fail("Position Index is incorrect", ErrorType.InvalidParams);
            }

            var transaction = block.Transactions[(int) positionIndex];
            var transactionModel = _modelMapper.MapTransaction(block.Hash, block.Number, (int) positionIndex, transaction);

            if(Logger.IsDebug) Logger.Debug($"eth_getTransactionByBlockHashAndIndex request {blockHash}, index: {positionIndex}, result: {GetJsonLog(transactionModel.ToJson())}");
            return ResultWrapper<Transaction>.Success(transactionModel);
        }

        public ResultWrapper<Transaction> eth_getTransactionByBlockNumberAndIndex(BlockParameter blockParameter, BigInteger positionIndex)
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

            if (positionIndex < 0 || positionIndex > result.Data.Transactions.Length - 1)
            {
                return ResultWrapper<Transaction>.Fail("Position Index is incorrect", ErrorType.InvalidParams);
            }

            Core.Block block = result.Data;
            var transaction = block.Transactions[(int) positionIndex];
            var transactionModel = _modelMapper.MapTransaction(block.Hash, block.Number, (int) positionIndex, transaction);

            if(Logger.IsDebug) Logger.Debug($"eth_getTransactionByBlockNumberAndIndex request {blockParameter}, index: {positionIndex}, result: {GetJsonLog(transactionModel.ToJson())}");
            return ResultWrapper<Transaction>.Success(transactionModel);
        }

        public ResultWrapper<TransactionReceipt> eth_getTransactionReceipt(Keccak txHash)
        {
            var transactionReceipt = _blockchainBridge.GetTransactionReceipt(txHash);
            if (transactionReceipt == null)
            {
                return ResultWrapper<TransactionReceipt>.Fail($"Cannot find transactionReceipt for transaction hash: {txHash}", ErrorType.NotFound);
            }

            var transactionReceiptModel = _modelMapper.MapTransactionReceipt(txHash, transactionReceipt);
            if (Logger.IsTrace) Logger.Trace($"eth_getTransactionReceipt request {txHash}, result: {GetJsonLog(transactionReceiptModel.ToJson())}");
            return ResultWrapper<TransactionReceipt>.Success(transactionReceiptModel);
        }

        public ResultWrapper<Block> eth_getUncleByBlockHashAndIndex(Keccak blockHashData, BigInteger positionIndex)
        {
            Keccak blockHash = blockHashData;
            var block = _blockchainBridge.FindBlock(blockHash, false);
            if (block == null)
            {
                return ResultWrapper<Block>.Fail($"Cannot find block for hash: {blockHash}", ErrorType.NotFound);
            }

            if (positionIndex < 0 || positionIndex > block.Ommers.Length - 1)
            {
                return ResultWrapper<Block>.Fail("Position Index is incorrect", ErrorType.InvalidParams);
            }

            var ommerHeader = block.Ommers[(int) positionIndex];
            var ommer = _blockchainBridge.FindBlock(ommerHeader.Hash, false);
            if (ommer == null)
            {
                return ResultWrapper<Block>.Fail($"Cannot find ommer for hash: {ommerHeader.Hash}", ErrorType.NotFound);
            }

            var blockModel = _modelMapper.MapBlock(ommer, false);

            if (Logger.IsTrace) Logger.Trace($"eth_getUncleByBlockHashAndIndex request {blockHashData}, index: {positionIndex}, result: {GetJsonLog(blockModel.ToJson())}");
            return ResultWrapper<Block>.Success(blockModel);
        }

        public ResultWrapper<Block> eth_getUncleByBlockNumberAndIndex(BlockParameter blockParameter, BigInteger positionIndex)
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

            if (positionIndex < 0 || positionIndex > result.Data.Ommers.Length - 1)
            {
                return ResultWrapper<Block>.Fail("Position Index is incorrect", ErrorType.InvalidParams);
            }

            var ommerHeader = result.Data.Ommers[(int) positionIndex];
            var ommer = _blockchainBridge.FindBlock(ommerHeader.Hash, false);
            if (ommer == null)
            {
                return ResultWrapper<Block>.Fail($"Cannot find ommer for hash: {ommerHeader.Hash}", ErrorType.NotFound);
            }

            var blockModel = _modelMapper.MapBlock(ommer, false);

            if(Logger.IsDebug) Logger.Debug($"eth_getUncleByBlockNumberAndIndex request {blockParameter}, index: {positionIndex}, result: {GetJsonLog(blockModel.ToJson())}");
            return ResultWrapper<Block>.Success(blockModel);
        }

        public ResultWrapper<IEnumerable<string>> eth_getCompilers()
        {
            return ResultWrapper<IEnumerable<string>>.Fail("eth_getCompilers is DEPRECATED");
        }

        public ResultWrapper<byte[]> eth_compileLLL(string code)
        {
            return ResultWrapper<byte[]>.Fail("eth_compileLLL is DEPRECATED");
        }

        public ResultWrapper<byte[]> eth_compileSolidity(string code)
        {
            return ResultWrapper<byte[]>.Fail("eth_compileSolidity is DEPRECATED");
        }

        public ResultWrapper<byte[]> eth_compileSerpent(string code)
        {
            return ResultWrapper<byte[]>.Fail("eth_compileSerpent is DEPRECATED");
        }

        public ResultWrapper<BigInteger> eth_newFilter(Filter filter)
        {
            var fromBlock = MapFilterBlock(filter.FromBlock);
            var toBlock = MapFilterBlock(filter.ToBlock);
            int filterId = _blockchainBridge.NewFilter(fromBlock, toBlock, filter.Address, filter.Topics);
            return ResultWrapper<BigInteger>.Success(filterId);
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

        public ResultWrapper<BigInteger> eth_newBlockFilter()
        {
            int filterId = _blockchainBridge.NewBlockFilter();
            return ResultWrapper<BigInteger>.Success(filterId);
        }

        public ResultWrapper<BigInteger> eth_newPendingTransactionFilter()
        {
            int filterId = _blockchainBridge.NewPendingTransactionFilter();
            return ResultWrapper<BigInteger>.Success(filterId);
        }

        public ResultWrapper<bool> eth_uninstallFilter(BigInteger filterId)
        {
            _blockchainBridge.UninstallFilter((int)filterId);
            return ResultWrapper<bool>.Success(true);
        }

        public ResultWrapper<IEnumerable<object>> eth_getFilterChanges(BigInteger filterId)
        {
            var id = (int)filterId;
            FilterType filterType = _blockchainBridge.GetFilterType(id);
            switch (filterType)
            {
                case FilterType.BlockFilter:
                    return _blockchainBridge.FilterExists(id)
                        ? ResultWrapper<IEnumerable<object>>.Success(_blockchainBridge.GetBlockFilterChanges(id)
                            .Select(b => new Data(b.Bytes)).ToArray())
                        : ResultWrapper<IEnumerable<object>>.Fail($"Filter with id: '{filterId}' does not exist.");
                case FilterType.PendingTransactionFilter:
                    return _blockchainBridge.FilterExists(id)
                        ? ResultWrapper<IEnumerable<object>>.Success(_blockchainBridge
                            .GetPendingTransactionFilterChanges(id).Select(b => new Data(b.Bytes)).ToArray())
                        : ResultWrapper<IEnumerable<object>>.Fail($"Filter with id: '{filterId}' does not exist.");
                case FilterType.LogFilter:
                    return _blockchainBridge.FilterExists(id)
                        ? ResultWrapper<IEnumerable<object>>.Success(
                            _blockchainBridge.GetLogFilterChanges(id).ToArray())
                        : ResultWrapper<IEnumerable<object>>.Fail($"Filter with id: '{filterId}' does not exist.");
                default:
                    throw new NotSupportedException($"Filter type {filterType} is not supported");
            }
        }

        public ResultWrapper<IEnumerable<FilterLog>> eth_getFilterLogs(BigInteger filterId)
        {
            var id = (int)filterId;

            return _blockchainBridge.FilterExists(id)
                ? ResultWrapper<IEnumerable<FilterLog>>.Success(_blockchainBridge.GetFilterLogs(id))
                : ResultWrapper<IEnumerable<FilterLog>>.Fail($"Filter with id: '{filterId}' does not exist.");
        }

        public ResultWrapper<IEnumerable<FilterLog>> eth_getLogs(Filter filter)
        {
            var fromBlock = MapFilterBlock(filter.FromBlock);
            var toBlock = MapFilterBlock(filter.ToBlock);

            return ResultWrapper<IEnumerable<FilterLog>>.Success(_blockchainBridge.GetLogs(fromBlock, toBlock,
                filter.Address,
                filter.Topics));
        }

        public ResultWrapper<IEnumerable<byte[]>> eth_getWork()
        {
            return ResultWrapper<IEnumerable<byte[]>>.Fail("eth_getWork not supported");
        }

        public ResultWrapper<bool> eth_submitWork(byte[] nonce, Keccak headerPowHash, byte[] mixDigest)
        {
            return ResultWrapper<bool>.Fail("eth_submitWork not supported");
        }

        public ResultWrapper<bool> eth_submitHashrate(string hashRate, string id)
        {
            return ResultWrapper<bool>.Fail("eth_submitHashrate not supported");
        }

        private ResultWrapper<BigInteger> GetOmmersCount(BlockParameter blockParameter)
        {
            if (blockParameter.Type == BlockParameterType.Pending)
            {
                var headBlock = _blockchainBridge.FindBlock(_blockchainBridge.BestSuggested.Hash, false);
                var count = headBlock.Ommers.Length;
                return ResultWrapper<BigInteger>.Success(count);
            }

            var block = GetBlock(blockParameter);
            if (block.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<BigInteger>.Fail(block.Result.Error);
            }

            return ResultWrapper<BigInteger>.Success(block.Data.Ommers.Length);
        }

        private ResultWrapper<BigInteger> GetTransactionCount(BlockParameter blockParameter)
        {
            if (blockParameter.Type == BlockParameterType.Pending)
            {
                var headBlock = _blockchainBridge.FindBlock(_blockchainBridge.BestSuggested.Hash, false);
                var count = headBlock.Transactions.Length;
                return ResultWrapper<BigInteger>.Success(count);
            }

            var block = GetBlock(blockParameter);
            if (block.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<BigInteger>.Fail(block.Result.Error);
            }

            return ResultWrapper<BigInteger>.Success(block.Data.Transactions.Length);
        }

        private ResultWrapper<byte[]> GetAccountCode(Address address, BlockParameter blockParameter)
        {
            if (blockParameter.Type == BlockParameterType.Pending)
            {
                var code = _blockchainBridge.GetCode(address);
                return ResultWrapper<byte[]>.Success(code);
            }

            var block = GetBlock(blockParameter);
            if (block.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<byte[]>.Fail(block.Result.Error);
            }

            return GetAccountCode(address, block.Data.Header.StateRoot);
        }

        private ResultWrapper<BigInteger> GetAccountNonce(Address address, BlockParameter blockParameter)
        {
            if (blockParameter.Type == BlockParameterType.Pending)
            {
                var nonce = _blockchainBridge.GetNonce(address);
                return ResultWrapper<BigInteger>.Success(nonce);
            }

            var block = GetBlock(blockParameter);
            if (block.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<BigInteger>.Fail(block.Result.Error);
            }

            return GetAccountNonce(address, block.Data.Header.StateRoot);
        }

        private ResultWrapper<BigInteger> GetAccountBalance(Address address, BlockParameter blockParameter)
        {
            if (blockParameter.Type == BlockParameterType.Pending)
            {
                var balance = _blockchainBridge.GetBalance(address);
                return ResultWrapper<BigInteger>.Success(balance);
            }

            var block = GetBlock(blockParameter);
            if (block.Result.ResultType == ResultType.Failure)
            {
                return ResultWrapper<BigInteger>.Fail(block.Result.Error);
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

        private ResultWrapper<BigInteger> GetAccountBalance(Address address, Keccak stateRoot)
        {
            var account = _blockchainBridge.GetAccount(address, stateRoot);
            if (account == null)
            {
                return ResultWrapper<BigInteger>.Fail("Cannot find account", ErrorType.NotFound);
            }

            return ResultWrapper<BigInteger>.Success(account.Balance);
        }

        private ResultWrapper<BigInteger> GetAccountNonce(Address address, Keccak stateRoot)
        {
            var account = _blockchainBridge.GetAccount(address, stateRoot);
            if (account == null)
            {
                return ResultWrapper<BigInteger>.Fail("Cannot find account", ErrorType.NotFound);
            }

            return ResultWrapper<BigInteger>.Success(account.Nonce);
        }

        private ResultWrapper<byte[]> GetAccountCode(Address address, Keccak stateRoot)
        {
            var account = _blockchainBridge.GetAccount(address, stateRoot);
            if (account == null)
            {
                return ResultWrapper<byte[]>.Fail("Cannot find account", ErrorType.NotFound);
            }

            var code = _blockchainBridge.GetCode(account.CodeHash);
            return ResultWrapper<byte[]>.Success(code);
        }
    }
}